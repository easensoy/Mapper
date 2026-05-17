using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    public static class TemplateLibraryDeployer
    {
        static readonly Dictionary<string, string[]> CatDependencies = new()
        {
            { "Five_State_Actuator_CAT", new[] { "FiveStateActuator" } },
            { "Sensor_Bool_CAT",         new[] { "Sensor_Bool" } },
            { "Actuator_Fault_CAT",      new[] { "FaultLatch" } },
            { "Robot_Task_CAT",          new[] { "Robot_Task_Core" } },
            { "Seven_State_Actuator_CAT",new[] { "SevenStateActuator2" } },
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
            "Five_State_Actuator_CAT", "Sensor_Bool_CAT", "Process1_Generic"
        };

        // PLC_RW_M262 is a pure I/O bridge basic FB (no HMI faceplate, no _HMI sub-FB),
        // so it deploys as a top-level .fbt rather than a CAT folder structure. Listing
        // it as a CAT made RegisterInDfbproj write '<None Include="PLC_RW_M262\PLC_RW_M262.cfg">'
        // — a folder path that never exists — which broke EAE on project open with
        // "Could not find a part of the path 'PLC_RW_M262\PLC_RW_M262.cfg'".
        // Treated as a Basic from now on; its .fbt is registered flat in dfbproj.
        static readonly string[] UniversalIoFbs = new[] { "PLC_RW_M262" };

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
            // Event-change handlers referenced by PLC_RW_M262's internal FB2/FB3
            // instances. Sourced from C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499.
            "changeEventProcess1", "changeEventProcess2",
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

            // I/O-bridge basics now live under Template Library/Basic/<name>/IEC61499/
            // (was CAT/ historically; the folder moved to Basic/ to match its
            // declared type). Source path was wrong → DeployArtifact warned
            // "Artifact not found: CAT/PLC_RW_M262", the .fbt never landed in
            // the .dfbproj on a clean deploy, and every symbolic link inside
            // PLC_RW_M262 rendered red in EAE's Hardware Configurator.
            foreach (var name in UniversalIoFbs)
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);
            PatchProcessFbsForRecipeAsInputVars(eaeProjectDir, result);
            PatchSensorBoolCatDstQi(eaeProjectDir, result);

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);

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
            foreach (var f in Directory.GetFiles(folder, "*.zip"))
            {
                var fn = Path.GetFileName(f);
                if (fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fn, name + ".zip", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            foreach (var f in Directory.GetFiles(folder, "*.zip"))
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
            {
                // PLC_RW_M262 is wired as a basic in UniversalIoFbs (so the
                // .fbt deploys flat, no .cfg sibling), but its .fbt body
                // contains an internal FBNetwork (FB2 = changeEventProcess1)
                // — that makes it a Composite to EAE's compiler. Registering
                // it as Basic produces "Type 'Main.PLC_RW_M262' is undefined"
                // because the dfbproj entry has the wrong IEC61499Type.
                var iecType = string.Equals(basic, "PLC_RW_M262", StringComparison.Ordinal)
                    ? "Composite" : "Basic";
                DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt", iecType);
            }

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