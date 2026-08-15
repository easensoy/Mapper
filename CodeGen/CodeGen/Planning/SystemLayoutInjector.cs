using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Interlocks;
using CodeGen.Translation.Process;

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


        public string GenerateFeedStationSyslayToPath(GenerationContext ctx, string targetSyslayPath,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            report = new BindingApplicationReport();
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (string.IsNullOrEmpty(targetSyslayPath))
                throw new ArgumentException("Target syslay path is required.", nameof(targetSyslayPath));

            var config = ctx.Config;
            var handoffPlan = ctx.Handoffs;

            var contents = ctx.Station;

            var fileName = Path.GetFileName(targetSyslayPath);
            var fullPath = targetSyslayPath;

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
            int processId = ctx.Slots[contents.Process.Name];




            // No top-level PLC_Start FB: Area_CAT/Station_CAT each hold their own plcStart; an external one double-bootstraps (EAE rejects).

            // Each resource's area/station stack, as layout.yml names it and the manifest types it.
            EmitInfrastructure(builder, ctx, role => role != "terminator" && role != "areaTerminator");

            // Instance name: Instance_Name_Overrides sheet, else suffix-stripping convention, else "Process1".
            var overrides = (config != null && !string.IsNullOrWhiteSpace(config.MappingRulesPath))
                ? InstanceNameOverridesLoader.Load(config.MappingRulesPath)
                : new InstanceNameOverridesLoader.Overrides();

            var processInstanceName = InstanceNameResolver.Resolve(contents.Process,
                overrides.ByComponentId, overrides.ByVueOneName);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            // ordinal -> phase name per process, emitted beside the project so a telemetry subscriber can
            // render the number it receives. Collected as each process is built; written once at the end.
            var phaseNames = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);

            var (processOuter, processNested, processRecipe) = BuildProcessFbParameters(
                ctx, contents.Process, processInstanceName, processId, withRecipe: true);
            if (processRecipe != null) phaseNames[processInstanceName] = processRecipe.ProcessPhaseNames;

            // EAE rejects a Parameter not declared as an InputVar on the FBType (ERR_MEMBER_VAR_NOTFOUND).

            // SAFETY: every actuator with a work command (CmdState=1) must also have a return-to-home (CmdState=3).
            if (processRecipe != null)
            {
                var adv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < processRecipe.StepType.Count; i++)
                {
                    if (processRecipe.StepType[i] != 1) continue;
                    var t = (processRecipe.CmdTargetName[i] ?? string.Empty).Trim();
                    if (t.Length == 0) continue;
                    // A process-announce CMD (a process publishing its own model-state, whether onto its own
                    // process_id slot or across the phase transport) is not an actuator move; the retract
                    // check applies only to real actuators.
                    if (string.Equals(t, Process.Recipes.ProcessPhaseTransport.CommandToken, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ctx.Twin.ByName(t) is { IsProcess: true }) continue;
                    if (processRecipe.CmdStateArr[i] == 1) adv.Add(t);
                    else if (processRecipe.CmdStateArr[i] == 3) ret.Add(t);
                }
                // Advancing and returning need not be the same process. Command ownership is declared on the
                // actuator's own transitions, so a transfer that carries a part across stations is legitimately
                // advanced by the upstream process and returned by the downstream one -- holding the part in
                // between is the point. Stranded means NO process in the model commands it home.
                var strandedAct = adv
                    .Where(a => !ret.Contains(a) &&
                                CodeGen.Translation.Process.Recipes.ProcessCompiler
                                    .ProcessesCommandingHome(a, ctx.Twin).Count == 0)
                    .ToList();
                if (strandedAct.Count > 0)
                    throw new InvalidOperationException(
                        $"[Recipe] Actuator '{strandedAct[0]}' has no return-to-home cmd step" +
                        (strandedAct.Count > 1
                            ? $" ({strandedAct.Count} affected: {string.Join(", ", strandedAct)})"
                            : string.Empty) +
                        " — refusing to generate code that strands an actuator at work. " +
                        "(no process state in the model owns a movement that drives it home.)");
            }

            builder.AddFB(FBIdGenerator.GenerateFBId(contents.Process.ComponentID),
                processInstanceName, "Process1_Generic", "Main", 3360, 1460,
                processOuter, processNested);

            // Station 2's Process FBs are the ones the roster allocates to M580, in roster order. They
            // reuse the SAME global sensors-first `contents`, so every Wait1Id matches the global FB id.
            var crossProcInstances = new List<string> { processInstanceName };
            var station2ProcFbs = new List<string>();
            foreach (var proc in ctx.ProcessesOn(PlcAssignment.M580))
            {
                var procName = InstanceNameResolver.Resolve(proc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var (pOuter, pNested, pRecipe) = BuildProcessFbParameters(
                    ctx, proc, procName, ctx.Slots[proc.Name], withRecipe: true);
                builder.AddFB(FBIdGenerator.GenerateFBId(proc.ComponentID),
                    procName, "Process1_Generic", "Main", 0, 0, pOuter, pNested);
                crossProcInstances.Add(procName);
                station2ProcFbs.Add(procName);
                if (pRecipe != null) phaseNames[procName] = pRecipe.ProcessPhaseNames;
                ReportStation2Recipe(report, procName, pRecipe, "M580");
                AppendProcessRecipeComment(builder, procName, pRecipe);
            }
            if (station2ProcFbs.Count == 0)
                report.Missing.Add(
                    "[Recipe] the roster allocates no Process to M580 — " +
                    "Station 2 will have actuators but no Process FB.");

            if (processRecipe != null && processRecipe.SkippedConditions.Count > 0)
            {
                var prefix = $" Recipe scope: {processRecipe.SkippedConditions.Count} " +
                             "Control.xml condition(s) were dropped because they reference " +
                             "components not present in this syslay (Button 2 filters to " +
                             $"Feeder + Checker + PartInHopper + PartAtChecker). Skipped:\n  - " +
                             string.Join("\n  - ", processRecipe.SkippedConditions);
                builder.AppendTopComment(prefix);
                foreach (var skip in processRecipe.SkippedConditions)
                    report.Missing.Add($"recipe: {skip}");
            }

            if (processRecipe != null &&
                !string.IsNullOrWhiteSpace(processRecipe.OrderingSummary))
            {
                builder.AppendTopComment(
                    " Recipe step ordering (serialised — collision-safe; each actuator " +
                    "returns home before any subsequent actuator advances; auto-retract " +
                    "is nested in place, not batched at the end): " +
                    processRecipe.OrderingSummary);
                report.Missing.Add($"recipe ordering: {processRecipe.OrderingSummary}");
            }

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
                var displayName = InstanceNameResolver.Resolve(actuator,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var actPlc = plcIndex.ResolveComponent(actuator.Name, bindings, ctx.Allocation);

                Dictionary<string, string> actParams;
                if (fbType == "Five_State_Actuator_CAT")
                {
                    actParams = BuildActuatorParameters(actuator, assignedId, ctx, scopedIds);
                    // actuator_name IS the ring key this FB answers to; TemplateMap.RingKey is the one
                    // function that also spells the recipe's CmdTargetName, so the two cannot drift.
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        TemplateMap.RingKey(displayName));

                    InterlockEmitter.GuardFiveState(actParams, actuator, ctx, scopedIds, report.Bound);
                }
                else if (string.Equals(fbType, TemplateMap.SevenStateCentreHomeCat, StringComparison.Ordinal))
                {
                    actParams = BuildMinimalActuatorParameters(actuator, assignedId, fbType);
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        TemplateMap.RingKey(displayName));
                    InterlockEmitter.ApplyCentreHome(actParams, actuator, ctx, scopedIds);
                    InterlockEmitter.GuardCentreHome(actParams, actuator, ctx, scopedIds, report.Bound);
                }
                else
                {
                    actParams = BuildMinimalActuatorParameters(actuator, assignedId, fbType);
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        TemplateMap.RingKey(displayName));
                    report.Missing.Add(
                        $"[Phase 6] {actuator.Name} ({fbType}): minimal params only — " +
                        "data-driven Target*/Rule*/Interlock* wiring deferred to recipe phase");
                }

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

                var senDisplayName = InstanceNameResolver.Resolve(sensor,
                    overrides.ByComponentId, overrides.ByVueOneName);

                builder.AddFB(FBIdGenerator.GenerateFBId(sensor.ComponentID),
                    senDisplayName, "Sensor_Bool_CAT", "Main",
                    sX, sY,
                    new Dictionary<string, string>
                    {
                        ["name"] = SyslayBuilder.FormatString(senDisplayName),
                        ["id"] = SyslayBuilder.FormatInt(assignedId),
                    });
            }

            // Synthesized M262 sensors: EXPLICIT ids so they never shift Feed actuator ids; off every report ring, so the Feed ring stays byte-identical.
            if (HandoffPlanner.DischargeActive)
            {
                int synthY = 5200;
                string prevSynthInit = contents.Sensors
                    .Select(s => (s.Name ?? string.Empty).Trim())
                    .First(n => ctx.Allocation.IsFeedSide(n));
                foreach (var (synthName, _, synthId) in MapperConfig.M262SynthSensors)
                {
                    // The twin owns it if it declares it; synthesizing a second FB of the same name
                    // would put two components on one ring slot.
                    if (contents.Sensors.Any(s => string.Equals(s.Name, synthName, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    builder.AddFB(FBIdGenerator.GenerateFBId("m262rigsensor-" + synthName),
                        synthName, "Sensor_Bool_CAT", "Main", 2000, synthY,
                        new Dictionary<string, string>
                        {
                            ["name"] = SyslayBuilder.FormatString(synthName),
                            ["id"] = SyslayBuilder.FormatInt(synthId),
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
                        var cfgLit = SyslayBuilder.FormatTelemetryConfig(
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
                        ["QI"] = SyslayBuilder.FormatBool(true),
                        ["ConnectionID"] = SyslayBuilder.FormatString(connectionId),
                        ["URL"] = SyslayBuilder.FormatString(brokerUrl),
                        ["ClientIdentifier"] = SyslayBuilder.FormatString(clientIdentifier),
                    };
                    if (config.MqttSecureTls)
                    {
                        p["ValidateCert"] = config.MqttValidateCert.ToString();
                        if (!string.IsNullOrWhiteSpace(config.MqttCaCert))
                            p["CACert"] = SyslayBuilder.FormatString(config.MqttCaCert);
                    }
                    builder.AddFB(FBIdGenerator.GenerateFBId(fbName), fbName,
                        "MQTT_CONNECTION", "Runtime.NetConnectivity", x, y, p);
                }

                bool tele = config.UseTelemetryCat;
                // The Feed controller's connection follows the Feed station onto M262 or RevPi (FB name +
                // ClientIdentifier). ConnectionID stays the shared 'SMC' so the embedded MqttPub topic/
                // payload is byte-for-byte unchanged. Byte-identical for M262 (suffix M262, ClientM262).
                const string feedSuffix = "M262";
                string feedClientId = config.MqttClientM262;
                string bx1Name  = tele ? "Telemetry_BX1"  : "MqttConn";
                string feedName = tele ? $"Telemetry_{feedSuffix}" : $"MqttConn_{feedSuffix}";
                string m580Name = tele ? "Telemetry_M580" : "MqttConn_M580";

                var mqttEntry = ctx.Roster.Get(bx1Name);
                int bx1X = mqttEntry?.X ?? 29000;
                int bx1Y = mqttEntry?.Y ?? 200;
                // Each conn is routed to its own sysres via SysresFbMirror.BucketFor; BX1 bring-up is in BuildBx1Wiring, Feed/M580 below.
                InjectMqttConn(bx1Name, config.MqttConnectionName, config.MqttClientId, bx1X, bx1Y);
                InjectMqttConn(feedName, config.MqttConnectionName, feedClientId,
                    ctx.Layout.Band(PlcAssignment.M262).ColumnBaseX, 200);
                InjectMqttConn(m580Name, config.MqttConnectionName, config.MqttClientM580,
                    ctx.Layout.Band(PlcAssignment.M580).ColumnBaseX, 200);
                builder.AddEventConnection($"{feedName}.INITO", $"{feedName}.CONNECT");
                builder.AddEventConnection($"{m580Name}.INITO", $"{m580Name}.CONNECT");
                // Each connection is brought up by its own resource's infrastructure, whatever that
                // resource calls it.
                builder.AddEventConnection(
                    $"{ctx.ResourceFor(PlcAssignment.M262).AreaFb}.INITO", $"{feedName}.INIT");
                builder.AddEventConnection(
                    $"{ctx.ResourceFor(PlcAssignment.M580).StationFb}.INITO", $"{m580Name}.INIT");
                // Partial swap: the RevPi ALSO hosts Feeder/Checker (embedded MqttPub bind ConnectionID
                // 'SMC'), so it needs its OWN local connection alongside the M262 Feed connection — else
                // those publishers have no active connection. INIT off a RevPi-local component (PartInHopper)
                // so there is no cross-device INIT wire. Full swap already puts the one Feed conn on RevPi.
                if (ctx.Profile.PartialRevPi)
                {
                    string revpiName = tele ? "Telemetry_RevPi" : "MqttConn_RevPi";
                    InjectMqttConn(revpiName, config.MqttConnectionName, config.MqttClientRevPi,
                        ctx.Layout.Band(PlcAssignment.RevPi).ColumnBaseX, 200);
                    builder.AddEventConnection($"{revpiName}.INITO", $"{revpiName}.CONNECT");
                    builder.AddEventConnection("PartInHopper.INITO", $"{revpiName}.INIT");
                }
                report.Missing.Add(
                    $"[MQTT] {(tele ? "Telemetry" : "MQTT_CONNECTION")} injected per resource — BX1 " +
                    $"(ClientId SMC_BX1) + Feed:{feedSuffix} ({feedClientId}) + M580 (SMC_M580), shared ConnectionID=" +
                    $"{config.MqttConnectionName} so each resource's embedded MqttPub binds locally; URL={brokerUrl}.");
            }


            RingWiringPlanner.BuildFeedStationWiring(builder, ctx);
            RingWiringPlanner.BuildStation2Wiring(builder, ctx, station2ProcFbs);
            RingWiringPlanner.BuildBx1Wiring(builder, ctx);

            // Cross-controller process-phase transport, one link per model-derived handoff whose producer and
            // consumer sit on different rings. CrossReference=True tells EAE to auto-generate the UDP proxy;
            // both ends are Process1_Generic. Syslay-only: the sysres leaves these boundary ports OPEN and EAE
            // bridges from here, exactly as it does for the ejector/robot cross-hops.
            // One link per (producer, consumer) pair. The plan has already rejected any fan-in the consumer's
            // single input group cannot carry, so every link here drives one input from one source.
            foreach (var link in handoffPlan.CrossControllerLinks())
            {
                var producerFb = ResolveProcessFbName(link.ProducerName, ctx.Twin, overrides, contents.Process, processInstanceName)
                    ?? throw new InvalidOperationException(
                        $"[Handoff] producer process '{link.ProducerName}' (condition '{link.ConditionName}' on " +
                        $"'{link.ConsumerName}') has no emitted FB, so its phase has no transport.");
                var consumerFb = ResolveProcessFbName(link.ConsumerName, ctx.Twin, overrides, contents.Process, processInstanceName)
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
            builder.AddFrame("FRAME_Station1",
                ctx.Layout.Band(PlcAssignment.M262).FrameOriginX, geom.FrameOriginY,
                ctx.Layout.Band(PlcAssignment.M262).FrameWidth, geom.FrameHeight,
                "LightYellow", "Station 1   —   PLC M262", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            builder.AddFrame("FRAME_Station2_M580",
                ctx.Layout.Band(PlcAssignment.M580).FrameOriginX, geom.FrameOriginY,
                ctx.Layout.Band(PlcAssignment.M580).FrameWidth, geom.FrameHeight,
                "MediumPurple", "Station 2   —   PLC M580", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            // BX1 is the Soft dPAC host (Cover P&P) — NOT Station 2 (which is the M580 frame above).
            builder.AddFrame("FRAME_BX1",
                ctx.Layout.Band(PlcAssignment.BX1).FrameOriginX, geom.FrameOriginY,
                ctx.Layout.Band(PlcAssignment.BX1).FrameWidth, geom.FrameHeight,
                "LightGreen", "Soft dPAC   —   PLC BX1", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");

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
        private static Dictionary<string, string> BuildMinimalActuatorParameters(
            VueOneComponent actuator, int assignedId, string fbType)
        {
            var dict = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString(TemplateMap.RingKey(actuator.Name)),
                ["actuator_id"]   = SyslayBuilder.FormatInt(assignedId),
            };
            // Seven_State Target Pick/Place/Home = 1/2/0 stay in lock-step with the recipe CMD state.
            if (string.Equals(fbType, "Seven_State_Actuator_CAT", StringComparison.OrdinalIgnoreCase))
            {
                dict["TargetPickState"]  = SyslayBuilder.FormatInt(1);
                dict["TargetPlaceState"] = SyslayBuilder.FormatInt(2);
                dict["TargetHomeState"]  = SyslayBuilder.FormatInt(0);
                // SevenStateActuator2's ECC gates every commanded transition on process_state_name = actuator_name; the ring never delivers it, so statically param it or the swivel stalls.
                dict["process_state_name"] = SyslayBuilder.FormatString(TemplateMap.RingKey(actuator.Name));
            }
            // Centre-home swivel settles at current_state_to_process 2=Work1 / 4=Work2 / 6=Home; Target*State feed the interlock manager at those values.
            if (string.Equals(fbType, TemplateMap.SevenStateCentreHomeCat, StringComparison.OrdinalIgnoreCase))
            {
                TargetEmitter.Apply(dict, work1: 2, work2: 4, home: 6);
                dict["enableToWork1FaultTimeout"] = SyslayBuilder.FormatBool(false);
                dict["enableToWork2FaultTimeout"] = SyslayBuilder.FormatBool(false);
                dict["faultTimeoutWork1"] = SyslayBuilder.FormatTimeMs(10000);
                dict["faultTimeoutWork2"] = SyslayBuilder.FormatTimeMs(10000);
                // Zeroed rule defaults; the real Bearing_PnP path overlays them via ApplyCentreHome.
                InterlockEmitter.ApplyZero(dict);
            }
            return dict;
        }

        // Placeholder placement; CanonicalLayout rewrites rostered names to their canvas coordinate post-syslay.
        private static (int X, int Y) PlcZonePosition(
            LayoutCatalog layout, PlcAssignment plc, int colIndexInPlc, LayoutRow row) =>
            (layout.Band(plc).ColumnBaseX + colIndexInPlc * layout.Geometry.ColumnPitchX,
             layout.RowY(row.ToString()));


        public static Dictionary<string, string> BuildActuatorParameters(
            VueOneComponent actuator, int assignedId,
            GenerationContext ctx,
            IReadOnlyDictionary<string, int>? scopedIds = null)
        {
            int toWorkMs = ResolveStateTimeMs(actuator, stateNumber: 1, fallbackMs: DefaultMotionMs);
            int toHomeMs = ResolveStateTimeMs(actuator, stateNumber: 3, fallbackMs: DefaultMotionMs);

            var atWorkIds = ResolveAtWorkStateIds(actuator);
            var atHomeIds = ResolveAtHomeStateIds(actuator);
            bool workSensorFitted = AnyComponentReferencesStates(ctx.Twin, actuator, atWorkIds);
            bool homeSensorFitted = AnyComponentReferencesStates(ctx.Twin, actuator, atHomeIds);

            // Cover actuators settle in coverMotionMs (Hr/Vr keep real DIs); the gripper has no grip/release DI, so it timer-acknowledges sensorless or the release WAIT stalls.
            if (ctx.IsCoverDetour(actuator.Name))
            {
                toWorkMs = GenerationConfig.Current.CoverMotionMs;
                toHomeMs = GenerationConfig.Current.CoverMotionMs;
                if (string.Equals(actuator.Name, "CoverPnp_Gripper", StringComparison.OrdinalIgnoreCase))
                {
                    workSensorFitted = false;
                    homeSensorFitted = false;
                    int ackMs = GenerationConfig.Current.CoverGripperAckMs;
                    if (ackMs > 0) { toWorkMs = ackMs; toHomeMs = ackMs; }
                }
            }

            // M262 Ejector is open-loop (only the DO03 coil, no DIs), so force sensorless or a sensored WAIT stalls forever.
            if (HandoffPlanner.DischargeActive
                && string.Equals(actuator.Name, "Ejector", StringComparison.OrdinalIgnoreCase))
            {
                workSensorFitted = false;
                homeSensorFitted = false;
            }

            // Grippers (bearing/shaft) grip a PART: their "atwork" is a grip-detect that only asserts when a
            // part is actually held, NOT a position DI that always toggles on arrival (why feeder/transfer,
            // both WorkSensorFitted=TRUE, confirm fine while the gripper stalls at WAIT gripper=AtWork). So
            // timer-acknowledge the close (a fast, bounded motion) -- the same reason CoverPnp_Gripper is
            // already sensorless. Position actuators (shaft_hr/vr) keep their real sensors. Scoped to the
            // no-clamp (_vc) path so the clamp/M262 output stays byte-identical.
            if (ctx.RingsMerged
                && actuator.Name.IndexOf("Gripper", StringComparison.OrdinalIgnoreCase) >= 0
                && !ctx.IsCoverDetour(actuator.Name))
            {
                workSensorFitted = false;
                homeSensorFitted = false;
            }

            // M580 shaft actuators keep real work sensors (timer-ack could release before the physical motion completes).

            var actuatorParams = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString(TemplateMap.RingKey(actuator.Name)),
                ["actuator_id"] = SyslayBuilder.FormatInt(assignedId),
                ["WorkSensorFitted"] = SyslayBuilder.FormatBool(workSensorFitted),
                ["HomeSensorFitted"] = SyslayBuilder.FormatBool(homeSensorFitted),
                ["toWorkTime"] = SyslayBuilder.FormatTimeMs(toWorkMs),
                ["toHomeTime"] = SyslayBuilder.FormatTimeMs(toHomeMs),
                ["faultTimeoutWork"] = SyslayBuilder.FormatTimeMs(toWorkMs * 2),
                ["faultTimeoutHome"] = SyslayBuilder.FormatTimeMs(toHomeMs * 2),
                ["enableToWorkFaultTimeout"] = SyslayBuilder.FormatBool(workSensorFitted),
                ["enableToHomeFaultTimeout"] = SyslayBuilder.FormatBool(homeSensorFitted),
            };

            // Target states feeding the embedded InterlockManager (Work1=atwork 2, Home=athome 4;
            // Five_State has no Work2).
            TargetEmitter.Apply(actuatorParams, work1: 2, work2: null, home: 4);

            InterlockEmitter.ApplyFiveState(actuatorParams, actuator, ctx, scopedIds);

            return actuatorParams;
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

        // Does any OTHER component's transition guard observe one of these states? That is what makes the
        // actuator's position sensed rather than timed.
        public static bool AnyComponentReferencesStates(
            CodeGen.Domain.Twin.TwinModel twin,
            VueOneComponent actuator,
            HashSet<string> stateIds) =>
            stateIds.Count > 0 && twin.Components
                .Where(c => !string.Equals(c.Id, actuator.ComponentID, StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => c.States).SelectMany(s => s.Transitions).SelectMany(t => t.Conditions)
                .Any(r => stateIds.Contains(r.State.Id));

        public static string StateRprtOut(string fbType)
        {
            return string.Equals(fbType, "Process1_Generic", StringComparison.Ordinal)
                ? "stateRptCmdAdptr_out"
                : "stateRprtCmd_out";
        }

        public static string StateRprtIn(string fbType)
        {
            return string.Equals(fbType, "Process1_Generic", StringComparison.Ordinal)
                ? "stateRptCmdAdptr_in"
                : "stateRprtCmd_in";
        }

        public static string StationAdptrOut(string fbType) => "stationAdptr_out";
        public static string StationAdptrIn(string fbType) => "stationAdptr_in";

        private static readonly HashSet<string> UniversalCatTypes = new(StringComparer.Ordinal)
        {
            "Five_State_Actuator_CAT", "Sensor_Bool_CAT", "Process1_Generic",
            "Station_CAT", "Area_CAT", "CaSAdptrTerminator", "Station", "Area"
        };

        // Stripped on cleanup: PLC_RW_M262 is re-emitted every run, so a stale instance would double-declare it on the sysres.
        private static readonly HashSet<string> LegacyIoBridgeTypes = new(StringComparer.Ordinal)
        {
            "PLC_RW_M262"
        };

        public class CleanupReport
        {
            public List<string> RemovedFbs { get; } = new();
            public List<string> PreservedFbs { get; } = new();
            public int RemovedConnections { get; set; }
            public List<string> DeviceCleanupLog { get; } = new();
        }

        public CleanupReport PrepareDemonstratorForGeneration(MapperConfig config)
        {
            var report = new CleanupReport();

            // Recreate the app shell (create-if-absent) BEFORE the SyslayPath2 check below.
            CodeGen.Devices.Core.ApplicationShellEmitter.EnsureApplicationShell(
                config, DeriveDemonstratorEaeRoot(config),
                line => report.DeviceCleanupLog.Add(line));

            if (string.IsNullOrEmpty(config.SyslayPath2) || !File.Exists(config.SyslayPath2))
                throw new FileNotFoundException(
                    $"Demonstrator syslay not configured or missing: '{config.SyslayPath2}'");

            CleanFile(config.SyslayPath2, "SubAppNetwork", report);

            // EAE renames the .sysres to the short-hex resource ID, so resolve the actual file by globbing the sysdev folder.
            foreach (var sysresPath in ResolveActualSysresPaths(config))
                CleanFile(sysresPath, "FBNetwork", report);

            CleanM262SysdevResources(config, report);

            // Orphan .sysres files are swept by EaeProjectLayout.SweepOrphanSysres, which runs once per
            // generation after the devices are emitted -- the point at which a resource id can actually
            // have moved. A second sweep here duplicated the rule and could only ever agree with it.
            SweepBridgeFbsFromAllSysres(config, report);

            return report;
        }

        // Process name (as the twin declares it) -> the FB instance name emitted for it, so handoff wiring can
        // be addressed by model name without the injector knowing which processes exist.
        private static string? ResolveProcessFbName(
            string processName, CodeGen.Domain.Twin.TwinModel twin,
            InstanceNameOverridesLoader.Overrides overrides,
            VueOneComponent? feedProcess, string feedProcessFbName)
        {
            if (twin.ByName(processName) is not { IsProcess: true } resolved) return null;
            var c = resolved.Source;
            if (feedProcess != null && ReferenceEquals(c, feedProcess)) return feedProcessFbName;
            var n = InstanceNameResolver.Resolve(c, overrides.ByComponentId, overrides.ByVueOneName);
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }

        // Remove stale MQTT bridge FBs (MqttFmt_/MqttPub_ names only, never MqttConn) + their connections from every .sysres in place.
        private static void SweepBridgeFbsFromAllSysres(MapperConfig config, CleanupReport report)
        {
            var syslayDir = Path.GetDirectoryName(config.SyslayPath2);
            if (string.IsNullOrEmpty(syslayDir)) return;
            var sysGuidDir = Path.GetDirectoryName(syslayDir);
            if (string.IsNullOrEmpty(sysGuidDir) || !Directory.Exists(sysGuidDir)) return;

            // Guard: only act on a real EAE System folder (one with .sysdev files).
            try { if (!Directory.EnumerateFiles(sysGuidDir, "*.sysdev").Any()) return; }
            catch { return; }

            System.Xml.Linq.XNamespace ns = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;
            bool IsBridge(string? n) =>
                n != null && (n.StartsWith("MqttFmt_", StringComparison.Ordinal)
                           || n.StartsWith("MqttPub_", StringComparison.Ordinal));

            List<string> sysresFiles;
            try { sysresFiles = Directory.EnumerateFiles(sysGuidDir, "*.sysres", SearchOption.AllDirectories).ToList(); }
            catch { return; }

            foreach (var file in sysresFiles)
            {
                System.Xml.Linq.XDocument doc;
                try { doc = System.Xml.Linq.XDocument.Load(file, System.Xml.Linq.LoadOptions.PreserveWhitespace); }
                catch { continue; }
                var net = doc.Root?.Element(ns + "FBNetwork") ?? doc.Root?.Element(ns + "SubAppNetwork");
                if (net == null) continue;

                int removedFb = 0, removedConn = 0;
                foreach (var fb in net.Elements(ns + "FB")
                             .Where(f => IsBridge((string?)f.Attribute("Name"))).ToList())
                { fb.Remove(); removedFb++; }

                foreach (var section in new[] { "EventConnections", "DataConnections" })
                {
                    var sec = net.Element(ns + section);
                    if (sec == null) continue;
                    foreach (var c in sec.Elements(ns + "Connection").Where(c =>
                    {
                        var s = (string?)c.Attribute("Source") ?? "";
                        var d = (string?)c.Attribute("Destination") ?? "";
                        return IsBridge(s.Split('.')[0]) || IsBridge(d.Split('.')[0]);
                    }).ToList())
                    { c.Remove(); removedConn++; }
                }

                if (removedFb > 0 || removedConn > 0)
                {
                    try
                    {
                        doc.Save(file);
                        report.DeviceCleanupLog.Add(
                            $"[CleanDevice] swept {removedFb} stale bridge FB(s) + {removedConn} wire(s) " +
                            $"from {Path.GetFileName(file)}");
                    }
                    catch { /* best-effort */ }
                }
            }
        }

        // Every .sysres that actually exists in the M262 sysdev folder (SysresPath2's directory).
        private static IEnumerable<string> ResolveActualSysresPaths(MapperConfig config)
        {
            if (string.IsNullOrEmpty(config.SysresPath2)) yield break;
            var dir = Path.GetDirectoryName(config.SysresPath2);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
            foreach (var f in Directory.EnumerateFiles(dir, "*.sysres",
                         SearchOption.TopDirectoryOnly))
                yield return f;
        }

        // Dedup <Resource> entries in the M262 sysdev (first survives); each dropped Resource's sibling .sysres is deleted, the .hcf left alone.
        private static void CleanM262SysdevResources(MapperConfig config, CleanupReport report)
        {
            void Log(string line) => report.DeviceCleanupLog.Add($"[CleanDevice] {line}");

            string? eaeRoot = DeriveDemonstratorEaeRoot(config);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                Log("could not derive EAE project root from MapperConfig.SyslayPath2; sysdev dedup skipped");
                return;
            }

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir))
            {
                Log($"IEC61499/System not found under {eaeRoot}; sysdev dedup skipped");
                return;
            }

            string? sysdevPath = null;
            foreach (var candidate in Directory.EnumerateFiles(
                systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(candidate);
                    var root = doc.Root;
                    if (root == null) continue;
                    var type  = (string?)root.Attribute("Type")      ?? string.Empty;
                    var nspac = (string?)root.Attribute("Namespace") ?? string.Empty;
                    if (string.Equals(type,  CodeGen.Mapping.PlcTargets.DeviceType(CodeGen.Translation.PlcAssignment.M262), StringComparison.Ordinal) &&
                        string.Equals(nspac, "SE.DPAC",   StringComparison.Ordinal))
                    {
                        sysdevPath = candidate;
                        break;
                    }
                }
                catch { /* skip malformed; keep scanning */ }
            }
            if (sysdevPath == null)
            {
                Log($"no M262 sysdev found under {systemDir}; nothing to dedupe");
                return;
            }

            Log($"reading sysdev at {sysdevPath}");

            XDocument sysdevDoc;
            try { sysdevDoc = XDocument.Load(sysdevPath); }
            catch (Exception ex)
            {
                Log($"failed to load sysdev {sysdevPath}: {ex.Message}");
                return;
            }
            var sysdevRoot = sysdevDoc.Root;
            if (sysdevRoot == null)
            {
                Log($"sysdev {sysdevPath} has no root element; nothing to dedupe");
                return;
            }

            XNamespace ns = sysdevRoot.GetDefaultNamespace();
            var resourcesEl = sysdevRoot.Element(ns + "Resources");
            var resources = resourcesEl?.Elements(ns + "Resource").ToList()
                ?? new List<XElement>();
            int count = resources.Count;

            Log($"found {count} resources");

            // {sysdev-folder}/{sysdev-stem}/ holds the .sysres + .hcf siblings; we touch .sysres only.
            var sysdevStem = Path.GetFileNameWithoutExtension(sysdevPath);
            var sysdevDir  = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!, sysdevStem);
            int sysresCount = 0;
            if (Directory.Exists(sysdevDir))
                sysresCount = Directory.GetFiles(
                    sysdevDir, "*.sysres", SearchOption.TopDirectoryOnly).Length;

            if (count == 1 && sysresCount == 1)
            {
                Log("M262 sysdev clean, no duplicates");
                return;
            }

            if (count <= 1)
            {
                Log($"M262 sysdev has {count} resource(s), nothing to dedupe");
                return;
            }

            var keep = resources[0];
            var firstResourceId = (string?)keep.Attribute("ID")
                ?? (string?)keep.Attribute("Name")
                ?? "(unknown)";

            int removed = 0;
            for (int i = 1; i < resources.Count; i++)
            {
                var dup = resources[i];
                var dupId   = (string?)dup.Attribute("ID")   ?? string.Empty;
                var dupName = (string?)dup.Attribute("Name") ?? string.Empty;
                var dupIdent = !string.IsNullOrEmpty(dupId) ? dupId : dupName;

                string deletedSysresPath = string.Empty;
                if (!string.IsNullOrEmpty(dupId) && Directory.Exists(sysdevDir))
                {
                    var candidate = Path.Combine(sysdevDir, dupId + ".sysres");
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            File.Delete(candidate);
                            deletedSysresPath = candidate;
                        }
                        catch (Exception ex)
                        {
                            Log($"failed to delete sysres {candidate}: {ex.Message}");
                        }
                    }
                }

                dup.Remove();
                removed++;

                if (deletedSysresPath.Length > 0)
                    Log($"removed duplicate resource {dupIdent}, deleted sysres file {deletedSysresPath}");
                else
                    Log($"removed duplicate resource {dupIdent} (no matching .sysres file on disk)");
            }

            try
            {
                sysdevDoc.Save(sysdevPath);
            }
            catch (Exception ex)
            {
                Log($"failed to save sysdev {sysdevPath} after dedup: {ex.Message}");
                return;
            }

            Log($"removed {removed} duplicate Resource entries, kept {firstResourceId}");
            Log($"kept resource {firstResourceId}");
        }

        // Walks up from config.SyslayPath2 for the folder whose parent contains a .dfbproj.
        private static string? DeriveDemonstratorEaeRoot(MapperConfig config)
        {
            var path = config?.SyslayPath2;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dfbproj").Length > 0)
                    return Path.GetDirectoryName(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static void CleanFile(string path, string netTag, CleanupReport report)
        {
            report.DeviceCleanupLog.Add($"[Clean] file={path} root=<{netTag}>");

            XNamespace ns = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(ns + netTag);
            if (net == null)
            {
                report.DeviceCleanupLog.Add($"[Clean] <{netTag}> not found in {Path.GetFileName(path)} — nothing to clean");
                return;
            }

            var fbsToRemove = new List<XElement>();
            var namesToRemove = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fb in net.Elements(ns + "FB").ToList())
            {
                var fbType = fb.Attribute("Type")?.Value ?? string.Empty;
                var fbName = fb.Attribute("Name")?.Value ?? string.Empty;
                var fbNs = fb.Attribute("Namespace")?.Value ?? string.Empty;

                bool isUniversal = UniversalCatTypes.Contains(fbType) ||
                    LegacyIoBridgeTypes.Contains(fbType) ||
                    (fbType == "plcStart" && fbNs == "SE.AppBase");

                if (isUniversal)
                {
                    fbsToRemove.Add(fb);
                    namesToRemove.Add(fbName);
                    report.RemovedFbs.Add($"{fbName} ({fbType})");
                    report.DeviceCleanupLog.Add($"[Clean]   FB {fbName} type={fbType} -> REMOVE");
                }
                else
                {
                    report.PreservedFbs.Add($"{fbName} ({fbType})");
                    report.DeviceCleanupLog.Add($"[Clean]   FB {fbName} type={fbType} -> PRESERVE");
                }
            }

            foreach (var fb in fbsToRemove) fb.Remove();

            int connRemovedHere = 0;
            foreach (var section in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
            {
                var s = net.Element(ns + section);
                if (s == null) continue;
                foreach (var conn in s.Elements(ns + "Connection").ToList())
                {
                    var src = conn.Attribute("Source")?.Value ?? string.Empty;
                    var dst = conn.Attribute("Destination")?.Value ?? string.Empty;
                    var srcFb = src.Split('.', 2)[0];
                    var dstFb = dst.Split('.', 2)[0];
                    if (namesToRemove.Contains(srcFb) || namesToRemove.Contains(dstFb))
                    {
                        conn.Remove();
                        report.RemovedConnections++;
                        connRemovedHere++;
                    }
                }
            }

            report.DeviceCleanupLog.Add(
                $"[Clean] {Path.GetFileName(path)}: removed {fbsToRemove.Count} FB(s), " +
                $"{connRemovedHere} connection(s)");

            doc.Save(path);
        }


        public static (Dictionary<string, string> Outer,
                       IDictionary<string, IDictionary<string, string>> Nested,
                       RecipeArrays? Recipe)
            BuildProcessFbParameters(GenerationContext ctx, VueOneComponent process,
                string processName, int processId, bool withRecipe)
        {
            // Recipe arrays travel as Process1_Generic Parameter values; withRecipe=false emits only the
            // two scalars and returns a null Recipe.
            var config = ctx.Config;
            bool useRecipeStruct = config.UseRecipeStruct;
            bool emitProcessTelemetry = config.MqttPublishEnabled;
            int? receiverSlot = ctx.Handoffs.ReceiverSlotOf(process.Name);
            var outer = new Dictionary<string, string>
            {
                ["process_name"] = SyslayBuilder.FormatString(processName),
                ["process_id"] = SyslayBuilder.FormatInt(processId)
            };
            // Where THIS instance receives a transported process phase: its producer's own allocated
            // process id, so the recipe's WAIT on that producer reads the slot the phase lands in. Absent
            // when the plan gives this process no cross-controller producer, so a consumer that receives
            // nothing carries no slot rather than a misleading default.
            if (receiverSlot is int slot)
                outer[Process.Recipes.ProcessPhaseTransport.ReceiverSlotParam] = SyslayBuilder.FormatInt(slot);


            RecipeArrays? recipe = null;
            if (withRecipe)
            {
                recipe = ctx.Recipes[process.Name?.Trim() ?? string.Empty];
                if (useRecipeStruct)
                {
                    // 6 arrays collapse into one Recipe struct; the deployer normalizers reshape the FBType to match under the same flag (else ERR_MEMBER_VAR_NOTFOUND).
                    outer["Recipe"] = SyslayBuilder.FormatRecipeTable(
                        recipe.StepType, recipe.CmdTargetName, recipe.CmdStateArr,
                        recipe.Wait1Id, recipe.Wait1State, recipe.NextStep);
                }
                else
                {
                    outer["StepType"]      = SyslayBuilder.FormatIntArray(recipe.StepType);
                    outer["CmdTargetName"] = SyslayBuilder.FormatStringArray(recipe.CmdTargetName);
                    outer["CmdStateArr"]   = SyslayBuilder.FormatIntArray(recipe.CmdStateArr);
                    outer["Wait1Id"]       = SyslayBuilder.FormatIntArray(recipe.Wait1Id);
                    outer["Wait1State"]    = SyslayBuilder.FormatIntArray(recipe.Wait1State);
                    outer["NextStep"]      = SyslayBuilder.FormatIntArray(recipe.NextStep);
                }
                // Telemetry-only companion to the control arrays: row -> VueOne State_Number. Emitted
                // only when MQTT publishing is on, so a telemetry-off tree carries no stale parameter.
                if (emitProcessTelemetry)
                    outer["ProcessStateByRow"] = SyslayBuilder.FormatIntArray(recipe.ProcessStateByRow);
            }

            var nested = new Dictionary<string, IDictionary<string, string>>(StringComparer.Ordinal);
            return (outer, nested, recipe);
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
                $"{cmd} CMD / {wait} WAIT, {recipe.SkippedConditions.Count} condition(s) dropped, " +
                $"{recipe.Warnings.Count} generator warning(s). Cross-PLC waits resolve on the " +
                "single-ring simulator or once the M580↔BX1 broker bridge is emitted.");
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

            if (recipe.SkippedConditions.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} skipped condition(s):\n  - " +
                    string.Join("\n  - ", recipe.SkippedConditions));
            }

            if (recipe.Warnings.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} recipe warning(s):\n  - " +
                    string.Join("\n  - ", recipe.Warnings));
            }
        }


        public string GenerateStation1TestSyslay(GenerationContext ctx,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            if (string.IsNullOrEmpty(ctx.Config.SyslayPath2))
                throw new InvalidOperationException("MapperConfig.SyslayPath2 is not configured.");
            return GenerateFeedStationSyslayToPath(ctx, ctx.Config.SyslayPath2, bindings, out report);
        }


        // opcua.xml stub in a folder named after the artefact stem, so EAE's Solution Integrity check passes.
        public static void EnsureOpcuaXmlBesideArtefact(string artefactPath)
            => CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(artefactPath);
    }
}
