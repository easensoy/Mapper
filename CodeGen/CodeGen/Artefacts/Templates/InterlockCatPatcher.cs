using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation.Interlocks;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    // Deploy-time interlock/target patches: the InterlockRule/InterlockTable/TargetStates datatypes
    // and the actuator-CAT + CommonInterlockEvaluator struct collapses (gated by interlock.yaml).
    internal static class InterlockCatPatcher
    {
        // The STRUCT the four parallel Rule arrays collapse into (RuleTable : ARRAY OF InterlockRule).
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

        internal static void DeployInterlockRuleDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "InterlockRule.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, InterlockRuleDt);
                if (!result.DataTypesDeployed.Contains("InterlockRule"))
                    result.DataTypesDeployed.Add("InterlockRule");
                result.PatchesApplied.Add("InterlockRule.dt deployed + registered");
                MapperLogger.Info("[Deploy] InterlockRule.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"InterlockRule.dt deploy failed: {ex.Message}");
            }
        }

        // Encapsulated interlock interface (Count + Rules[]); Rules ArraySize from interlock.yaml ruleArraySize.
        static string BuildInterlockTableDt() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"InterlockTable\" Comment=\"Encapsulated interlock interface: Count + the InterlockRule array\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT input wrapping the rule count and rules\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"Count\" Type=\"INT\" />\r\n" +
            $"    <VarDeclaration Name=\"Rules\" Type=\"InterlockRule\" ArraySize=\"{InterlockConfig.Current.RuleArraySize}\" Namespace=\"Main\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        internal static void DeployInterlockTableDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "InterlockTable.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, BuildInterlockTableDt());
                if (!result.DataTypesDeployed.Contains("InterlockTable"))
                    result.DataTypesDeployed.Add("InterlockTable");
                result.PatchesApplied.Add("InterlockTable.dt deployed + registered (encapsulated interlock input)");
                MapperLogger.Info("[Deploy] InterlockTable.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"InterlockTable.dt deploy failed: {ex.Message}");
            }
        }

        const string TargetStatesDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"TargetStates\" Comment=\"Actuator target states: Work1/Work2/Home\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT input wrapping the target states\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"Work1\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Work2\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Home\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        internal static void DeployTargetStatesDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "TargetStates.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, TargetStatesDt);
                if (!result.DataTypesDeployed.Contains("TargetStates"))
                    result.DataTypesDeployed.Add("TargetStates");
                result.PatchesApplied.Add("TargetStates.dt deployed + registered (encapsulated target input)");
                MapperLogger.Info("[Deploy] TargetStates.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"TargetStates.dt deploy failed: {ex.Message}");
            }
        }

        static readonly Dictionary<string, string> TargetVarToField = new()
        {
            ["TargetWork1State"] = "Work1",
            ["TargetWork2State"] = "Work2",
            ["TargetHomeState"]  = "Home",
        };
        static readonly Dictionary<string, (string X, string Y)> TargetVarCoords = new()
        {
            ["TargetWork1State"] = ("1380", "2092"),
            ["TargetWork2State"] = ("1440", "2172"),
            ["TargetHomeState"]  = ("1380", "2192"),
        };
        static readonly Dictionary<string, string> EvaluatorEventToTargetVar = new()
        {
            ["INIT"]      = "TargetWork1State",
            ["REQ_WORK2"] = "TargetWork2State",
            ["REQ_HOME"]  = "TargetHomeState",
        };

        // Fold an actuator CAT's target InputVars into one Target : TargetStates; reduce==false restores scalars.
        internal static void NormalizeTargetStates(
            string eaeProjectDir, string catFileName, string interlockFbName,
            string[] targetInputs, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                    catFileName, SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal)) ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catFileName} not found; Target normalize skipped.");
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

                if (reduce)
                {
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
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Target");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Target");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "Target");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "Target");
                    foreach (var a in targetInputs)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", a), new System.Xml.Linq.XAttribute("Type", "INT")));
                            changed = true;
                        }
                        if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == a))
                        {
                            var (x, y) = TargetVarCoords[a];
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("x", x), new System.Xml.Linq.XAttribute("y", y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == a))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", a),
                                new System.Xml.Linq.XAttribute("Destination", interlockFbName + "." + a)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    var catLabel = Path.GetFileNameWithoutExtension(catFileName);
                    result.PatchesApplied.Add(reduce
                        ? $"{catLabel}: target states -> Target : TargetStates (encapsulated)"
                        : $"{catLabel}: Target -> scalar target states (legacy)");
                    MapperLogger.Info($"[Deploy] {catLabel} Target normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catFileName} Target normalize failed: {ex.Message}");
            }
        }

        // Fold the CommonInterlockEvaluator's three target InputVars into one Target : TargetStates + rewrite
        // the Work1/Work2/Home algorithms to Target.Work1/Work2/Home; reduce==false restores scalars.
        internal static void NormalizeCommonInterlockEvaluatorTargets(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                    "CommonInterlockEvaluator.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal)) ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("CommonInterlockEvaluator.fbt not found; Target normalize skipped.");
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
                    result.Warnings.Add("CommonInterlockEvaluator.fbt: missing InterfaceList/BasicFB; Target normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");
                var targetVars = TargetVarToField.Keys.ToArray();
                bool changed = false;

                if (reduce)
                {
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
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Target");
                    if (inputVars != null)
                        foreach (var a in targetVars)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                            {
                                inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("Name", a), new System.Xml.Linq.XAttribute("Type", "INT")));
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Target")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Target");
                        var evName = (string?)ev.Attribute("Name");
                        if (evName != null && EvaluatorEventToTargetVar.TryGetValue(evName, out var tv)
                            && !ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == tv))
                        {
                            ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", tv)));
                            changed = true;
                        }
                    }
                }

                foreach (var alg in basic.Elements(ns + "Algorithm"))
                {
                    var stEl = alg.Element(ns + "ST");
                    if (stEl == null) continue;
                    var st = stEl.Value;
                    var before = st;
                    foreach (var kv in TargetVarToField)
                        st = reduce ? st.Replace(kv.Key, "Target." + kv.Value)
                                    : st.Replace("Target." + kv.Value, kv.Key);
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
                        ? "CommonInterlockEvaluator: target states -> Target : TargetStates + algorithms (encapsulated)"
                        : "CommonInterlockEvaluator: Target -> scalar target states + algorithms (legacy)");
                    MapperLogger.Info($"[Deploy] CommonInterlockEvaluator Target normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"CommonInterlockEvaluator Target normalize failed: {ex.Message}");
            }
        }

        // Order matches struct field order.
        static readonly string[] RuleArrayNames =
            { "RuleFromState", "RuleToState", "RuleSourceID", "RuleBlockedState" };
        static readonly Dictionary<string, string> RuleArrayToField = new()
        {
            ["RuleFromState"] = "FromState",
            ["RuleToState"] = "ToState",
            ["RuleSourceID"] = "SourceID",
            ["RuleBlockedState"] = "BlockedState",
        };

        // Interlock-struct reduction on an actuator CAT (gated by interlock.yaml useStruct): collapse the
        // four parallel Rule arrays into one RuleTable : InterlockRule[10]; reduce==false restores the arrays.
        internal static void NormalizeFiveStateRuleArrays(
            string eaeProjectDir, string catFileName, string interlockFbName,
            bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catFileName, SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catFileName} not found; RuleTable normalize skipped.");
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
                string cap = InterlockConfig.Current.RuleArraySize.ToString();

                if (reduce)
                {
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
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "RuleTable");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "RuleTable");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "RuleTable");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "RuleTable");

                    var coords = new Dictionary<string, (string X, string Y)>
                    {
                        ["RuleCount"] = ("1440", "1492"),
                        ["RuleFromState"] = ("1320", "2052"),
                        ["RuleToState"] = ("1320", "1752"),
                        ["RuleSourceID"] = ("1300", "1852"),
                        ["RuleBlockedState"] = ("1320", "1952"),
                    };
                    foreach (var a in scalarAndArrays)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                        {
                            var vd = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("Type", "INT"));
                            if (a != "RuleCount") vd.SetAttributeValue("ArraySize", cap);
                            inputVars.Add(vd);
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
                                new System.Xml.Linq.XAttribute("Destination", interlockFbName + "." + a)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    var catLabel = Path.GetFileNameWithoutExtension(catFileName);
                    result.PatchesApplied.Add(reduce
                        ? $"{catLabel}: 4 arrays + RuleCount -> RuleTable : InterlockTable (encapsulated)"
                        : $"{catLabel}: RuleTable -> 4 arrays + RuleCount (legacy interface)");
                    MapperLogger.Info($"[Deploy] {catLabel} RuleTable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT RuleTable normalize failed: {ex.Message}");
            }
        }

        // Interlock-struct reduction on the CommonInterlockEvaluator Basic FB (gated by interlock.yaml
        // useStruct): collapse the four Rule arrays into RuleTable : InterlockRule[10] across the InputVars,
        // the event With lists, AND the Evaluate ST; reduce==false restores the four arrays.
        internal static void NormalizeCommonInterlockEvaluatorRules(
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
                var scalarAndArrays = RuleArrayNames.Concat(new[] { "RuleCount" }).ToArray();
                string cap = InterlockConfig.Current.RuleArraySize.ToString();

                if (reduce)
                {
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
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "RuleTable");
                    if (inputVars != null)
                        foreach (var a in scalarAndArrays)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                            {
                                var vd = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("Name", a),
                                    new System.Xml.Linq.XAttribute("Type", "INT"));
                                if (a != "RuleCount") vd.SetAttributeValue("ArraySize", cap);
                                inputVars.Add(vd);
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "RuleTable");
                        foreach (var a in scalarAndArrays)
                            if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                            {
                                ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                                changed = true;
                            }
                    }
                }

                var stEl = basic.Elements(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Evaluate")?
                    .Element(ns + "ST");
                if (stEl != null)
                {
                    var st = stEl.Value;
                    var before = st;
                    if (reduce)
                    {
                        st = st.Replace("RuleCount", "RuleTable.Count");
                        st = st.Replace("RuleTable[i].", "RuleTable.Rules[i].");          // migrate a flat RuleTable[i].X form
                        foreach (var a in RuleArrayNames)
                            st = st.Replace(a + "[i]", "RuleTable.Rules[i]." + RuleArrayToField[a]);
                    }
                    else
                    {
                        st = st.Replace("RuleTable.Count", "RuleCount");
                        foreach (var a in RuleArrayNames)
                        {
                            st = st.Replace("RuleTable.Rules[i]." + RuleArrayToField[a], a + "[i]");
                            st = st.Replace("RuleTable[i]." + RuleArrayToField[a], a + "[i]"); // legacy flat
                        }
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
                        ? "CommonInterlockEvaluator: 4 arrays + RuleCount -> RuleTable : InterlockTable + Evaluate ST (encapsulated)"
                        : "CommonInterlockEvaluator: RuleTable -> 4 arrays + RuleCount + Evaluate ST (legacy)");
                    MapperLogger.Info($"[Deploy] CommonInterlockEvaluator RuleTable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"CommonInterlockEvaluator RuleTable normalize failed: {ex.Message}");
            }
        }

        // Restores Five_State_Actuator_CAT's TargetWork1State/TargetHomeState as wired inputs (VarDecl +
        // INIT With + Input pin + InterlockManager DataConnection), stripping any baked-on params.
        internal static void NormalizeFiveStateInterlockConstants(
            string eaeProjectDir, DeployResult result)
        {
            var consts = new[]
            {
                new { Name = "TargetWork1State", X = "1380", Y = "2092" },
                new { Name = "TargetHomeState",  X = "1380", Y = "2192" },
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

                bool changed = false;

                foreach (var c in consts)
                {
                    changed |= RemoveElems(interlock.Elements(ns + "Parameter"),
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

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Five_State_Actuator_CAT: interlock constants restored as wired inputs (hardware interface)");
                    MapperLogger.Info(
                        "[Deploy] Five_State_Actuator_CAT interlock-constant normalize: wired inputs restored");
                }
                else
                {
                    result.PatchesApplied.Add(
                        "Five_State_Actuator_CAT: interlock interface already wired (no change)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Five_State_Actuator_CAT interlock-constant normalize failed: {ex.Message}");
            }
        }
    }
}
