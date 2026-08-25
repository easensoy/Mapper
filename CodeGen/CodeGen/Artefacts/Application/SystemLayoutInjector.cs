using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodeGen.Translation;
using CodeGen.Translation.Interlocks;
using CodeGen.Translation.Process;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public class SystemInjector
    {
        public class BindingApplicationReport
        {
            public List<(string Component, string Detail)> Bound { get; } = new();
            public List<string> Missing { get; } = new();
            public List<(string Pin, string Value)> HcfPinAssignments { get; } = new();
        }


        private static string DescribeBinding(ActuatorBinding b) =>
            $"athome={b.AthomeTag ?? "-"} atwork={b.AtworkTag ?? "-"} outputToWork={b.OutputToWorkTag ?? "-"} outputToHome={b.OutputToHomeTag ?? "-"}";

        private static string DescribeBinding(SensorBinding b) =>
            $"input={b.InputTag ?? "-"}";


        // The application layer for the whole plan: every process the roster allocates, every
        // component it places. Nothing here is scoped to a particular station.
        public string EmitApplicationLayer(GenerationContext ctx,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            report = new BindingApplicationReport();
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            var config = ctx.Config;
            var handoffPlan = ctx.Handoffs;

            var contents = ctx.Station;

            if (string.IsNullOrEmpty(ctx.Config.SyslayPath2))
                throw new InvalidOperationException("MapperConfig.SyslayPath2 is not configured.");
            var fullPath = ctx.Config.SyslayPath2;
            var fileName = Path.GetFileName(fullPath);

            var layerId = FBIdGenerator.GenerateFBId(fileName);
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                "Phase 1: Process1 recipe arrays are emitted as syslay Parameter values on the " +
                "Process1 instance (StepType, CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep). " +
                "Scope filter trims to the Feed Station slice — Feeder + Checker actuators, " +
                "PartInHopper + PartAtChecker sensors; out-of-scope component waits " +
                "fall back to (0,0). Sensor-to-process DataConnections still not generated. " +
                "Demonstrator was cleaned of universal-architecture instances before this generation; " +
                "restore via 'git checkout' on the Demonstrator repo to revert.");

            // Process slots are allocated in Config/smc-rig.yml, keyed by the twin's own process name. They
            // stay above the component id space, so no Wait1Id collides with one. A slot may still coincide
            // with a Feed component id (10 == Shaft_Hr): harmless once the rings merge, because Feed only
            // WAITs there and its CMD states 1/3 are values Shaft_Hr never reports.
            // No top-level PLC_Start FB: Area_CAT/Station_CAT each hold their own plcStart; an external
            // one double-bootstraps and EAE rejects it.

            // Each resource's area/station stack, as layout.yml names it and the manifest types it.
            EmitInfrastructure(builder, ctx, role => role != "terminator" && role != "areaTerminator");

            // Instance names come from the plan, so the layout, the resource plan and every wiring pass
            // name the same FB. ordinal -> phase name per process is collected as each one is built and
            // written beside the project once at the end.
            var phaseNames = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);

            // Every process the roster placed, emitted on whichever resource owns it. One loop over the
            // declared resources: a renamed process, or a second one on any target, is a roster row.
            var processesOn = new Dictionary<PlcAssignment, List<string>>();
            foreach (var resource in ctx.Profile.Layout.Resources)
            {
                var placed = new List<string>();
                foreach (var proc in ctx.ProcessesOn(resource.Plc))
                {
                    var instance = ctx.InstanceName(proc.Name);
                    var (outer, recipe) = BuildProcessFbParameters(
                        ctx, proc, instance, ctx.Slots[proc.Name], withRecipe: true);
                    var at = ctx.Roster.Get(proc.Name);
                    builder.AddFB(FBIdGenerator.GenerateFBId(proc.ComponentID), instance,
                        TemplateManifest.ProcessType.Name, "Main", at?.X ?? 0, at?.Y ?? 0, outer);
                    placed.Add(instance);
                    if (recipe == null) continue;
                    phaseNames[instance] = recipe.ProcessPhaseNames;
                    AssertNothingStranded(ctx, recipe);
                    ReportStation2Recipe(report, instance, recipe, resource.Label);
                    AppendProcessRecipeComment(builder, instance, recipe);
                    if (!string.IsNullOrWhiteSpace(recipe.OrderingSummary))
                        report.Missing.Add($"recipe ordering ({instance}): {recipe.OrderingSummary}");
                }
                processesOn[resource.Plc] = placed;
            }

            if (processesOn.Values.All(p => p.Count == 0))
                throw new InvalidOperationException(
                    "[Recipe] the roster places no Process on any resource, so there is nothing to run. " +
                    "Every process the twin declares needs a target: pin it in layout.yml, or let it be " +
                    "anchored by a component it commands.");


            // The planned slots, keyed by ComponentID: interlock RuleSourceID and the engine's Wait1Id
            // are the same number because both read the one allocation.
            var scopedIds = ProcessRecipeArrayGenerator.ScopedIds(contents, ctx.Slots);

            // PLC partitioning index (name-based guess when MapperConfig is null).
            var plcIndex = config != null
                ? HcfSymbolIndex.Build(config)
                : new HcfSymbolIndex();
            var perPlcCount = new Dictionary<PlcAssignment, int>
            {
                [PlcAssignment.M262] = 0,
                [PlcAssignment.RevPi] = 0,   // RevPi hosts the Feed station in M262's stead
                [PlcAssignment.M580] = 0,
                [PlcAssignment.BX1]  = 0,
                [PlcAssignment.Unknown] = 0,
            };

            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                var actuator = contents.Actuators[i];
                int assignedId = ctx.Slots[actuator.Name.Trim()];
                var fbType = ctx.CatTypes[actuator.Name.Trim()];
                var displayName = ctx.InstanceName(actuator.Name);
                var actPlc = plcIndex.ResolveComponent(actuator.Name, bindings, ctx.Allocation);

                // The CAT declares which parameter groups its instance carries; the plan decided every
                // value. actuator_name IS the ring key this FB answers to, and TemplateMap.RingKey is
                // the one function that also spells the recipe's CmdTargetName, so the two cannot drift.
                var actParams = BuildActuatorParameters(actuator, assignedId, fbType, ctx);
                actParams["actuator_name"] = Iec61499Literal.FormatString(
                    TemplateMap.RingKey(displayName));
                if (Interlock(ctx, actuator).Count > 0 || TemplateManifest.ProtocolOrNull(fbType)?.CrossesBothWays == true)
                    report.Bound.Add((actuator.Name,
                        $"interlock RuleCount={Interlock(ctx, actuator).Count}"));

                ActuatorBinding? actBinding = null;
                bindings?.Actuators.TryGetValue(actuator.Name, out actBinding);
                if (actBinding != null) report.Bound.Add((actuator.Name, DescribeBinding(actBinding)));
                else if (bindings != null) report.Missing.Add(actuator.Name);

                // Placeholder position; CanonicalLayout overrides known names post-syslay.
                int colInPlc = perPlcCount[actPlc]++;
                var (zoneX, zoneY) = PlcZonePosition(ctx.Layout, actPlc, colInPlc, LayoutRow.Actuator);

                builder.AddFB(FBIdGenerator.GenerateFBId(actuator.ComponentID),
                    displayName, fbType, "Main",
                    zoneX, zoneY, actParams);

                if (!string.Equals(displayName, actuator.Name, StringComparison.Ordinal))
                    report.Missing.Add(
                        $"[Layout] '{actuator.Name}' emitted as FB instance '{displayName}' " +
                        "(rename from Instance_Name_Overrides xlsx sheet)");
            }

            var perPlcSensorCount = new Dictionary<PlcAssignment, int>
            {
                [PlcAssignment.M262] = 0,
                [PlcAssignment.RevPi] = 0,
                [PlcAssignment.M580] = 0,
                [PlcAssignment.BX1]  = 0,
                [PlcAssignment.Unknown] = 0,
            };

            for (int i = 0; i < contents.Sensors.Count; i++)
            {
                var sensor = contents.Sensors[i];
                int assignedId = ctx.Slots[sensor.Name.Trim()];

                SensorBinding? senBinding = null;
                bindings?.Sensors.TryGetValue(sensor.Name, out senBinding);
                if (senBinding != null) report.Bound.Add((sensor.Name, DescribeBinding(senBinding)));
                else if (bindings != null) report.Missing.Add(sensor.Name);

                var senPlc = plcIndex.ResolveComponent(sensor.Name, bindings, ctx.Allocation);
                int senCol = perPlcSensorCount[senPlc]++;
                var (sX, sY) = PlcZonePosition(ctx.Layout, senPlc, senCol, LayoutRow.Sensor);

                var senDisplayName = ctx.InstanceName(sensor.Name);

                builder.AddFB(FBIdGenerator.GenerateFBId(sensor.ComponentID),
                    senDisplayName, "Sensor_Bool_CAT", "Main",
                    sX, sY,
                    new Dictionary<string, string>
                    {
                        ["name"] = Iec61499Literal.FormatString(senDisplayName),
                        ["id"] = Iec61499Literal.FormatInt(assignedId),
                    });
            }

            // Synthesized M262 sensors: EXPLICIT ids so they never shift Feed actuator ids; off every report ring, so the Feed ring stays byte-identical.
            // Only when the twin actually declares the cross-controller tail the synth sensor rides.
            if (ctx.CrossRingSegment.Count > 0)
            {
                int synthY = 5200;
                string prevSynthInit = contents.Sensors
                    .Select(s => (s.Name ?? string.Empty).Trim())
                    .First(n => ctx.Allocation.IsFeedSide(n));
                foreach (var (synthName, synthId) in MapperConfig.M262SynthSensors)
                {
                    // The twin owns it if it declares it; synthesizing a second FB of the same name
                    // would put two components on one ring slot.
                    if (contents.Sensors.Any(s => string.Equals(s.Name, synthName, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    builder.AddFB(FBIdGenerator.GenerateFBId("m262rigsensor-" + synthName),
                        synthName, "Sensor_Bool_CAT", "Main", 2000, synthY,
                        new Dictionary<string, string>
                        {
                            ["name"] = Iec61499Literal.FormatString(synthName),
                            ["id"] = Iec61499Literal.FormatInt(synthId),
                        });
                    builder.AddEventConnection($"{prevSynthInit}.INITO", $"{synthName}.INIT");
                    prevSynthInit = synthName;
                    synthY += 500;
                }
            }

            // The terminators cap each CaS chain, so they follow the components they terminate.
            EmitInfrastructure(builder, ctx, role => role == "terminator");
            EmitInfrastructure(builder, ctx, role => role == "areaTerminator");

            // Embedded MQTT_PUBLISH binds to a connection by matching ConnectionID value (no wire); gated so output is unchanged when off.
            if (config != null && config.MqttPublishEnabled)
            {
                string brokerUrl = config.MqttBrokerUrl;
                // Scheme follows MqttSecureTls so it can't contradict the mode: insecure→mqtt:// (needs BX1 "Insecure Application", else RC101); secure→mqtts:// (needs a TLS broker, else RC100).
                string mqttScheme = config.MqttSecureTls ? "mqtts" : "mqtt";
                brokerUrl = System.Text.RegularExpressions.Regex.Replace(
                    brokerUrl, @"^[A-Za-z][A-Za-z0-9+.\-]*://", mqttScheme + "://");

                // One MQTT_CONNECTION per PLC: UNIQUE ClientIdentifier (mosquitto evicts duplicate ids), shared ConnectionID so each resource's embedded MqttPub binds locally.
                void InjectMqttConn(string fbName, string connectionId, string clientIdentifier, int x, int y)
                {
                    if (config.UseTelemetryCat)
                    {
                        // Telemetry composite wraps the MQTT_CONNECTION with the same ConnectionID, so the embedded MqttPub still binds.
                        var cfgLit = Iec61499Literal.FormatTelemetryConfig(
                            true, connectionId, brokerUrl, clientIdentifier,
                            config.MqttSecureTls ? config.MqttValidateCert : 0,
                            config.MqttSecureTls ? (config.MqttCaCert ?? string.Empty) : string.Empty);
                        builder.AddFB(FBIdGenerator.GenerateFBId(fbName), fbName,
                            "Telemetry", "Main", x, y,
                            new Dictionary<string, string> { ["Config"] = cfgLit });
                        return;
                    }
                    var p = new Dictionary<string, string>
                    {
                        ["QI"] = Iec61499Literal.FormatBool(true),
                        ["ConnectionID"] = Iec61499Literal.FormatString(connectionId),
                        ["URL"] = Iec61499Literal.FormatString(brokerUrl),
                        ["ClientIdentifier"] = Iec61499Literal.FormatString(clientIdentifier),
                    };
                    if (config.MqttSecureTls)
                    {
                        p["ValidateCert"] = config.MqttValidateCert.ToString();
                        if (!string.IsNullOrWhiteSpace(config.MqttCaCert))
                            p["CACert"] = Iec61499Literal.FormatString(config.MqttCaCert);
                    }
                    builder.AddFB(FBIdGenerator.GenerateFBId(fbName), fbName,
                        "MQTT_CONNECTION", "Runtime.NetConnectivity", x, y, p);
                }

                bool tele = config.UseTelemetryCat;
                // The Feed controller's connection follows the Feed station onto M262 or RevPi (FB name +
                // ClientIdentifier). ConnectionID stays the shared 'SMC' so the embedded MqttPub topic/
                // payload is byte-for-byte unchanged. Byte-identical for M262 (suffix M262, ClientM262).
                const string feedSuffix = "M262";
                string feedClientId = TelemetrySettings.Current.For(PlcAssignment.M262).Client;
                string bx1Name  = TelemetrySettings.Current.For(PlcAssignment.BX1).NameFor(tele);
                string feedName = tele ? $"Telemetry_{feedSuffix}" : $"MqttConn_{feedSuffix}";
                string m580Name = tele ? "Telemetry_M580" : "MqttConn_M580";

                var mqttEntry = ctx.Roster.Get(bx1Name);
                int bx1X = mqttEntry?.X ?? 29000;
                int bx1Y = mqttEntry?.Y ?? 200;
                // Each conn is routed to its own sysres via SysresFbMirror.BucketFor; BX1 bring-up is in BuildBx1Wiring, Feed/M580 below.
                InjectMqttConn(bx1Name, config.MqttConnectionName, TelemetrySettings.Current.For(PlcAssignment.BX1).Client, bx1X, bx1Y);
                InjectMqttConn(feedName, config.MqttConnectionName, feedClientId,
                    ctx.Layout.Band(PlcAssignment.M262).ColumnBaseX, 200);
                InjectMqttConn(m580Name, config.MqttConnectionName, TelemetrySettings.Current.For(PlcAssignment.M580).Client,
                    ctx.Layout.Band(PlcAssignment.M580).ColumnBaseX, 200);
                builder.AddEventConnection($"{feedName}.INITO", $"{feedName}.CONNECT");
                builder.AddEventConnection($"{m580Name}.INITO", $"{m580Name}.CONNECT");
                // Each connection is brought up by its own resource's infrastructure, whatever that
                // resource calls it.
                builder.AddEventConnection(
                    $"{ctx.ResourceFor(PlcAssignment.M262).AreaFb}.INITO", $"{feedName}.INIT");
                builder.AddEventConnection(
                    $"{ctx.ResourceFor(PlcAssignment.M580).StationFb}.INITO", $"{m580Name}.INIT");
                // A resource that RECEIVES relocated components hosts publishers of its own, so it needs
                // its own connection alongside the one the components came from. It is brought up from
                // the head of the ring it participates in - a component the plan already put there - so
                // no bring-up wire crosses a device and no instance name is spelled here.
                var relocated = ctx.ResourceFor(PlcAssignment.RevPi);
                if (relocated.Capabilities.ReceivesRelocatedComponents && ctx.Profile.PartialRevPi
                    && !string.IsNullOrWhiteSpace(relocated.InitAnchor))
                {
                    var conn = TelemetrySettings.Current.For(PlcAssignment.RevPi);
                    string revpiName = conn.NameFor(tele);
                    InjectMqttConn(revpiName, config.MqttConnectionName, conn.Client,
                        ctx.Layout.Band(PlcAssignment.RevPi).ColumnBaseX, 200);
                    builder.AddEventConnection($"{revpiName}.INITO", $"{revpiName}.CONNECT");
                    builder.AddEventConnection($"{relocated.InitAnchor}.INITO", $"{revpiName}.INIT");
                }
                report.Missing.Add(
                    $"[MQTT] {(tele ? "Telemetry" : "MQTT_CONNECTION")} injected per resource — BX1 " +
                    $"(ClientId SMC_BX1) + Feed:{feedSuffix} ({feedClientId}) + M580 (SMC_M580), shared ConnectionID=" +
                    $"{config.MqttConnectionName} so each resource's embedded MqttPub binds locally; URL={brokerUrl}.");
            }


            RingWiringPlanner.BuildFeedStationWiring(builder, ctx, processesOn[PlcAssignment.M262]);
            RingWiringPlanner.BuildStation2Wiring(builder, ctx, processesOn[PlcAssignment.M580]);
            RingWiringPlanner.BuildBx1Wiring(builder, ctx, processesOn[PlcAssignment.BX1]);

            // Cross-controller process-phase transport, one link per model-derived handoff whose producer and
            // consumer sit on different rings. CrossReference=True tells EAE to auto-generate the UDP proxy;
            // both ends are Process1_Generic. Syslay-only: the sysres leaves these boundary ports OPEN and EAE
            // bridges from here, exactly as it does for the ejector/robot cross-hops.
            // One link per (producer, consumer) pair. The plan has already rejected any fan-in the consumer's
            // single input group cannot carry, so every link here drives one input from one source.
            foreach (var link in handoffPlan.CrossControllerLinks())
            {
                var producerFb = ResolveProcessFbName(link.ProducerName, ctx)
                    ?? throw new InvalidOperationException(
                        $"[Handoff] producer process '{link.ProducerName}' (condition '{link.ConditionName}' on " +
                        $"'{link.ConsumerName}') has no emitted FB, so its phase has no transport.");
                var consumerFb = ResolveProcessFbName(link.ConsumerName, ctx)
                    ?? throw new InvalidOperationException(
                        $"[Handoff] consumer process '{link.ConsumerName}' has no emitted FB to receive " +
                        $"'{link.ProducerName}' phases.");
                builder.AddEventConnection($"{producerFb}.{Process.Recipes.ProcessPhaseTransport.EventOut}",
                    $"{consumerFb}.{Process.Recipes.ProcessPhaseTransport.EventIn}", crossReference: true);
                builder.AddDataConnection($"{producerFb}.{Process.Recipes.ProcessPhaseTransport.DataOut}",
                    $"{consumerFb}.{Process.Recipes.ProcessPhaseTransport.DataIn}", crossReference: true);
            }

            _ = config;

            // Frame widths MUST enclose all this PLC's FBs: EAE's MoveStyle="AnyContained" auto-grows a frame westward around any FB past its right edge, swallowing neighbours.
            var geom = ctx.Layout.Geometry;
            var style = ctx.Layout.FrameStyle;
            foreach (var band in ctx.Layout.Bands.Where(b => b.Frame != null))
                builder.AddFrame(band.Frame!.Name, band.FrameOriginX, geom.FrameOriginY,
                    band.FrameWidth, geom.FrameHeight, band.Frame.Colour, band.Frame.Caption,
                    style.TextColour, style.TextAlignment, style.Font);

            var doc = builder.Build();
            doc.Save(fullPath);

            // EAE Solution Integrity requires an opcua.xml inside a folder named after the syslay stem.
            EnsureOpcuaXmlBesideArtefact(fullPath);

            // The HMI is derived from the finished layout (FB Id -> TagName, FB Type -> faceplate).
            CodeGen.Hmi.HmiGenerator.Emit(fullPath, ctx);

            // Telemetry sidecar: lets a subscriber render the published phase ordinal as the twin's own
            // state name. Written outside the solution and read by nothing in the generated project.
            if (config != null && config.MqttPublishEnabled && phaseNames.Count > 0)
            {
                var mapPath = ProcessPhaseMapEmitter.Emit(
                    config, phaseNames, CodeGen.Services.MapperLogger.Info);
                if (mapPath != null)
                    CodeGen.Services.MapperLogger.Info($"[Telemetry] phase-name map -> {mapPath}");
            }

            return fullPath;
        }

        // Default fallback timing used only when Control.xml omits or zeros out State.Time.
        private static int DefaultMotionMs => GenerationConfig.Current.DefaultMotionMs;

        // Minimal params (actuator_name + actuator_id) for actuators that are NOT plain 5-state cylinders.
        // Rules the plan decided for this actuator; a CAT with no rule interface has none.
        private static InterlockPlan Interlock(GenerationContext ctx, VueOneComponent actuator) =>
            ctx.Interlocks.TryGetValue((actuator.Name ?? string.Empty).Trim(), out var p)
                ? p : InterlockPlan.Empty;

        // Placeholder placement; CanonicalLayout rewrites rostered names to their canvas coordinate post-syslay.
        private static (int X, int Y) PlcZonePosition(
            LayoutCatalog layout, PlcAssignment plc, int colIndexInPlc, LayoutRow row) =>
            (layout.Band(plc).ColumnBaseX + colIndexInPlc * layout.Geometry.ColumnPitchX,
             layout.RowY(row.ToString()));


        // Formats what the plan decided. Which groups a CAT carries is DECLARED by the CAT; the
        // values are the plan's. Nothing here inspects the twin or chooses a policy.
        public static Dictionary<string, string> BuildActuatorParameters(
            VueOneComponent actuator, int assignedId, string fbType, GenerationContext ctx)
        {
            var type = TemplateManifest.Find(fbType);
            var protocol = TemplateManifest.ProtocolOrNull(fbType);
            var p = new Dictionary<string, string>
            {
                ["actuator_name"] = Iec61499Literal.FormatString(TemplateMap.RingKey(actuator.Name)),
                ["actuator_id"]   = Iec61499Literal.FormatInt(assignedId),
            };

            if (type is { SensorTimed: true })
            {
                var t = ctx.ActuatorTiming[actuator.Name.Trim()];
                p["WorkSensorFitted"] = Iec61499Literal.FormatBool(t.WorkSensorFitted);
                p["HomeSensorFitted"] = Iec61499Literal.FormatBool(t.HomeSensorFitted);
                p["toWorkTime"] = Iec61499Literal.FormatTimeMs(t.ToWorkMs);
                p["toHomeTime"] = Iec61499Literal.FormatTimeMs(t.ToHomeMs);
                p["faultTimeoutWork"] = Iec61499Literal.FormatTimeMs(t.FaultWorkMs);
                p["faultTimeoutHome"] = Iec61499Literal.FormatTimeMs(t.FaultHomeMs);
                p["enableToWorkFaultTimeout"] = Iec61499Literal.FormatBool(t.WorkSensorFitted);
                p["enableToHomeFaultTimeout"] = Iec61499Literal.FormatBool(t.HomeSensorFitted);
            }

            TargetEmitter.Apply(p, protocol);

            // A CAT with a work stop either side of a centre reference has a watchdog per side, on
            // the duration the CAT itself declares for a crossing.
            if (protocol?.CrossesBothWays == true)
            {
                p["enableToWork1FaultTimeout"] = Iec61499Literal.FormatBool(false);
                p["enableToWork2FaultTimeout"] = Iec61499Literal.FormatBool(false);
                p["faultTimeoutWork1"] = Iec61499Literal.FormatTimeMs(protocol.CrossingFaultTimeoutMs);
                p["faultTimeoutWork2"] = Iec61499Literal.FormatTimeMs(protocol.CrossingFaultTimeoutMs);
            }
            if (protocol?.Target is { Count: > 0 }) InterlockEmitter.Write(p, Interlock(ctx, actuator), ctx.InterlockCapacity);
            return p;
        }

        public static int ResolveStateTimeMs(VueOneComponent actuator, int stateNumber, int fallbackMs)
        {
            var s = actuator.States.FirstOrDefault(st => st.StateNumber == stateNumber);
            if (s == null || s.Time <= 0) return fallbackMs;
            return s.Time;
        }

        // atWork = the static state at the far end of motion (StateNumber=2).
        public static HashSet<string> ResolveAtWorkStateIds(VueOneComponent actuator)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in actuator.States.Where(st => st.StateNumber == 2 && st.StaticState))
                ids.Add(s.StateID);
            return ids;
        }

        // atHome = static states at StateNumber=0 (Initial) and =4 (post-cycle latch).
        public static HashSet<string> ResolveAtHomeStateIds(VueOneComponent actuator)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in actuator.States.Where(st =>
                (st.StateNumber == 0 || st.StateNumber == 4) && st.StaticState))
                ids.Add(s.StateID);
            return ids;
        }

        // SAFETY, for EVERY process: an actuator this recipe drives to work must be driven home by some
        // process in the model. Advancing and returning need not be the same one - a transfer that carries
        // a part across stations is advanced upstream and returned downstream, and holding it in between
        // is the point - so stranded means NO process commands it home.
        private static void AssertNothingStranded(GenerationContext ctx, RecipeArrays recipe)
        {
            var advanced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                if (recipe.StepType[i] != Process.StepType.Cmd) continue;
                var target = (recipe.CmdTargetName[i] ?? string.Empty).Trim();
                if (target.Length == 0) continue;
                // A process announcing its own phase is not an actuator move.
                if (string.Equals(target, Process.Recipes.ProcessPhaseTransport.CommandToken,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (ctx.Twin.ByName(target) is { IsProcess: true }) continue;
                // A CAT that declares no command protocol folds its whole move into one handshake, so it
                // has no advance to pair with a return. Asked of the DECLARATION, never of the name.
                if (!ctx.CatTypes.TryGetValue(target, out var cat) ||
                    TemplateManifest.ProtocolOrNull(cat)?.Command is not { Count: > 0 }) continue;
                if (recipe.CmdStateArr[i] == 1) advanced.Add(target);
                else if (recipe.CmdStateArr[i] == 3) returned.Add(target);
            }
            var stranded = advanced
                .Where(a => !returned.Contains(a) &&
                            Process.Recipes.ProcessCompiler
                                .ProcessesCommandingHome(a, ctx.Twin, ctx.RecipeInputs).Count == 0)
                .OrderBy(a => a, StringComparer.Ordinal).ToList();
            if (stranded.Count > 0)
                throw new InvalidOperationException(
                    $"[Recipe] Actuator '{stranded[0]}' has no return-to-home cmd step" +
                    (stranded.Count > 1
                        ? $" ({stranded.Count} affected: {string.Join(", ", stranded)})"
                        : string.Empty) +
                    " - refusing to generate code that strands an actuator at work. " +
                    "(no process state in the model owns a movement that drives it home.)");
        }

        private static string? ResolveProcessFbName(string processName, GenerationContext ctx) =>
            ctx.Twin.ByName(processName) is { IsProcess: true } resolved
                ? ctx.InstanceName(resolved.Name)
                : null;
        

        public static (Dictionary<string, string> Outer, RecipeArrays? Recipe)
            BuildProcessFbParameters(GenerationContext ctx, VueOneComponent process,
                string processName, int processId, bool withRecipe)
        {
            // Recipe arrays travel as Process1_Generic Parameter values; withRecipe=false emits only the
            // two scalars and returns a null Recipe.
            var config = ctx.Config;
            bool emitProcessTelemetry = config.MqttPublishEnabled;
            int? receiverSlot = ctx.Handoffs.ReceiverSlotOf(process.Name);
            var outer = new Dictionary<string, string>
            {
                ["process_name"] = Iec61499Literal.FormatString(processName),
                ["process_id"] = Iec61499Literal.FormatInt(processId)
            };
            // Where THIS instance receives a transported process phase: its producer's own allocated
            // process id, so the recipe's WAIT on that producer reads the slot the phase lands in. Absent
            // when the plan gives this process no cross-controller producer, so a consumer that receives
            // nothing carries no slot rather than a misleading default.
            if (receiverSlot is int slot)
                outer[Process.Recipes.ProcessPhaseTransport.ReceiverSlotParam] = Iec61499Literal.FormatInt(slot);


            RecipeArrays? recipe = null;
            if (withRecipe)
            {
                recipe = ctx.Recipes[process.Name?.Trim() ?? string.Empty];
                                    // One Recipe struct array; the deployer normalizers reshape the FBType to match (else ERR_MEMBER_VAR_NOTFOUND).
                    outer["Recipe"] = Iec61499Literal.FormatRecipeTable(
                        recipe.StepType, recipe.CmdTargetName, recipe.CmdStateArr,
                        recipe.Wait1Id, recipe.Wait1State, recipe.NextStep,
                        recipe.AltCount, recipe.TermCount);
                
                // Telemetry-only companion to the control arrays: row -> VueOne State_Number. Emitted
                // only when MQTT publishing is on, so a telemetry-off tree carries no stale parameter.
                if (emitProcessTelemetry)
                    outer["ProcessStateByRow"] = Iec61499Literal.FormatIntArray(recipe.ProcessStateByRow);
            }

            return (outer, recipe);
        }

        // Renders the planned infrastructure of every declared resource, in declaration order. A
        // resource that declares no role for a slot simply contributes nothing.
        private static void EmitInfrastructure(SyslayBuilder builder, GenerationContext ctx,
            Func<string, bool> wanted)
        {
            foreach (var resource in ctx.Layout.Resources)
            foreach (var fb in ctx.ResourceFor(resource.Plc).Infrastructure)
            {
                if (!wanted(fb.Role)) continue;
                builder.AddFB(FBIdGenerator.GenerateFBId(fb.Name),
                    fb.Name, fb.Template.Name, fb.Namespace, fb.X, fb.Y,
                    fb.Parameters.Count == 0 ? null : new Dictionary<string, string>(fb.Parameters));
            }
        }

        private static void ReportStation2Recipe(BindingApplicationReport report,
            string processName, RecipeArrays? recipe, string plcLabel)
        {
            if (recipe == null)
            {
                report.Missing.Add($"[Recipe] {processName}: no recipe built (no station contents).");
                return;
            }
            int cmd = recipe.StepType.Count(t => t == 1);
            int wait = recipe.StepType.Count(t => t == 2);
            report.Missing.Add(
                $"[Recipe] {processName} ({plcLabel}): {recipe.StepType.Count}-row recipe — " +
                $"{cmd} CMD / {wait} WAIT, {recipe.Warnings.Count} generator warning(s).");
            foreach (var w in recipe.Warnings)
                report.Missing.Add($"[Recipe] {processName}: {w}");
        }

        private static void AppendProcessRecipeComment(SyslayBuilder builder,
            string processName, RecipeArrays? recipe)
        {
            if (recipe == null) return;

            if (recipe.TransitionTable.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} Control.xml transition chain used for recipe:\n  - " +
                    string.Join("\n  - ", recipe.TransitionTable));
            }

            if (!string.IsNullOrWhiteSpace(recipe.OrderingSummary))
            {
                builder.AppendTopComment(
                    $" {processName} recipe ordering: " + recipe.OrderingSummary);
            }


            if (recipe.Warnings.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} recipe warning(s):\n  - " +
                    string.Join("\n  - ", recipe.Warnings));
            }
        }


        // opcua.xml stub in a folder named after the artefact stem, so EAE's Solution Integrity check passes.
        public static void EnsureOpcuaXmlBesideArtefact(string artefactPath)
            => CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(artefactPath);
    }
}
