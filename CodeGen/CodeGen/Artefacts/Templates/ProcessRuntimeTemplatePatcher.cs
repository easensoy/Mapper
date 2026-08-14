using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    internal static class ProcessRuntimeTemplatePatcher
    {
        // The engine emits SCNF from four ECStates, one of which is WAIT_STEP. WAIT_STEP is re-entered
        // from WAIT_HOLD on every `state_change`, i.e. on every ring message from any component, and
        // check_wait does not touch CurrentProcessState — so publishing off SCNF republishes the same
        // phase hundreds of times while a process merely waits. IDLE1 is entered exactly once per recipe
        // row (from INIT, from ADVANCE, and from WAIT_STEP once the wait is satisfied), so a dedicated
        // event emitted there is the one publish-per-step signal the telemetry needs.
        internal const string PhaseEventName = "PHASECNF";

        // TELEMETRY ONLY. The engine reports progress as CurrentStep, a compiled recipe-row index that
        // means nothing outside the generator. This adds a parallel row->VueOne-State_Number lookup and
        // one derived output so the phase the model actually names can be published.
        //
        // It cannot change control flow: ProcessStateByRow is read nowhere else, CurrentProcessState is
        // written and never tested, and the assignment is appended to algorithms that already run on
        // every step change. No ECC state, transition, event or existing algorithm line is altered.
        // Reverting is a no-op because the strip path removes both declarations and both assignments.
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
                        // Standalone when we added it; if anything ever merged it onto an algorithm
                        // action, drop only the emission so the algorithm keeps running.
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

                            // Appended to the algorithms that already set CurrentStep, so the derived value
                            // tracks the active row without adding an event, a transition or an FB.
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

                            // An OutputVar is only sampled onto its data connections when an event it is
                            // WITH-associated to fires; with no association the value never leaves the FB
                            // and every subscriber reads the initial 0 forever. Associating it here rather
                            // than on SCNF is what makes the phase publish once per step instead of on
                            // every ring message (see PhaseEventName).
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

                            // Appended after the existing action, so LoadStep has already refreshed the
                            // value by the time the event fires. Adding an emission cannot alter control
                            // flow: no transition tests it and only the publisher consumes it.
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
        //
        // The shipped Process1_Generic pins the slot as a Parameter on its internal ProcessStateBusHandler,
        // so EVERY Process FB in a project receives its transported phase into the same state_table index --
        // one consumer per project, and only if that one literal happens to equal its producer's id.
        // Declaring rdy_id on the TYPE's interface and wiring it to the handler makes the slot a per-instance
        // parameter, which is what lets each consumer name its own producer.
        //
        // Modelled on process_id, which is the same shape: an INT InputVar sampled WITH INIT and connected
        // straight through to the handler. Idempotent, and structurally verified before it returns.
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

                // By TYPE, not by instance name: the handler is the FB that declares the slot, and a renamed
                // instance must not silently skip the wiring.
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

                if (!dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == Slot))
                    dataConns.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", Slot), new XAttribute("Destination", destination)));

                // The internal literal has to go: a Parameter and a data connection on the same input are two
                // sources for one value, and the literal is exactly the project-wide slot being removed.
                RemoveElems(handler.Elements(ns + "Parameter"), p => (string?)p.Attribute("Name") == Slot);

                SaveXmlWithRetry(doc, fbt);

                // Re-read what was written. An in-memory edit that never reached disk would leave every
                // instance parameter addressing a pin the deployed type does not declare.
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

        // Recipe-struct collapse on Process1_Generic (gated by UseRecipeStruct); reduce==false restores the 6 arrays.
        internal static void NormalizeProcess1RecipeArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
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
            }, notFoundNote: "Process1_Generic.fbt not found; recipe-struct normalize skipped.");

        // Recipe-struct collapse on ProcessRuntime_Generic_v1 incl. every algorithm's ST (gated by UseRecipeStruct); reduce==false restores the 6 arrays.
        internal static void NormalizeProcessRuntimeRecipeArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
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
            }, notFoundNote: "ProcessRuntime_Generic_v1.fbt not found; recipe-struct normalize skipped.");

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
            // The size is StateTableAllocation.Capacity, not a literal: the engine, the bus handler, the
            // ring relay and the interlock evaluator all declare state_table and must agree, or a report
            // writes past the end of one of them.
            int cap = CodeGen.Translation.StateTableAllocation.Capacity;
            const string declPrefix =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"";
            string oldDecl = declPrefix + "1\" />";
            string newDecl = declPrefix + cap + "\" />";

            if (text.Contains(newDecl)) return;
            if (!text.Contains(oldDecl))
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: state_table is neither ArraySize=\"1\" (the shape this " +
                    $"repairs) nor ArraySize=\"{cap}\" (the shape the template ships). Verify by hand.");
                return;
            }
            File.WriteAllText(fbtPath, text.Replace(oldDecl, newDecl));
            result.PatchesApplied.Add($"ProcessRuntime_Generic_v1.state_table ArraySize 1 -> {cap}");
            MapperLogger.Info($"[Deploy] Patched ProcessRuntime_Generic_v1.state_table ArraySize 1 -> {cap}");

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
