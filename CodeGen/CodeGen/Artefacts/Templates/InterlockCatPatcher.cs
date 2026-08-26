using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using static CodeGen.Services.FbtXmlEditor;
using System.IO;
using CodeGen.Configuration;
using CodeGen.Translation.Interlocks;

using CodeGen.Mapping;
namespace CodeGen.Services
{
    // Deploy-time interlock patches, gated by interlock.yaml. No other FBT patching lives here.
    internal static class InterlockCatPatcher
    {
        internal static void DeployInterlockRuleDatatype(Configuration.CompilerConfiguration cfg, FbtEditScope scope, DeployResult result)
            => DeployDatatype(scope, "InterlockRule",
                TemplateDocument.Load(cfg, @"DataType\InterlockRule.dt"), result);

        internal static void DeployInterlockTableDatatype(
            Configuration.CompilerConfiguration cfg, FbtEditScope scope, int capacity, DeployResult result)
            => DeployDatatype(scope, "InterlockTable",
                TemplateDocument.Load(cfg, @"DataType\InterlockTable.dt", new Dictionary<string, string>
                {
                    ["RuleArraySize"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }), result, $"(rule capacity {capacity})");

        internal static void DeployTargetStatesDatatype(Configuration.CompilerConfiguration cfg, FbtEditScope scope, DeployResult result)
            => DeployDatatype(scope, "TargetStates",
                TemplateDocument.Load(cfg, @"DataType\TargetStates.dt"), result, "(encapsulated target input)");


        // Fold an actuator CAT's target InputVars into one Target : TargetStates, which is the shape the
        // evaluator takes and the shape the plan writes.
        internal static void NormalizeTargetStates(
            FbtEditScope scope, string catFileName, string interlockFbName,
            string[] targetInputs, DeployResult result)
            => EditDeployedFbt(scope, catFileName, $"{catFileName} Target normalize failed", result,
                (doc, root, ns, fbt) =>
            {
                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add($"{catFileName}: missing InterfaceList/FBNetwork; Target normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => e.Elements(ns + "With").Any(w =>
                        targetInputs.Contains((string?)w.Attribute("Var")) || (string?)w.Attribute("Var") == "Target"))
                    ?? iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");
                bool changed = false;

                var tgtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "Target");
                if (tgtVar == null && inputVars != null)
                {
                    var t = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                        new System.Xml.Linq.XAttribute("Name", "Target"),
                        new System.Xml.Linq.XAttribute("Type", "TargetStates"),
                        new System.Xml.Linq.XAttribute("Namespace", "Main"));
                    var first = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => targetInputs.Contains((string?)v.Attribute("Name")));
                    if (first != null) first.AddBeforeSelf(t); else inputVars.Add(t);
                    changed = true;
                }
                foreach (var a in targetInputs)
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == a);
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == a);
                }
                if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Target"))
                {
                    initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Target")));
                    changed = true;
                }
                if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == "Target"))
                {
                    var pin = new System.Xml.Linq.XElement(ns + "Input",
                        new System.Xml.Linq.XAttribute("Name", "Target"),
                        new System.Xml.Linq.XAttribute("x", "1380"),
                        new System.Xml.Linq.XAttribute("y", "2092"),
                        new System.Xml.Linq.XAttribute("Type", "Data"));
                    var lastInput = net.Elements(ns + "Input").LastOrDefault();
                    if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                    changed = true;
                }
                if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == "Target"))
                {
                    dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                        new System.Xml.Linq.XAttribute("Source", "Target"),
                        new System.Xml.Linq.XAttribute("Destination", interlockFbName + ".Target")));
                    changed = true;
                }
            


                if (changed)
                {
                    SaveXmlWithRetry(doc, fbt, scope.Retries);
                    var catLabel = Path.GetFileNameWithoutExtension(catFileName);
                    result.PatchesApplied.Add($"{catLabel}: target states -> Target : TargetStates");
                    MapperLogger.Info($"[Deploy] {catLabel} Target normalize");
                }
            }, notFoundNote: $"{catFileName} not found; Target normalize skipped.");

        // The same fold on the evaluator, plus rewriting its algorithms to Target.Work1/Work2/Home.
        internal static void NormalizeCommonInterlockEvaluatorTargets(
            FbtEditScope scope, DeployResult result)
            => EditDeployedFbt(scope, scope.Manifest.FbtOf("interlockEvaluator"), "CommonInterlockEvaluator Target normalize failed", result,
                (doc, root, ns, fbt) =>
            {
                var iface = root.Element(ns + "InterfaceList");
                var basic = root.Element(ns + "BasicFB");
                if (iface == null || basic == null)
                {
                    result.Warnings.Add("CommonInterlockEvaluator.fbt: missing InterfaceList/BasicFB; Target normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");
                var targetVars = TargetVarToField.Keys.ToArray();
                bool changed = false;

                var tgtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "Target");
                if (tgtVar == null && inputVars != null)
                {
                    var t = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                        new System.Xml.Linq.XAttribute("Name", "Target"),
                        new System.Xml.Linq.XAttribute("Type", "TargetStates"),
                        new System.Xml.Linq.XAttribute("Namespace", "Main"));
                    var first = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => targetVars.Contains((string?)v.Attribute("Name")));
                    if (first != null) first.AddBeforeSelf(t); else inputVars.Add(t);
                    changed = true;
                }
                foreach (var a in targetVars)
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                {
                    if (!ev.Elements(ns + "With").Any(w => targetVars.Contains((string?)w.Attribute("Var")) || (string?)w.Attribute("Var") == "Target")) continue;
                    foreach (var a in targetVars)
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                    if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Target"))
                    {
                        ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Target")));
                        changed = true;
                    }
                }
            


                foreach (var alg in basic.Elements(ns + "Algorithm"))
                {
                    var stEl = alg.Element(ns + "ST");
                    if (stEl == null) continue;
                    var st = stEl.Value;
                    var before = st;
                    foreach (var kv in TargetVarToField)
                        st = st.Replace(kv.Key, "Target." + kv.Value);
                    if (st != before)
                    {
                        stEl.ReplaceNodes(new System.Xml.Linq.XCData(st));
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveXmlWithRetry(doc, fbt, scope.Retries);
                    result.PatchesApplied.Add(
                        "CommonInterlockEvaluator: target states -> Target : TargetStates + algorithms");
                    MapperLogger.Info("[Deploy] CommonInterlockEvaluator Target normalize");
                }
            }, notFoundNote: "CommonInterlockEvaluator.fbt not found; Target normalize skipped.");

        static readonly Dictionary<string, string> TargetVarToField = new()
        {
            ["TargetWork1State"] = "Work1",
            ["TargetWork2State"] = "Work2",
            ["TargetHomeState"]  = "Home",
        };

        // Order matches struct field order.
        static readonly string[] RuleArrayNames =
            { "RuleFromState", "RuleToState", "RuleSourceID", "RuleBlockedState", "RuleTermCount" };
        static readonly Dictionary<string, string> RuleArrayToField = new()
        {
            ["RuleFromState"] = "FromState",
            ["RuleToState"] = "ToState",
            ["RuleSourceID"] = "SourceID",
            ["RuleBlockedState"] = "BlockedState",
            ["RuleTermCount"] = "TermCount",
        };

        // Collapse an actuator CAT's rule arrays into the one RuleTable the evaluator takes.
        internal static void NormalizeFiveStateRuleArrays(
            FbtEditScope scope, string catFileName, string interlockFbName,
            DeployResult result)
            => EditDeployedFbt(scope, catFileName, $"{catFileName} RuleTable normalize failed", result,
                (doc, root, ns, fbt) =>
            {

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add($"{catFileName}: missing InterfaceList/FBNetwork; RuleTable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                // Find the event whose WITH carries the rule data — search, don't hardcode (Centre-Home differs from INIT).
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => e.Elements(ns + "With").Any(w =>
                        RuleArrayNames.Contains((string?)w.Attribute("Var"))
                        || (string?)w.Attribute("Var") == "RuleTable"))
                    ?? iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");

                bool changed = false;
                var scalarAndArrays = RuleArrayNames.Concat(new[] { "RuleCount" }).ToArray();

                                    var rtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleTable");
                    if (rtVar != null)
                    {
                        if ((string?)rtVar.Attribute("Type") != "InterlockTable" || rtVar.Attribute("ArraySize") != null)
                        {
                            rtVar.SetAttributeValue("Type", "InterlockTable");
                            rtVar.SetAttributeValue("Namespace", "Main");
                            rtVar.Attribute("ArraySize")?.Remove();
                            changed = true;
                        }
                    }
                    else if (inputVars != null)
                    {
                        var rt = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Type", "InterlockTable"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        var rc = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleCount");
                        if (rc != null) rc.AddBeforeSelf(rt); else inputVars.Add(rt);
                        changed = true;
                    }
                    foreach (var a in scalarAndArrays)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == a);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == a);
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
                            new System.Xml.Linq.XAttribute("Destination", interlockFbName + ".RuleTable")));
                        changed = true;
                    }
                

                if (changed)
                {
                    SaveXmlWithRetry(doc, fbt, scope.Retries);
                    var catLabel = Path.GetFileNameWithoutExtension(catFileName);
                    result.PatchesApplied.Add(
                        $"{catLabel}: rule arrays -> RuleTable : InterlockTable");
                    MapperLogger.Info($"[Deploy] {catLabel} RuleTable normalize");
                }
            }, notFoundNote: $"{catFileName} not found; RuleTable normalize skipped.");

        // The same collapse on the evaluator, across InputVars, event With lists AND the Evaluate ST.
        internal static void NormalizeCommonInterlockEvaluatorRules(
            FbtEditScope scope, DeployResult result)
            => EditDeployedFbt(scope, scope.Manifest.FbtOf("interlockEvaluator"), "CommonInterlockEvaluator RuleTable normalize failed", result,
                (doc, root, ns, fbt) =>
            {

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
                var scalarAndArrays = RuleArrayNames.Concat(new[] { "RuleCount" }).ToArray();

                                    var rtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleTable");
                    if (rtVar != null)
                    {
                        if ((string?)rtVar.Attribute("Type") != "InterlockTable" || rtVar.Attribute("ArraySize") != null)
                        {
                            rtVar.SetAttributeValue("Type", "InterlockTable");
                            rtVar.SetAttributeValue("Namespace", "Main");
                            rtVar.Attribute("ArraySize")?.Remove();
                            changed = true;
                        }
                    }
                    else if (inputVars != null)
                    {
                        var rt = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Type", "InterlockTable"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        var rc = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleCount");
                        if (rc != null) rc.AddBeforeSelf(rt); else inputVars.Add(rt);
                        changed = true;
                    }
                    foreach (var a in scalarAndArrays)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => scalarAndArrays.Contains((string?)w.Attribute("Var")) || (string?)w.Attribute("Var") == "RuleTable")) continue;
                        foreach (var a in scalarAndArrays)
                            changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable"))
                        {
                            ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "RuleTable")));
                            changed = true;
                        }
                    }
                

                var stEl = basic.Elements(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Evaluate")?
                    .Element(ns + "ST");
                if (stEl != null)
                {
                    var st = stEl.Value;
                    var before = st;
                                            st = st.Replace("RuleCount", "RuleTable.Count");
                        st = System.Text.RegularExpressions.Regex.Replace(
                            st, @"RuleTable\[([^\]]+)\]\.", "RuleTable.Rules[$1].");   // flat RuleTable[x].F form
                        // Any subscript, not just [i]: the evaluation walks the terms of an alternative
                        // on a second index, and an unconverted access would not compile.
                        foreach (var a in RuleArrayNames)
                            st = System.Text.RegularExpressions.Regex.Replace(
                                st, a + @"\s*\[([^\]]+)\]", "RuleTable.Rules[$1]." + RuleArrayToField[a]);
                    
                    if (st != before)
                    {
                        stEl.ReplaceNodes(new System.Xml.Linq.XCData(st));
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveXmlWithRetry(doc, fbt, scope.Retries);
                    result.PatchesApplied.Add("CommonInterlockEvaluator: 4 arrays + RuleCount -> RuleTable : InterlockTable + Evaluate ST (encapsulated)");
                    MapperLogger.Info("[Deploy] CommonInterlockEvaluator RuleTable normalize: struct");
                }
            }, notFoundNote: "CommonInterlockEvaluator.fbt not found; RuleTable normalize skipped.");

        // One unit: both actuator CATs and the shared evaluator must flip TOGETHER.
        internal static void ApplyInterlockNormalizers(Configuration.CompilerConfiguration cfg,
            FbtEditScope scope, int capacity, DeployResult result)
        {
            {
                DeployInterlockRuleDatatype(cfg, scope, result);
                DeployInterlockTableDatatype(cfg, scope, capacity, result);
            }
            NormalizeFiveStateRuleArrays(scope, scope.Manifest.FbtOf("fiveStateCat"), "InterlockManager", result);
            NormalizeFiveStateRuleArrays(scope, scope.Manifest.FbtOf("centreHomeCat"), "CommonInterlockManager", result);
            NormalizeCommonInterlockEvaluatorRules(scope, result);

            DeployTargetStatesDatatype(cfg, scope, result);
            NormalizeTargetStates(scope, scope.Manifest.FbtOf("fiveStateCat"), "InterlockManager",
                new[] { "TargetWork1State", "TargetHomeState" }, result);
            NormalizeTargetStates(scope, scope.Manifest.FbtOf("centreHomeCat"), "CommonInterlockManager",
                new[] { "TargetWork1State", "TargetWork2State", "TargetHomeState" }, result);
            NormalizeCommonInterlockEvaluatorTargets(scope, result);
        }

        // Guard: every member an actuator CAT connects to its interlock FB MUST be an InputVar on the
        // shared evaluator (EAE's ERR_MEMBER_VAR_NOTFOUND). A mismatch is a stale scalar/struct mix, so
        // re-run once, then ABORT rather than deploy it.
        internal static void AssertInterlockInterfaceConsistent(Configuration.CompilerConfiguration cfg,
            FbtEditScope scope, int capacity, DeployResult result)
        {
            var missing = FindInterlockInterfaceMismatches(scope);
            if (missing.Count == 0) return;

            MapperLogger.Info("[Interlock][Guard] interface mismatch detected; re-running the interlock normalizers to self-heal.");
            ApplyInterlockNormalizers(cfg, scope, capacity, result);
            missing = FindInterlockInterfaceMismatches(scope);
            if (missing.Count == 0)
            {
                result.PatchesApplied.Add("[Interlock][Guard] scalar/struct interface mismatch self-healed on re-run.");
                return;
            }
            throw new InvalidOperationException(
                "Interlock CAT/evaluator interface MISMATCH — the actuator CAT(s) connect member(s) the " +
                "CommonInterlockEvaluator does not have: " + string.Join("; ", missing) + ". This is a stale " +
                "scalar/struct interlock mix, almost always because EAE was holding a CAT .fbt open/locked during " +
                "Generate so the struct reshape could not be written. CLOSE EAE (at least every CAT editor tab), " +
                "then Generate again. Generation ABORTED so the broken tree is never deployed.");
        }

        // Read-only (no locks). Returns human-readable mismatch strings; empty means consistent.
        static List<string> FindInterlockInterfaceMismatches(FbtEditScope scope)
        {
            var mismatches = new List<string>();
            var evalPath = FindDeployedFbt(scope.Root, scope.Manifest.FbtOf("interlockEvaluator"));
            if (string.IsNullOrEmpty(evalPath)) return mismatches;   // absent -> nothing to check
            HashSet<string> evalInputs;
            try
            {
                var ed = XDocument.Load(evalPath);
                var ens = ed.Root!.GetDefaultNamespace();
                evalInputs = ed.Root.Element(ens + "InterfaceList")?.Element(ens + "InputVars")?
                    .Elements(ens + "VarDeclaration").Select(v => (string?)v.Attribute("Name") ?? "")
                    .Where(n => n.Length > 0).ToHashSet() ?? new HashSet<string>();
            }
            catch { return mismatches; }                             // unreadable -> don't false-abort
            if (evalInputs.Count == 0) return mismatches;

            foreach (var (cat, fb) in new[] {
                (scope.Manifest.FbtOf("fiveStateCat"), "InterlockManager"),
                (scope.Manifest.FbtOf("centreHomeCat"), "CommonInterlockManager") })
            {
                var catPath = FindDeployedFbt(scope.Root, cat);
                if (string.IsNullOrEmpty(catPath)) continue;
                try
                {
                    var cd = XDocument.Load(catPath);
                    var cns = cd.Root!.GetDefaultNamespace();
                    var dataConns = cd.Root.Element(cns + "FBNetwork")?.Element(cns + "DataConnections");
                    if (dataConns == null) continue;
                    foreach (var conn in dataConns.Elements(cns + "Connection"))
                    {
                        var dest = (string?)conn.Attribute("Destination") ?? "";
                        if (!dest.StartsWith(fb + ".", StringComparison.Ordinal)) continue;
                        var member = dest[(fb.Length + 1)..];
                        if (member.Length > 0 && !evalInputs.Contains(member))
                            mismatches.Add($"{Path.GetFileNameWithoutExtension(cat)} connects '{member}' -> {fb}");
                    }
                }
                catch { /* unreadable CAT -> skip (don't false-abort) */ }
            }
            return mismatches;
        }
    }
}
