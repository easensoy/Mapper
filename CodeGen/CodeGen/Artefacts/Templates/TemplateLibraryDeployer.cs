using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation.Interlocks;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;
using static CodeGen.Services.TemplateArtifactDeployer;
using static CodeGen.Services.FbtXmlEditor;
using static CodeGen.Services.HmiTemplatePatcher;
using static CodeGen.Services.TelemetryTemplatePatcher;
using static CodeGen.Services.ProcessRuntimeTemplatePatcher;
using static CodeGen.Services.ActuatorCatTemplatePatcher;
using static CodeGen.Services.RingRelayPatcher;
using static CodeGen.Services.SwivelCatPatcher;
using static CodeGen.Services.InterlockCatPatcher;

namespace CodeGen.Services
{
    public static class TemplateLibraryDeployer
    {
        static readonly Dictionary<string, string[]> CatDependencies = new()
        {
            { "Five_State_Actuator_CAT", new[] { "FiveStateActuator" } },
            { "Sensor_Bool_CAT",         new[] { "Sensor_Bool" } },
            { "Actuator_Fault_CAT",      new[] { "FaultLatch" } },
            { "Robot_Task_CAT",          new[] { "Robot_Task_Core" } },
            { "Seven_State_Actuator_CAT", new[] { "SevenStateActuator", "SevenStateActuator2" } },
            { "Seven_State_Actuator_Centre_Home_CAT",
              new[] { "SevenStateCentreHomeActuator", "No_Sensor_Handler_7SCH", "FaultLatch_7SCH",
                      "actuatorStateEvents_7SCH" } },
            { "Station_CAT",             new[] { "Station_Core", "Station_Fault", "Station_Status" } },
            { "Process1_Generic",        new[] { "ProcessRuntime_Generic_v1", "ProcessStateBusHandler" } },
        };

        static readonly Dictionary<string, string> ComponentTypeToCat = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Actuator_5",  "Five_State_Actuator_CAT" },
            { "Actuator_7",  "Seven_State_Actuator_CAT" },
            { "Sensor_2",    "Sensor_Bool_CAT" },
            { "Process_Any", "Process1_Generic" },
        };

        static readonly string[] UniversalCats = new[]
        {
            "Five_State_Actuator_CAT", "Sensor_Bool_CAT", "Process1_Generic",
            "Seven_State_Actuator_CAT",
            "Seven_State_Actuator_Centre_Home_CAT",
        };

        static readonly string[] UniversalComposites = new[]
        {
            "Area", "Station", "CaSAdptrTerminator", "faultDetection",
            "faultDetection_7SCH",
        };

        static readonly string[] UniversalAdapters = new[]
        {
            "CaSAdptr", "AreaHMIAdptr", "StationHMIAdptr", "stateRptCmdAdptr"
        };

        static readonly string[] UniversalBasics = new[]
        {
            "FiveStateActuator", "Sensor_Bool",
            "Station_Core", "Station_Fault", "Station_Status",
            "ProcessRuntime_Generic_v1", "ProcessStateBusHandler",
            "FaultLatch", "actuatorStateEvents",
            "updateComponentState", "updateComponentState_Sensor",
            "No_Sensor_Handler",
            "CommonInterlockEvaluator",
            "changeEventProcess1", "changeEventProcess2",
            "SevenStateActuator", "SevenStateActuator2",
            "SevenStateCentreHomeActuator", "No_Sensor_Handler_7SCH", "FaultLatch_7SCH",
            "actuatorStateEvents_7SCH",
        };

        static readonly string[] UniversalHmiCats = new[]
        {
            "Area_CAT", "Station_CAT"
        };

        static readonly string[] UniversalDataTypes = new[]
        {
            "Component_State",
            "Component_State_Msg"
        };

