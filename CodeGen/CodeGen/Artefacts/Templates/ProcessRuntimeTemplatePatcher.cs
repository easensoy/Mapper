using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using static CodeGen.Services.FbtXmlEditor;
using System.IO;
using CodeGen.Configuration;

namespace CodeGen.Services
{
    internal static class ProcessRuntimeTemplatePatcher
    {
        // Emitted from IDLE1, which is entered exactly once per recipe row. SCNF is emitted from four ECStates
        // incl. WAIT_STEP, which re-enters on every ring message and would republish the same phase.
        internal const string PhaseEventName = "PHASECNF";

        // TELEMETRY ONLY: a parallel recipe-row -> VueOne State_Number lookup plus one derived output, so the
        // phase the model names can be published alongside the meaningless compiled CurrentStep index.
        // Cannot change control flow: CurrentProcessState is written and never tested, and the strip path reverts it.
        internal static void PatchProcessTelemetryState(string eaeProjectDir, MapperConfig cfg, DeployResult result)
        {
            var candidates = new[]
            {
                Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt"),
                Path.Combine(eaeProjectDir, "IEC61499", "Process1_Generic", "Process1_Generic.fbt"),
            };
            int size = GenerationConfig.Current.RecipeArraySize;

            foreach (var fbtPath in candidates)
            {
                if (!File.Exists(fbtPath)) continue;
                bool isEngine = fbtPath.EndsWith("ProcessRuntime_Generic_v1.fbt", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) continue;
                    System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                    var iface = root.Element(ns + "InterfaceList");
                    if (iface == null) continue;

                    var inputs = iface.Element(ns + "InputVars");
                    var outputs = iface.Element(ns + "OutputVars");

                    // Always strip first so a telemetry-off deploy leaves no residue and a re-deploy is idempotent.
                    iface.Descendants(ns + "VarDeclaration")
                        .Where(v => (string?)v.Attribute("Name") is "ProcessStateByRow" or "CurrentProcessState")
                        .ToList().ForEach(v => v.Remove());
                    iface.Descendants(ns + "With")
                        .Where(w => (string?)w.Attribute("Var") == "CurrentProcessState")
                        .ToList().ForEach(w => w.Remove());
                    iface.Descendants(ns + "Event")
                        .Where(e => (string?)e.Attribute("Name") == PhaseEventName)
                        .ToList().ForEach(e => e.Remove());
                    foreach (var act in root.Descendants(ns + "ECAction")
                                 .Where(a => (string?)a.Attribute("Output") == PhaseEventName).ToList())
                    {
                        // If anything ever merged the emission onto an algorithm action, drop only the emission.
                        if (act.Attribute("Algorithm") != null) act.SetAttributeValue("Output", null);
                        else act.Remove();
                    }
                    foreach (var alg in root.Descendants(ns + "Algorithm"))
                    {
                        var st = alg.Element(ns + "ST");
                        if (st == null) continue;
                        var text = st.Value;
                        if (!text.Contains("CurrentProcessState", StringComparison.Ordinal)) continue;
                        var kept = string.Join("\n", text.Split('\n')
                            .Where(l => !l.Contains("CurrentProcessState", StringComparison.Ordinal)));
                        st.ReplaceAll(new System.Xml.Linq.XCData(kept));
                    }

                    if (cfg.MqttPublishEnabled)
                    {
                        inputs?.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "ProcessStateByRow"),
                            new System.Xml.Linq.XAttribute("Type", "INT"),
                            new System.Xml.Linq.XAttribute("ArraySize", size.ToString()),
                            new System.Xml.Linq.XAttribute("Comment",
                                "Telemetry only: recipe row -> VueOne process State_Number. Not read by control logic.")));

                        if (isEngine)
                        {
                            outputs?.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", "CurrentProcessState"),
                                new System.Xml.Linq.XAttribute("Type", "INT"),
                                new System.Xml.Linq.XAttribute("Comment",
                                    "Telemetry only: the VueOne State_Number of the active row. Never tested by control logic.")));

