using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using static CodeGen.Services.FbtXmlEditor;
using System.IO;
using CodeGen.Configuration;

using CodeGen.Mapping;
namespace CodeGen.Services
{
    internal static class ProcessRuntimeTemplatePatcher
    {
        // The one Recipe InputVar that replaces the parallel step arrays. Both patchers - the engine's
        // own type and the composite that wraps it - must declare it identically, or the composite's
        // parameter would name a pin of a different shape.
        static bool EnsureRecipeVar(System.Xml.Linq.XElement? inputVars,
            System.Xml.Linq.XNamespace ns, string size)
        {
            if (inputVars == null) return false;
            var existing = inputVars.Elements(ns + "VarDeclaration")
                .FirstOrDefault(v => (string?)v.Attribute("Name") == "Recipe");

            // RECONCILE, never create-if-absent: the recipe the planner emits is sized from
            // config.yaml, so a declaration left at the archive's size would be a type that
            // cannot hold the rows the instance carries.
            if (existing != null)
            {
                if ((string?)existing.Attribute("ArraySize") == size) return false;
                existing.SetAttributeValue("ArraySize", size);
                return true;
            }
            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                new System.Xml.Linq.XAttribute("Name", "Recipe"),
                new System.Xml.Linq.XAttribute("Type", "RecipeStep"),
                new System.Xml.Linq.XAttribute("ArraySize", size),
                new System.Xml.Linq.XAttribute("Namespace", Configuration.GenerationConfig.Namespace)));
            return true;
        }

        // Emitted from IDLE1, which is entered exactly once per recipe row. SCNF is emitted from four ECStates
        // incl. WAIT_STEP, which re-enters on every ring message and would republish the same phase.
        internal const string PhaseEventName = "PHASECNF";

        // TELEMETRY ONLY: a parallel recipe-row -> VueOne State_Number lookup plus one derived output, so the
        // phase the model names can be published alongside the meaningless compiled CurrentStep index.
        // Cannot change control flow: CurrentProcessState is written and never tested, and the strip path reverts it.
        internal static void PatchProcessTelemetryState(FbtEditScope scope, Configuration.CompilerConfiguration cfg,
            int recipeCapacity, DeployResult result)
        {
            var candidates = new[]
            {
                Path.Combine(scope.Root, "IEC61499", TemplateManifest.FbtOf("processEngine")),
                Path.Combine(scope.Root, "IEC61499",
                    CodeGen.Mapping.TemplateManifest.ProcessType.Name,
                    CodeGen.Mapping.TemplateManifest.ProcessType.Name + ".fbt"),
            };
            int size = recipeCapacity;

            foreach (var fbtPath in candidates)
            {
                if (!File.Exists(fbtPath)) continue;
                bool isEngine = fbtPath.EndsWith(TemplateManifest.FbtOf("processEngine"), StringComparison.OrdinalIgnoreCase);
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

                    if (cfg.Telemetry.PublishEnabled)
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
                    result.PatchesApplied.Add(cfg.Telemetry.PublishEnabled
                        ? $"{Path.GetFileName(fbtPath)}: process telemetry state added (ProcessStateByRow[{size}]{(isEngine ? " + CurrentProcessState" : "")})"
                        : $"{Path.GetFileName(fbtPath)}: process telemetry state stripped (MQTT publishing off)");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(fbtPath)} process telemetry patch failed: {ex.Message} — a deploy-time patch could not be applied, so the deployed type does not have the shape the planner's parameters name. Usually EAE holding the .fbt open during Generate: CLOSE EAE and Generate again. Generation ABORTED rather than shipping a tree EAE will not run.", ex);
                }
            }
        }

        static readonly string[] EngineDebugVars = { "CurrentStep", "CurrentStepType", "WaitSatisfied" };

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


        internal static void DeployRecipeStepDatatype(Configuration.CompilerConfiguration cfg, FbtEditScope scope, DeployResult result)
            => DeployDatatype(scope, "RecipeStep",
                TemplateDocument.Load(cfg, @"DataType\RecipeStep.dt"), result, "(sim Recipe struct)");

        // Promote the process-phase receiver slot from a literal inside the composite to an instance input.
        // The shipped Process1_Generic pins it as a Parameter on its internal ProcessStateBusHandler, so every
        // Process FB in a project receives its phase into the same state_table index. Modelled on process_id.
        internal static void PromoteProcessPhaseReceiverSlot(FbtEditScope scope)
            => RequireDeployedFbt(scope, TemplateManifest.FbtOf("processCat"),
                "Process1_Generic receiver-slot promotion failed", (doc, root, ns, fbt) =>
            {
                string Slot = CodeGen.Translation.Process.Recipes.ProcessPhaseTransport.ReceiverSlotParam;
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

                SaveXmlWithRetry(doc, fbt, scope.Retries);

                // Re-read from disk: an edit that never landed would leave every instance addressing a pin the type does not declare.
                var check = LoadXmlWithRetry(fbt, LoadOptions.PreserveWhitespace, scope.Retries).Root
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
            FbtEditScope scope, int recipeCapacity, DeployResult result)
            => EditDeployedFbt(scope, TemplateManifest.FbtOf("processCat"), "Process1_Generic recipe-struct normalize failed", result,
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
                var size = recipeCapacity.ToString();

                bool changed = false;

                                    foreach (var (nm, _) in RecipeArrays)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == nm);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == nm);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == nm);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == nm);
                    }
                    changed |= EnsureRecipeVar(inputVars, ns, size);
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
            FbtEditScope scope, int recipeCapacity, DeployResult result)
            => EditDeployedFbt(scope, TemplateManifest.FbtOf("processEngine"), "ProcessRuntime_Generic_v1 recipe-struct normalize failed", result,
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
                var size = recipeCapacity.ToString();

                bool changed = false;

                                    foreach (var (nm, _) in RecipeArrays)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == nm);
                    changed |= EnsureRecipeVar(inputVars, ns, size);
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
        // state_table is declared by the engine, the bus handler and the ring relay; all three must carry the
        // SAME ArraySize, else a report indexed on a shorter declaration writes past the end of that one.
        private static readonly string[] StateTableOwners =
        {
            TemplateManifest.FbtOf("processEngine"),
            TemplateManifest.FbtOf("processStateBus"),
            TemplateManifest.FbtOf("ringRelay"),
        };

        internal static void PatchStateTableCapacity(FbtEditScope scope, int capacity, DeployResult result)
        {
            // The namespace comes from the declaration, not a second spelling of it: a pattern pinned
            // to "Main" would silently match nothing if the project were emitted into another one,
            // and this patch would then leave every state_table at the archive's capacity.
            var ns = System.Text.RegularExpressions.Regex.Escape(Configuration.GenerationConfig.Namespace);
            var decl = new System.Text.RegularExpressions.Regex(
                @"(<VarDeclaration Name=""state_table"" Type=""Component_State"" Namespace=""" +
                ns + @""" ArraySize="")(\d+)("")");
            foreach (var file in StateTableOwners)
            {
                var path = FindDeployedFbt(scope.Root, file);
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
    }
}