        public static DeployResult DeployUniversalArchitecture(MapperConfig cfg)
        {
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;
            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory from syslay path.");

            // ExtractToEae is copy-if-absent, so force-re-extract the artefacts reshaped by later patches (delete first).
            foreach (var ext in new[] { ".fbt", ".doc.xml", ".meta.xml" })
            foreach (var basic in new[] { "No_Sensor_Handler_7SCH", "SevenStateCentreHomeActuator", "ProcessRuntime_Generic_v1" })
            {
                var stale = Path.Combine(eaeProjectDir, "IEC61499", basic + ext);
                try { if (File.Exists(stale)) File.Delete(stale); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove stale {stale}: {ex.Message}"); }
            }

            // ExtractToEae is copy-if-absent, so force-re-extract the CATs reshaped by later patches (delete first).
            foreach (var catRefresh in new[] { "Sensor_Bool_CAT", "Five_State_Actuator_CAT", "Seven_State_Actuator_Centre_Home_CAT", "Process1_Generic" })
            {
                var dir = Path.Combine(eaeProjectDir, "IEC61499", catRefresh);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove deployed CAT {dir}: {ex.Message}"); }
            }

            foreach (var name in UniversalBasics)
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);

            SweepRetiredType(eaeProjectDir, "SimCentreHomeSensor_7SCH", result);

            foreach (var name in UniversalAdapters)
                DeployArtifact(libPath, "Adapter", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in UniversalComposites)
                DeployArtifact(libPath, "Composite", name, eaeProjectDir, result, isBasic: false);

            foreach (var name in UniversalHmiCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            foreach (var name in UniversalCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            DeployArtifact(libPath, "Basic", "Robot_Task_Core", eaeProjectDir, result, isBasic: true);
            DeployArtifact(libPath, "CAT", "Robot_Task_CAT", eaeProjectDir, result, isBasic: false, isCat: true);

            if (cfg.DeployBx1IoBroker)
            {
                DeployArtifact(libPath, "Basic", "changeEventM262_2", eaeProjectDir, result, isBasic: true);
                // Force-re-extract the safe-start gate (copy-if-absent) so a corrected type reaches an already-generated tree.
                if (cfg.Bx1CoverSafeStart)
                {
                    var fsFbt = Path.Combine(eaeProjectDir, "IEC61499", "Bx1CoverFailsafe.fbt");
                    if (File.Exists(fsFbt)) { try { File.Delete(fsFbt); } catch { /* locked -> keep existing */ } }
                    DeployArtifact(libPath, "Basic", "Bx1CoverFailsafe", eaeProjectDir, result, isBasic: true);
                }
                DeployArtifact(libPath, "Composite", "PLC_RW_BX1", eaeProjectDir, result, isBasic: false);
                if (cfg.Bx1BridgeInsideComposite)
                    CodeGen.Devices.BX1.Bx1IoBrokerInjector.EmbedCoverBridgeInComposite(
                        Path.Combine(eaeProjectDir, "IEC61499", "PLC_RW_BX1.fbt"));
                // SAFE: cover_hr safe-start gate — forced HOME on start; does NOT cover EAE Clean/STOP (needs TM3BC ToHome fallback 16#0002).
                if (cfg.Bx1CoverSafeStart &&
                    CodeGen.Devices.BX1.Bx1IoBrokerInjector.InjectCoverFailsafeIntoBrokerType(eaeProjectDir))
                    result.Warnings.Add("[Deploy][BX1] CoverPNP_Hr safe-start gate (Bx1CoverFailsafe) " +
                        "inserted into PLC_RW_BX1 — cover_hr forced HOME on every start.");
            }

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);
            PatchProcessRuntimeCompatibility(eaeProjectDir, result);
            PatchSensorBoolCatDstQi(eaeProjectDir, result);
            PatchCatSymlinkQi(eaeProjectDir, "Five_State_Actuator_CAT", result);
            // QI=TRUE on the SYMLINKMULTIVARDST/SRC or the subscriber is dropped and the core is islanded from its IO.
            PatchCatSymlinkQi(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT", result);
            EnsureSevenStateStateOut(eaeProjectDir, result);
            foreach (var hmiCat in new[] { "Five_State_Actuator_CAT", "Seven_State_Actuator_Centre_Home_CAT",
                                           "Sensor_Bool_CAT", "Robot_Task_CAT" })
                FixCatHmiOpcuaFrame(eaeProjectDir, hmiCat, result);
            PatchActuatorModeInitialValue(eaeProjectDir, "FiveStateActuator.fbt", result);
            PatchActuatorModeInitialValue(eaeProjectDir, "SevenStateCentreHomeActuator.fbt", result);
            PatchSwivelAtHomeInitRecovery(eaeProjectDir, addArc: true, result);
            PatchSwivelAtHomeCoilClear(eaeProjectDir, clearCoils: true, result);
            PatchSwivelAtHomeBothCoils(eaeProjectDir, MapperConfig.SwivelHomeHoldBothCoils, result);
            // SAFE: SwivelBrakeHome runs LAST — directional brake (reverse the driving coil only when homing from AtWork1, away from the ejector).
            PatchSwivelBrakeHome(eaeProjectDir, MapperConfig.SwivelBrakeHome,
                GenerationConfig.Current.BearingPnpHomeBrakeMs, result);
            PatchSwivelRelaxWorkLatch(eaeProjectDir, relax: true, result);
            PatchSwivelInterlockEventCarriesStateVal(eaeProjectDir, add: true, result);
            PatchRingReportClearDest(eaeProjectDir, result);
            PatchRingCommandCnfOnlyOnDestination(eaeProjectDir, result);
            NormalizeFiveStateInterlockConstants(eaeProjectDir, result);
            PatchProcess1RecipeArraySize(eaeProjectDir, result);
            PatchProcessNameStringSize(eaeProjectDir, result);

            // Additive, gated by MqttPublishEnabled; PUBLISH binds to the injected MQTT_CONNECTION by matching ConnectionID value (no wire).
            if (cfg.MqttPublishEnabled)
            {
                DeployMqttFormatter(eaeProjectDir, result);
                PatchCatMqttPublish(eaeProjectDir, "Five_State_Actuator_CAT",
                    stateEventSource: "ActuatorCore.pst_out",
                    stateDataSource: "ActuatorCore.current_state_to_process",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "actuator_name", cfg, result);
                PatchCatMqttPublish(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT",
                    stateEventSource: "ActuatorCore.pst_out",
                    stateDataSource: "ActuatorCore.current_state_to_process",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "actuator_name", cfg, result);
                PatchCatMqttPublish(eaeProjectDir, "Robot_Task_CAT",
                    stateEventSource: "StateMachine.pst_out",
                    stateDataSource: "StateMachine.current_state_to_process",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "actuator_name", cfg, result);
                PatchCatMqttPublish(eaeProjectDir, "Sensor_Bool_CAT",
                    stateEventSource: "FB1.CNF",
                    stateDataSource: "FB1.Status",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "name", cfg, result);
            }

            // Interlock interface -> RuleTable:InterlockTable struct (gated by interlock.yaml useStruct); false restores the 4 arrays + scalar RuleCount.
            bool interlockStruct = InterlockConfig.Current.UseStruct;
            if (interlockStruct)
            {
                DeployInterlockRuleDatatype(eaeProjectDir, result);
                DeployInterlockTableDatatype(eaeProjectDir, result);
            }
            NormalizeFiveStateRuleArrays(eaeProjectDir, "Five_State_Actuator_CAT.fbt", "InterlockManager", interlockStruct, result);
            NormalizeFiveStateRuleArrays(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT.fbt", "CommonInterlockManager", interlockStruct, result);
            NormalizeCommonInterlockEvaluatorRules(eaeProjectDir, interlockStruct, result);

            // Actuator target states -> Target:TargetStates struct (gated by useTargetStruct); false restores the scalar inputs.
            bool targetStruct = InterlockConfig.Current.UseTargetStruct;
            if (targetStruct)
                DeployTargetStatesDatatype(eaeProjectDir, result);
            NormalizeTargetStates(eaeProjectDir, "Five_State_Actuator_CAT.fbt", "InterlockManager",
                new[] { "TargetWork1State", "TargetHomeState" }, targetStruct, result);
            NormalizeTargetStates(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT.fbt", "CommonInterlockManager",
                new[] { "TargetWork1State", "TargetWork2State", "TargetHomeState" }, targetStruct, result);
            NormalizeCommonInterlockEvaluatorTargets(eaeProjectDir, targetStruct, result);

            // Wrap each resource MQTT_CONNECTION in the 'Telemetry' composite (gated by UseTelemetryCat); false emits the raw MQTT_CONNECTION.
            if (cfg.UseTelemetryCat)
            {
                // Sweep first (copy-if-absent staleness): removes current + legacy 'Telemetry_CAT' artifacts before deploying fresh.
                SweepTelemetryCat(eaeProjectDir, result);
                DeployTelemetryConfigDatatype(eaeProjectDir, result);
                DeployTelemetryHealthDatatype(eaeProjectDir, result);
                DeployArtifact(libPath, "Basic", "TelemetryUnpack", eaeProjectDir, result, isBasic: true);
                DeployArtifact(libPath, "Basic", "TelemetryPack", eaeProjectDir, result, isBasic: true);
                DeployArtifact(libPath, "Composite", "Telemetry", eaeProjectDir, result, isBasic: false);
            }
            else
            {
                SweepTelemetryCat(eaeProjectDir, result);
            }
            NormalizeSwivelSimSensorSource(eaeProjectDir, result);
            StripCatHomeSensorPoll(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT", result);
            NormalizeFiveStateSimSensorSource(eaeProjectDir, result);
            NormalizeFiveStateFaultEnables(eaeProjectDir, result);
            // Recipe-struct collapse on Process1_Generic + engine (gated by UseRecipeStruct); false reverts to the 6 arrays.
            bool recipeStruct = cfg.UseRecipeStruct;
            if (recipeStruct)
                DeployRecipeStepDatatype(eaeProjectDir, result);
            NormalizeProcess1RecipeArrays(eaeProjectDir, recipeStruct, result);
            NormalizeProcessRuntimeRecipeArrays(eaeProjectDir, recipeStruct, result);
            NormalizeProcessEngineDebugWatch(eaeProjectDir, result);
            // check_wait is LEVEL-triggered (WaitSatisfied := state_table[...].state = Wait1State); a WAIT already-true when it arms satisfies immediately.

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);

            VerifyArraySizeConsistency(eaeProjectDir, result);

            // Trust-preservation guard: when an M262 sysdev exists, device-layer writes are skipped; application content still runs.
            bool m262DeviceExists = M262SysdevEmitter.M262SysdevAlreadyExists(cfg);

            string sysdevId = string.Empty;
            try
            {
                var sysdev = M262SysdevEmitter.Emit(cfg);
                result.SysdevPath = sysdev.SysdevPath;
                result.SystemFilePath = sysdev.SystemFilePath;
                result.MappingsAdded = sysdev.MappingsAdded;
                sysdevId = ReadSysdevId(sysdev.SysdevPath);
                if (sysdev.DevicePreserved)
                {
                    MapperLogger.Info(
                        "[Device] M262 sysdev exists, skipping device creation and " +
                        "config writes to preserve trust binding");
                    MapperLogger.Info("[Device] M262 sysdev preserved (trust binding intact)");
                }
                else
                {
                    MapperLogger.Info(
                        $"[Deploy] sysdev rewritten as M262_dPAC; {sysdev.MappingsAdded} APP→RES0 mapping(s) ensured");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"M262 sysdev emit failed: {ex.Message}");
            }

            if (m262DeviceExists)
            {
                MapperLogger.Info(
                    "[Device] M262 sysdev exists, skipping Topology Equipment JSON " +
                    "and network-profile writes to preserve trust binding");
            }
            else
            {
                try
                {
                    var topo = M262TopologyEmitter.Emit(cfg, sysdevId);
                    MapperLogger.Info(
                        $"[Deploy] Topology emitted: {topo.FilesWritten.Count} files, " +
                        $"{topo.TopologyProjEntriesAdded} topologyproj entries added");
                    foreach (var w in topo.Warnings)
                        result.Warnings.Add($"Topology: {w}");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"M262 topology emit failed: {ex.Message}");
                }
            }

            try
            {
                var hcf = M262HwConfigCopier.Copy(cfg);
                result.HcfPath = hcf.HcfPath;
                result.HcfParametersOverwritten.AddRange(hcf.ParametersOverwritten);
                foreach (var w in hcf.Warnings)
                    result.Warnings.Add($"HCF: {w}");
                MapperLogger.Info($"[Deploy] hcf copied from baseline; {hcf.ParametersOverwritten.Count} channel parameter(s) overwritten");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"M262 hcf copy failed: {ex.Message}");
            }

            result.Success = true;
            return result;
        }

        static void DeployDataTypes(string libPath, string eaeProjectDir, DeployResult result)
        {
            var srcDir = Path.Combine(libPath, "DataType");
            if (!Directory.Exists(srcDir))
            {
                result.Warnings.Add("Library DataType folder missing — Component_State*.dt won't be deployed.");
                return;
            }
            var destDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
            Directory.CreateDirectory(destDir);

            foreach (var name in UniversalDataTypes)
            {
                var src = Path.Combine(srcDir, name + ".dt");
                if (!File.Exists(src))
                {
                    result.Warnings.Add($"DataType source missing: {name}.dt");
                    continue;
                }
                var dst = Path.Combine(destDir, name + ".dt");
                if (!File.Exists(dst) ||
                    new FileInfo(src).Length != new FileInfo(dst).Length)
                {
                    File.Copy(src, dst, overwrite: true);
                    result.FilesExtracted++;
                }
                result.DataTypesDeployed.Add(name);
                MapperLogger.Info($"[Deploy] DataType: {name}");
            }
        }









        static void DeployMqttFormatter(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dst = Path.Combine(eaeProjectDir, "IEC61499", "MqttStateFormatter.fbt");
                File.WriteAllText(dst, MqttStateFormatterFbt);
                result.PatchesApplied.Add("MqttStateFormatter.fbt deployed (INT→STRING[255] payload)");
                MapperLogger.Info("[Deploy][MQTT] MqttStateFormatter.fbt written to IEC61499/");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"MqttStateFormatter deploy failed: {ex.Message}");
            }
        }




        // Fan an MQTT_PUBLISH off the CAT's post-update state event (additive). MQTT_PUBLISH does NOT resolve $${PATH} at runtime → topicNameSource must be a concrete per-instance name.
        static void PatchCatMqttPublish(string eaeProjectDir, string catName,
            string stateEventSource, string stateDataSource, string initSource,
            string topicNameSource,
            MapperConfig cfg, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catName + ".fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catName}.fbt not found; MQTT publish patch skipped.");
                return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var net = root.Element(ns + "FBNetwork");
                if (net == null) { result.Warnings.Add($"{catName}.fbt: no FBNetwork; MQTT patch skipped."); return; }

                // Remove existing MqttPub/MqttFmt FBs + wires before re-emitting (CAT folder is copy-if-absent).
                var staleFbs = net.Elements(ns + "FB")
                    .Where(f => (string?)f.Attribute("Name") is "MqttPub" or "MqttFmt")
                    .ToList();
                int removedFbs = staleFbs.Count;
                foreach (var f in staleFbs) f.Remove();

                int removedWires = 0;
                foreach (var section in new[] { "EventConnections", "DataConnections" })
                {
                    var sec = net.Element(ns + section);
                    if (sec == null) continue;
                    var staleConns = sec.Elements(ns + "Connection")
                        .Where(c =>
                        {
                            var s = (string?)c.Attribute("Source") ?? string.Empty;
                            var d = (string?)c.Attribute("Destination") ?? string.Empty;
                            return s.StartsWith("MqttFmt.", StringComparison.Ordinal)
                                || s.StartsWith("MqttPub.", StringComparison.Ordinal)
                                || d.StartsWith("MqttFmt.", StringComparison.Ordinal)
                                || d.StartsWith("MqttPub.", StringComparison.Ordinal);
                        })
                        .ToList();
                    removedWires += staleConns.Count;
                    foreach (var c in staleConns) c.Remove();
                }
                if (removedFbs > 0 || removedWires > 0)
                    result.PatchesApplied.Add(
                        $"{catName}: removed stale MQTT patch ({removedFbs} FB(s), {removedWires} wire(s)) before re-emit");

                // Allocate FB IDs from max(existing FB ID)+1, not the IDCounter alone (later-added FBs sit past the counter and would collide).
                var idAttr = root.Elements(ns + "Attribute")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                int idc = 0;
                if (idAttr != null && int.TryParse((string?)idAttr.Attribute("Value"), out var parsed)) idc = parsed;
                int maxFbId = net.Elements(ns + "FB")
                    .Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0)
                    .DefaultIfEmpty(0).Max();
                int baseId = Math.Max(maxFbId + 1, idc);   // never collide, never go backwards
                int fmtId = baseId, pubId = baseId + 1;
                if (idAttr != null) idAttr.SetAttributeValue("Value", (baseId + 2).ToString());

                string Q(string s) => "'" + s + "'";   // ST string literal

                var fmtFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", fmtId),
                    new System.Xml.Linq.XAttribute("Name", "MqttFmt"),
                    new System.Xml.Linq.XAttribute("Type", "MqttStateFormatter"),
                    new System.Xml.Linq.XAttribute("x", "8000"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));

                // A configured generic FB is a hashed variant in namespace "Main"; the hash is derived from InterfaceParams (CNTX:=1) and changes if the channel count changes.
                const string MqttPublishVariant = "MQTT_PUBLISH_115480E69E664F878";
                var pubFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", pubId),
                    new System.Xml.Linq.XAttribute("Name", "MqttPub"),
                    new System.Xml.Linq.XAttribute("Type", MqttPublishVariant),
                    new System.Xml.Linq.XAttribute("x", "8600"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));
                // The numbered channel ports (Topic1/Payload1/... + PUBLISH1) do not exist until CNTX:=1 sets the channel count via InterfaceParams.
                pubFb.Add(new System.Xml.Linq.XElement(ns + "Attribute",
                    new System.Xml.Linq.XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new System.Xml.Linq.XAttribute("Value", "Runtime.NetConnectivity#CNTX:=1")));
                void P(System.Xml.Linq.XElement fb, string n, string v) =>
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", n),
                        new System.Xml.Linq.XAttribute("Value", v)));
                P(pubFb, "QI", "TRUE");
                // ConnectionID is the shared binding key: the MQTT_CONNECTION and every embedded publisher carry the same value (not the unique ClientIdentifier).
                P(pubFb, "ConnectionID", Q(cfg.MqttConnectionName));

                P(pubFb, "RootPath", Q(cfg.MqttTopicRoot));
                // Topic1 wired below, not a parameter.
                P(pubFb, "QoS1", cfg.MqttQoS.ToString());
                P(pubFb, "Retain1", cfg.MqttRetain ? "TRUE" : "FALSE");

                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) { lastFb.AddAfterSelf(pubFb); lastFb.AddAfterSelf(fmtFb); }
                else { net.Add(fmtFb); net.Add(pubFb); }

                // A <Frame> with <Parameter> children is invalid inside a CAT FBNetwork; strip any stale FRAME_MQTT.
                net.Elements(ns + "Frame")
                   .Where(fr => (string?)fr.Attribute("Name") == "FRAME_MQTT").Remove();

                var ec = net.Element(ns + "EventConnections");
                if (ec == null) { ec = new System.Xml.Linq.XElement(ns + "EventConnections"); net.Add(ec); }
                var dc = net.Element(ns + "DataConnections");
                if (dc == null) { dc = new System.Xml.Linq.XElement(ns + "DataConnections"); net.Add(dc); }

                void Conn(System.Xml.Linq.XElement parent, string s, string d) =>
                    parent.Add(new System.Xml.Linq.XElement(ns + "Connection",
                        new System.Xml.Linq.XAttribute("Source", s),
                        new System.Xml.Linq.XAttribute("Destination", d)));

                Conn(ec, stateEventSource, "MqttFmt.REQ");
                Conn(ec, "MqttFmt.CNF", "MqttPub.PUBLISH1");
                Conn(ec, initSource, "MqttFmt.INIT");
                Conn(ec, initSource, "MqttPub.INIT");
                Conn(dc, stateDataSource, "MqttFmt.state");
                Conn(dc, "MqttFmt.payload", "MqttPub.Payload1");
                Conn(dc, topicNameSource, "MqttPub.Topic1");

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"{catName}: MQTT publish injected (fan {stateEventSource} → MqttFmt → MqttPub.PUBLISH1, " +
                    $"ConnectionID={cfg.MqttConnectionName}, Topic=$${{PATH}}state)");
                MapperLogger.Info($"[Deploy][MQTT] {catName}.fbt: MQTT_PUBLISH wired off {stateEventSource}");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName} MQTT publish patch failed: {ex.Message}");
            }
        }


        const string MqttStateFormatterFbt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE FBType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<FBType GUID=\"f0a1b2c3-d4e5-4f60-8a1b-2c3d4e5f6071\" Name=\"MqttStateFormatter\" Comment=\"Basic FB - INT state to STRING payload for MQTT publish\" Namespace=\"Main\">\r\n" +
            "  <Identification Standard=\"61499-2\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/22/2026\" Remarks=\"v1 bare state-to-string\" />\r\n" +
            "  <InterfaceList>\r\n" +
            "    <EventInputs>\r\n" +
            "      <Event Name=\"INIT\" Comment=\"Initialization Request\"><With Var=\"state\" /></Event>\r\n" +
            "      <Event Name=\"REQ\" Comment=\"Format on state change\"><With Var=\"state\" /></Event>\r\n" +
            "    </EventInputs>\r\n" +
            "    <EventOutputs>\r\n" +
            "      <Event Name=\"INITO\" Comment=\"Initialization Confirm\"><With Var=\"payload\" /></Event>\r\n" +
            "      <Event Name=\"CNF\" Comment=\"Payload ready\"><With Var=\"payload\" /></Event>\r\n" +
            "    </EventOutputs>\r\n" +
            "    <InputVars>\r\n" +
            "      <VarDeclaration Name=\"state\" Type=\"INT\" Comment=\"current_state_to_process\" />\r\n" +
            "    </InputVars>\r\n" +
            "    <OutputVars>\r\n" +
            "      <VarDeclaration Name=\"payload\" Type=\"STRING[255]\" Comment=\"MQTT payload (state as text). Sized [255] so EAE does not flag WRN_UNSIZED_STRING (the yellow pin marker) inside every actuator/sensor CAT.\" />\r\n" +
            "    </OutputVars>\r\n" +
            "  </InterfaceList>\r\n" +
            "  <BasicFB>\r\n" +
            "    <Attribute Name=\"FBType.Basic.Algorithm.Order\" Value=\"INIT,Fmt\" />\r\n" +
            "    <ECC>\r\n" +
            "      <ECState Name=\"START\" Comment=\"Initial State\" x=\"300\" y=\"300\" />\r\n" +
            "      <ECState Name=\"INIT\" x=\"700\" y=\"120\"><ECAction Algorithm=\"Fmt\" Output=\"INITO\" /></ECState>\r\n" +
            "      <ECState Name=\"Format\" x=\"700\" y=\"520\"><ECAction Algorithm=\"Fmt\" Output=\"CNF\" /></ECState>\r\n" +
            "      <ECTransition Source=\"START\" Destination=\"INIT\" Condition=\"INIT\" x=\"450\" y=\"200\" />\r\n" +
            "      <ECTransition Source=\"INIT\" Destination=\"START\" Condition=\"1\" x=\"500\" y=\"300\" />\r\n" +
            "      <ECTransition Source=\"START\" Destination=\"Format\" Condition=\"REQ\" x=\"450\" y=\"420\" />\r\n" +
            "      <ECTransition Source=\"Format\" Destination=\"START\" Condition=\"1\" x=\"500\" y=\"520\" />\r\n" +
            "    </ECC>\r\n" +
            "    <Algorithm Name=\"INIT\" Comment=\"Initialization algorithm\"><ST><![CDATA[;]]></ST></Algorithm>\r\n" +
            "    <Algorithm Name=\"Fmt\" Comment=\"INT state to JSON payload\"><ST><![CDATA[payload := CONCAT(CONCAT('{state:', INT_TO_STRING(state)), '}');]]></ST></Algorithm>\r\n" +
            "  </BasicFB>\r\n" +
            "</FBType>\r\n";




        static void VerifyArraySizeConsistency(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                if (!Directory.Exists(iec)) return;

                var sizes = new Dictionary<(string, string), string>(
                    EqualityComparer<(string, string)>.Default);

                foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(fbt); }
                    catch { continue; }
                    var fbType = Path.GetFileNameWithoutExtension(fbt);
                    foreach (var vd in doc.Descendants().Where(e => e.Name.LocalName == "VarDeclaration"))
                    {
                        var name = (string?)vd.Attribute("Name");
                        var arr = (string?)vd.Attribute("ArraySize");
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(arr)) continue;
                        sizes[(fbType, name)] = arr;
                    }
                }

                foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(fbt); }
                    catch { continue; }
                    var instances = doc.Descendants()
                        .Where(e => e.Name.LocalName == "FB")
                        .ToDictionary(
                            e => (string?)e.Attribute("Name") ?? "",
                            e => (string?)e.Attribute("Type") ?? "",
                            StringComparer.Ordinal);

                    foreach (var conn in doc.Descendants().Where(e => e.Name.LocalName == "Connection"))
                    {
                        var src = ((string?)conn.Attribute("Source") ?? "").Split('.', 2);
                        var dst = ((string?)conn.Attribute("Destination") ?? "").Split('.', 2);
                        if (src.Length != 2 || dst.Length != 2) continue;
                        if (!instances.TryGetValue(src[0], out var srcType)) continue;
                        if (!instances.TryGetValue(dst[0], out var dstType)) continue;

                        sizes.TryGetValue((srcType, src[1]), out var srcSize);
                        sizes.TryGetValue((dstType, dst[1]), out var dstSize);
                        if (srcSize == null || dstSize == null) continue;
                        if (!string.Equals(srcSize, dstSize, StringComparison.Ordinal))
                        {
                            var msg = $"ArraySize mismatch in {Path.GetFileName(fbt)}: " +
                                $"{src[0]}.{src[1]} (size {srcSize}) -> {dst[0]}.{dst[1]} (size {dstSize})";
                            result.Warnings.Add(msg);
                            MapperLogger.Warn("[Verify] " + msg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ArraySize verification crashed: {ex.Message}");
            }
        }

        public static DeployResult Deploy(MapperConfig cfg, List<VueOneComponent> components)
        {
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException(
                    "Cannot determine EAE project directory from syslay path.");

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);

            foreach (var basic in neededBasics)
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null)
                {
                    result.Warnings.Add($"Basic package not found: {basic}");
                    continue;
                }
                ExtractToEae(zipPath, eaeProjectDir, result);
                result.BasicFBsDeployed.Add(basic);
                MapperLogger.Info($"[Deploy] Basic: {basic}");
            }

            foreach (var cat in neededCats)
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null)
                {
                    result.Warnings.Add($"CAT package not found: {cat}");
                    continue;
                }
                ExtractToEae(zipPath, eaeProjectDir, result);
                result.CATsDeployed.Add(cat);
                MapperLogger.Info($"[Deploy] CAT: {cat}");
            }

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);

            result.Success = true;
            return result;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat))
                    cats.Add(cat);

                if (string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                    ComponentTypeToCat.TryGetValue("Process_Any", out var procCat))
                    cats.Add(procCat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
            {
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps)
                        basics.Add(dep);
            }
            return basics;
        }

        static string? FindPackage(string libPath, string subfolder, string name, string extension)
        {
            var dir = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(dir)) return null;

            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fileName.Contains(extension, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

    }

    public class DeployResult
    {
        public bool Success { get; set; }
        public List<string> BasicFBsDeployed { get; set; } = new();
        public List<string> CATsDeployed { get; set; } = new();
        public List<string> AdaptersDeployed { get; set; } = new();
        public List<string> CompositesDeployed { get; set; } = new();
        public List<string> DataTypesDeployed { get; set; } = new();
        public List<string> PatchesApplied { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int FilesExtracted { get; set; }
        public int FilesSkipped { get; set; }

        public string? SysdevPath { get; set; }
        public string? SystemFilePath { get; set; }
        public int MappingsAdded { get; set; }

        public string? HcfPath { get; set; }
        public List<string> HcfParametersOverwritten { get; set; } = new();
    }
}