                            // Appended to the algorithms that already set CurrentStep, so no event, transition or FB is added.
                            foreach (var name in new[] { "LoadStep", "AdvanceStep" })
                            {
                                var alg = root.Descendants(ns + "Algorithm")
                                    .FirstOrDefault(a => (string?)a.Attribute("Name") == name);
                                var st = alg?.Element(ns + "ST");
                                if (st == null)
                                {
                                    result.Warnings.Add(
                                        $"ProcessRuntime_Generic_v1: algorithm '{name}' not found; process telemetry state not derived there.");
                                    continue;
                                }
                                st.ReplaceAll(new System.Xml.Linq.XCData(
                                    st.Value.TrimEnd() + "\nCurrentProcessState := ProcessStateByRow[CurrentStep];"));
                            }

                            // An OutputVar only reaches its data connections when a WITH-associated event fires;
                            // unassociated it stays at 0 forever. Associating here, not on SCNF, gives one publish per step.
                            var evOut = iface.Element(ns + "EventOutputs");
                            if (evOut == null)
                                result.Warnings.Add(
                                    "ProcessRuntime_Generic_v1: EventOutputs not found; CurrentProcessState "
                                    + "cannot be published and process telemetry would report 0 forever.");
                            else
                                evOut.Add(new System.Xml.Linq.XElement(ns + "Event",
                                    new System.Xml.Linq.XAttribute("Name", PhaseEventName),
                                    new System.Xml.Linq.XAttribute("Comment",
                                        "Telemetry only: fires once per recipe row, carrying that row's phase."),
                                    new System.Xml.Linq.XElement(ns + "With",
                                        new System.Xml.Linq.XAttribute("Var", "CurrentProcessState"))));

