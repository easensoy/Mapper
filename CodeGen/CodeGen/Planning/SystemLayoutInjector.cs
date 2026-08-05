using System;
﻿using System;
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
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private static XElement EnsureSection(XElement net, string tag)
        {
            var s = net.Elements().FirstOrDefault(e => e.Name.LocalName == tag);
            if (s != null) return s;
            s = new XElement(Ns + tag);
            net.Add(s);
            return s;
        }

        private static void AddConn(XElement section, string src, string dst,
            SystemInjectionResult result)
        {
            bool exists = section.Elements()
                .Where(e => e.Name.LocalName == "Connection")
                .Any(c =>
                    string.Equals(c.Attribute("Source")?.Value, src, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Attribute("Destination")?.Value, dst, StringComparison.OrdinalIgnoreCase));
            if (exists) return;

            section.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", src),
                new XAttribute("Destination", dst)));
            result.InjectedFBs.Add($"  wire: {src} → {dst}");
        }

        private static void SetParam(XElement fb, string name, string value)
        {
            var el = fb.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Parameter" &&
                                     e.Attribute("Name")?.Value == name);
            if (el != null) el.SetAttributeValue("Value", value);
            else fb.Add(new XElement(Ns + "Parameter",
                new XAttribute("Name", name),
                new XAttribute("Value", value)));
        }

        private static List<VueOneComponent> Actuators(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Actuator", StringComparison.OrdinalIgnoreCase) == true
                        && c.States.Count == 5).ToList();

        private static List<VueOneComponent> Sensors(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Sensor", StringComparison.OrdinalIgnoreCase) == true
                        && c.States.Count == 2).ToList();

        private static List<VueOneComponent> Processes(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Process", StringComparison.OrdinalIgnoreCase) == true).ToList();



        public string GeneratePusherTestSyslayToPath(string targetSyslayPath, IoBindings? bindings = null)
        {
            return GeneratePusherTestSyslayToPath(targetSyslayPath, bindings, out _);
        }

        public string GeneratePusherTestSyslayToPath(string targetSyslayPath, IoBindings? bindings,
            out BindingApplicationReport report)
        {
            if (string.IsNullOrEmpty(targetSyslayPath))
                throw new ArgumentException("Target syslay path is required.", nameof(targetSyslayPath));

            report = new BindingApplicationReport();
            var fileName = Path.GetFileName(targetSyslayPath);
            var layerId = FBIdGenerator.GenerateFBId(fileName);
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                "v1 limitations: Pusher test only. Demonstrator was cleaned of universal-architecture instances " +
                "before this generation; restore via 'git checkout' on the Demonstrator repo to revert.");

            var pusherId = FBIdGenerator.GenerateFBId("Pusher_Test_v1");
            var parameters = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString("pusher"),
                ["actuator_id"] = SyslayBuilder.FormatInt(0),
                ["WorkSensorFitted"] = SyslayBuilder.FormatBool(false),
                ["HomeSensorFitted"] = SyslayBuilder.FormatBool(false),
                ["toWorkTime"] = SyslayBuilder.FormatTimeMs(2000),
                ["toHomeTime"] = SyslayBuilder.FormatTimeMs(2000),
                ["enableToWorkFaultTimeout"] = SyslayBuilder.FormatBool(false),
                ["enableToHomeFaultTimeout"] = SyslayBuilder.FormatBool(false),
                ["faultTimeoutWork"] = SyslayBuilder.FormatTimeMs(4000),
                ["faultTimeoutHome"] = SyslayBuilder.FormatTimeMs(4000),
            };

            var pusherBinding = bindings?.Actuators.GetValueOrDefault("Pusher")
                ?? bindings?.Actuators.GetValueOrDefault("Feeder");
            if (pusherBinding != null)
                report.Bound.Add(("Pusher", DescribeBinding(pusherBinding)));
            else
                report.Missing.Add("Pusher");

            builder.AddFB(pusherId, "Pusher", "Five_State_Actuator_CAT", "Main", 1300, 2480, parameters);

            var doc = builder.Build();
            doc.Save(targetSyslayPath);
            return targetSyslayPath;
        }

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


        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath)
        {
            return GenerateFeedStationSyslayToPath(controlXmlPath, targetSyslayPath, null, null, out _);
        }

        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            return GenerateFeedStationSyslayToPath(controlXmlPath, targetSyslayPath, bindings, null, out report);
        }

        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath,
            IoBindings? bindings, MapperConfig? config, out BindingApplicationReport report)
        {
            report = new BindingApplicationReport();
            if (string.IsNullOrEmpty(controlXmlPath))
                throw new ArgumentException("Control.xml path is required.", nameof(controlXmlPath));
            if (!File.Exists(controlXmlPath))
                throw new FileNotFoundException($"Control.xml not found: {controlXmlPath}");
            if (string.IsNullOrEmpty(targetSyslayPath))
                throw new ArgumentException("Target syslay path is required.", nameof(targetSyslayPath));

            var reader = new CodeGen.IO.SystemXmlReader();
            var allComponents = reader.ReadAllComponents(controlXmlPath);

            // Merge the M262 Feed ring into the cross-PLC ring only when a Feed process has a cross-controller gate.
            Configuration.MapperConfig.MergeFeedRing =
                Process.Recipes.FeedRingMerge.Needed(allComponents);

            var process = FindStation1Process(allComponents);
            if (process == null)
                throw new InvalidOperationException(
                    "No Process referencing a 'Feeder' actuator was found in Control.xml.");

            var grouping = new StationGroupingService();
            var fullContents = grouping.GroupStationContents(process, allComponents);

            // Sensors-first ordering here is load-bearing: it drives state_table[] index / FB id (actuator_id) / recipe Wait1Id. Absent components are skipped.
            var allowedActuators = new[]
            {
                "Feeder", "Checker", "Transfer", "Ejector",
                "Bearing_PnP",
                "Bearing_Gripper",
                "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper",
                "Clamp",
                "CoverPNP_Hr", "CoverPNP_Vr",
                "CoverPnp_Gripper",
            };
            if (MapperConfig.EnableRobotTaskTail)
                allowedActuators = allowedActuators.Append("Robot").ToArray();
            var allowedSensors = new[]
            {
                "PartInHopper", "PartAtChecker",
                "BearingSensor", "ShaftSensor",
                // BOTH spellings: the twin's name for this one varies by revision (see TemplateMap.IsTopCoverSensor).
                "TopCoverSenosr", "TopCoverSensor",
                // LAST on purpose: this list's order assigns the positional sensor ids, so appending
                // leaves every existing sensor id untouched. A twin that omits it falls through to the
                // synth injection, so both shapes generate the same ids.
                "PartAtAssembly",
            };
            // Source from full Control.xml (StationGroupingService only populates Feed_Station's conditions); grippers are Type="Robot", so accept both.
            var contents = new StationContents(
                fullContents.Process,
                allowedActuators
                    .Select(n => allComponents.FirstOrDefault(c =>
                        (string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(c.Name, n, StringComparison.Ordinal)))
                    .Where(a => a != null).Select(a => a!).ToList(),
                allowedSensors
                    .Select(n => allComponents.FirstOrDefault(c =>
                        string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Name, n, StringComparison.Ordinal)))
                    .Where(s => s != null).Select(s => s!).ToList());

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

            int sensorIdStart = 0;
            // A twin may declare PartAtAssembly itself rather than leave it to the synth injection below.
            // It then keeps the SAME reserved slot (HandoffPlanner.PartAtAssembly.Id), which sits in the
            // hole TopCoverSenosr's pin leaves behind -- so it must NOT push the actuator range up, or
            // every actuator id (and with it every recipe slot, interlock SourceID and HCF binding) shifts.
            int actuatorIdStart = contents.Sensors.Count
                - contents.Sensors.Count(s => HandoffPlanner.IsPartAtAssembly(s.Name));
            // process_ids stay in [0..19] (state_table ARRAY[20]) and above the component id space (max actuator_id 16), so no Wait1Id collides with one.
            int assemblyProcessId = MapperConfig.AssemblyProcessId;
            int disassemblyProcessId = MapperConfig.DisassemblyProcessId;
            // Feed_Station keeps process_id 10 (== Shaft_Hr); harmless under MergeFeedRing (Feed only WAITs, its CMD states 1/3 ≠ Shaft_Hr targets 0/2).
            int processId = MapperConfig.FeedStationProcessId;

            // TopCoverSenosr's state_table slot -- computed, model-independent (nothing to do with the clamp).
            // Occupy every id held by an ASSEMBLY-ring member (M580/BX1 components, the cross-PLC segment, and --
            // when MergeFeedRing merges the rings -- the Feed components), plus the synth/process ids, then take the
            // highest free component-range slot. The covers are occupied by MarkOcc below at their REAL positional
            // ids (13/14/15 no-clamp, 14/15/16 clamp) -- NOT from a fixed RigCatalog value, which drifts when Clamp is
            // absent (RigCatalog says 14/15/16 but the covers shift down to 13/14/15) and would wrongly reserve 16.
            // Clamp: the Feed ids sit on a SEPARATE ring -> {0,4,5,6} free -> 6 (the rig-proven value). Merged
            // no-clamp: the Feed + M580 + cover ids fill [0..15] but nothing occupies 16 -> 16 (a truly free slot;
            // pinning it to 6 collides with Transfer on the merged ring and deadlocks the cover-place gate).
            var occ = new HashSet<int> { assemblyProcessId, disassemblyProcessId, MapperConfig.RobotActuatorId };
            foreach (var syn in MapperConfig.M262SynthSensors) occ.Add(syn.Id);            // PartAtAssembly (3)
            var cross = RigCatalog.Current.CrossRingSegment;                               // Ejector/Robot/PartAtAssembly
            void MarkOcc(string nm, int id)
            {
                if (CodeGen.Mapping.TemplateMap.IsTopCoverSensor(nm)) return; // this is the slot being placed
                var plc = HcfSymbolIndex.NameBasedPlcGuess(nm);
                if (plc is PlcAssignment.M580 or PlcAssignment.BX1 || cross.Contains(nm) || MapperConfig.MergeFeedRing)
                    occ.Add(id);
            }
            for (int i = 0; i < contents.Sensors.Count; i++)
                MarkOcc(contents.Sensors[i].Name,
                    HandoffPlanner.IsPartAtAssembly(contents.Sensors[i].Name)
                        ? HandoffPlanner.PartAtAssembly.Id      // pinned, same slot the synth would take
                        : sensorIdStart + i);
            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                // The robot task arm is APPENDED to allowedActuators, so its LOCAL positional id here is wrong --
                // globally it takes RobotActuatorId (already reserved in occ above), well outside the cover range.
                // Marking its local slot would falsely reserve a free cover slot (the id-16 no-clamp collision).
                if (CodeGen.Mapping.TemplateMap.IsRobotTaskArm(contents.Actuators[i])) continue;
                MarkOcc(contents.Actuators[i].Name, actuatorIdStart + i);
            }
            for (int slot = 16; slot >= 0; slot--)
                if (!occ.Contains(slot)) { MapperConfig.TopCoverSensorId = slot; break; }



            // No top-level PLC_Start FB: Area_CAT/Station_CAT each hold their own plcStart; an external one double-bootstraps (EAE rejects).

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_HMI"),
                "Area_HMI", "Area_CAT", "Main", 240, 140);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area"),
                "Area", "Area", "Main", 400, 580,
                new Dictionary<string, string>
                {
                    ["AreaName"] = SyslayBuilder.FormatString("Area")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station1"),
                "Station1", "Station", "Main", 2120, 600,
                new Dictionary<string, string>
                {
                    ["StationName"] = SyslayBuilder.FormatString("Station1")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station1_HMI"),
                "Station1_HMI", "Station_CAT", "Main", 2220, 100);

            // Station 2 stack — coordinates here just need to be unique; the post-syslay CanonicalLayout pass rewrites them.
            builder.AddFB(FBIdGenerator.GenerateFBId("Station2"),
                "Station2", "Station", "Main", 12000, 600,
                new Dictionary<string, string>
                {
                    ["StationName"] = SyslayBuilder.FormatString("Station2")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station2_HMI"),
                "Station2_HMI", "Station_CAT", "Main", 12100, 100);

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
                contents.Process, allComponents, processInstanceName, processId, contents,
                useRecipeStruct: config != null && config.UseRecipeStruct,
                    emitProcessTelemetry: config != null && config.MqttPublishEnabled);
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
                    // A process-announce CMD (a process publishing its own model-state onto its process_id slot)
                    // is not an actuator move; the retract check applies only to real actuators.
                    if (allComponents.Any(c => string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase)
                        && string.Equals((c.Name ?? string.Empty).Trim(), t, StringComparison.OrdinalIgnoreCase))) continue;
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
                                    .ProcessesCommandingHome(a, allComponents).Count == 0)
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

            // Station 2 Process FBs reuse the SAME global sensors-first `contents` so every Wait1Id matches the global FB id.
            var assemblyStationProc = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, "Assembly_Station", StringComparison.Ordinal));
            var crossProcInstances = new List<string> { processInstanceName };
            if (assemblyStationProc != null)
            {
                var assemblyName = InstanceNameResolver.Resolve(assemblyStationProc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var (aOuter, aNested, aRecipe) = BuildProcessFbParameters(
                    assemblyStationProc, allComponents, assemblyName, assemblyProcessId,
                    contents: contents,
                    useRecipeStruct: config != null && config.UseRecipeStruct,
                    emitProcessTelemetry: config != null && config.MqttPublishEnabled);
                builder.AddFB(FBIdGenerator.GenerateFBId(assemblyStationProc.ComponentID),
                    assemblyName, "Process1_Generic", "Main", 12200, 1460,
                    aOuter, aNested);
                crossProcInstances.Add(assemblyName);
                if (aRecipe != null) phaseNames[assemblyName] = aRecipe.ProcessPhaseNames;
                ReportStation2Recipe(report, assemblyName, aRecipe, "M580");
                AppendProcessRecipeComment(builder, assemblyName, aRecipe);
            }
            else
            {
                report.Missing.Add(
                    "[Recipe] Assembly_Station Process not found in Control.xml — " +
                    "Station 2 (M580) frame will have actuators but no Process FB.");
            }

            // disassemblyFbName is captured so BuildStation2Wiring threads the SAME FB the sysres does; null → syslay stays Assembly-only.
            string? disassemblyFbName = null;
            var disassyProc = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(c.Name, "Disassembly", StringComparison.Ordinal)
                 || string.Equals(c.Name, "Disassembly_Station", StringComparison.Ordinal)));
            if (disassyProc != null)
            {
                var disassyName = InstanceNameResolver.Resolve(disassyProc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                disassemblyFbName = disassyName;
                var (dOuter, dNested, dRecipe) = BuildProcessFbParameters(
                    disassyProc, allComponents, disassyName, disassemblyProcessId,
                    contents: contents,
                    useRecipeStruct: config != null && config.UseRecipeStruct,
                    emitProcessTelemetry: config != null && config.MqttPublishEnabled);
                builder.AddFB(FBIdGenerator.GenerateFBId(disassyProc.ComponentID),
                    disassyName, "Process1_Generic", "Main", 20800, 1460,
                    dOuter, dNested);
                crossProcInstances.Add(disassyName);
                if (dRecipe != null) phaseNames[disassyName] = dRecipe.ProcessPhaseNames;
                ReportStation2Recipe(report, disassyName, dRecipe, "M580");
                AppendProcessRecipeComment(builder, disassyName, dRecipe);
            }
            else
            {
                report.Missing.Add(
                    "[Recipe] Disassembly Process not found in Control.xml — " +
                    "BX1 zone will have actuators but no Disassembly Process FB.");
            }

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

            // Sensors-first id map == the recipe's Wait1Id scheme, so InterlockManager.RuleSourceID and the engine read the same state_table slots.
            var scopedIds = ProcessRecipeArrayGenerator.BuildScopedComponentMap(
                contents.Sensors, contents.Actuators);

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
                int assignedId = actuatorIdStart + i;
                var fbType = ResolveActuatorFBType(actuator);
                // Force the UR3e's dedicated non-colliding slot (its positional id clashes on the M580 state_table) so CAT actuator_id == robot Wait1Id.
                if (MapperConfig.EnableRobotTaskTail && TemplateMap.IsRobotTaskArm(actuator))
                    assignedId = MapperConfig.RobotActuatorId;
                var displayName = InstanceNameResolver.Resolve(actuator,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var actPlc = plcIndex.ResolveComponent(actuator.Name, bindings);

                Dictionary<string, string> actParams;
                if (fbType == "Five_State_Actuator_CAT")
                {
                    actParams = BuildActuatorParameters(actuator, assignedId, allComponents, scopedIds);
                    // actuator_name IS the ring key this FB answers to; TemplateMap.RingKey is the one
                    // function that also spells the recipe's CmdTargetName, so the two cannot drift.
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        TemplateMap.RingKey(displayName));

                    InterlockEmitter.GuardFiveState(actParams, actuator, allComponents, scopedIds, report.Bound);
                }
                else if (string.Equals(fbType, "Seven_State_Actuator_Centre_Home_CAT", StringComparison.Ordinal))
                {
                    actParams = BuildMinimalActuatorParameters(actuator, assignedId, fbType);
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        TemplateMap.RingKey(displayName));
                    InterlockEmitter.ApplyCentreHome(actParams, actuator, allComponents, scopedIds);
                    InterlockEmitter.GuardCentreHome(actParams, actuator, allComponents, scopedIds, report.Bound);
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
                var (zoneX, zoneY) = PlcZoneActuatorPosition(actPlc, colInPlc);

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
                int assignedId = sensorIdStart + i;
                // TopCoverSenosr rides the cover ring into the Assembly state_table; pin it OUT of the
                // positional sequence to its own slot so its report never collides with the PartAtAssembly
                // synth sensor at slot 3. It stays counted in contents.Sensors, so actuator ids are unshifted.
                if (
                    CodeGen.Mapping.TemplateMap.IsTopCoverSensor(sensor.Name))
                    assignedId = MapperConfig.TopCoverSensorId;
                // Twin-declared PartAtAssembly takes the slot the synth injection reserves for it, so a
                // model that declares it and one that does not generate the same ids.
                else if (HandoffPlanner.IsPartAtAssembly(sensor.Name))
                    assignedId = HandoffPlanner.PartAtAssembly.Id;

                SensorBinding? senBinding = null;
                bindings?.Sensors.TryGetValue(sensor.Name, out senBinding);
                if (senBinding != null) report.Bound.Add((sensor.Name, DescribeBinding(senBinding)));
                else if (bindings != null) report.Missing.Add(sensor.Name);

                var senPlc = plcIndex.ResolveComponent(sensor.Name, bindings);
                int senCol = perPlcSensorCount[senPlc]++;
                var (sX, sY) = PlcZoneSensorPosition(senPlc, senCol);

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
            if (MapperConfig.EnableRobotTaskTail)
            {
                int synthY = 5200;
                string prevSynthInit = "PartInHopper";
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

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn1_Term"),
                "Stn1_Term", "CaSAdptrTerminator", "Main", 4780, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn2_Term"),
                "Stn2_Term", "CaSAdptrTerminator", "Main", 14000, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_Term"),
                "Area_Term", "CaSAdptrTerminator", "Main", 3760, 720);

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
                bool feedRevPi = MapperConfig.FeedStationController == FeedController.RevPi;
                string feedSuffix = feedRevPi ? "RevPi" : "M262";
                string feedClientId = feedRevPi ? config.MqttClientRevPi : config.MqttClientM262;
                string bx1Name  = tele ? "Telemetry_BX1"  : "MqttConn";
                string feedName = tele ? $"Telemetry_{feedSuffix}" : $"MqttConn_{feedSuffix}";
                string m580Name = tele ? "Telemetry_M580" : "MqttConn_M580";

                var mqttEntry = CodeGen.Mapping.ComponentRegistry.Get(bx1Name);
                int bx1X = mqttEntry?.X ?? 29000;
                int bx1Y = mqttEntry?.Y ?? 200;
                // Each conn is routed to its own sysres via SysresFbMirror.BucketFor; BX1 bring-up is in BuildBx1Wiring, Feed/M580 below.
                InjectMqttConn(bx1Name, config.MqttConnectionName, config.MqttClientId, bx1X, bx1Y);
                InjectMqttConn(feedName, config.MqttConnectionName, feedClientId,
                    LayoutGrid.ColumnBaseX(PlcAssignment.M262), 200);
                InjectMqttConn(m580Name, config.MqttConnectionName, config.MqttClientM580,
                    LayoutGrid.ColumnBaseX(PlcAssignment.M580), 200);
                builder.AddEventConnection($"{feedName}.INITO", $"{feedName}.CONNECT");
                builder.AddEventConnection($"{m580Name}.INITO", $"{m580Name}.CONNECT");
                builder.AddEventConnection("Area.INITO", $"{feedName}.INIT");
                builder.AddEventConnection("Station2.INITO", $"{m580Name}.INIT");
                // Partial swap: the RevPi ALSO hosts Feeder/Checker (embedded MqttPub bind ConnectionID
                // 'SMC'), so it needs its OWN local connection alongside the M262 Feed connection — else
                // those publishers have no active connection. INIT off a RevPi-local component (PartInHopper)
                // so there is no cross-device INIT wire. Full swap already puts the one Feed conn on RevPi.
                if (MapperConfig.PartialRevPi)
                {
                    string revpiName = tele ? "Telemetry_RevPi" : "MqttConn_RevPi";
                    InjectMqttConn(revpiName, config.MqttConnectionName, config.MqttClientRevPi,
                        LayoutGrid.ColumnBaseX(PlcAssignment.RevPi), 200);
                    builder.AddEventConnection($"{revpiName}.INITO", $"{revpiName}.CONNECT");
                    builder.AddEventConnection("PartInHopper.INITO", $"{revpiName}.INIT");
                }
                report.Missing.Add(
                    $"[MQTT] {(tele ? "Telemetry" : "MQTT_CONNECTION")} injected per resource — BX1 " +
                    $"(ClientId SMC_BX1) + Feed:{feedSuffix} ({feedClientId}) + M580 (SMC_M580), shared ConnectionID=" +
                    $"{config.MqttConnectionName} so each resource's embedded MqttPub binds locally; URL={brokerUrl}.");
            }


            RingWiringPlanner.BuildFeedStationWiring(builder, contents);
            RingWiringPlanner.BuildStation2Wiring(builder, contents, disassemblyFbName);
            RingWiringPlanner.BuildBx1Wiring(builder, contents, config);

            // CycleReady cross-controller handoff: the dedicated CrossComm
            // link Disassembly(M580) -> Feed_Station(M262). CrossReference=True tells EAE to auto-generate the UDP
            // proxy; both FBs are Process1_Generic (CycleReadyEventOut/CycleReadyOut outputs on Disassembly,
            // CycleReadyEvent/CycleReady inputs on Feed). Feed's ProcessHandler.SETRDY writes
            // state_table[DisassemblyProcessId] = the value Feed's WAIT gate keys on. Syslay-only; the sysres
            // leaves these boundary ports OPEN and EAE bridges from here (same as the ejector/robot cross-hops).
            if (CodeGen.Configuration.MapperConfig.CycleReadyActive && disassemblyFbName != null)
            {
                builder.AddEventConnection($"{disassemblyFbName}.CycleReadyEventOut",
                    $"{processInstanceName}.CycleReadyEvent", crossReference: true);
                builder.AddDataConnection($"{disassemblyFbName}.CycleReadyOut",
                    $"{processInstanceName}.CycleReady", crossReference: true);
            }

            _ = config;

            // Frame widths (from LayoutGrid) MUST enclose all this PLC's FBs: EAE's MoveStyle="AnyContained" auto-grows a frame westward around any FB past its right edge, swallowing neighbours.
            builder.AddFrame("FRAME_Station1",
                LayoutGrid.FrameOriginX(PlcAssignment.M262), LayoutGrid.FrameOriginY,
                LayoutGrid.FrameWidth(PlcAssignment.M262), LayoutGrid.FrameHeight,
                "LightYellow", "Station 1   —   PLC M262", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            builder.AddFrame("FRAME_Station2_M580",
                LayoutGrid.FrameOriginX(PlcAssignment.M580), LayoutGrid.FrameOriginY,
                LayoutGrid.FrameWidth(PlcAssignment.M580), LayoutGrid.FrameHeight,
                "MediumPurple", "Station 2   —   PLC M580", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            // BX1 is the Soft dPAC host (Cover P&P) — NOT Station 2 (which is the M580 frame above).
            builder.AddFrame("FRAME_BX1",
                LayoutGrid.FrameOriginX(PlcAssignment.BX1), LayoutGrid.FrameOriginY,
                LayoutGrid.FrameWidth(PlcAssignment.BX1), LayoutGrid.FrameHeight,
                "LightGreen", "Soft dPAC   —   PLC BX1", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");

            var doc = builder.Build();
            doc.Save(fullPath);

            // EAE Solution Integrity requires an opcua.xml inside a folder named after the syslay stem.
            EnsureOpcuaXmlBesideArtefact(fullPath);

            // The HMI is derived from the finished layout (FB Id -> TagName, FB Type -> faceplate).
            CodeGen.Hmi.HmiGenerator.Emit(fullPath, config);

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


        // Component → emitted FB Type via TemplateMap; the 6 sites that must agree are INVARIANTS.md I-4.
        internal static string ResolveActuatorFBType(VueOneComponent actuator)
        {
            if (actuator == null) return "Five_State_Actuator_CAT";
            // Only the real UR3e (IsRobotTaskArm) → Robot_Task_CAT; Type="Robot" grippers stay Five_State/Vacuum.
            if (MapperConfig.EnableRobotTaskTail && TemplateMap.IsRobotTaskArm(actuator))
                return "Robot_Task_CAT";
            return TemplateMap.ResolveActuatorCatType(
                actuator.Name ?? string.Empty,
                actuator.States?.Count ?? 0,
                TemplateMap.IsBranchedSevenState(actuator));
        }

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
            if (string.Equals(fbType, "Seven_State_Actuator_Centre_Home_CAT", StringComparison.OrdinalIgnoreCase))
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

        // Placeholder placement; CanonicalLayout rewrites registered names to their ComponentRegistry coordinate post-syslay.
        private static (int X, int Y) PlcZoneActuatorPosition(PlcAssignment plc, int colIndexInPlc)
        {
            return (LayoutGrid.ColumnBaseX(plc) + colIndexInPlc * LayoutGrid.ColumnPitchX,
                    LayoutGrid.RowY(plc, LayoutRow.Actuator));
        }

        private static (int X, int Y) PlcZoneSensorPosition(PlcAssignment plc, int colIndexInPlc)
        {
            return (LayoutGrid.ColumnBaseX(plc) + colIndexInPlc * LayoutGrid.ColumnPitchX,
                    LayoutGrid.RowY(plc, LayoutRow.Sensor));
        }

        internal static bool IsBx1CoverActuator(string name) =>
            name is "CoverPNP_Hr" or "CoverPNP_Vr" or "CoverPnp_Gripper";

        public static Dictionary<string, string> BuildActuatorParameters(
            VueOneComponent actuator, int assignedId,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds = null)
        {
            int toWorkMs = ResolveStateTimeMs(actuator, stateNumber: 1, fallbackMs: DefaultMotionMs);
            int toHomeMs = ResolveStateTimeMs(actuator, stateNumber: 3, fallbackMs: DefaultMotionMs);

            var atWorkIds = ResolveAtWorkStateIds(actuator);
            var atHomeIds = ResolveAtHomeStateIds(actuator);
            bool workSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atWorkIds);
            bool homeSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atHomeIds);

            // Cover actuators settle in coverMotionMs (Hr/Vr keep real DIs); the gripper has no grip/release DI, so it timer-acknowledges sensorless or the release WAIT stalls.
            if (IsBx1CoverActuator(actuator.Name))
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
            if (MapperConfig.EnableRobotTaskTail
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
            if (MapperConfig.MergeFeedRing
                && actuator.Name.IndexOf("Gripper", StringComparison.OrdinalIgnoreCase) >= 0
                && !IsBx1CoverActuator(actuator.Name))
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

            InterlockEmitter.ApplyFiveState(actuatorParams, actuator, allComponents, scopedIds);

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

        public static bool AnyComponentReferencesStates(
            IReadOnlyList<VueOneComponent> allComponents,
            VueOneComponent actuator,
            HashSet<string> stateIds)
        {
            if (stateIds.Count == 0) return false;
            foreach (var c in allComponents)
            {
                if (string.Equals(c.ComponentID, actuator.ComponentID, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var st in c.States)
                    foreach (var t in st.Transitions)
                        foreach (var cond in t.Conditions)
                            if (!string.IsNullOrEmpty(cond.ID) && stateIds.Contains(cond.ID))
                                return true;
            }
            return false;
        }

        // Legacy literal-substring lookup, no longer used by BuildActuatorParameters.

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

            SweepOrphanSysresPerSysdev(config, report);

            SweepBridgeFbsFromAllSysres(config, report);

            return report;
        }

        // Delete .sysres files referenced by no <Resource> in the sysdev, else EAE raises a "Repair Instances" dialog.
        private static void SweepOrphanSysresPerSysdev(MapperConfig config, CleanupReport report)
        {
            void Log(string line) => report.DeviceCleanupLog.Add($"[CleanDevice] {line}");

            string? eaeRoot = DeriveDemonstratorEaeRoot(config);
            if (string.IsNullOrEmpty(eaeRoot)) return; // harness / no project root → skip

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return;

            List<string> sysdevFiles;
            try { sysdevFiles = Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories).ToList(); }
            catch { return; }
            if (sysdevFiles.Count == 0) return; // not a real System folder

            foreach (var sysdevPath in sysdevFiles)
            {
                XDocument doc;
                try { doc = XDocument.Load(sysdevPath); }
                catch { continue; }
                var root = doc.Root;
                if (root == null) continue;
                XNamespace dns = root.GetDefaultNamespace();

                var activeIds = new HashSet<string>(
                    (root.Element(dns + "Resources")?.Elements(dns + "Resource")
                        ?? Enumerable.Empty<XElement>())
                        .Select(r => (string?)r.Attribute("ID") ?? string.Empty)
                        .Where(s => s.Length > 0),
                    StringComparer.Ordinal);
                if (activeIds.Count == 0) continue; // nothing referenced → don't touch

                // Resource files live in {sysdevFolder}/{sysdevStem}/
                var sysdevStem = Path.GetFileNameWithoutExtension(sysdevPath);
                var resDir = Path.Combine(Path.GetDirectoryName(sysdevPath)!, sysdevStem);
                if (!Directory.Exists(resDir)) continue;

                List<string> sysresFiles;
                try { sysresFiles = Directory.GetFiles(resDir, "*.sysres", SearchOption.TopDirectoryOnly).ToList(); }
                catch { continue; }
                if (sysresFiles.Count <= 1) continue; // 0 or 1 file → no possible orphan

                // Skip the whole sysdev unless every active resource has its {ID}.sysres (else filename==ID is broken).
                bool allActivePresent = activeIds.All(id =>
                    sysresFiles.Any(f => string.Equals(
                        Path.GetFileNameWithoutExtension(f), id, StringComparison.Ordinal)));
                if (!allActivePresent)
                {
                    Log($"{Path.GetFileName(sysdevPath)}: an active Resource has no matching .sysres on disk — orphan sweep skipped (filename!=ID convention not satisfied)");
                    continue;
                }

                foreach (var file in sysresFiles)
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (activeIds.Contains(stem)) continue; // active → keep
                    try
                    {
                        File.Delete(file);
                        Log($"deleted orphan sysres {Path.GetFileName(file)} under {sysdevStem} " +
                            $"(referenced by no Resource in {Path.GetFileName(sysdevPath)}; active = {string.Join(",", activeIds)})");
                    }
                    catch (Exception ex)
                    {
                        Log($"failed to delete orphan sysres {file}: {ex.Message}");
                    }
                }
            }
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

            System.Xml.Linq.XNamespace ns = "https://www.se.com/LibraryElements";
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
                    if (string.Equals(type,  "M262_dPAC", StringComparison.Ordinal) &&
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

            // Fast-path: one Resource + one .sysres = canonical clean state.
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

            // 2+ Resources — keep the first, drop the rest and their backing .sysres files.
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

            XNamespace ns = "https://www.se.com/LibraryElements";
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

        public static VueOneComponent? FindStation1Process(List<VueOneComponent> all)
        {
            var feeder = all.FirstOrDefault(c =>
                string.Equals(c.Type, "Actuator", StringComparison.Ordinal) &&
                string.Equals(c.Name, "Feeder", StringComparison.Ordinal));
            if (feeder == null) return null;

            return all.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.Ordinal) &&
                c.States.Any(s => s.Transitions.Any(t =>
                    t.Conditions.Any(cond =>
                        string.Equals(cond.ComponentID, feeder.ComponentID, StringComparison.OrdinalIgnoreCase)))));
        }

        public static (Dictionary<string, string> Outer,
                       IDictionary<string, IDictionary<string, string>> Nested,
                       RecipeArrays? Recipe)
            BuildProcessFbParameters(VueOneComponent process, List<VueOneComponent> allComponents,
                string processName, int processId,
                StationContents? contents = null, bool useRecipeStruct = false,
                bool emitProcessTelemetry = false)
        {
            // Recipe arrays travel as Process1_Generic Parameter values; if `contents` is null, emit only the two scalars and return a null Recipe.
            var outer = new Dictionary<string, string>
            {
                ["process_name"] = SyslayBuilder.FormatString(processName),
                ["process_id"] = SyslayBuilder.FormatInt(processId)
            };

            RecipeArrays? recipe = null;
            if (contents != null)
            {
                recipe = ProcessRecipeArrayGenerator.Generate(process, contents, allComponents, processId);
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


        public string GenerateStation1TestSyslay(MapperConfig config, string controlXmlPath,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            if (string.IsNullOrEmpty(config.SyslayPath2))
                throw new InvalidOperationException("MapperConfig.SyslayPath2 is not configured.");
            // Reset SimulatorRecipeMode (the State-Transition Table preview sets it transiently) so no preview run carries over onto the rig.
            Configuration.MapperConfig.SimulatorRecipeMode = false;
            return GenerateFeedStationSyslayToPath(controlXmlPath, config.SyslayPath2, bindings, config, out report);
        }


        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // opcua.xml stub in a folder named after the artefact stem, so EAE's Solution Integrity check passes.
        public static void EnsureOpcuaXmlBesideArtefact(string artefactPath)
            => CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(artefactPath);
    }
}
