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
    // Deploy-time patchers for the actuator/sensor CATs. Consumed via `using static`.
    internal static class ActuatorCatTemplatePatcher
    {
        // Force QI=TRUE on Sensor_Bool_CAT's internal SYMLINKMULTIVARDST; without it the DST defaults
        // FALSE (disabled subscriber, publishes to '$${PATH}Input' silently dropped). Idempotent.
        internal static void PatchSensorBoolCatDstQi(string eaeProjectDir, DeployResult result)
        {
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
                    foreach (var p in dst.Elements(ns + "Parameter")
                                 .Where(p => (string?)p.Attribute("Name") == "QI"))
                        p.SetAttributeValue("Value", "TRUE");
                }
                else
                {
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

        // Force QI=TRUE on an actuator CAT's internal SYMLINKMULTIVARDST (Inputs) + SYMLINKMULTIVARSRC
        // (Output); without QI the DST rejects sensor publishes and the SRC never writes the solenoid.
        internal static void PatchCatSymlinkQi(string eaeProjectDir, string catName, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499",
                catName, catName + ".fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        catName + ".fbt", SearchOption.AllDirectories)
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
                        $"{catName}.fbt: no SYMLINKMULTIVARDST/SRC FB found; QI guard skipped.");
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
                        $"{catName}: ensured {(string?)fb.Attribute("Name")} " +
                        $"({(string?)fb.Attribute("Type")}) QI=TRUE");
                }

                doc.Save(fbt);
                MapperLogger.Info(
                    $"[Deploy] {catName}.fbt: QI=TRUE ensured on " +
                    $"{targets.Count} SYMLINKMULTIVAR FB(s) (DST subscriber + SRC publisher enabled)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName}.fbt QI guard failed: {ex.Message}");
            }
        }

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

        // Keeps the centre-home swivel's Inputs block on the real sensor symlinks and strips sim-position
        // wiring; hard-fails if SimCentreHomeSensor_7SCH survives (the rig can't use sim wiring).
        internal static void NormalizeSwivelSimSensorSource(string eaeProjectDir, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Seven_State_Actuator_Centre_Home_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt not found; swivel sim-sensor normalize skipped.");
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null)
                {
                    result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt: FBNetwork not found; swivel sim-sensor normalize skipped.");
                    return;
                }
                var inputs = net.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == "Inputs");
                if (inputs == null)
                {
                    result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt: Inputs FB not found; swivel sim-sensor normalize skipped.");
                    return;
                }

                const string simFbName = "SimPosition";
                bool changed = false;

                XElement EnsureSection(string localName)
                {
                    var section = net.Element(ns + localName);
                    if (section != null) return section;
                    section = new XElement(ns + localName);
                    net.Add(section);
                    changed = true;
                    return section;
                }

                var eventConns = EnsureSection("EventConnections");
                var dataConns = EnsureSection("DataConnections");

                void SetParam(System.Xml.Linq.XElement fb, string name, string value)
                {
                    var p = fb.Elements(ns + "Parameter")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == name);
                    if (p == null)
                    {
                        fb.Add(new XElement(ns + "Parameter",
                            new XAttribute("Name", name),
                            new XAttribute("Value", value)));
                        changed = true;
                        return;
                    }
                    if ((string?)p.Attribute("Value") != value)
                    {
                        p.SetAttributeValue("Value", value);
                        changed = true;
                    }
                }

                void RemoveEvent(string source, string destination)
                {
                    foreach (var c in eventConns.Elements(ns + "Connection")
                                 .Where(c => (string?)c.Attribute("Source") == source &&
                                             (string?)c.Attribute("Destination") == destination)
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }
                }

                void AddEvent(string source, string destination)
                {
                    if (eventConns.Elements(ns + "Connection").Any(c =>
                            (string?)c.Attribute("Source") == source &&
                            (string?)c.Attribute("Destination") == destination))
                        return;
                    eventConns.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", source),
                        new XAttribute("Destination", destination)));
                    changed = true;
                }

                void RemoveDataTo(params string[] destinations)
                {
                    var destinationSet = destinations.ToHashSet(StringComparer.Ordinal);
                    foreach (var c in dataConns.Elements(ns + "Connection")
                                 .Where(c => destinationSet.Contains((string?)c.Attribute("Destination") ?? string.Empty))
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }
                }

                void AddData(string source, string destination)
                {
                    if (dataConns.Elements(ns + "Connection").Any(c =>
                            (string?)c.Attribute("Source") == source &&
                            (string?)c.Attribute("Destination") == destination))
                        return;
                    dataConns.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", source),
                        new XAttribute("Destination", destination)));
                    changed = true;
                }

                void RemoveSimPosition()
                {
                    foreach (var c in eventConns.Elements(ns + "Connection")
                                 .Where(c =>
                                 {
                                     var s = (string?)c.Attribute("Source") ?? string.Empty;
                                     var d = (string?)c.Attribute("Destination") ?? string.Empty;
                                     return s.StartsWith(simFbName + ".", StringComparison.Ordinal) ||
                                            d.StartsWith(simFbName + ".", StringComparison.Ordinal);
                                 })
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }

                    foreach (var c in dataConns.Elements(ns + "Connection")
                                 .Where(c =>
                                 {
                                     var s = (string?)c.Attribute("Source") ?? string.Empty;
                                     var d = (string?)c.Attribute("Destination") ?? string.Empty;
                                     return s.StartsWith(simFbName + ".", StringComparison.Ordinal) ||
                                            d.StartsWith(simFbName + ".", StringComparison.Ordinal);
                                 })
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }

                    foreach (var fb in net.Elements(ns + "FB")
                                 .Where(f => (string?)f.Attribute("Name") == simFbName)
                                 .ToList())
                    {
                        fb.Remove();
                        changed = true;
                    }
                }

                // Inputs stays subscribed to the real physical sensor names.
                SetParam(inputs, "NAME1", "'$${PATH}athome'");
                SetParam(inputs, "NAME2", "'$${PATH}atwork1'");
                SetParam(inputs, "NAME3", "'$${PATH}atWork2'");

                RemoveEvent("Inputs.INITO", "SimPosition.INIT");
                RemoveEvent("SimPosition.INITO", "ActuatorCore.INIT");
                RemoveEvent("ActuatorCore.pst_out", "SimPosition.REQ");
                RemoveEvent("SimPosition.CNF", "FB1.EI");

                RemoveDataTo(
                    "ActuatorCore.atHome", "ActuatorCore.atWork1", "ActuatorCore.atWork2",
                    "IThis.atHome", "IThis.atWork1", "IThis.atWork2",
                    "FaultHandling.atHome", "FaultHandling.atWork1", "FaultHandling.atWork2",
                    "SimPosition.CurrentState");

                RemoveSimPosition();

                AddEvent("Inputs.INITO", "ActuatorCore.INIT");
                // Rig homes on the real atHome sensor, not the ReturnToHomeHandler work->home timer.
                AddData("Inputs.VALUE1", "ActuatorCore.atHome");
                AddData("Inputs.VALUE2", "ActuatorCore.atWork1");
                AddData("Inputs.VALUE3", "ActuatorCore.atWork2");
                AddData("Inputs.VALUE1", "IThis.atHome");
                AddData("Inputs.VALUE2", "IThis.atWork1");
                AddData("Inputs.VALUE3", "IThis.atWork2");
                AddData("Inputs.VALUE1", "FaultHandling.atHome");
                AddData("Inputs.VALUE2", "FaultHandling.atWork1");
                AddData("Inputs.VALUE3", "FaultHandling.atWork2");

                bool hasSimPosition =
                    net.Elements(ns + "FB").Any(f =>
                        string.Equals((string?)f.Attribute("Name"), simFbName, StringComparison.Ordinal) ||
                        string.Equals((string?)f.Attribute("Type"), "SimCentreHomeSensor_7SCH", StringComparison.Ordinal)) ||
                    eventConns.Elements(ns + "Connection").Any(ReferencesSimPosition) ||
                    dataConns.Elements(ns + "Connection").Any(ReferencesSimPosition);

                bool ReferencesSimPosition(XElement connection)
                {
                    var source = (string?)connection.Attribute("Source") ?? string.Empty;
                    var destination = (string?)connection.Attribute("Destination") ?? string.Empty;
                    return source.StartsWith(simFbName + ".", StringComparison.Ordinal) ||
                           destination.StartsWith(simFbName + ".", StringComparison.Ordinal);
                }

                if (hasSimPosition)
                {
                    throw new InvalidOperationException(
                        "Hardware/Test Runtime cannot use simulator centre-home wiring: " +
                        "Seven_State_Actuator_Centre_Home_CAT still contains SimPosition/SimCentreHomeSensor_7SCH.");
                }

                if (changed)
                {
                    SaveXmlWithRetry(doc, fbt);
                    result.PatchesApplied.Add("Seven_State_Actuator_Centre_Home_CAT: simulator position model removed; physical sensor wiring restored");
                    MapperLogger.Info("[Deploy] Centre-Home swivel sim-sensor source normalize: physical sensor wiring restored");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Hardware/Test Runtime cannot continue because the centre-home swivel CAT could not be restored to physical sensor wiring. " +
                    "Close any open Seven_State_Actuator_Centre_Home_CAT editor tab in EAE and regenerate.",
                    ex);
            }
        }

        // Bearing_PnP home is recipe-only: strips any injected poll machinery (HomePoll/PollGate1/PollGate2/
        // PollWindow + connections), adds nothing. The CAT's 'Inputs' SYMLINKMULTIVARDST is sample-on-REQ —
        // if the core stops re-observing positions, fix the CAT/interface, don't re-add a polling FB.
        internal static void StripCatHomeSensorPoll(string eaeProjectDir, string catName, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catName + ".fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catName}.fbt not found; home-poll strip skipped.");
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null) return;

                var pollFbNames = new[] { "HomePoll", "PollWindow", "PollGate1", "PollGate2" };
                bool Has(string n) => net.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == n);
                if (!pollFbNames.Any(Has)) return;

                bool RefsPoll(string? ep)
                {
                    var e = ep ?? string.Empty;
                    foreach (var n in pollFbNames)
                        if (e.StartsWith(n + ".", StringComparison.Ordinal)) return true;
                    return false;
                }

                foreach (var f in net.Elements(ns + "FB")
                             .Where(f => pollFbNames.Contains((string?)f.Attribute("Name")))
                             .ToList())
                    f.Remove();
                foreach (var secName in new[] { "EventConnections", "DataConnections" })
                {
                    var cc = net.Element(ns + secName);
                    if (cc == null) continue;
                    foreach (var c in cc.Elements(ns + "Connection")
                                 .Where(c => RefsPoll((string?)c.Attribute("Source"))
                                          || RefsPoll((string?)c.Attribute("Destination")))
                                 .ToList())
                        c.Remove();
                }

                SaveXmlWithRetry(doc, fbt);
                result.PatchesApplied.Add(
                    $"{catName}: removed HomePoll/PollGate1/PollGate2/PollWindow poll machinery + connections (Bearing_PnP home is recipe-only now).");
                MapperLogger.Info($"[Deploy] {catName}.fbt: stripped HomePoll/PollGate/PollWindow (poll removed; home recipe-only)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName} home-poll strip failed: {ex.Message}");
            }
        }

        // Restores the Five_State CAT's Inputs DST to the physical sensor symlinks ($${PATH}athome/atwork).
        internal static void NormalizeFiveStateSimSensorSource(string eaeProjectDir, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Five_State_Actuator_CAT.fbt not found; Five_State sim-sensor normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                var inputs = net?.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == "Inputs");
                if (inputs == null)
                {
                    result.Warnings.Add("Five_State_Actuator_CAT.fbt: Inputs FB not found; Five_State sim-sensor normalize skipped.");
                    return;
                }

                var want = new[]
                {
                    ("NAME1", "'$${PATH}athome'"),
                    ("NAME2", "'$${PATH}atwork'"),
                };
                bool changed = false;
                foreach (var (pn, val) in want)
                {
                    var p = inputs.Elements(ns + "Parameter")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == pn);
                    if (p == null) continue;
                    if ((string?)p.Attribute("Value") != val) { p.SetAttributeValue("Value", val); changed = true; }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Five_State_Actuator_CAT: Inputs athome/atwork -> physical sensor symlinks (hardware)");
                    MapperLogger.Info("[Deploy] Five_State sim-sensor source normalize: physical sensor symlinks restored");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State sim-sensor normalize failed: {ex.Message}");
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

        // Restores Five_State_Actuator_CAT's two wired fault-enable inputs (VarDecl + INIT With + Input pin + FB17/FB14.IN2).
        internal static void NormalizeFiveStateFaultEnables(
            string eaeProjectDir, DeployResult result)
        {
            var map = new[]
            {
                new { Enable = "enableToWorkFaultTimeout", Dest = "FB17.IN2", X = "1280", Y = "5772" },
                new { Enable = "enableToHomeFaultTimeout", Dest = "FB14.IN2", X = "1260", Y = "5292" },
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
                    var conn = dataConns?.Elements(ns + "Connection")
                        .FirstOrDefault(c => (string?)c.Attribute("Destination") == m.Dest);
                    if (conn != null && (string?)conn.Attribute("Source") != m.Enable)
                    {
                        conn.SetAttributeValue("Source", m.Enable);
                        changed = true;
                    }

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

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Five_State_Actuator_CAT: fault-enable inputs restored as wired inputs (hardware)");
                    MapperLogger.Info("[Deploy] Five_State_Actuator_CAT fault-enable normalize: wired inputs restored");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT fault-enable normalize failed: {ex.Message}");
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

        // Force the actuator's "mode" InputVar InitialValue=1 (auto); without it mode=0 at boot fires no
        // mode_event and the ECC is stuck in AtHomeInit forever.
        internal static void PatchActuatorModeInitialValue(string eaeProjectDir, string fbtFileName, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", fbtFileName);
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        fbtFileName, SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
                var modeVar = inputVars?
                    .Elements(ns + "VarDeclaration")
                    .FirstOrDefault(v => (string?)v.Attribute("Name") == "mode");
                if (modeVar == null)
                {
                    result.Warnings.Add(
                        $"{fbtFileName}: no 'mode' InputVar found; Mode-default guard skipped.");
                    return;
                }

                var iv = (string?)modeVar.Attribute("InitialValue");
                if (iv == "1") return;
                modeVar.SetAttributeValue("InitialValue", "1");
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    $"{fbtFileName}: forced mode InputVar InitialValue=1 (powers up in auto mode)");
                MapperLogger.Info(
                    $"[Deploy] {fbtFileName}: mode InputVar InitialValue=1 " +
                    "(actuator ECC no longer stuck in AtHomeInit at boot)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{fbtFileName} Mode-default guard failed: {ex.Message}");
            }
        }

        // Swivel work-arrival latch: relax=true (rig) fires ToWorkN->AtWorkN on atWorkN=TRUE alone;
        // strict=false (sim) also requires atWorkOther=FALSE.
        internal static void PatchSwivelRelaxWorkLatch(string eaeProjectDir, bool relax, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var latches = new[]
                {
                    ("ToWork1", "AtWork1", "atWork1 = TRUE", "atWork1 = TRUE AND atWork2 = FALSE"),
                    ("ToWork2", "AtWork2", "atWork2 = TRUE", "atWork2 = TRUE AND atWork1 = FALSE"),
                };
                int changed = 0;
                foreach (var (src, dst, relaxed, strict) in latches)
                {
                    var tr = root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == src &&
                        (string?)t.Attribute("Destination") == dst);
                    if (tr == null) continue;
                    var want = relax ? relaxed : strict;
                    if ((string?)tr.Attribute("Condition") != want)
                    {
                        tr.SetAttributeValue("Condition", want);
                        changed++;
                    }
                }
                if (changed > 0)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        $"SevenStateCentreHomeActuator.fbt: work-arrival latch {(relax ? "RELAXED (atWorkN=TRUE only)" : "restored (mutually exclusive)")} on {changed} transition(s)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"SevenStateCentreHomeActuator.fbt work-latch patch failed: {ex.Message}");
            }
        }

        internal static void PatchSwivelInterlockEventCarriesStateVal(string eaeProjectDir, bool add, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var ilckEvent = root.Descendants(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "ilck_event");
                if (ilckEvent == null)
                {
                    result.Warnings.Add("SevenStateCentreHomeActuator.fbt: ilck_event not found; state_val sampling skipped.");
                    return;
                }

                bool hasStateVal = ilckEvent.Elements(ns + "With")
                    .Any(w => (string?)w.Attribute("Var") == "state_val");
                bool changed = false;

                if (add && !hasStateVal)
                {
                    ilckEvent.AddFirst(new System.Xml.Linq.XElement(ns + "With",
                        new System.Xml.Linq.XAttribute("Var", "state_val")));
                    changed = true;
                }
                else if (!add && hasStateVal)
                {
                    foreach (var w in ilckEvent.Elements(ns + "With")
                                 .Where(w => (string?)w.Attribute("Var") == "state_val")
                                 .ToList())
                    {
                        w.Remove();
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        $"SevenStateCentreHomeActuator.fbt: ilck_event {(add ? "samples" : "no longer samples")} state_val");
                    MapperLogger.Info(
                        $"[Deploy] SevenStateCentreHomeActuator.fbt: ilck_event state_val sampling add={add}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"SevenStateCentreHomeActuator.fbt ilck_event state_val patch failed: {ex.Message}");
            }
        }


        // Ring relay: REQ (a component reporting its OWN state) must clear component_state_out.dest_name —
        // Component_State_Msg is a reused struct, so a stale dest_name spuriously satisfies a target
        // actuator's BREQ match (dest_name==name) and clobbers its state_cmd.
        internal static void PatchRingReportClearDest(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "updateComponentState.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "updateComponentState.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var req = root.Descendants(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "REQ");
                var st = req?.Element(ns + "ST");
                if (st == null)
                {
                    result.Warnings.Add("updateComponentState.fbt: no REQ algorithm; report-dest-clear skipped.");
                    return;
                }
                if (st.Value.Contains("dest_name"))
                    return;

                const string newBody =
                    "component_state_out.src_id := id;\r\n" +
                    "component_state_out.source_name := name;\r\n" +
                    "component_state_out.dest_name := '';\r\n" +
                    "component_state_out.state := state_sts;\r\n" +
                    "state_table[id].name := name;\r\n" +
                    "state_table[id].state := state_sts;\r\n";
                st.ReplaceAll(new System.Xml.Linq.XCData(newBody));
                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "updateComponentState.fbt: REQ now clears component_state_out.dest_name -- a state REPORT no longer carries a stale command target, so a sensor report can no longer overwrite an actuator's state_cmd.");
                MapperLogger.Info("[Deploy] updateComponentState.fbt: REQ clears dest_name (ring report-vs-command leftover fix)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"updateComponentState.fbt report-dest-clear patch failed: {ex.Message}");
            }
        }

        // Ring relay: BCNF always forwards, but CNF fires into the actuator core only on dest match — else an
        // unrelated report replays the last retained state_cmd through ActuatorCore.pst_event.
        internal static void PatchRingCommandCnfOnlyOnDestination(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "updateComponentState.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "updateComponentState.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
                if (ecc == null)
                {
                    result.Warnings.Add("updateComponentState.fbt: no ECC; destination-gated CNF patch skipped.");
                    return;
                }

                const string commonCondition = "BREQ AND name <> component_state_in.source_name";
                const string addressedCondition = commonCondition + " AND component_state_in.dest_name = name";
                const string passThroughCondition = commonCondition + " AND component_state_in.dest_name <> name";

                bool changed = false;

                var addressedTransition = ecc.Elements(ns + "ECTransition")
                    .FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "START" &&
                        (string?)t.Attribute("Destination") == "BREQ");
                if (addressedTransition == null)
                {
                    ecc.Add(new System.Xml.Linq.XElement(ns + "ECTransition",
                        new System.Xml.Linq.XAttribute("Source", "START"),
                        new System.Xml.Linq.XAttribute("Destination", "BREQ"),
                        new System.Xml.Linq.XAttribute("Condition", addressedCondition),
                        new System.Xml.Linq.XAttribute("x", "825.226"),
                        new System.Xml.Linq.XAttribute("y", "407.2253")));
                    changed = true;
                }
                else if ((string?)addressedTransition.Attribute("Condition") != addressedCondition)
                {
                    addressedTransition.SetAttributeValue("Condition", addressedCondition);
                    changed = true;
                }

                var passState = ecc.Elements(ns + "ECState")
                    .FirstOrDefault(s => (string?)s.Attribute("Name") == "BREQ_PASS");
                if (passState == null)
                {
                    passState = new System.Xml.Linq.XElement(ns + "ECState",
                        new System.Xml.Linq.XAttribute("Name", "BREQ_PASS"),
                        new System.Xml.Linq.XAttribute("x", "1036"),
                        new System.Xml.Linq.XAttribute("y", "752"),
                        new System.Xml.Linq.XElement(ns + "ECAction",
                            new System.Xml.Linq.XAttribute("Algorithm", "BREQ"),
                            new System.Xml.Linq.XAttribute("Output", "BCNF")));
                    var reqState = ecc.Elements(ns + "ECState")
                        .FirstOrDefault(s => (string?)s.Attribute("Name") == "BREQ");
                    if (reqState != null)
                        reqState.AddAfterSelf(passState);
                    else
                        ecc.AddFirst(passState);
                    changed = true;
                }
                else
                {
                    var actions = passState.Elements(ns + "ECAction").ToList();
                    if (!actions.Any(a =>
                            (string?)a.Attribute("Algorithm") == "BREQ" &&
                            (string?)a.Attribute("Output") == "BCNF") ||
                        actions.Any(a => (string?)a.Attribute("Output") == "CNF"))
                    {
                        passState.Elements(ns + "ECAction").Remove();
                        passState.Add(new System.Xml.Linq.XElement(ns + "ECAction",
                            new System.Xml.Linq.XAttribute("Algorithm", "BREQ"),
                            new System.Xml.Linq.XAttribute("Output", "BCNF")));
                        changed = true;
                    }
                }

                var passTransition = ecc.Elements(ns + "ECTransition")
                    .FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "START" &&
                        (string?)t.Attribute("Destination") == "BREQ_PASS");
                if (passTransition == null)
                {
                    ecc.Add(new System.Xml.Linq.XElement(ns + "ECTransition",
                        new System.Xml.Linq.XAttribute("Source", "START"),
                        new System.Xml.Linq.XAttribute("Destination", "BREQ_PASS"),
                        new System.Xml.Linq.XAttribute("Condition", passThroughCondition),
                        new System.Xml.Linq.XAttribute("x", "721"),
                        new System.Xml.Linq.XAttribute("y", "655")));
                    changed = true;
                }
                else if ((string?)passTransition.Attribute("Condition") != passThroughCondition)
                {
                    passTransition.SetAttributeValue("Condition", passThroughCondition);
                    changed = true;
                }

                var passReturn = ecc.Elements(ns + "ECTransition")
                    .FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "BREQ_PASS" &&
                        (string?)t.Attribute("Destination") == "START");
                if (passReturn == null)
                {
                    ecc.Add(new System.Xml.Linq.XElement(ns + "ECTransition",
                        new System.Xml.Linq.XAttribute("Source", "BREQ_PASS"),
                        new System.Xml.Linq.XAttribute("Destination", "START"),
                        new System.Xml.Linq.XAttribute("Condition", "1"),
                        new System.Xml.Linq.XAttribute("x", "793"),
                        new System.Xml.Linq.XAttribute("y", "760")));
                    changed = true;
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "updateComponentState.fbt: CNF is now destination-gated; non-target BREQ messages pass with BCNF only, preventing stale actuator command replay.");
                    MapperLogger.Info("[Deploy] updateComponentState.fbt: gated CNF to dest_name match only (stale command replay fix)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"updateComponentState.fbt destination-gated CNF patch failed: {ex.Message}");
            }
        }

        // Gated SwivelBrakeHome: a timed reverse-coil brake at centre so the swivel homes directly from
        // AtWork1 (Disassembly) without coasting into the ejector. Directional — at AtHome the algorithm
        // reverses the coil only when homing from AtWork1; from AtWork2 (Assembly) it de-energises unchanged.
        // No-op when disabled; the ECC/CAT are force-refreshed so a flag flip reverts.
        internal static void PatchSwivelBrakeHome(string eaeProjectDir, bool enabled, int brakeMs, DeployResult result)
        {
            if (!enabled) return;
            if (brakeMs <= 0) brakeMs = 500;

            // Core ECC: SevenStateCentreHomeActuator.fbt
            var ecc = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(ecc))
            {
                ecc = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(ecc)) { result.Warnings.Add("Swivel brake: core ECC not found; skipped."); return; }
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(ecc, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root; if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                // 'atHome' -> directional brake (reverse the coil only when homing from AtWork1).
                var atHomeAlgo = root.Descendants(ns + "Algorithm").FirstOrDefault(a => (string?)a.Attribute("Name") == "atHome");
                if (atHomeAlgo == null) { result.Warnings.Add("Swivel brake: 'atHome' algorithm not found; skipped."); return; }
                atHomeAlgo.Element(ns + "ST")?.ReplaceNodes(new XCData(
                    "current_state_to_process := 6;\r\nIF outputToWork2 = TRUE THEN\r\n\toutputToWork1:= TRUE;\r\n\toutputToWork2:= FALSE;\r\nELSE\r\n\toutputToWork1:= FALSE;\r\n\toutputToWork2:= FALSE;\r\nEND_IF;\r\n"));

                root.Descendants(ns + "Algorithm").FirstOrDefault(a => (string?)a.Attribute("Name") == "AtHomeInit")
                    ?.Element(ns + "ST")?.ReplaceNodes(new XCData(
                        "current_state_to_process := 0;\r\noutputToWork1:= FALSE;\r\noutputToWork2:= FALSE;\r\n"));

                var eos = root.Descendants(ns + "EventOutputs").FirstOrDefault();
                if (eos != null && !eos.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "brake_start"))
                    eos.Add(new XElement(ns + "Event", new XAttribute("Name", "brake_start"),
                        new XAttribute("Comment", "centre-home brake pulse start")));
                var eis = root.Descendants(ns + "EventInputs").FirstOrDefault();
                if (eis != null && !eis.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "brake_done"))
                    eis.Add(new XElement(ns + "Event", new XAttribute("Name", "brake_done"),
                        new XAttribute("Comment", "centre-home brake pulse elapsed")));

                var atHome = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHome");
                if (atHome != null && !atHome.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "brake_start"))
                    atHome.Add(new XElement(ns + "ECAction", new XAttribute("Output", "brake_start")));

                // AtHome -> AtHomeInit non-sensor arc = brake_done (a safety cap only; the sensor arc below is primary).
                root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit"
                        && (string?)t.Attribute("Condition") != "atHome = FALSE")
                    ?.SetAttributeValue("Condition", "brake_done");

                // CRITICAL: AtHomeInit must emit output_event (drives the Output SYMLINKMULTIVARSRC to write
                // both coils FALSE) — stock emits only pst_out, so the reverse coil stays energised and overshoots to AtWork1.
                var atHomeInit = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHomeInit");
                if (atHomeInit != null && !atHomeInit.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "output_event"))
                    atHomeInit.Add(new XElement(ns + "ECAction", new XAttribute("Output", "output_event")));

                // SENSOR-STOPPED de-energise (the real fix): AtHome -> AtHomeInit on atHome=FALSE cuts the
                // coil at the DI02 centre-window edge, not after the fixed brake_done timer (which over-drove to AtWork1).
                var brakeDoneArc = root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                    (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit" &&
                    (string?)t.Attribute("Condition") == "brake_done");
                bool hasSensorArc = root.Descendants(ns + "ECTransition").Any(t =>
                    (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit" &&
                    (string?)t.Attribute("Condition") == "atHome = FALSE");
                if (brakeDoneArc != null && !hasSensorArc)
                    brakeDoneArc.AddBeforeSelf(new XElement(ns + "ECTransition",
                        new XAttribute("Source", "AtHome"), new XAttribute("Destination", "AtHomeInit"),
                        new XAttribute("Condition", "atHome = FALSE"),
                        new XAttribute("x", "1445.13"), new XAttribute("y", "2470.42")));

                doc.Save(ecc);
                result.PatchesApplied.Add("SevenStateCentreHomeActuator.fbt: SENSOR-STOPPED centre-home brake (atHome reverses the coil; AtHome->AtHomeInit on atHome=FALSE cuts at the centre-window edge; AtHomeInit now PUBLISHES output_event so the coil actually releases; brake_done = safety cap)");
            }
            catch (Exception ex) { result.Warnings.Add($"Swivel brake core ECC patch failed: {ex.Message}"); return; }

            // Composite: brakeTimer E_DELAY + wiring
            var cat = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                "Seven_State_Actuator_Centre_Home_CAT.fbt", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(cat) || !File.Exists(cat)) { result.Warnings.Add("Swivel brake: composite not found; skipped."); return; }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(cat, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root; if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Descendants(ns + "FBNetwork").FirstOrDefault();
                var actuator = net?.Elements(ns + "FB").FirstOrDefault(f => (string?)f.Attribute("Name") == "ActuatorCore");
                if (net == null || actuator == null) { result.Warnings.Add("Swivel brake: composite ActuatorCore missing; skipped."); return; }

                var existing = net.Elements(ns + "FB").FirstOrDefault(f => (string?)f.Attribute("Name") == "brakeTimer");
                if (existing == null)
                {
                    int maxId = net.Elements(ns + "FB")
                        .Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0).DefaultIfEmpty(0).Max();
                    int id = maxId + 1;
                    actuator.AddAfterSelf(new XElement(ns + "FB",
                        new XAttribute("ID", id), new XAttribute("Name", "brakeTimer"),
                        new XAttribute("Type", "E_DELAY"), new XAttribute("x", "3100"), new XAttribute("y", "4880"),
                        new XAttribute("Namespace", "IEC61499.Standard"),
                        new XElement(ns + "Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", $"T#{brakeMs}ms"))));
                    var idc = root.Descendants(ns + "Attribute").FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                    if (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var c) && c <= id)
                        idc.SetAttributeValue("Value", id + 1);
                }
                else
                {
                    existing.Elements(ns + "Parameter").FirstOrDefault(p => (string?)p.Attribute("Name") == "DT")
                        ?.SetAttributeValue("Value", $"T#{brakeMs}ms");
                }

                var evc = net.Elements(ns + "EventConnections").FirstOrDefault();
                if (evc != null)
                {
                    void AddConn(string src, string dst)
                    {
                        if (!evc.Elements(ns + "Connection").Any(c =>
                                (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst))
                            evc.Add(new XElement(ns + "Connection",
                                new XAttribute("Source", src), new XAttribute("Destination", dst)));
                    }
                    AddConn("ActuatorCore.brake_start", "brakeTimer.START");
                    AddConn("brakeTimer.EO", "ActuatorCore.brake_done");
                }

                doc.Save(cat);
                result.PatchesApplied.Add($"Seven_State_Actuator_Centre_Home_CAT.fbt: brakeTimer E_DELAY (T#{brakeMs}ms) wired brake_start->START, EO->brake_done");
                MapperLogger.Info($"[Deploy] centre-home BRAKE ON: reverse-coil pulse {brakeMs}ms at centre (errs toward AtWork1/away from ejector)");
            }
            catch (Exception ex) { result.Warnings.Add($"Swivel brake composite patch failed: {ex.Message}"); }
        }

        internal static void PatchSwivelAtHomeInitRecovery(string eaeProjectDir, bool addArc, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
                if (ecc == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: no <ECC>; AtHomeInit sensor-recovery skipped.");
                    return;
                }

                // SELF-HOME ON POWER-UP: the swivel has no spring-centre, so the only way HOME is its
                // initial state is to DRIVE it there — rig (addArc) redirects INIT work-position arcs to
                // ToHome; sim (!addArc) restores INIT->work and strips the self-home arcs.
                // SAFETY: the arm physically swings toward centre at power-up — the swing path must be clear before a cold download.
                var initArcs = ecc.Elements(ns + "ECTransition")
                    .Where(t => (string?)t.Attribute("Source") == "INIT").ToList();
                bool IsSelfHomeArc(System.Xml.Linq.XElement t) =>
                    (string?)t.Attribute("Source") == "AtHomeInit" &&
                    (string?)t.Attribute("Destination") == "ToHome";
                bool IsStaleWorkArc(System.Xml.Linq.XElement t) =>
                    (string?)t.Attribute("Source") == "AtHomeInit" &&
                    ((string?)t.Attribute("Destination") == "AtWork1" ||
                     (string?)t.Attribute("Destination") == "AtWork2");

                if (!addArc)
                {
                    bool ch = false;
                    foreach (var t in initArcs)
                    {
                        if ((string?)t.Attribute("Destination") != "ToHome") continue;
                        var cond = (string?)t.Attribute("Condition") ?? string.Empty;
                        t.SetAttributeValue("Destination",
                            cond.Contains("atWork1 = TRUE") ? "AtWork1" : "ToWork2");
                        ch = true;
                    }
                    foreach (var t in ecc.Elements(ns + "ECTransition")
                                 .Where(x => IsSelfHomeArc(x) || IsStaleWorkArc(x)).ToList())
                    { t.Remove(); ch = true; }
                    if (ch)
                    {
                        doc.Save(fbt);
                        result.PatchesApplied.Add(
                            "SevenStateCentreHomeActuator.fbt: INIT boot arcs restored to work states; self-home arcs stripped (sim path)");
                    }
                    return;
                }

                // RIG: drive home via INIT only. AtHomeInit must have no self-driving exit (a self-home arc
                // re-fires on noisy DIs and cycles the swivel) — redirect INIT to ToHome and strip every
                // AtHomeInit -> {ToHome,AtWork1,AtWork2} arc; the stock AtHomeInit -> ToWork1/ToWork2 (Pick/Place) arcs stay.
                bool changed = false;

                foreach (var t in initArcs)
                {
                    var dest = (string?)t.Attribute("Destination");
                    if (dest == "AtWork1" || dest == "ToWork2")
                    { t.SetAttributeValue("Destination", "ToHome"); changed = true; }
                }

                foreach (var t in ecc.Elements(ns + "ECTransition")
                             .Where(x => IsSelfHomeArc(x) || IsStaleWorkArc(x)).ToList())
                { t.Remove(); changed = true; }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "SevenStateCentreHomeActuator.fbt: HOME-ON-INIT (boot only) -- INIT work-position arcs redirected to ToHome; " +
                        "ALL AtHomeInit->{ToHome,AtWork1,AtWork2} self-home/recovery arcs stripped (they re-fired on noisy sensors and cycled the swivel)");
                    MapperLogger.Info(
                        "[Deploy] SevenStateCentreHomeActuator.fbt: self-homes at power-up via INIT->ToHome; AtHomeInit is now a stable rest state (no self-driving exit)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"SevenStateCentreHomeActuator.fbt AtHomeInit sensor-recovery patch failed: {ex.Message}");
            }
        }

        // Wires AtHome to the coil-clearing 'atHome' algorithm + output_event so the Output
        // SYMLINKMULTIVARSRC writes both work coils FALSE (swaps which existing algorithm AtHome runs).
        internal static void PatchSwivelAtHomeCoilClear(string eaeProjectDir, bool clearCoils, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var atHomeState = root.Descendants(ns + "ECState")
                    .FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHome");
                if (atHomeState == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: no AtHome ECState; coil-clear patch skipped.");
                    return;
                }
                var ecAction = atHomeState.Elements(ns + "ECAction").FirstOrDefault();
                if (ecAction == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: AtHome has no ECAction; coil-clear patch skipped.");
                    return;
                }

                var algoNames = root.Descendants(ns + "Algorithm")
                    .Select(a => (string?)a.Attribute("Name"))
                    .Where(n => n != null)
                    .ToHashSet();
                string want = clearCoils ? "atHome" : "AtHomeEnd";
                if (!algoNames.Contains(want))
                {
                    result.Warnings.Add(
                        $"SevenStateCentreHomeActuator.fbt: algorithm '{want}' not found; AtHome coil-clear patch skipped.");
                    return;
                }

                var current = (string?)ecAction.Attribute("Algorithm");
                bool changed = false;
                if (current != want)
                {
                    ecAction.SetAttributeValue("Algorithm", want);
                    changed = true;
                }

                var outputEventAction = atHomeState.Elements(ns + "ECAction")
                    .FirstOrDefault(a => (string?)a.Attribute("Output") == "output_event");
                if (clearCoils)
                {
                    if (outputEventAction == null)
                    {
                        atHomeState.Add(new XElement(ns + "ECAction",
                            new XAttribute("Output", "output_event")));
                        changed = true;
                    }
                }
                else if (outputEventAction != null)
                {
                    outputEventAction.Remove();
                    changed = true;
                }

                if (!changed) return;
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    $"SevenStateCentreHomeActuator.fbt: AtHome ECState now runs '{want}' " +
                    (clearCoils
                        ? "and emits output_event (clears both coils at home)"
                        : "without output_event (legacy no-clear mode)"));
                MapperLogger.Info(
                    $"[Deploy] SevenStateCentreHomeActuator.fbt: AtHome -> '{want}' " +
                    (clearCoils ? "(coils cleared and published at home)" : "(coils held)"));
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"SevenStateCentreHomeActuator.fbt AtHome coil-clear patch failed: {ex.Message}");
            }
        }

        // Gated MapperConfig.SwivelHomeHoldBothCoils (default OFF): OFF de-energises both 'atHome' coils
        // (a venting swivel rests off-centre); TRUE holds both to drive a cylinder into a mechanical mid-stop.
        // SAFETY: with NO mid-stop, both-on drives toward an extreme — rig only, e-stop ready, abort if it heads to Work2.
        internal static void PatchSwivelAtHomeBothCoils(string eaeProjectDir, bool holdBothCoils, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var atHomeAlgo = root.Descendants(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "atHome");
                var st = atHomeAlgo?.Element(ns + "ST");
                if (st == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: no 'atHome' algorithm ST; home-hold patch skipped.");
                    return;
                }

                string coil = holdBothCoils ? "TRUE" : "FALSE";
                string body = st.Value;
                string newBody = System.Text.RegularExpressions.Regex.Replace(
                    body, @"outputToWork1:=\s*(?:TRUE|FALSE);", $"outputToWork1:= {coil};");
                newBody = System.Text.RegularExpressions.Regex.Replace(
                    newBody, @"outputToWork2:=\s*(?:TRUE|FALSE);", $"outputToWork2:= {coil};");
                if (newBody == body) return;

                st.ReplaceNodes(new System.Xml.Linq.XCData(newBody));
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    $"SevenStateCentreHomeActuator.fbt: home 'atHome' coils -> {coil}/{coil} " +
                    (holdBothCoils ? "(HOLD at mid-stop -- centre-home overshoot fix)" : "(de-energise -- default)"));
                MapperLogger.Info(
                    $"[Deploy] SevenStateCentreHomeActuator.fbt: home coils -> {coil}/{coil} " +
                    (holdBothCoils ? "(both-coils hold at centre)" : "(de-energise)"));
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"SevenStateCentreHomeActuator.fbt home-hold patch failed: {ex.Message}");
            }
        }

    }
}