                            // Appended after the existing action, so LoadStep has refreshed the value before the event fires.
                            var idle = root.Descendants(ns + "ECState")
                                .FirstOrDefault(s => (string?)s.Attribute("Name") == "IDLE1");
                            if (idle == null)
                                result.Warnings.Add(
                                    "ProcessRuntime_Generic_v1: ECState 'IDLE1' not found; process phase would "
                                    + "never be published.");
                            else
                                idle.Add(new System.Xml.Linq.XElement(ns + "ECAction",
                                    new System.Xml.Linq.XAttribute("Output", PhaseEventName)));
                        }
                    }

                    doc.Save(fbtPath);
                    result.PatchesApplied.Add(cfg.MqttPublishEnabled
                        ? $"{Path.GetFileName(fbtPath)}: process telemetry state added (ProcessStateByRow[{size}]{(isEngine ? " + CurrentProcessState" : "")})"
                        : $"{Path.GetFileName(fbtPath)}: process telemetry state stripped (MQTT publishing off)");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{Path.GetFileName(fbtPath)} process telemetry patch failed: {ex.Message}");
                }
            }
        }

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
            => EditDeployedFbt(eaeProjectDir, "ProcessRuntime_Generic_v1.fbt", "Engine debug-watch normalize failed", result,
                (doc, root, ns, fbt) =>
            {
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
            }, notFoundNote: "ProcessRuntime_Generic_v1.fbt not found; engine debug-watch normalize skipped.");

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


        internal static void DeployRecipeStepDatatype(MapperConfig cfg, string eaeProjectDir, DeployResult result)
            => DeployDatatype(eaeProjectDir, "RecipeStep",
                TemplateDocument.Load(cfg, @"DataType\RecipeStep.dt"), result, "(sim Recipe struct)");

        // Promote the process-phase receiver slot from a literal inside the composite to an instance input.
        // The shipped Process1_Generic pins it as a Parameter on its internal ProcessStateBusHandler, so every
        // Process FB in a project receives its phase into the same state_table index. Modelled on process_id.
        internal static void PromoteProcessPhaseReceiverSlot(string eaeProjectDir)
            => RequireDeployedFbt(eaeProjectDir, "Process1_Generic.fbt",
                "Process1_Generic receiver-slot promotion failed", (doc, root, ns, fbt) =>
            {
                const string Slot = CodeGen.Translation.Process.Recipes.ProcessPhaseTransport.ReceiverSlotParam;
                var iface = root.Element(ns + "InterfaceList")
                    ?? throw new InvalidOperationException($"{fbt}: no InterfaceList.");
                var net = root.Element(ns + "FBNetwork")
                    ?? throw new InvalidOperationException($"{fbt}: no FBNetwork.");
                var inputVars = iface.Element(ns + "InputVars")
                    ?? throw new InvalidOperationException($"{fbt}: InterfaceList declares no InputVars.");
                var init = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT")
                    ?? throw new InvalidOperationException($"{fbt}: no INIT event to associate {Slot} with.");
                var dataConns = net.Element(ns + "DataConnections")
                    ?? throw new InvalidOperationException($"{fbt}: FBNetwork has no DataConnections.");

                // By TYPE, not by instance name, so a renamed instance cannot silently skip the wiring.
                var handler = net.Elements(ns + "FB")
                        .FirstOrDefault(f => (string?)f.Attribute("Type") == "ProcessStateBusHandler")
                    ?? throw new InvalidOperationException(
                        $"{fbt}: no ProcessStateBusHandler instance, so {Slot} has nothing to drive.");
                var handlerName = (string?)handler.Attribute("Name") ?? string.Empty;
                var destination = handlerName + "." + Slot;

                if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == Slot))
                    inputVars.Add(new XElement(ns + "VarDeclaration",
                        new XAttribute("Name", Slot), new XAttribute("Type", "INT"),
                        new XAttribute("Comment", "Consumer: state_table slot the transported process phase lands in")));

                if (!init.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == Slot))
                    init.Add(new XElement(ns + "With", new XAttribute("Var", Slot)));

                if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == Slot))
                {
                    var pin = new XElement(ns + "Input",
                        new XAttribute("Name", Slot), new XAttribute("x", "300"),
                        new XAttribute("y", "1900"), new XAttribute("Type", "Data"));
                    var last = net.Elements(ns + "Input").LastOrDefault();
                    if (last != null) last.AddAfterSelf(pin); else net.Add(pin);
                }

                var wires = new ConnectionSet(dataConns, ns);
                if (!wires.HasSource(Slot)) wires.Append(Slot, destination);

                // The internal literal must go: a Parameter and a data connection on one input are two sources for one value.
                RemoveElems(handler.Elements(ns + "Parameter"), p => (string?)p.Attribute("Name") == Slot);

                SaveXmlWithRetry(doc, fbt);

                // Re-read from disk: an edit that never landed would leave every instance addressing a pin the type does not declare.
                var check = LoadXmlWithRetry(fbt, LoadOptions.PreserveWhitespace).Root
                    ?? throw new InvalidOperationException($"{fbt}: unreadable after the receiver-slot promotion.");
                var cns = check.GetDefaultNamespace();
                var missing = new List<string>();
                if (!check.Descendants(cns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == Slot))
                    missing.Add($"InputVar '{Slot}'");
                if (!check.Descendants(cns + "With").Any(w => (string?)w.Attribute("Var") == Slot))
                    missing.Add($"INIT With '{Slot}'");
                if (!check.Descendants(cns + "Input").Any(i => (string?)i.Attribute("Name") == Slot))
                    missing.Add($"FBNetwork input pin '{Slot}'");
                if (!check.Descendants(cns + "Connection").Any(c => (string?)c.Attribute("Source") == Slot
                        && (string?)c.Attribute("Destination") == destination))
                    missing.Add($"data connection '{Slot}' -> '{destination}'");
                if (check.Descendants(cns + "Parameter").Any(p => (string?)p.Attribute("Name") == Slot))
                    missing.Add($"the internal '{Slot}' literal is still present");
                if (missing.Count > 0)
                    throw new InvalidOperationException(
                        $"Process1_Generic receiver-slot promotion did not apply to {fbt}: " +
                        string.Join("; ", missing) + ".");

                MapperLogger.Info($"[Deploy] Process1_Generic: {Slot} promoted to an instance input -> {destination}");
            });

        // The recipe is one Recipe : RecipeStep array. This collapses the six legacy parallel arrays onto it.
        internal static void NormalizeProcess1RecipeArrays(
            string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "Process1_Generic.fbt", "Process1_Generic recipe-struct normalize failed", result,
                (doc, root, ns, fbt) =>
            {

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
                

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Process1_Generic: recipe arrays -> Recipe struct");
                    MapperLogger.Info("[Deploy] Process1_Generic recipe normalize");
                }
            }, notFoundNote: "Process1_Generic.fbt not found; recipe-struct normalize skipped.");

        // The same collapse on the engine, including every algorithm's ST.
        internal static void NormalizeProcessRuntimeRecipeArrays(
            string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "ProcessRuntime_Generic_v1.fbt", "ProcessRuntime_Generic_v1 recipe-struct normalize failed", result,
                (doc, root, ns, fbt) =>
            {

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
                        st = st.Replace(arr, str);
                    }
                    if (st != before) { stEl.ReplaceNodes(new System.Xml.Linq.XCData(st)); changed = true; }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("ProcessRuntime_Generic_v1: recipe arrays -> Recipe struct");
                    MapperLogger.Info("[Deploy] ProcessRuntime_Generic_v1 recipe normalize");
                }
            }, notFoundNote: "ProcessRuntime_Generic_v1.fbt not found; recipe-struct normalize skipped.");

        // END->END dead-end self-loop (run-once) silences WRN_ECC_DEAD_END; cyclic routes END->ADVANCE instead.
        internal static void PatchProcessRuntimeEccDeadEnd(string fbtPath, DeployResult result)
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

            const string dest = "ADVANCE";

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
                "(CYCLIC restart: AdvanceStep wraps CurrentStep to the END row's NextStep=0)");
            MapperLogger.Info(
                $"[Deploy] Patched ProcessRuntime_Generic_v1.fbt END -> {dest} (cyclic loop)");
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

        // state_table is declared by the engine, the bus handler and the ring relay; all three must carry the
        // SAME ArraySize, else a report indexed on a shorter declaration writes past the end of that one.
        private static readonly string[] StateTableOwners =
        {
            "ProcessRuntime_Generic_v1.fbt",
            "ProcessStateBusHandler.fbt",
            "updateComponentState.fbt",
        };

        internal static void PatchStateTableCapacity(string eaeProjectDir, int capacity, DeployResult result)
        {
            var decl = new System.Text.RegularExpressions.Regex(
                @"(<VarDeclaration Name=""state_table"" Type=""Component_State"" Namespace=""Main"" ArraySize="")(\d+)("")");
            foreach (var file in StateTableOwners)
            {
                var path = FindDeployedFbt(eaeProjectDir, file);
                if (string.IsNullOrEmpty(path)) continue;
                var text = File.ReadAllText(path);
                var m = decl.Match(text);
                if (!m.Success) continue;
                if (m.Groups[2].Value == capacity.ToString()) continue;
                File.WriteAllText(path, decl.Replace(text, "${1}" + capacity + "${3}"));
                result.PatchesApplied.Add(
                    $"{file}: state_table ArraySize {m.Groups[2].Value} -> {capacity}");
                MapperLogger.Info($"[Deploy] {file}: state_table ArraySize {m.Groups[2].Value} -> {capacity}");
            }
        }

        internal static void PatchKnownArraySizeBugs(string eaeProjectDir, DeployResult result)
        {
            var fbtPath = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbtPath)) return;

            var text = File.ReadAllText(fbtPath);

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
                PatchProcessRuntimeEccDeadEnd(enginePath, result);
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
