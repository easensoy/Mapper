using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    internal static class ProcessRuntimeTemplatePatcher
    {
        // process_name InputVar must be STRING[150] or deploy fails ("Cannot connect parameter to data input process_name").
        internal static void PatchProcessNameStringSize(string eaeProjectDir, DeployResult result)
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
                    if (current.StartsWith("STRING[", StringComparison.Ordinal))
                    {
                        int lb = current.IndexOf('[');
                        int rb = current.IndexOf(']', lb + 1);
                        if (rb > lb &&
                            int.TryParse(current.Substring(lb + 1, rb - lb - 1),
                                out var size) && size >= 150)
                        {
                            continue;
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

        static readonly string[] EngineDebugVars = { "CurrentStep", "CurrentStepType", "WaitSatisfied" };

        internal static void NormalizeProcessEngineDebugWatch(string eaeProjectDir, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not found; engine debug-watch normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var eventOutputs = root.Element(ns + "InterfaceList")?.Element(ns + "EventOutputs");
                if (eventOutputs == null)
                {
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: EventOutputs not found; engine debug-watch normalize skipped.");
                    return;
                }

                bool changed = false;
                foreach (var evName in new[] { "CMDREQ", "SCNF" })
                {
                    var ev = eventOutputs.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == evName);
                    if (ev == null) continue;
                    foreach (var v in EngineDebugVars)
                    {
                        foreach (var w in ev.Elements(ns + "With")
                                     .Where(w => (string?)w.Attribute("Var") == v).ToList())
                        { w.Remove(); changed = true; }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("ProcessRuntime_Generic_v1: debug-watch WITH entries removed (hardware)");
                    MapperLogger.Info("[Deploy] Engine debug-watch normalize: debug WITH entries stripped");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Engine debug-watch normalize failed: {ex.Message}");
            }
        }

        // Struct field name == array name so the ST rewrite is 1:1 ("StepType[CurrentStep]" -> "Recipe[CurrentStep].StepType").
        static readonly (string Name, string Type)[] RecipeArrays = new[]
        {
            ("StepType", "INT"),
            ("CmdTargetName", "STRING[150]"),
            ("CmdStateArr", "INT"),
            ("Wait1Id", "INT"),
            ("Wait1State", "INT"),
            ("NextStep", "INT"),
        };

        const string RecipeStepDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"RecipeStep\" Comment=\"One recipe step as a struct: StepType/CmdTargetName/CmdStateArr/Wait1Id/Wait1State/NextStep\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/24/2026\" Remarks=\"array-of-struct packaging of the 6 recipe arrays\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"StepType\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"CmdTargetName\" Type=\"STRING[150]\" />\r\n" +
            "    <VarDeclaration Name=\"CmdStateArr\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Wait1Id\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Wait1State\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"NextStep\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        internal static void DeployRecipeStepDatatype(string eaeProjectDir, DeployResult result)
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

        // Recipe-struct collapse on Process1_Generic (gated by UseRecipeStruct); reduce==false restores the 6 arrays.
        internal static void NormalizeProcess1RecipeArrays(
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

        // Recipe-struct collapse on ProcessRuntime_Generic_v1 incl. every algorithm's ST (gated by UseRecipeStruct); reduce==false restores the 6 arrays.
        internal static void NormalizeProcessRuntimeRecipeArrays(
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

        // END->END dead-end self-loop (run-once) silences WRN_ECC_DEAD_END; cyclic routes END->ADVANCE instead.
        internal static void PatchProcessRuntimeEccDeadEnd(string fbtPath, bool cyclic, DeployResult result)
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

            string dest = cyclic ? "ADVANCE" : "END";

            var endTrans = ecc.Elements(ns + "ECTransition")
                .Where(t => (string?)t.Attribute("Source") == "END").ToList();
            if (endTrans.Count == 1 && (string?)endTrans[0].Attribute("Destination") == dest) return;
            foreach (var et in endTrans) et.Remove();

            var lastTrans = ecc.Elements(ns + "ECTransition").LastOrDefault();
            var endState = ecc.Elements(ns + "ECState")
                .First(s => (string?)s.Attribute("Name") == "END");
            var ex = (string?)endState.Attribute("x") ?? "1983.655";
            var ey = (string?)endState.Attribute("y") ?? "968.8892";
            var loop = new System.Xml.Linq.XElement(ns + "ECTransition",
                new System.Xml.Linq.XAttribute("Source", "END"),
                new System.Xml.Linq.XAttribute("Destination", dest),
                new System.Xml.Linq.XAttribute("Condition", "1"),
                new System.Xml.Linq.XAttribute("x", ex),
                new System.Xml.Linq.XAttribute("y", ey));
            if (lastTrans != null) lastTrans.AddAfterSelf(loop);
            else ecc.Add(loop);

            doc.Save(fbtPath);
            result.PatchesApplied.Add(
                $"ProcessRuntime_Generic_v1: END -> {dest} " +
                (cyclic ? "(CYCLIC restart: AdvanceStep wraps CurrentStep to the END row's NextStep=0)"
                        : "(run-once dead-end: engine parks at END)"));
            MapperLogger.Info(
                $"[Deploy] Patched ProcessRuntime_Generic_v1.fbt END -> {dest} ({(cyclic ? "cyclic loop" : "park")})");
        }

        // START's only outgoing transition must be START->INIT (remove the Mode-guard IDLE1 bypass, else INIT never runs).
        internal static void PatchProcessRuntimeStartBypass(string fbtPath, DeployResult result)
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

            // Match Source=START, Destination=IDLE1, Condition (spaces stripped) containing "CycleType<>0".
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

        // Make EndSequence a no-op on the step pointer so CurrentStep stays pinned at the END row.
        internal static void PatchProcessRuntimeEndSequenceNoOp(string fbtPath, DeployResult result)
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


        internal static void PatchProcess1RecipeArraySize(string eaeProjectDir, DeployResult result)
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

        internal static void PatchKnownArraySizeBugs(string eaeProjectDir, DeployResult result)
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

            // Fix the shipped check_wait typo (RHS Wait1Id -> Wait1State) or no wait can ever be satisfied.
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

        internal static void PatchProcessRuntimeCompatibility(string eaeProjectDir, DeployResult result)
        {
            var enginePath = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(enginePath))
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not deployed; runtime compatibility patch skipped.");
                return;
            }

            try
            {
                PatchProcessRuntimeEccDeadEnd(enginePath, MapperConfig.EnableCyclicRestart, result);
                PatchProcessRuntimeStartBypass(enginePath, result);
                PatchProcessRuntimeEndSequenceNoOp(enginePath, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime_Generic_v1 compatibility patch failed: {ex.Message}");
            }
        }
    }
}
