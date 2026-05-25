using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Devices.M262;
using CodeGen.Devices.Shared;

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
            // Seven_State_Actuator_CAT re-added per user request 2026-05-21
            // for Bearing_PnP routing (Mapper Validator was failing the
            // PARALLEL+ALTERNATIVE-branched 13-state actuator with "No template
            // found"). SevenStateActuator2 is its internal "InterlockManager"
            // sub-FB analogue (same role as CommonInterlockEvaluator for
            // Five_State). The previous removal was driven by data-driven
            // patch failures; with Bearing_PnP routed to the verbatim CAT
            // (no runtime parameter graft) those failures no longer apply.
            { "Seven_State_Actuator_CAT", new[] { "SevenStateActuator", "SevenStateActuator2" } },
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
            // Seven_State_Actuator_CAT restored for Bearing_PnP (13-state
            // PARALLEL+ALTERNATIVE branched). Deployed verbatim from the
            // Template Library — no runtime data-driven patching.
            "Seven_State_Actuator_CAT",
        };

        // No I/O-bridge FB is deployed. PLC_RW_M262 (the old "M262IO" broker)
        // is retired: Sensor_Bool_CAT / Five_State_Actuator_CAT do direct
        // symlink I/O to the TM3 channels via their own $${PATH} macros, so
        // nothing ever instantiates PLC_RW_M262. Shipping its orphan .fbt only
        // added an unreferenced type + a dfbproj entry, so Mapper no longer
        // deploys it.

        static readonly string[] UniversalComposites = new[]
        {
            "Area", "Station", "CaSAdptrTerminator", "faultDetection"
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
            // Embedded by Jyotsna's new Five_State_Actuator_CAT as its
            // "InterlockManager" sub-FB (Type=Main:CommonInterlockEvaluator,
            // a true Basic FB). Must be deployed or EAE fails to open the
            // project: "type or namespace 'Main:CommonInterlockEvaluator'
            // does not exist".
            "CommonInterlockEvaluator",
            // Event-change handlers referenced by PLC_RW_M262's internal FB2/FB3
            // instances. Sourced from C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499.
            "changeEventProcess1", "changeEventProcess2",
            // SevenStateActuator + SevenStateActuator2 — Basic FBs embedded by
            // Seven_State_Actuator_CAT. Both must be deployed when the CAT is in
            // scope or EAE fails with "type or namespace SevenStateActuator2
            // does not exist". Restored 2026-05-21 alongside the CAT for
            // Bearing_PnP routing.
            "SevenStateActuator", "SevenStateActuator2",
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

            foreach (var name in UniversalBasics)
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in UniversalAdapters)
                DeployArtifact(libPath, "Adapter", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in UniversalComposites)
                DeployArtifact(libPath, "Composite", name, eaeProjectDir, result, isBasic: false);

            foreach (var name in UniversalHmiCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            foreach (var name in UniversalCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);
            PatchProcessFbsForRecipeAsInputVars(eaeProjectDir, result);
            PatchSensorBoolCatDstQi(eaeProjectDir, result);
            PatchFiveStateActuatorCatQi(eaeProjectDir, result);
            PatchFiveStateActuatorModeInitialValue(eaeProjectDir, result);
            // Simulator-only interface reduction. Bidirectional normalizer:
            // when SimulatorFullSystem is on it bakes the two constant interlock
            // targets onto the embedded InterlockManager FB and removes their
            // wired boundary inputs (17→15); when off it restores the wired
            // inputs. MUST run every deploy because ExtractToEae/CopyDirToEae are
            // copy-if-absent, so the deployed CAT persists across runs and both
            // buttons share it. Pairs with BuildActuatorParameters'
            // dropInterlockConstants gate (same cfg.SimulatorFullSystem flag).
            NormalizeFiveStateInterlockConstants(eaeProjectDir, cfg.SimulatorFullSystem, result);
            PatchProcess1RecipeArraySize(eaeProjectDir, result);
            PatchProcessNameStringSize(eaeProjectDir, result);
            // PatchSevenStateActuatorDataDriven removed — see CatToBasics comment.

            // MQTT event-driven state publishing (no-loss fix). Deploy-time,
            // additive, gated by cfg.MqttPublishEnabled so the stock template
            // library .cat.zip stays pristine and the hardware/sim paths are
            // untouched when MQTT is off. When on: drop the MqttStateFormatter
            // basic FB, then patch each CAT to fan an MQTT_PUBLISH off the
            // post-update state-change event (ActuatorCore.pst_out for the
            // actuator — fires on entry to all 5 states; FB1.CNF for the
            // sensor). The single shared MQTT_CONNECTION is injected into the
            // syslay by SystemLayoutInjector; PUBLISH binds to it by matching
            // ConnectionID value (no wire). See RUNBOOK.txt + the jitter gate.
            if (cfg.MqttPublishEnabled)
            {
                DeployMqttFormatter(eaeProjectDir, result);
                PatchCatMqttPublish(eaeProjectDir, "Five_State_Actuator_CAT",
                    stateEventSource: "ActuatorCore.pst_out",
                    stateDataSource: "ActuatorCore.current_state_to_process",
                    initSource: "StateHandling.INITO", cfg, result);
                PatchCatMqttPublish(eaeProjectDir, "Sensor_Bool_CAT",
                    stateEventSource: "FB1.CNF",
                    stateDataSource: "FB1.Status",
                    initSource: "StateHandling.INITO", cfg, result);
            }

            // Simulator interface reduction (the 4 Rule arrays -> 1 RuleTable).
            // The struct-literal capability was proven by the StructLiteralProbe
            // spike (now removed). Deploy the InterlockRule datatype on the sim
            // path, then run the two bidirectional normalizers that reshape the
            // CAT and the CommonInterlockEvaluator Basic FB: reduce==true (sim)
            // collapses the 4 arrays to one RuleTable; reduce==false (hardware)
            // restores the 4 arrays — so the byte-identical hardware slice is
            // untouched. Same cfg.SimulatorFullSystem flag drives the Mapper's
            // RuleTable emission (BuildActuatorParameters.dropInterlockConstants).
            if (cfg.SimulatorFullSystem)
                DeployInterlockRuleDatatype(eaeProjectDir, result);
            NormalizeFiveStateRuleArrays(eaeProjectDir, cfg.SimulatorFullSystem, result);
            NormalizeCommonInterlockEvaluatorRules(eaeProjectDir, cfg.SimulatorFullSystem, result);
            NormalizeFiveStateFaultEnables(eaeProjectDir, cfg.SimulatorFullSystem, result);
            // Process FB recipe struct: collapse the 6 overlapping recipe arrays
            // into one Recipe : ARRAY OF RecipeStep on Process1_Generic + the
            // ProcessRuntime engine (datatype, NOT a new FB). Same flag, same
            // bidirectional pattern as RuleTable; hardware keeps the 6 arrays.
            if (cfg.SimulatorFullSystem)
                DeployRecipeStepDatatype(eaeProjectDir, result);
            NormalizeProcess1RecipeArrays(eaeProjectDir, cfg.SimulatorFullSystem, result);
            NormalizeProcessRuntimeRecipeArrays(eaeProjectDir, cfg.SimulatorFullSystem, result);

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);
            // NOTE: MQTT_PUBLISH / MQTT_CONNECTION resolve via the already-
            // referenced Runtime.Base library (the deployed runtime DLL is
            // Runtime.Base and contains Runtime.NetConnectivity#MQTT_*).
            // Confirmed: EAE Projects\TrainingIIoT uses MQTT_CONNECTION with
            // ONLY a Runtime.Base reference, no separate Runtime.NetConnectivity.
            // A separate reference is a PHANTOM library ("Missing Referenced
            // Libraries: Runtime.NetConnectivity,24.1.0.22") so we do NOT add one.

            VerifyArraySizeConsistency(eaeProjectDir, result);

            // Trust-preservation guard. When an M262 sysdev already exists,
            // every device-layer write (sysdev rewrite, sysres resource-decl
            // rename, Topology Equipment JSON, network profiles) is skipped
            // to keep the controller-side trust binding intact. Application
            // content — sysres FBNetwork mirror, dfbproj registrations,
            // .hcf — still runs.
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

        /// <summary>
        /// Ensure the internal SYMLINKMULTIVARDST (FB2) inside the deployed
        /// Sensor_Bool_CAT.fbt carries Parameter QI=TRUE. Without QI the DST
        /// defaults QI=FALSE, which disables it as a live subscriber so every
        /// publish to '$${PATH}Input' is silently dropped — the sensor never
        /// registers state on the ring (runtime-confirmed root cause). The
        /// template zip already ships QI=TRUE; this is an idempotent
        /// deploy-time guard so a future zip re-swap that loses QI can't
        /// silently reintroduce the bug. Adds the parameter only when the
        /// SYMLINKMULTIVARDST FB lacks it; no-op once present. Touches only
        /// Sensor_Bool_CAT.fbt — no event/ECC/NAME1 changes.
        /// </summary>
        static void PatchSensorBoolCatDstQi(string eaeProjectDir, DeployResult result)
        {
            // Sensor_Bool_CAT deploys flat-ish under IEC61499/Sensor_Bool_CAT/.
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "Sensor_Bool_CAT", "Sensor_Bool_CAT.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "Sensor_Bool_CAT.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var dst = root.Descendants(ns + "FB").FirstOrDefault(f =>
                    ((string?)f.Attribute("Type") ?? string.Empty)
                        .StartsWith("SYMLINKMULTIVARDST", StringComparison.Ordinal));
                if (dst == null)
                {
                    result.Warnings.Add("Sensor_Bool_CAT.fbt: no SYMLINKMULTIVARDST FB found; QI guard skipped.");
                    return;
                }

                bool hasQi = dst.Elements(ns + "Parameter").Any(p =>
                    (string?)p.Attribute("Name") == "QI");
                if (hasQi)
                {
                    // Already present (idempotent no-op) — but force the value
                    // to TRUE in case a swap shipped QI=FALSE.
                    foreach (var p in dst.Elements(ns + "Parameter")
                                 .Where(p => (string?)p.Attribute("Name") == "QI"))
                        p.SetAttributeValue("Value", "TRUE");
                }
                else
                {
                    // Insert QI=TRUE right after the NAME1 parameter so it
                    // sits alongside it, matching the shipped template shape.
                    var name1 = dst.Elements(ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == "NAME1");
                    var qi = new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "QI"),
                        new System.Xml.Linq.XAttribute("Value", "TRUE"));
                    if (name1 != null) name1.AddAfterSelf(qi);
                    else dst.Add(qi);
                }

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"Sensor_Bool_CAT: ensured {(string?)dst.Attribute("Name")} " +
                    $"({(string?)dst.Attribute("Type")}) QI=TRUE");
                MapperLogger.Info(
                    "[Deploy] Sensor_Bool_CAT.fbt: SYMLINKMULTIVARDST QI=TRUE ensured " +
                    "(live subscriber enabled — publishes no longer dropped)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Sensor_Bool_CAT.fbt QI guard failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The recipe-array InputVars (StepType, CmdTargetName, CmdStateArr,
        /// Wait1Id, Wait1State, NextStep) ship at ArraySize="10" in the
        /// Process1_Generic.fbt and ProcessRuntime_Generic_v1.fbt templates,
        /// but Mapper now emits up to 20 entries (Transfer widening +
        /// auto-retract). EAE silently truncates the over-long array literal
        /// so ProcessEngine receives a partial recipe and stalls on
        /// StepType=0 (Unknown step). Force ArraySize="20" on all six in BOTH
        /// deployed FBTs to match the existing state_table ArraySize=20
        /// convention. Idempotent: SetAttributeValue replaces if present,
        /// inserts if absent; no-op once at 20. Runs on hardware AND sim
        /// paths. (PatchProcessRuntimeEngine's RecipeArrayDecls path is gated
        /// off because the new template already ships the arrays as InputVars,
        /// so this unconditional guard is the only thing that fixes the size.)
        /// </summary>
        /// <summary>
        /// Expands the <c>process_name</c> InputVar inside
        /// <c>Process1_Generic.fbt</c> from the shipped <c>STRING[15]</c> to
        /// <c>STRING[150]</c> (the size the reference <c>Process2_CAT</c>
        /// uses for the same field). Without this, any Process FB instance
        /// whose name exceeds 15 characters — e.g. <c>Assembly_Station</c>
        /// (16 chars), <c>Disassembly_Station</c> (19 chars) — fails to wire
        /// at deploy time with EAE's runtime CreateResource:
        /// <code>
        /// Unable to deploy: Deploy Full Command: Error while parsing the application
        /// CreateResource: Cannot connect parameter to data input process_name
        /// </code>
        /// because the string literal Mapper emits in the syslay Parameter
        /// (<c>Value="'Assembly_Station'"</c>) overflows the InputVar's
        /// declared size.
        ///
        /// <para>Idempotent — only touches the one VarDeclaration; channel
        /// rewrites, recipe arrays, and everything else stay untouched.
        /// Runs on hardware AND sim paths.</para>
        /// </summary>
        static void PatchProcessNameStringSize(string eaeProjectDir, DeployResult result)
        {
            var candidates = new[]
            {
                Path.Combine(eaeProjectDir, "IEC61499",
                    "Process1_Generic", "Process1_Generic.fbt"),
                Path.Combine(eaeProjectDir, "IEC61499",
                    "ProcessRuntime_Generic_v1.fbt"),
            };
            foreach (var fbtPath in candidates)
            {
                if (!File.Exists(fbtPath)) continue;
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(fbtPath,
                        System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) continue;
                    System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                    var processName = root.Descendants(ns + "VarDeclaration")
                        .FirstOrDefault(v =>
                            string.Equals((string?)v.Attribute("Name"),
                                "process_name", StringComparison.Ordinal));
                    if (processName == null) continue;

                    var typeAttr = processName.Attribute("Type");
                    var current = typeAttr?.Value ?? string.Empty;
                    // Already wide enough? STRING[150] / STRING[100] / etc.
                    if (current.StartsWith("STRING[", StringComparison.Ordinal))
                    {
                        // Extract the inner number.
                        int lb = current.IndexOf('[');
                        int rb = current.IndexOf(']', lb + 1);
                        if (rb > lb &&
                            int.TryParse(current.Substring(lb + 1, rb - lb - 1),
                                out var size) && size >= 150)
                        {
                            continue;   // already fine
                        }
                    }
                    processName.SetAttributeValue("Type", "STRING[150]");
                    doc.Save(fbtPath);
                    result.PatchesApplied.Add(
                        $"{Path.GetFileName(fbtPath)}: process_name {current} -> STRING[150] " +
                        "(supports long Process names like Assembly_Station)");
                    MapperLogger.Info(
                        $"[Deploy] {Path.GetFileName(fbtPath)}: process_name expanded to STRING[150].");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"process_name STRING-size patch failed on {Path.GetFileName(fbtPath)}: {ex.Message}");
                }
            }
        }

        static void PatchProcess1RecipeArraySize(string eaeProjectDir, DeployResult result)
        {
            string[] recipeArrays =
            {
                "StepType", "CmdTargetName", "CmdStateArr",
                "Wait1Id", "Wait1State", "NextStep",
            };

            void PatchOne(string fbtPath, string label)
            {
                if (!File.Exists(fbtPath))
                {
                    fbtPath = Directory.EnumerateFiles(
                            Path.Combine(eaeProjectDir, "IEC61499"),
                            Path.GetFileName(fbtPath), SearchOption.AllDirectories)
                        .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                        ?? string.Empty;
                    if (string.IsNullOrEmpty(fbtPath)) return;
                }
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(
                        fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) return;
                    System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                    // Single source of truth for the recipe-array capacity lives
                    // in CodeGen so the deployed .fbt ArraySize and the recipe-
                    // length guard in ProcessRecipeArrayGenerator.Generate() can
                    // never drift apart.
                    var target = CodeGen.Translation.Process.ProcessRecipeArrayGenerator
                        .RecipeArraySize.ToString();
                    int changed = 0;
                    foreach (var vd in root.Descendants(ns + "VarDeclaration"))
                    {
                        var nm = (string?)vd.Attribute("Name") ?? string.Empty;
                        if (Array.IndexOf(recipeArrays, nm) < 0) continue;
                        if ((string?)vd.Attribute("ArraySize") == target) continue;
                        vd.SetAttributeValue("ArraySize", target);
                        changed++;
                    }
                    if (changed > 0)
                    {
                        doc.Save(fbtPath);
                        result.PatchesApplied.Add(
                            $"{label}: forced ArraySize={target} on {changed} recipe array InputVar(s)");
                        MapperLogger.Info(
                            $"[Deploy] {label}: recipe arrays ArraySize -> {target} ({changed} changed)");
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{label} recipe ArraySize guard failed: {ex.Message}");
                }
            }

            PatchOne(Path.Combine(eaeProjectDir, "IEC61499", "Process1_Generic",
                "Process1_Generic.fbt"), "Process1_Generic.fbt");
            PatchOne(Path.Combine(eaeProjectDir, "IEC61499",
                "ProcessRuntime_Generic_v1.fbt"), "ProcessRuntime_Generic_v1.fbt");
        }

        /// <summary>
        /// Same QI=FALSE-by-default bug as Sensor_Bool_CAT, in
        /// Five_State_Actuator_CAT. Its internal SYMLINKMULTIVARDST
        /// (Name "Inputs", subscribes $${PATH}athome/atwork) and
        /// SYMLINKMULTIVARSRC (Name "Output", publishes
        /// $${PATH}OutputToHome/OutputToWork) both default QI=FALSE — the
        /// DST rejects every TM3DI16 publish so the actuator never sees its
        /// sensors, and the SRC never writes TM3DQ16 so the solenoid stays
        /// cold even when the ECC commands it (rig-confirmed). Force QI=TRUE
        /// on BOTH: insert after NAME1 if absent, flip FALSE→TRUE if present.
        /// Idempotent deploy-time guard against a future zip re-swap losing
        /// QI. Runs on hardware AND sim paths. Five_State_Actuator_CAT only —
        /// no event/ECC/NAME1 changes; Sensor_Bool_CAT untouched here.
        /// </summary>
        static void PatchFiveStateActuatorCatQi(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499",
                "Five_State_Actuator_CAT", "Five_State_Actuator_CAT.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                    ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var targets = root.Descendants(ns + "FB").Where(f =>
                {
                    var t = (string?)f.Attribute("Type") ?? string.Empty;
                    return t.StartsWith("SYMLINKMULTIVARDST", StringComparison.Ordinal)
                        || t.StartsWith("SYMLINKMULTIVARSRC", StringComparison.Ordinal);
                }).ToList();

                if (targets.Count == 0)
                {
                    result.Warnings.Add(
                        "Five_State_Actuator_CAT.fbt: no SYMLINKMULTIVARDST/SRC FB found; QI guard skipped.");
                    return;
                }

                foreach (var fb in targets)
                {
                    bool hasQi = fb.Elements(ns + "Parameter").Any(p =>
                        (string?)p.Attribute("Name") == "QI");
                    if (hasQi)
                    {
                        foreach (var p in fb.Elements(ns + "Parameter")
                                     .Where(p => (string?)p.Attribute("Name") == "QI"))
                            p.SetAttributeValue("Value", "TRUE");
                    }
                    else
                    {
                        var name1 = fb.Elements(ns + "Parameter")
                            .FirstOrDefault(p => (string?)p.Attribute("Name") == "NAME1");
                        var qi = new System.Xml.Linq.XElement(ns + "Parameter",
                            new System.Xml.Linq.XAttribute("Name", "QI"),
                            new System.Xml.Linq.XAttribute("Value", "TRUE"));
                        if (name1 != null) name1.AddAfterSelf(qi);
                        else fb.Add(qi);
                    }
                    result.PatchesApplied.Add(
                        $"Five_State_Actuator_CAT: ensured {(string?)fb.Attribute("Name")} " +
                        $"({(string?)fb.Attribute("Type")}) QI=TRUE");
                }

                doc.Save(fbt);
                MapperLogger.Info(
                    $"[Deploy] Five_State_Actuator_CAT.fbt: QI=TRUE ensured on " +
                    $"{targets.Count} SYMLINKMULTIVAR FB(s) (DST subscriber + SRC publisher enabled)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT.fbt QI guard failed: {ex.Message}");
            }
        }

        // ============================================================
        // MQTT event-driven state publishing — deploy-time injection.
        // All three methods below are no-ops unless cfg.MqttPublishEnabled
        // (the caller already gates them). Strictly additive to the CAT
        // FB-network; idempotent (skip if MqttPub already present).
        // ============================================================

        /// <summary>
        /// Drops the <c>MqttStateFormatter</c> basic FB (INT state → STRING
        /// payload via <c>INT_TO_STRING</c>) into the deployed
        /// <c>IEC61499/</c> folder if absent. The CAT patch wires this FB
        /// between the actuator/sensor state output and MQTT_PUBLISH.Payload1.
        /// Idempotent.
        /// </summary>
        static void DeployMqttFormatter(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dst = Path.Combine(eaeProjectDir, "IEC61499", "MqttStateFormatter.fbt");
                if (File.Exists(dst)) { result.PatchesApplied.Add("MqttStateFormatter.fbt already present"); return; }
                File.WriteAllText(dst, MqttStateFormatterFbt);
                result.PatchesApplied.Add("MqttStateFormatter.fbt deployed (INT→STRING payload)");
                MapperLogger.Info("[Deploy][MQTT] MqttStateFormatter.fbt written to IEC61499/");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"MqttStateFormatter deploy failed: {ex.Message}");
            }
        }

        // Embedded InterlockRule datatype (4 INT fields) for the simulator
        // interface reduction — the 4 parallel Rule arrays collapse to one
        // RuleTable : InterlockRule[10]. Hand-authored WITHOUT EAE's nxtDataType
        // signature; verified accepted by the StructLiteralProbe spike (EAE
        // regenerates the signature on load).
        const string InterlockRuleDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"InterlockRule\" Comment=\"One interlock rule as a struct: FromState/ToState/SourceID/BlockedState\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/24/2026\" Remarks=\"array-of-struct packaging of the 4 Rule arrays\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"FromState\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"ToState\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"SourceID\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"BlockedState\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        /// <summary>
        /// Deploy the InterlockRule datatype (simulator path only) and register
        /// it, so EAE resolves the <c>RuleTable : InterlockRule[10]</c> inputs the
        /// normalizers add to Five_State_Actuator_CAT and CommonInterlockEvaluator.
        /// Idempotent (copy-if-absent + dedup registration).
        /// </summary>
        static void DeployInterlockRuleDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "InterlockRule.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, InterlockRuleDt);
                if (!result.DataTypesDeployed.Contains("InterlockRule"))
                    result.DataTypesDeployed.Add("InterlockRule");
                result.PatchesApplied.Add("InterlockRule.dt deployed + registered (sim RuleTable struct)");
                MapperLogger.Info("[Deploy] InterlockRule.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"InterlockRule.dt deploy failed: {ex.Message}");
            }
        }

        // The four parallel interlock-rule arrays that collapse into one
        // RuleTable : InterlockRule[10]. Order matches struct field order.
        static readonly string[] RuleArrayNames =
            { "RuleFromState", "RuleToState", "RuleSourceID", "RuleBlockedState" };
        static readonly Dictionary<string, string> RuleArrayToField = new()
        {
            ["RuleFromState"] = "FromState",
            ["RuleToState"] = "ToState",
            ["RuleSourceID"] = "SourceID",
            ["RuleBlockedState"] = "BlockedState",
        };

        // Remove matching elements via instance Remove() (no
        // IEnumerable<XElement>.Remove() extension — this file has no
        // `using System.Xml.Linq`). Returns true if anything was removed.
        static bool RemoveElems(IEnumerable<System.Xml.Linq.XElement>? src,
            Func<System.Xml.Linq.XElement, bool> pred)
        {
            if (src == null) return false;
            var hits = src.Where(pred).ToList();
            foreach (var h in hits) h.Remove();
            return hits.Count > 0;
        }

        /// <summary>
        /// Simulator interface reduction on Five_State_Actuator_CAT: collapse the
        /// four parallel Rule arrays (face InputVar + INIT With + boundary Input +
        /// DataConnection to InterlockManager) into a single
        /// RuleTable : InterlockRule[10]. Bidirectional + idempotent like
        /// NormalizeFiveStateInterlockConstants (deployer is copy-if-absent, so the
        /// .fbt persists and must be reshaped to match the flag each deploy).
        /// reduce==true collapses to RuleTable; reduce==false restores the four
        /// arrays so the byte-identical hardware slice is untouched.
        /// </summary>
        static void NormalizeFiveStateRuleArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Five_State_Actuator_CAT.fbt not found; RuleTable normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add("Five_State_Actuator_CAT.fbt: missing InterfaceList/FBNetwork; RuleTable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");

                bool changed = false;

                if (reduce)
                {
                    foreach (var a in RuleArrayNames)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == a);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == a);
                    }
                    if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == "RuleTable"))
                    {
                        var rc = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleCount");
                        var rt = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Type", "InterlockRule"),
                            new System.Xml.Linq.XAttribute("ArraySize", "10"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        if (rc != null) rc.AddBeforeSelf(rt); else inputVars.Add(rt);
                        changed = true;
                    }
                    if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable"))
                    {
                        initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "RuleTable")));
                        changed = true;
                    }
                    if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == "RuleTable"))
                    {
                        var pin = new System.Xml.Linq.XElement(ns + "Input",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("x", "1320"),
                            new System.Xml.Linq.XAttribute("y", "1852"),
                            new System.Xml.Linq.XAttribute("Type", "Data"));
                        var lastInput = net.Elements(ns + "Input").LastOrDefault();
                        if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                        changed = true;
                    }
                    if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == "RuleTable"))
                    {
                        dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                            new System.Xml.Linq.XAttribute("Source", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Destination", "InterlockManager.RuleTable")));
                        changed = true;
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "RuleTable");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "RuleTable");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "RuleTable");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "RuleTable");

                    var coords = new Dictionary<string, (string X, string Y)>
                    {
                        ["RuleFromState"] = ("1320", "2052"),
                        ["RuleToState"] = ("1320", "1752"),
                        ["RuleSourceID"] = ("1300", "1852"),
                        ["RuleBlockedState"] = ("1320", "1952"),
                    };
                    foreach (var a in RuleArrayNames)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("Type", "INT"),
                                new System.Xml.Linq.XAttribute("ArraySize", "10")));
                            changed = true;
                        }
                        if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == a))
                        {
                            var (x, y) = coords[a];
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("x", x),
                                new System.Xml.Linq.XAttribute("y", y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == a))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", a),
                                new System.Xml.Linq.XAttribute("Destination", "InterlockManager." + a)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Five_State_Actuator_CAT: 4 Rule arrays -> RuleTable (sim interface reduced)"
                        : "Five_State_Actuator_CAT: RuleTable -> 4 Rule arrays (hardware interface)");
                    MapperLogger.Info($"[Deploy] Five_State_Actuator_CAT RuleTable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT RuleTable normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on the CommonInterlockEvaluator Basic FB:
        /// collapse the four Rule arrays into RuleTable : InterlockRule[10] across
        /// the InputVars, the three event &lt;With&gt; lists (REQ_WORK1/WORK2/HOME),
        /// AND the Evaluate ST (RuleFromState[i] -> RuleTable[i].FromState, etc).
        /// Bidirectional + idempotent. reduce==false restores the four arrays so
        /// hardware is byte-identical. Logic is unchanged either way — the same
        /// numbers feed the Evaluate loop, just read as struct fields.
        /// </summary>
        static void NormalizeCommonInterlockEvaluatorRules(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "CommonInterlockEvaluator.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("CommonInterlockEvaluator.fbt not found; RuleTable normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var basic = root.Element(ns + "BasicFB");
                if (iface == null || basic == null)
                {
                    result.Warnings.Add("CommonInterlockEvaluator.fbt: missing InterfaceList/BasicFB; RuleTable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");

                bool changed = false;

                if (reduce)
                {
                    foreach (var a in RuleArrayNames)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                    if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == "RuleTable"))
                    {
                        var rc = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleCount");
                        var rt = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Type", "InterlockRule"),
                            new System.Xml.Linq.XAttribute("ArraySize", "10"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        if (rc != null) rc.AddAfterSelf(rt); else inputVars.Add(rt);
                        changed = true;
                    }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => RuleArrayNames.Contains((string?)w.Attribute("Var")))) continue;
                        foreach (var a in RuleArrayNames)
                            changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable"))
                        {
                            var rcWith = ev.Elements(ns + "With").FirstOrDefault(w => (string?)w.Attribute("Var") == "RuleCount");
                            var rtWith = new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "RuleTable"));
                            if (rcWith != null) rcWith.AddAfterSelf(rtWith); else ev.Add(rtWith);
                            changed = true;
                        }
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "RuleTable");
                    if (inputVars != null)
                        foreach (var a in RuleArrayNames)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                            {
                                inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("ArraySize", "10"),
                                    new System.Xml.Linq.XAttribute("Name", a),
                                    new System.Xml.Linq.XAttribute("Type", "INT")));
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "RuleTable");
                        foreach (var a in RuleArrayNames)
                            if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                            {
                                ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                                changed = true;
                            }
                    }
                }

                // Evaluate ST: swap array indexing <-> struct member access.
                var stEl = basic.Elements(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Evaluate")?
                    .Element(ns + "ST");
                if (stEl != null)
                {
                    var st = stEl.Value;
                    var before = st;
                    foreach (var a in RuleArrayNames)
                    {
                        var arr = a + "[i]";
                        var str = "RuleTable[i]." + RuleArrayToField[a];
                        st = reduce ? st.Replace(arr, str) : st.Replace(str, arr);
                    }
                    if (st != before)
                    {
                        stEl.ReplaceNodes(new System.Xml.Linq.XCData(st));
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "CommonInterlockEvaluator: 4 Rule arrays -> RuleTable + Evaluate ST rewritten (sim)"
                        : "CommonInterlockEvaluator: RuleTable -> 4 Rule arrays + Evaluate ST restored (hardware)");
                    MapperLogger.Info($"[Deploy] CommonInterlockEvaluator RuleTable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"CommonInterlockEvaluator RuleTable normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on Five_State_Actuator_CAT: drop the two
        /// derived fault-enable inputs (enableToWorkFaultTimeout /
        /// enableToHomeFaultTimeout). The Mapper always sets each equal to its
        /// sensor-fitted flag, and the CAT's FB17/FB14 already AND the enable with
        /// the SAME sensor-fitted input — so re-pointing FB17.IN2/FB14.IN2 at the
        /// sensor-fitted lines gives AND(fitted, fitted) = fitted: identical
        /// behaviour, two fewer pins, no new FB and no event-timing change.
        /// Bidirectional + idempotent. reduce==false restores the inputs so the
        /// hardware path is byte-identical.
        /// </summary>
        static void NormalizeFiveStateFaultEnables(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var map = new[]
            {
                new { Enable = "enableToWorkFaultTimeout", Dest = "FB17.IN2", Fitted = "WorkSensorFitted", X = "1280", Y = "5772" },
                new { Enable = "enableToHomeFaultTimeout", Dest = "FB14.IN2", Fitted = "HomeSensorFitted", X = "1260", Y = "5292" },
            };

            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Five_State_Actuator_CAT.fbt not found; fault-enable normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add("Five_State_Actuator_CAT.fbt: missing InterfaceList/FBNetwork; fault-enable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");

                bool changed = false;

                foreach (var m in map)
                {
                    // Re-point the AND-gate IN2: reduce -> sensor-fitted, restore -> enable input.
                    var wanted = reduce ? m.Fitted : m.Enable;
                    var conn = dataConns?.Elements(ns + "Connection")
                        .FirstOrDefault(c => (string?)c.Attribute("Destination") == m.Dest);
                    if (conn != null && (string?)conn.Attribute("Source") != wanted)
                    {
                        conn.SetAttributeValue("Source", wanted);
                        changed = true;
                    }

                    if (reduce)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"),
                            v => (string?)v.Attribute("Name") == m.Enable);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"),
                            w => (string?)w.Attribute("Var") == m.Enable);
                        changed |= RemoveElems(net.Elements(ns + "Input"),
                            i => (string?)i.Attribute("Name") == m.Enable);
                    }
                    else
                    {
                        if (inputVars != null &&
                            !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == m.Enable))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", m.Enable),
                                new System.Xml.Linq.XAttribute("Type", "BOOL")));
                            changed = true;
                        }
                        if (initEvent != null &&
                            !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == m.Enable))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                                new System.Xml.Linq.XAttribute("Var", m.Enable)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == m.Enable))
                        {
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", m.Enable),
                                new System.Xml.Linq.XAttribute("x", m.X),
                                new System.Xml.Linq.XAttribute("y", m.Y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Five_State_Actuator_CAT: fault-enable inputs derived from sensor-fitted (sim — 2 inputs removed)"
                        : "Five_State_Actuator_CAT: fault-enable inputs restored as wired inputs (hardware)");
                    MapperLogger.Info($"[Deploy] Five_State_Actuator_CAT fault-enable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT fault-enable normalize failed: {ex.Message}");
            }
        }

        // The six parallel recipe arrays that collapse into one Recipe : ARRAY OF
        // RecipeStep. Struct field name == array name so the engine ST rewrite is
        // a clean 1:1 ("StepType[CurrentStep]" -> "Recipe[CurrentStep].StepType").
        static readonly (string Name, string Type)[] RecipeArrays = new[]
        {
            ("StepType", "INT"),
            ("CmdTargetName", "STRING[15]"),
            ("CmdStateArr", "INT"),
            ("Wait1Id", "INT"),
            ("Wait1State", "INT"),
            ("NextStep", "INT"),
        };

        // Embedded RecipeStep datatype (one recipe row as a struct; mixed INT +
        // STRING[15]). Hand-authored without nxtDataType — EAE regenerates it.
        const string RecipeStepDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"RecipeStep\" Comment=\"One recipe step as a struct: StepType/CmdTargetName/CmdStateArr/Wait1Id/Wait1State/NextStep\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/24/2026\" Remarks=\"array-of-struct packaging of the 6 recipe arrays\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"StepType\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"CmdTargetName\" Type=\"STRING[15]\" />\r\n" +
            "    <VarDeclaration Name=\"CmdStateArr\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Wait1Id\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Wait1State\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"NextStep\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        /// <summary>
        /// Deploy the RecipeStep datatype (simulator path) + register it, so EAE
        /// resolves the Recipe : RecipeStep[] inputs the recipe normalizers add to
        /// Process1_Generic and ProcessRuntime_Generic_v1. Idempotent.
        /// </summary>
        static void DeployRecipeStepDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "RecipeStep.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, RecipeStepDt);
                if (!result.DataTypesDeployed.Contains("RecipeStep"))
                    result.DataTypesDeployed.Add("RecipeStep");
                result.PatchesApplied.Add("RecipeStep.dt deployed + registered (sim Recipe struct)");
                MapperLogger.Info("[Deploy] RecipeStep.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"RecipeStep.dt deploy failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on Process1_Generic (composite): collapse
        /// the 6 recipe arrays (face InputVar + INIT With + boundary Input +
        /// DataConnection to ProcessEngine) into one Recipe : RecipeStep[].
        /// Bidirectional + idempotent; reduce==false restores the 6 arrays.
        /// </summary>
        static void NormalizeProcess1RecipeArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Process1_Generic.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Process1_Generic.fbt not found; recipe-struct normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add("Process1_Generic.fbt: missing InterfaceList/FBNetwork; recipe normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");
                var size = CodeGen.Translation.Process.ProcessRecipeArrayGenerator.RecipeArraySize.ToString();

                bool changed = false;

                if (reduce)
                {
                    foreach (var (nm, _) in RecipeArrays)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == nm);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == nm);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == nm);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == nm);
                    }
                    if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == "Recipe"))
                    {
                        inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "Recipe"),
                            new System.Xml.Linq.XAttribute("Type", "RecipeStep"),
                            new System.Xml.Linq.XAttribute("ArraySize", size),
                            new System.Xml.Linq.XAttribute("Namespace", "Main")));
                        changed = true;
                    }
                    if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Recipe"))
                    { initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Recipe"))); changed = true; }
                    if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == "Recipe"))
                    {
                        var pin = new System.Xml.Linq.XElement(ns + "Input",
                            new System.Xml.Linq.XAttribute("Name", "Recipe"),
                            new System.Xml.Linq.XAttribute("x", "300"),
                            new System.Xml.Linq.XAttribute("y", "1300"),
                            new System.Xml.Linq.XAttribute("Type", "Data"));
                        var last = net.Elements(ns + "Input").LastOrDefault();
                        if (last != null) last.AddAfterSelf(pin); else net.Add(pin);
                        changed = true;
                    }
                    if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == "Recipe"))
                    {
                        dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                            new System.Xml.Linq.XAttribute("Source", "Recipe"),
                            new System.Xml.Linq.XAttribute("Destination", "ProcessEngine.Recipe")));
                        changed = true;
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Recipe");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Recipe");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "Recipe");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "Recipe");
                    var coords = new Dictionary<string, (string X, string Y)>
                    {
                        ["StepType"] = ("300", "1300"), ["CmdTargetName"] = ("300", "1750"),
                        ["CmdStateArr"] = ("300", "2200"), ["Wait1Id"] = ("300", "2650"),
                        ["Wait1State"] = ("300", "3100"), ["NextStep"] = ("300", "3550"),
                    };
                    foreach (var (nm, ty) in RecipeArrays)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == nm))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", nm),
                                new System.Xml.Linq.XAttribute("Type", ty),
                                new System.Xml.Linq.XAttribute("ArraySize", size)));
                            changed = true;
                        }
                        if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == nm))
                        { initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", nm))); changed = true; }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == nm))
                        {
                            var (x, y) = coords[nm];
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", nm),
                                new System.Xml.Linq.XAttribute("x", x),
                                new System.Xml.Linq.XAttribute("y", y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var last = net.Elements(ns + "Input").LastOrDefault();
                            if (last != null) last.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == nm))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", nm),
                                new System.Xml.Linq.XAttribute("Destination", "ProcessEngine." + nm)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Process1_Generic: 6 recipe arrays -> Recipe struct (sim)"
                        : "Process1_Generic: Recipe struct -> 6 recipe arrays (hardware)");
                    MapperLogger.Info($"[Deploy] Process1_Generic recipe normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Process1_Generic recipe-struct normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on ProcessRuntime_Generic_v1 (Basic FB):
        /// collapse the 6 recipe arrays across InputVars, the INIT event Withs, AND
        /// every algorithm's ST ("StepType[CurrentStep]" ->
        /// "Recipe[CurrentStep].StepType", etc.). Bidirectional + idempotent;
        /// reduce==false restores the 6 arrays. Logic unchanged.
        /// </summary>
        static void NormalizeProcessRuntimeRecipeArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not found; recipe-struct normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var basic = root.Element(ns + "BasicFB");
                if (iface == null || basic == null)
                {
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: missing InterfaceList/BasicFB; recipe normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");
                var size = CodeGen.Translation.Process.ProcessRecipeArrayGenerator.RecipeArraySize.ToString();

                bool changed = false;

                if (reduce)
                {
                    foreach (var (nm, _) in RecipeArrays)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == nm);
                    if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == "Recipe"))
                    {
                        inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "Recipe"),
                            new System.Xml.Linq.XAttribute("Type", "RecipeStep"),
                            new System.Xml.Linq.XAttribute("ArraySize", size),
                            new System.Xml.Linq.XAttribute("Namespace", "Main")));
                        changed = true;
                    }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => RecipeArrays.Any(a => a.Name == (string?)w.Attribute("Var")))) continue;
                        foreach (var (nm, _) in RecipeArrays)
                            changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == nm);
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Recipe"))
                        { ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Recipe"))); changed = true; }
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Recipe");
                    if (inputVars != null)
                        foreach (var (nm, ty) in RecipeArrays)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == nm))
                            {
                                inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("Name", nm),
                                    new System.Xml.Linq.XAttribute("Type", ty),
                                    new System.Xml.Linq.XAttribute("ArraySize", size)));
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Recipe")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Recipe");
                        foreach (var (nm, _) in RecipeArrays)
                            if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == nm))
                            { ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", nm))); changed = true; }
                    }
                }

                // ST rewrite across every algorithm.
                foreach (var alg in basic.Elements(ns + "Algorithm"))
                {
                    var stEl = alg.Element(ns + "ST");
                    if (stEl == null) continue;
                    var st = stEl.Value;
                    var before = st;
                    foreach (var (nm, _) in RecipeArrays)
                    {
                        var arr = nm + "[CurrentStep]";
                        var str = "Recipe[CurrentStep]." + nm;
                        st = reduce ? st.Replace(arr, str) : st.Replace(str, arr);
                    }
                    if (st != before) { stEl.ReplaceNodes(new System.Xml.Linq.XCData(st)); changed = true; }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "ProcessRuntime_Generic_v1: 6 recipe arrays -> Recipe struct + ST rewritten (sim)"
                        : "ProcessRuntime_Generic_v1: Recipe struct -> 6 recipe arrays + ST restored (hardware)");
                    MapperLogger.Info($"[Deploy] ProcessRuntime_Generic_v1 recipe normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime_Generic_v1 recipe-struct normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Additive deploy-time patch: fan an <c>MQTT_PUBLISH</c> off the
        /// CAT's POST-UPDATE state-change event so every transition is
        /// published the same scan it happens — before any OPC UA / WebSocket
        /// sampler runs (those sit downstream of the 200/100 ms sampling floor
        /// and alias brief states out; this does not). Nothing existing is
        /// removed: the state event keeps all its current targets and we only
        /// add one fan-out (EAE allows multi-fan-out from an event output).
        /// </summary>
        /// <param name="stateEventSource">Post-update event that carries the new
        /// state. Actuator: <c>ActuatorCore.pst_out</c> (fires on entry to all
        /// five states, after the algorithm writes current_state_to_process).
        /// Sensor: <c>FB1.CNF</c> (fires after Status is written).</param>
        /// <param name="stateDataSource">INT state value to publish
        /// (<c>ActuatorCore.current_state_to_process</c> / <c>FB1.Status</c>).</param>
        /// <param name="initSource">Existing INITO event to seed MqttFmt/MqttPub INIT.</param>
        static void PatchCatMqttPublish(string eaeProjectDir, string catName,
            string stateEventSource, string stateDataSource, string initSource,
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

                // Idempotent: skip if already patched.
                if (net.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == "MqttPub"))
                {
                    result.PatchesApplied.Add($"{catName}: MQTT publish already present (skipped)");
                    return;
                }

                // Bump the FB IDCounter so the two new FBs get unique IDs.
                var idAttr = root.Elements(ns + "Attribute")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                int idc = 1000;
                if (idAttr != null && int.TryParse((string?)idAttr.Attribute("Value"), out var parsed)) idc = parsed;
                int fmtId = idc, pubId = idc + 1;
                if (idAttr != null) idAttr.SetAttributeValue("Value", (idc + 2).ToString());

                string Q(string s) => "'" + s + "'";   // ST string literal

                var fmtFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", fmtId),
                    new System.Xml.Linq.XAttribute("Name", "MqttFmt"),
                    new System.Xml.Linq.XAttribute("Type", "MqttStateFormatter"),
                    new System.Xml.Linq.XAttribute("x", "8000"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));

                // EAE stores a configured GENERIC FB as a HASHED VARIANT in
                // namespace "Main" — confirmed by the working MQTT_SUBSCRIBE in
                // TrainingIIoT: Type="MQTT_SUBSCRIBE_115480E69E664F878"
                // Namespace="Main" + InterfaceParams="Runtime.NetConnectivity#CNTX:=1".
                // The hash 115480E69E664F878 is derived from the InterfaceParams
                // (CNTX:=1) and is identical for PUBLISH (EAE itself looked for
                // MQTT_PUBLISH_115480E69E664F878.gfbt). Emitting the plain base
                // type name in namespace Runtime.NetConnectivity is what caused
                // ERR_NO_SUCH_TYPE. NOTE: if channel count ever changes from 1,
                // this hash changes too.
                const string MqttPublishVariant = "MQTT_PUBLISH_115480E69E664F878";
                var pubFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", pubId),
                    new System.Xml.Linq.XAttribute("Name", "MqttPub"),
                    new System.Xml.Linq.XAttribute("Type", MqttPublishVariant),
                    new System.Xml.Linq.XAttribute("x", "8600"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));
                // MQTT_PUBLISH is a GENERIC multi-channel FB. Its numbered
                // channel ports (Topic1/Payload1/QoS1/Retain1 + event PUBLISH1)
                // do NOT exist until the channel count is set via this
                // InterfaceParams attribute — exactly like the SYMLINKMULTIVAR
                // FBs already in this CAT. CNTX:=1 = one publish channel.
                // Confirmed against working MQTT_SUBSCRIBE / MQTT_CONNECTION
                // instances in EAE Projects\TrainingIIoT
                // (Configuration.GenericFBType.InterfaceParams =
                // "Runtime.NetConnectivity#CNTX:=1"). Omitting it is what
                // produced "Port 'MqttPub.Topic1' does not exist".
                pubFb.Add(new System.Xml.Linq.XElement(ns + "Attribute",
                    new System.Xml.Linq.XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new System.Xml.Linq.XAttribute("Value", "Runtime.NetConnectivity#CNTX:=1")));
                void P(System.Xml.Linq.XElement fb, string n, string v) =>
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", n),
                        new System.Xml.Linq.XAttribute("Value", v)));
                P(pubFb, "QI", "TRUE");
                // ConnectionID is a STRING (the working MQTT_CONNECTION/SUBSCRIBE
                // use $ConnectionID='SoftdPAC'); both the connection and every
                // publisher must carry the SAME string to bind.
                P(pubFb, "ConnectionID", Q(cfg.MqttConnectionId.ToString()));
                P(pubFb, "RootPath", Q("$${PATH}"));   // EAE resolves per-instance; falls back to Mapper-stamped topic if unresolved
                P(pubFb, "Topic1", Q("state"));
                P(pubFb, "QoS1", cfg.MqttQoS.ToString());
                P(pubFb, "Retain1", cfg.MqttRetain ? "TRUE" : "FALSE");

                // Insert the two FBs after the last existing <FB>.
                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) { lastFb.AddAfterSelf(pubFb); lastFb.AddAfterSelf(fmtFb); }
                else { net.Add(fmtFb); net.Add(pubFb); }

                // Ensure connection containers exist.
                var ec = net.Element(ns + "EventConnections");
                if (ec == null) { ec = new System.Xml.Linq.XElement(ns + "EventConnections"); net.Add(ec); }
                var dc = net.Element(ns + "DataConnections");
                if (dc == null) { dc = new System.Xml.Linq.XElement(ns + "DataConnections"); net.Add(dc); }

                void Conn(System.Xml.Linq.XElement parent, string s, string d) =>
                    parent.Add(new System.Xml.Linq.XElement(ns + "Connection",
                        new System.Xml.Linq.XAttribute("Source", s),
                        new System.Xml.Linq.XAttribute("Destination", d)));

                // Events (additive fan-out — stateEventSource keeps its targets).
                Conn(ec, stateEventSource, "MqttFmt.REQ");
                Conn(ec, "MqttFmt.CNF", "MqttPub.PUBLISH1");
                Conn(ec, initSource, "MqttFmt.INIT");
                Conn(ec, initSource, "MqttPub.INIT");
                // Data.
                Conn(dc, stateDataSource, "MqttFmt.state");
                Conn(dc, "MqttFmt.payload", "MqttPub.Payload1");

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"{catName}: MQTT publish injected (fan {stateEventSource} → MqttFmt → MqttPub.PUBLISH1, " +
                    $"ConnectionID={cfg.MqttConnectionId}, Topic=$${{PATH}}state)");
                MapperLogger.Info($"[Deploy][MQTT] {catName}.fbt: MQTT_PUBLISH wired off {stateEventSource}");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName} MQTT publish patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator-only interface reduction for Five_State_Actuator_CAT. The
        /// InterlockManager constants TargetWork1State (=2, AtWork) and
        /// TargetHomeState (=4, AtHome end-of-cycle latch) are IDENTICAL on every
        /// actuator instance — <c>BuildActuatorParameters</c> hardcodes them. On
        /// the simulator path we delete those two boundary inputs from the CAT
        /// and bake the constants straight onto the embedded InterlockManager FB
        /// as <c>&lt;Parameter&gt;</c>, shrinking the wired instance interface
        /// from 17 to 15 WITHOUT changing any runtime value.
        ///
        /// This is a BIDIRECTIONAL NORMALIZER, not a one-way strip, because the
        /// template deployer copies artefacts only when absent
        /// (ExtractToEae/CopyDirToEae are copy-if-missing). The deployed CAT is
        /// therefore a single persistent file shared by BOTH the Test Simulator
        /// and Test Runtime buttons. <paramref name="reduce"/>==true strips and
        /// bakes; ==false restores the wired inputs and removes the baked params.
        /// Call it on every deploy with reduce = cfg.SimulatorFullSystem so the
        /// CAT shape always matches the <c>&lt;Parameter&gt;</c> set
        /// BuildActuatorParameters emits for the same flag. Idempotent both ways.
        /// </summary>
        static void NormalizeFiveStateInterlockConstants(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            // Values mirror BuildActuatorParameters EXACTLY (2 = AtWork settled,
            // 4 = AtHome end-of-cycle latch) so the reduction changes the
            // interface only, never behaviour. Re-add coordinates mirror the
            // stock template (EAE recomputes layout on load regardless).
            var consts = new[]
            {
                new { Name = "TargetWork1State", Value = "2", X = "1380", Y = "2092" },
                new { Name = "TargetHomeState",  Value = "4", X = "1380", Y = "2192" },
            };

            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add(
                    "Five_State_Actuator_CAT.fbt not found; interlock-constant normalize skipped.");
                return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(
                    fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add(
                        "Five_State_Actuator_CAT.fbt: missing InterfaceList/FBNetwork; normalize skipped.");
                    return;
                }

                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?
                    .Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");
                var interlock = net.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == "InterlockManager");
                if (interlock == null)
                {
                    result.Warnings.Add(
                        "Five_State_Actuator_CAT.fbt: InterlockManager FB not found; normalize skipped.");
                    return;
                }

                // Remove matching elements via instance Remove() (no
                // IEnumerable<XElement>.Remove() extension — this file has no
                // `using System.Xml.Linq`). Returns true if anything was removed.
                bool RemoveWhere(IEnumerable<System.Xml.Linq.XElement>? src,
                    Func<System.Xml.Linq.XElement, bool> pred)
                {
                    if (src == null) return false;
                    var hits = src.Where(pred).ToList();
                    foreach (var h in hits) h.Remove();
                    return hits.Count > 0;
                }

                bool changed = false;

                foreach (var c in consts)
                {
                    if (reduce)
                    {
                        changed |= RemoveWhere(inputVars?.Elements(ns + "VarDeclaration"),
                            v => (string?)v.Attribute("Name") == c.Name);
                        changed |= RemoveWhere(initEvent?.Elements(ns + "With"),
                            w => (string?)w.Attribute("Var") == c.Name);
                        changed |= RemoveWhere(net.Elements(ns + "Input"),
                            i => (string?)i.Attribute("Name") == c.Name);
                        changed |= RemoveWhere(dataConns?.Elements(ns + "Connection"),
                            x => (string?)x.Attribute("Source") == c.Name);

                        bool hasParam = interlock.Elements(ns + "Parameter")
                            .Any(p => (string?)p.Attribute("Name") == c.Name);
                        if (!hasParam)
                        {
                            interlock.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                                new System.Xml.Linq.XAttribute("Name", c.Name),
                                new System.Xml.Linq.XAttribute("Value", c.Value)));
                            changed = true;
                        }
                    }
                    else
                    {
                        // Restore the wired interface (hardware / Button 3).
                        changed |= RemoveWhere(interlock.Elements(ns + "Parameter"),
                            p => (string?)p.Attribute("Name") == c.Name);

                        if (inputVars != null &&
                            !inputVars.Elements(ns + "VarDeclaration")
                                .Any(v => (string?)v.Attribute("Name") == c.Name))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", c.Name),
                                new System.Xml.Linq.XAttribute("Type", "INT")));
                            changed = true;
                        }
                        if (initEvent != null &&
                            !initEvent.Elements(ns + "With")
                                .Any(w => (string?)w.Attribute("Var") == c.Name))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                                new System.Xml.Linq.XAttribute("Var", c.Name)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input")
                                .Any(i => (string?)i.Attribute("Name") == c.Name))
                        {
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", c.Name),
                                new System.Xml.Linq.XAttribute("x", c.X),
                                new System.Xml.Linq.XAttribute("y", c.Y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin);
                            else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null &&
                            !dataConns.Elements(ns + "Connection")
                                .Any(x => (string?)x.Attribute("Source") == c.Name))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", c.Name),
                                new System.Xml.Linq.XAttribute("Destination",
                                    "InterlockManager." + c.Name)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Five_State_Actuator_CAT: interlock constants baked onto InterlockManager " +
                          "(sim interface reduced — TargetWork1State=2, TargetHomeState=4)"
                        : "Five_State_Actuator_CAT: interlock constants restored as wired inputs (hardware interface)");
                    MapperLogger.Info(
                        $"[Deploy] Five_State_Actuator_CAT interlock-constant normalize: reduce={reduce}");
                }
                else
                {
                    result.PatchesApplied.Add(
                        $"Five_State_Actuator_CAT: interlock interface already {(reduce ? "reduced" : "wired")} (no change)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Five_State_Actuator_CAT interlock-constant normalize failed: {ex.Message}");
            }
        }

        // Embedded MqttStateFormatter basic FB (v1: bare INT state → STRING).
        // v1.1 extends the Fmt algorithm to JSON {actuator,state,ts}.
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
            "      <VarDeclaration Name=\"payload\" Type=\"STRING\" Comment=\"MQTT payload (state as text)\" />\r\n" +
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
            "    <Algorithm Name=\"Fmt\" Comment=\"INT state to STRING payload\"><ST><![CDATA[payload := INT_TO_STRING(state);]]></ST></Algorithm>\r\n" +
            "  </BasicFB>\r\n" +
            "</FBType>\r\n";

        /// <summary>
        /// Same Mode=0-by-default class of bug as ProcessRuntime_Generic_v1's
        /// Mode/CycleType (fixed via InitialValue=1). The FiveStateActuator
        /// basic FB's "mode" InputVar has no InitialValue, so it powers up 0.
        /// Every AtHomeInit/AtWork exit ECTransition requires mode = 1/2/3
        /// (auto/cycle/setup); no mode_event fires at boot, so with mode=0
        /// the actuator ECC is stuck in AtHomeInit forever (rig-confirmed).
        /// Force the mode InputVar's InitialValue=1 so every actuator
        /// instance powers up in auto mode. Idempotent deploy-time guard
        /// (insert the attribute if absent, set to 1 if present). Runs every
        /// deploy so a future zip re-swap losing it is auto-fixed.
        /// FiveStateActuator basic FB only — no CAT/ECC/recipe changes.
        /// </summary>
        static void PatchFiveStateActuatorModeInitialValue(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "FiveStateActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "FiveStateActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                // The "mode" VarDeclaration inside <InputVars>.
                var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
                var modeVar = inputVars?
                    .Elements(ns + "VarDeclaration")
                    .FirstOrDefault(v => (string?)v.Attribute("Name") == "mode");
                if (modeVar == null)
                {
                    result.Warnings.Add(
                        "FiveStateActuator.fbt: no 'mode' InputVar found; Mode-default guard skipped.");
                    return;
                }

                var iv = (string?)modeVar.Attribute("InitialValue");
                if (iv == "1") return; // already correct — idempotent no-op
                modeVar.SetAttributeValue("InitialValue", "1");
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    "FiveStateActuator: forced mode InputVar InitialValue=1 (powers up in auto mode)");
                MapperLogger.Info(
                    "[Deploy] FiveStateActuator.fbt: mode InputVar InitialValue=1 " +
                    "(actuator ECC no longer stuck in AtHomeInit at boot)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"FiveStateActuator.fbt Mode-default guard failed: {ex.Message}");
            }
        }

        // Seven_State_Actuator_CAT data-driven patch removed 2026-05-21.

        static void PatchKnownArraySizeBugs(string eaeProjectDir, DeployResult result)
        {
            var fbtPath = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbtPath)) return;

            var text = File.ReadAllText(fbtPath);
            const string oldDecl =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"1\" />";
            const string newDecl =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"20\" />";

            if (text.Contains(newDecl)) return;
            if (!text.Contains(oldDecl))
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: state_table declaration not found in expected " +
                    "shape (ArraySize=\"1\"). Skipping ArraySize patch — verify by hand.");
                return;
            }
            File.WriteAllText(fbtPath, text.Replace(oldDecl, newDecl));
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1.state_table ArraySize 1 -> 20");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.state_table ArraySize 1 -> 20");

            // Schneider template-side typo in check_wait: the right-hand-side reads
            // Wait1Id where it should read Wait1State. As shipped, no recipe of any
            // shape can ever satisfy a wait — the comparison checks state equals the
            // component's registry id, never the desired wait state. Patch to the
            // semantically correct comparison every deploy.
            const string brokenCheckWait =
                "WaitSatisfied := state_table[Wait1Id[CurrentStep]].state = Wait1Id[CurrentStep];";
            const string fixedCheckWait =
                "WaitSatisfied := state_table[Wait1Id[CurrentStep]].state = Wait1State[CurrentStep];";
            text = File.ReadAllText(fbtPath);
            if (text.Contains(brokenCheckWait))
            {
                File.WriteAllText(fbtPath, text.Replace(brokenCheckWait, fixedCheckWait));
                result.PatchesApplied.Add("ProcessRuntime_Generic_v1.check_wait typo Wait1Id -> Wait1State");
                MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.check_wait typo (Wait1Id -> Wait1State on RHS)");
            }
        }

        /// <summary>
        /// Phase 1 recipe-arrays-as-InputVars patch. Mutates the deployed
        /// ProcessRuntime_Generic_v1.fbt and Process1_Generic.fbt so the recipe arrays
        /// (StepType, CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep) are
        /// exposed as InputVars on both the engine FB and its outer composite, and the
        /// engine's initialize algorithm no longer hardcodes the 3-step Pusher demo
        /// recipe. The Mapper then writes the per-Process recipe as syslay Parameter
        /// values on the Process1 instance via SystemLayoutInjector.BuildProcessFbParameters.
        ///
        /// Idempotent: re-deploying skips files that already declare the array InputVars.
        /// </summary>
        static void PatchProcessFbsForRecipeAsInputVars(string eaeProjectDir, DeployResult result)
        {
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            var enginePath = Path.Combine(iec, "ProcessRuntime_Generic_v1.fbt");
            var compositePath = Path.Combine(iec, "Process1_Generic", "Process1_Generic.fbt");

            try
            {
                if (File.Exists(enginePath))
                {
                    PatchProcessRuntimeEngine(enginePath, result);
                    PatchProcessRuntimeEccDeadEnd(enginePath, result);
                    PatchProcessRuntimeStartBypass(enginePath, result);
                    PatchProcessRuntimeEndSequenceNoOp(enginePath, result);
                }
                else
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not deployed; recipe-as-input patch skipped.");

                if (File.Exists(compositePath))
                    PatchProcess1GenericComposite(compositePath, result);
                else
                    result.Warnings.Add("Process1_Generic.fbt not deployed; recipe-as-input patch skipped.");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Recipe-as-input FBT patch failed: {ex.Message}");
            }
        }

        static readonly (string Name, string Type, string ArraySize, string? Comment)[] RecipeArrayDecls = new[]
        {
            ("StepType",      "INT",         "64", "Phase 1: recipe arrays now external. 1=command, 2=wait, 9=end. Mapper writes literal at instance level via Process1_Generic."),
            ("CmdTargetName", "STRING[15]",  "64", (string?)null),
            ("CmdStateArr",   "INT",         "64", (string?)null),
            ("Wait1Id",       "INT",         "64", (string?)null),
            ("Wait1State",    "INT",         "64", (string?)null),
            ("NextStep",      "INT",         "64", (string?)null),
        };

        /// <summary>
        /// Silence EAE's WRN_ECC_DEAD_END on the <c>END</c> state of
        /// ProcessRuntime_Generic_v1 by adding a self-transition
        /// <c>END → END</c> with <c>Condition="1"</c> — the same shape EAE
        /// itself uses for the existing <c>WAIT_STEP → WAIT_STEP</c> loop.
        /// Runtime behaviour is unchanged (END already terminates the
        /// sequence; this just makes the dead-end explicit so the compiler
        /// stops warning). Idempotent: skips if an END→END transition is
        /// already present. Runs outside PatchProcessRuntimeEngine's
        /// recipe-array idempotency gate so it applies even on FBTs that
        /// were already recipe-patched.
        /// </summary>
        static void PatchProcessRuntimeEccDeadEnd(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
            if (ecc == null)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: <ECC> not found; END dead-end patch skipped.");
                return;
            }

            bool hasEndState = ecc.Elements(ns + "ECState")
                .Any(s => (string?)s.Attribute("Name") == "END");
            if (!hasEndState)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: ECState END not found; dead-end patch skipped.");
                return;
            }

            // Idempotency: already has an END self-transition?
            bool alreadyLooped = ecc.Elements(ns + "ECTransition").Any(t =>
                (string?)t.Attribute("Source") == "END" &&
                (string?)t.Attribute("Destination") == "END");
            if (alreadyLooped) return;

            // Append after the last ECTransition, mirroring the self-closed
            // WAIT_STEP→WAIT_STEP shape (no Attribute child, has x/y).
            var lastTrans = ecc.Elements(ns + "ECTransition").LastOrDefault();
            var endState = ecc.Elements(ns + "ECState")
                .First(s => (string?)s.Attribute("Name") == "END");
            var ex = (string?)endState.Attribute("x") ?? "1983.655";
            var ey = (string?)endState.Attribute("y") ?? "968.8892";
            var loop = new System.Xml.Linq.XElement(ns + "ECTransition",
                new System.Xml.Linq.XAttribute("Source", "END"),
                new System.Xml.Linq.XAttribute("Destination", "END"),
                new System.Xml.Linq.XAttribute("Condition", "1"),
                new System.Xml.Linq.XAttribute("x", ex),
                new System.Xml.Linq.XAttribute("y", ey));
            if (lastTrans != null) lastTrans.AddAfterSelf(loop);
            else ecc.Add(loop);

            doc.Save(fbtPath);
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1: added END->END self-transition (WRN_ECC_DEAD_END fix)");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.fbt END dead-end (added END->END Condition=1)");
        }

        /// <summary>
        /// Remove the START -> IDLE1 bypass transition whose Condition is
        /// "Mode = 1 AND CycleType &lt;&gt; 0". Mode/CycleType both default
        /// to InitialValue=1, so that guard is TRUE at power-up and the ECC
        /// walks START -> IDLE1 directly, skipping the INIT state — so
        /// initializeinit never runs and the WITH-sampled recipe arrays are
        /// never sampled before LoadStep executes (Audit 1 root cause).
        /// After this patch the only outgoing transition from START is
        /// START -> INIT (Condition "INIT"). Idempotent: matches the
        /// Destination + Condition rather than a fixed element, and is a
        /// no-op once the transition is gone. Runs outside
        /// PatchProcessRuntimeEngine's recipe-array idempotency gate so it
        /// applies even to already-recipe-patched FBTs.
        /// </summary>
        static void PatchProcessRuntimeStartBypass(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
            if (ecc == null)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: <ECC> not found; START-bypass patch skipped.");
                return;
            }

            // Normalise whitespace/entity differences: EAE writes the XML
            // text as Condition="Mode = 1 AND CycleType &lt;&gt; 0"; once
            // parsed the attribute value is the literal
            //   Mode = 1 AND CycleType <> 0
            // Match on Source=START, Destination=IDLE1 and a Condition that,
            // with all spaces stripped, contains "CycleType<>0" — robust to
            // spacing variants.
            var bypass = ecc.Elements(ns + "ECTransition").Where(t =>
                (string?)t.Attribute("Source") == "START" &&
                (string?)t.Attribute("Destination") == "IDLE1" &&
                ((string?)t.Attribute("Condition") ?? string.Empty)
                    .Replace(" ", string.Empty)
                    .Contains("CycleType<>0", StringComparison.Ordinal)).ToList();

            int startOutgoing = ecc.Elements(ns + "ECTransition")
                .Count(t => (string?)t.Attribute("Source") == "START");

            if (bypass.Count == 0)
            {
                // Already removed (idempotent no-op). Still emit the
                // verification line so a regression is visible at deploy.
                result.PatchesApplied.Add(
                    $"ProcessRuntime_Generic_v1: START-bypass already absent; " +
                    $"START has {startOutgoing} outgoing transition(s)");
                MapperLogger.Info(
                    $"[Deploy] ProcessRuntime_Generic_v1.fbt: START-bypass not present; " +
                    $"START outgoing transitions = {startOutgoing}");
                return;
            }

            foreach (var t in bypass) t.Remove();
            doc.Save(fbtPath);

            int startOutgoingAfter = ecc.Elements(ns + "ECTransition")
                .Count(t => (string?)t.Attribute("Source") == "START");
            var remaining = ecc.Elements(ns + "ECTransition")
                .Where(t => (string?)t.Attribute("Source") == "START")
                .Select(t => $"START->{(string?)t.Attribute("Destination")} [{(string?)t.Attribute("Condition")}]")
                .ToList();

            result.PatchesApplied.Add(
                $"ProcessRuntime_Generic_v1: removed {bypass.Count} START->IDLE1 bypass " +
                $"transition(s); START now has {startOutgoingAfter} outgoing: " +
                string.Join(" ; ", remaining));
            MapperLogger.Info(
                $"[Deploy] ProcessRuntime_Generic_v1.fbt: removed START->IDLE1 'Mode=1 AND CycleType<>0' " +
                $"bypass; START outgoing transitions now = {startOutgoingAfter} ({string.Join(" ; ", remaining)})");
        }

        /// <summary>
        /// Pin CurrentStep at the END row's index by making EndSequence a
        /// no-op on the step pointer. The shipped EndSequence is implemented
        /// identically to AdvanceStep — it executes
        ///   CurrentStep := NextStep[CurrentStep];
        ///   CurrentStepType := StepType[CurrentStep];
        /// which, combined with the END -> END dead-end self-loop we add in
        /// PatchProcessRuntimeEccDeadEnd, walks CurrentStep through the
        /// recipe forever once END is reached (the END row's NextStep is 0,
        /// so the first re-entry wraps CurrentStep back to 0 and every
        /// subsequent state_change tick advances it again). The Watch then
        /// shows CurrentStep cycling and CurrentStepType flipping between
        /// recipe values even though no command is reissued (END has no
        /// transition back to ISSUE_CMD). Replacing the EndSequence body
        /// with a no-op preserves CurrentStep and CurrentStepType so END
        /// renders as a stable resting state in the Watch. Idempotent via
        /// a marker comment; emits the patch as a CDATA section so it
        /// matches the surrounding Algorithm bodies in the FBT.
        /// </summary>
        static void PatchProcessRuntimeEndSequenceNoOp(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var alg = root.Descendants(ns + "Algorithm")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "EndSequence");
            if (alg == null)
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: Algorithm EndSequence not found; END no-op patch skipped.");
                return;
            }

            var st = alg.Element(ns + "ST");
            if (st == null)
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: EndSequence has no <ST>; END no-op patch skipped.");
                return;
            }

            const string noOpMarker = "(* END no-op: CurrentStep pinned *)";
            if ((st.Value ?? string.Empty).Contains(noOpMarker, StringComparison.Ordinal))
            {
                // Already patched — idempotent no-op.
                result.PatchesApplied.Add(
                    "ProcessRuntime_Generic_v1: EndSequence no-op already in place (CurrentStep pinned)");
                return;
            }

            string noOpBody =
                noOpMarker + "\r\n" +
                "PreviousStepText := ThisStepText;\r\n" +
                "ThisStepText := 'Resting in END';\r\n" +
                "NextStepText := 'Recipe complete';";

            st.ReplaceNodes(new System.Xml.Linq.XCData(noOpBody));

            doc.Save(fbtPath);
            result.PatchesApplied.Add(
                "ProcessRuntime_Generic_v1: EndSequence replaced with no-op (CurrentStep pinned at END row, stops Watch cycling)");
            MapperLogger.Info(
                "[Deploy] Patched ProcessRuntime_Generic_v1.fbt EndSequence (no-op so CurrentStep stays at the END row once reached)");
        }

        static void PatchProcessRuntimeEngine(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var initEvent = root.Descendants(ns + "Event")
                .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
            var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
            var internalVars = root.Descendants(ns + "InternalVars").FirstOrDefault();
            var initAlgo = root.Descendants(ns + "Algorithm")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "initializeinit");

            if (initEvent == null || inputVars == null || initAlgo == null)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: expected INIT event / InputVars / initializeinit not found; recipe-as-input patch skipped.");
                return;
            }

            var existingInputVarNames = new HashSet<string>(
                inputVars.Elements(ns + "VarDeclaration")
                    .Select(v => (string?)v.Attribute("Name") ?? string.Empty),
                StringComparer.Ordinal);

            // Idempotency check: if every recipe array is already an InputVar, this FBT was patched before.
            if (RecipeArrayDecls.All(d => existingInputVarNames.Contains(d.Name)))
                return;

            // 1. Promote recipe arrays from InternalVars to InputVars (move if present, else add fresh).
            foreach (var decl in RecipeArrayDecls)
            {
                var existingInternal = internalVars?
                    .Elements(ns + "VarDeclaration")
                    .FirstOrDefault(v => (string?)v.Attribute("Name") == decl.Name);
                existingInternal?.Remove();

                if (!existingInputVarNames.Contains(decl.Name))
                {
                    var newDecl = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                        new System.Xml.Linq.XAttribute("Name", decl.Name),
                        new System.Xml.Linq.XAttribute("Type", decl.Type),
                        new System.Xml.Linq.XAttribute("ArraySize", decl.ArraySize));
                    if (decl.Comment != null)
                        newDecl.Add(new System.Xml.Linq.XAttribute("Comment", decl.Comment));
                    inputVars.Add(newDecl);
                }
            }

            // 2. Add <With Var=...> entries on INIT for the new arrays (skip duplicates).
            var existingWithVars = new HashSet<string>(
                initEvent.Elements(ns + "With")
                    .Select(w => (string?)w.Attribute("Var") ?? string.Empty),
                StringComparer.Ordinal);
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingWithVars.Contains(decl.Name)) continue;
                initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                    new System.Xml.Linq.XAttribute("Var", decl.Name)));
            }

            // 3. Strip the recipe-population block from initializeinit. The replacement only
            //    initialises engine state — recipe values now arrive via InputVars.
            var stElement = initAlgo.Descendants(ns + "ST").FirstOrDefault();
            if (stElement != null)
            {
                stElement.RemoveNodes();
                stElement.Add(new System.Xml.Linq.XCData(
                    "CurrentStep := 0;\n" +
                    "CurrentStepType := 0;\n" +
                    "WaitSatisfied := FALSE;\n" +
                    "PusherID := 0;\n\n" +
                    "cmd_target_name := '';\n" +
                    "cmd_state := 0;\n\n" +
                    "PreviousStepText := '';\n" +
                    "ThisStepText := 'Initialised';\n" +
                    "NextStepText := '';"));
            }

            doc.Save(fbtPath);
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1: recipe arrays InternalVars->InputVars; INIT With clauses added; initializeinit stripped of recipe ST");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.fbt for recipe-as-input architecture");
        }

        static void PatchProcess1GenericComposite(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var initEvent = root.Descendants(ns + "Event")
                .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
            var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
            var fbNetwork = root.Descendants(ns + "FBNetwork").FirstOrDefault();
            var dataConnections = fbNetwork?.Descendants(ns + "DataConnections").FirstOrDefault();

            if (initEvent == null || inputVars == null || fbNetwork == null || dataConnections == null)
            {
                result.Warnings.Add("Process1_Generic.fbt: expected INIT event / InputVars / FBNetwork / DataConnections not found; recipe-as-input patch skipped.");
                return;
            }

            var existingInputVarNames = new HashSet<string>(
                inputVars.Elements(ns + "VarDeclaration")
                    .Select(v => (string?)v.Attribute("Name") ?? string.Empty),
                StringComparer.Ordinal);

            // Idempotency check.
            if (RecipeArrayDecls.All(d => existingInputVarNames.Contains(d.Name)))
                return;

            // 1. Add 6 InputVars on the outer composite.
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingInputVarNames.Contains(decl.Name)) continue;
                var newDecl = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                    new System.Xml.Linq.XAttribute("Name", decl.Name),
                    new System.Xml.Linq.XAttribute("Type", decl.Type),
                    new System.Xml.Linq.XAttribute("ArraySize", decl.ArraySize));
                if (decl.Comment != null)
                    newDecl.Add(new System.Xml.Linq.XAttribute("Comment", decl.Comment));
                inputVars.Add(newDecl);
            }

            // 2. <With Var=...> on INIT.
            var existingWithVars = new HashSet<string>(
                initEvent.Elements(ns + "With")
                    .Select(w => (string?)w.Attribute("Var") ?? string.Empty),
                StringComparer.Ordinal);
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingWithVars.Contains(decl.Name)) continue;
                initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                    new System.Xml.Linq.XAttribute("Var", decl.Name)));
            }

            // 3. Graphic <Input Name="..." Type="Data"/> stubs inside FBNetwork (sibling of FBs and connections).
            var existingInputStubs = new HashSet<string>(
                fbNetwork.Elements(ns + "Input")
                    .Select(i => (string?)i.Attribute("Name") ?? string.Empty),
                StringComparer.Ordinal);
            int stubY = 3200;
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingInputStubs.Contains(decl.Name)) { stubY += 100; continue; }
                fbNetwork.Add(new System.Xml.Linq.XElement(ns + "Input",
                    new System.Xml.Linq.XAttribute("Name", decl.Name),
                    new System.Xml.Linq.XAttribute("x", "200"),
                    new System.Xml.Linq.XAttribute("y", stubY.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new System.Xml.Linq.XAttribute("Type", "Data")));
                stubY += 100;
            }

            // 4. DataConnections: outer-input -> ProcessEngine.<input>.
            var existingDataConns = new HashSet<(string, string)>(
                dataConnections.Elements(ns + "Connection")
                    .Select(c => ((string?)c.Attribute("Source") ?? string.Empty,
                                  (string?)c.Attribute("Destination") ?? string.Empty)));
            foreach (var decl in RecipeArrayDecls)
            {
                var src = decl.Name;
                var dst = "ProcessEngine." + decl.Name;
                if (existingDataConns.Contains((src, dst))) continue;
                dataConnections.Add(new System.Xml.Linq.XElement(ns + "Connection",
                    new System.Xml.Linq.XAttribute("Source", src),
                    new System.Xml.Linq.XAttribute("Destination", dst)));
            }

            doc.Save(fbtPath);
            result.PatchesApplied.Add("Process1_Generic: 6 array InputVars + INIT With clauses + Input stubs + DataConnections to ProcessEngine");
            MapperLogger.Info("[Deploy] Patched Process1_Generic.fbt for recipe-as-input architecture");
        }

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

        static void DeployArtifact(string libPath, string subfolder, string name,
            string eaeProjectDir, DeployResult result, bool isBasic, bool isCat = false)
        {
            var folder = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(folder))
            {
                result.Warnings.Add($"Library subfolder missing: {subfolder}");
                return;
            }

            var zipPath = FindArtifactZip(folder, name);
            if (zipPath != null)
            {
                ExtractToEae(zipPath, eaeProjectDir, result);
            }
            else
            {
                var dirPath = FindArtifactDir(folder, name);
                if (dirPath != null)
                {
                    CopyDirToEae(dirPath, eaeProjectDir, result);
                }
                else
                {
                    result.Warnings.Add($"Artifact not found: {subfolder}/{name}");
                    return;
                }
            }

            if (isCat) result.CATsDeployed.Add(name);
            else if (string.Equals(subfolder, "Adapter", StringComparison.OrdinalIgnoreCase))
                result.AdaptersDeployed.Add(name);
            else if (string.Equals(subfolder, "Composite", StringComparison.OrdinalIgnoreCase))
                result.CompositesDeployed.Add(name);
            else if (isBasic) result.BasicFBsDeployed.Add(name);
        }

        static string? FindArtifactZip(string folder, string name)
        {
            // ".subcats.zip" files are wrapper bundles (a zip whose only entry
            // is the real .cat.zip) — NEVER a directly-deployable artifact.
            // Extracting one leaves the CAT folder uncreated while the dfbproj
            // still registers <name>\<name>.cfg, so EAE dies with
            // "Could not find a part of the path …\<name>.cfg". Exclude them.
            //
            // Then order candidates by filename DESCENDING so that when
            // several dated versions coexist (e.g. right after a template
            // library update) the NEWEST is chosen deterministically instead
            // of relying on Directory.GetFiles' filesystem/alphabetical order
            // (which silently picked the wrong zip after the old .cat.zip was
            // moved aside).
            var zips = Directory.GetFiles(folder, "*.zip")
                .Where(f => !Path.GetFileName(f)
                    .Contains(".subcats.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var f in zips)
            {
                var fn = Path.GetFileName(f);
                if (fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fn, name + ".zip", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            foreach (var f in zips)
            {
                if (Path.GetFileName(f).Contains(name + ".", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            return null;
        }

        static string? FindArtifactDir(string folder, string name)
        {
            foreach (var d in Directory.GetDirectories(folder))
            {
                var dn = Path.GetFileName(d);
                if (dn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dn, name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        static void CopyDirToEae(string sourceDir, string eaeProjectDir, DeployResult result)
        {
            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var parts = rel.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    rel = string.Join("/", parts.Skip(1));

                var targetPath = Path.Combine(eaeProjectDir, rel);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
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

        static void ExtractToEae(string zipPath, string eaeProjectDir, DeployResult result)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };
            string? prefixToStrip = null;

            var firstFile = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (firstFile != null)
            {
                var parts = firstFile.FullName.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    prefixToStrip = parts[0] + "/";
            }

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var relativePath = entry.FullName;
                if (prefixToStrip != null && relativePath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(prefixToStrip.Length);

                var targetPath = Path.Combine(eaeProjectDir, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!File.Exists(targetPath))
                {
                    entry.ExtractToFile(targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
            }
        }

        static void GenerateCfgFiles(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            foreach (var cat in result.CATsDeployed)
            {
                var catDir = Path.Combine(iec61499Dir, cat);
                var cfgPath = Path.Combine(catDir, $"{cat}.cfg");
                if (File.Exists(cfgPath)) continue;
                if (!Directory.Exists(catDir)) continue;

                var hmi = cat + "_HMI";
                var cfg = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<CAT xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" Name=""{cat}"" CATFile=""{cat}\{cat}.fbt"" SymbolDefFile=""..\HMI\{cat}\{cat}.def.cs"" SymbolEventFile=""..\HMI\{cat}\{cat}.event.cs"" DesignFile=""..\HMI\{cat}\{cat}.Design.resx"" xmlns=""http://www.nxtcontrol.com/IEC61499.xsd"">
  <HMIInterface Name=""IThis"" FileName=""{cat}\{hmi}.fbt"" UsedInCAT=""true"" Usage=""Private"">
    <Symbol Name=""sDefault"" FileName=""..\HMI\{cat}\{cat}_sDefault.cnv.cs"">
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.Designer.cs</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.resx</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.xml</DependentFiles>
    </Symbol>
  </HMIInterface>
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.opcua.xml"" />
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.opcua.xml"" />
  <HWConfiguration xsi:nil=""true"" />
</CAT>";
                File.WriteAllText(cfgPath, cfg);
                result.FilesExtracted++;
                MapperLogger.Info($"[Deploy] Generated {cat}.cfg");

                var metaPath = Path.Combine(catDir, $"{hmi}.meta.xml");
                if (!File.Exists(metaPath))
                {
                    File.WriteAllBytes(metaPath, Array.Empty<byte>());
                    result.FilesExtracted++;
                    MapperLogger.Info($"[Deploy] Created empty {hmi}.meta.xml placeholder");
                }
            }
        }

        static void RegisterInDfbproj(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec61499Dir)) return;

            var dfbproj = Directory.GetFiles(iec61499Dir, "*.dfbproj").FirstOrDefault();
            if (dfbproj == null) return;

            foreach (var cat in result.CATsDeployed)
                DfbprojRegistrar.RegisterCat(dfbproj, cat);

            foreach (var basic in result.BasicFBsDeployed)
                DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt", "Basic");

            foreach (var adapter in result.AdaptersDeployed)
                DfbprojRegistrar.RegisterBasicFb(dfbproj, adapter + ".adp", "Adapter");

            foreach (var composite in result.CompositesDeployed)
                DfbprojRegistrar.RegisterBasicFb(dfbproj, composite + ".fbt", "Composite");

            foreach (var dt in result.DataTypesDeployed)
                DfbprojRegistrar.RegisterDataType(dfbproj, $@"DataType\{dt}.dt");

            DfbprojRegistrar.RegisterReference(dfbproj, "SE.DPAC",   "24.1.0.33");
            DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppBase", "24.1.0.21");
            DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoTMx",   "24.1.0.19");

            DfbprojRegistrar.SweepIec61499Folder(dfbproj, iec61499Dir);

            File.SetLastWriteTime(dfbproj, DateTime.Now);
            MapperLogger.Info($"[Deploy] dfbproj updated: {Path.GetFileName(dfbproj)}");
        }

        static string ReadSysdevId(string sysdevPath)
        {
            if (string.IsNullOrEmpty(sysdevPath) || !File.Exists(sysdevPath)) return string.Empty;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(sysdevPath);
                return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        static string? DeriveEaeProjectDir(MapperConfig cfg)
        {
            var syslayPath = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(syslayPath)) return null;

            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent != null && Directory.Exists(Path.Combine(dir, "..")))
                {
                    var iec = Path.Combine(dir);
                    var checkDir = dir;
                    while (checkDir != null)
                    {
                        if (Directory.GetFiles(checkDir, "*.dfbproj").Any())
                            return Path.GetDirectoryName(checkDir);
                        checkDir = Path.GetDirectoryName(checkDir);
                    }
                }
                dir = Path.GetDirectoryName(dir);
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
