using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Interlocks;
using CodeGen.Translation.Process;

namespace CodeGen.Translation
{
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SevenStateActuatorCatType = "Seven_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_Generic";
        private const string RobotCatType = "Robot_Task_CAT";

        private const int ActuatorYGap = 800;
        private const int SensorYGap = 480;
        private const int ProcessYGap = 800;
        private const int DefaultYGap = 800;

        private const int ActuatorX = 1300;
        private const int SevenStateActuatorX = 1820;
        private const int SensorX = 1560;
        private const int ProcessX = 3000;
        private const int RobotX = 5000;

        private static readonly HashSet<string> StandardAttributes =
            new(StringComparer.OrdinalIgnoreCase)
            { "Name", "Type", "ID", "Namespace", "x", "y", "Mapping" };

        private static readonly Dictionary<string, string> NameMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "Bearing_PnP", "swivel" },
            };

        private static readonly Dictionary<string, string> CatTypeToSheet =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "Process1_Generic", "Process_DD_CAT" },
            };

        private string? _mappingRulesPath;
        private Dictionary<string, List<MappingRuleEntry>>? _ruleCache;
        private List<VueOneComponent> _allComponents = new();
        private SystemInjectionResult? _currentResult;


        public SystemInjectionResult Inject(MapperConfig config, List<VueOneComponent> components,
            string? controlXmlPath = null, string? mappingRulesPath = null,
            List<VueOneComponent>? crossReferenceComponents = null)
        {
            _mappingRulesPath = mappingRulesPath;
            _ruleCache = null;
            _allComponents = crossReferenceComponents ?? components;

            var result = new SystemInjectionResult
            {
                SyslayPath = config.SyslayPath,
                SysresPath = config.SysresPath
            };
            _currentResult = result;
            try
            {
                if (!File.Exists(config.SyslayPath))
                    throw new FileNotFoundException($"syslay not found: {config.SyslayPath}");
                if (!File.Exists(config.SysresPath))
                    throw new FileNotFoundException($"sysres not found: {config.SysresPath}");

                if (controlXmlPath != null && File.Exists(controlXmlPath))
                    PatchProcessNames(components, controlXmlPath);

                foreach (var c in Unsupported(components, config))
                    result.UnsupportedComponents.Add($"{c.Name} ({c.Type}, {c.States.Count} states)");

                if (!string.IsNullOrEmpty(_mappingRulesPath) && File.Exists(_mappingRulesPath))
                    result.InjectedFBs.Add($"[Rules] Loaded mapping rules from {Path.GetFileName(_mappingRulesPath)}");

                var syslayIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                InjectSyslay(config, components, result, syslayIds);
                InjectSysres(config, components, result, syslayIds);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }


        private List<MappingRuleEntry> GetRulesForCat(string catType)
        {
            if (string.IsNullOrEmpty(_mappingRulesPath) || !File.Exists(_mappingRulesPath))
                return new List<MappingRuleEntry>();

            _ruleCache ??= new Dictionary<string, List<MappingRuleEntry>>(StringComparer.OrdinalIgnoreCase);

            if (!_ruleCache.TryGetValue(catType, out var rules))
            {
                var sheetName = CatTypeToSheet.TryGetValue(catType, out var mapped) ? mapped : catType;
                rules = MappingRuleEngine.GetActiveRulesForCat(_mappingRulesPath, sheetName);
                _ruleCache[catType] = rules;
            }
            return rules;
        }


        private void InjectSyslay(MapperConfig config, List<VueOneComponent> components,
            SystemInjectionResult result, Dictionary<string, string> syslayIds)
        {
            var doc = XDocument.Load(config.SyslayPath);
            var net = doc.Root?.Element(Ns + "SubAppNetwork")
                ?? throw new Exception("SubAppNetwork not found in syslay");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var newActuators = new List<string>();

            InjectGroup(net, Processes(components), ProcessCatType, ProcessX, false, renames, result, syslayIds, null);
            InjectGroup(net, Sensors(components), SensorCatType, SensorX, false, renames, result, syslayIds, null);
            InjectGroup(net, Actuators(components), ActuatorCatType, ActuatorX, false, renames, result, syslayIds, newActuators);
            InjectGroup(net, SevenStateActuators(components), SevenStateActuatorCatType, SevenStateActuatorX, false, renames, result, syslayIds, newActuators);
            InjectGroup(net, Robots(components, config), RobotCatType, RobotX, false, renames, result, syslayIds, null);

            if (renames.Any())
                RewriteConnections(net, renames);

            string? proc = FirstFbOfType(net, ProcessCatType)?.Attribute("Name")?.Value;
            if (proc != null)
            {
                var fiveStateActs = FbsOfType(net, ActuatorCatType)
                    .Select(fb => fb.Attribute("Name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList()!;
                WireActuators(net, fiveStateActs!, proc, result);

                var sevenStateActs = FbsOfType(net, SevenStateActuatorCatType)
                    .Select(fb => fb.Attribute("Name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList()!;
                WireSevenStateActuators(net, sevenStateActs!, proc, result);

                if (newActuators.Any())
                    ExtendInitChain(net, newActuators, proc, result);
            }
            else
            {
                result.UnsupportedComponents.Add($"No {ProcessCatType} found in syslay — wiring skipped");
            }

            doc.Save(config.SyslayPath);
        }

        private void InjectSysres(MapperConfig config, List<VueOneComponent> components,
            SystemInjectionResult result, Dictionary<string, string> syslayIds)
        {
            var doc = XDocument.Load(config.SysresPath);
            var net = doc.Root?.Element(Ns + "FBNetwork")
                ?? throw new Exception("FBNetwork not found in sysres");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            InjectGroup(net, Processes(components), ProcessCatType, ProcessX, true, renames, result, syslayIds, null);
            InjectGroup(net, Sensors(components), SensorCatType, SensorX, true, renames, result, syslayIds, null);
            InjectGroup(net, Actuators(components), ActuatorCatType, ActuatorX, true, renames, result, syslayIds, null);
            InjectGroup(net, SevenStateActuators(components), SevenStateActuatorCatType, SevenStateActuatorX, true, renames, result, syslayIds, null);
            InjectGroup(net, Robots(components, config), RobotCatType, RobotX, true, renames, result, syslayIds, null);

            if (renames.Any())
                RewriteConnections(net, renames);

            doc.Save(config.SysresPath);
        }

        private void InjectGroup(
            XElement net,
            List<VueOneComponent> group,
            string catType,
            int columnX,
            bool isSysres,
            Dictionary<string, string> renames,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds,
            List<string>? newList)
        {
            if (!group.Any()) return;

            var groupNames = new HashSet<string>(
                group.Select(c => FbName(c, catType)), StringComparer.OrdinalIgnoreCase);

            var spares = FbsOfType(net, catType)
                .Where(fb => !groupNames.Contains(fb.Attribute("Name")?.Value ?? ""))
                .ToList();
            int spareIdx = 0;

            int nextY = ComputeStartY(net, catType);

            foreach (var comp in group)
            {
                string fbName = FbName(comp, catType);

                var present = FindFb(net, fbName, catType);
                if (present != null)
                {
                    ApplyParams(present, comp, catType);
                    RecordId(present, fbName, isSysres, syslayIds);
                    result.SkippedFBs.Add($"{fbName} already present, params updated");
                    continue;
                }

                if (spareIdx < spares.Count)
                {
                    var slot = spares[spareIdx++];
                    var old = slot.Attribute("Name")?.Value ?? "";
                    renames[old] = fbName;
                    slot.SetAttributeValue("Name", fbName);
                    ApplyParams(slot, comp, catType);
                    RecordId(slot, fbName, isSysres, syslayIds);
                    result.InjectedFBs.Add($"{fbName} (remapped from {old})");
                    newList?.Add(fbName);
                    continue;
                }

                string id = isSysres ? MakeId(fbName, "sysres") : MakeId(fbName, "syslay");

                var fb = new XElement(Ns + "FB",
                    new XAttribute("ID", id),
                    new XAttribute("Name", fbName),
                    new XAttribute("Type", catType),
                    new XAttribute("Namespace", "Main"),
                    new XAttribute("x", columnX),
                    new XAttribute("y", nextY));

                if (isSysres && syslayIds.TryGetValue(fbName, out var slId))
                    fb.SetAttributeValue("Mapping", slId);

                ApplyParams(fb, comp, catType);
                net.Add(fb);

                RecordId(fb, fbName, isSysres, syslayIds);
                result.InjectedFBs.Add($"{fbName} → x={columnX}, y={nextY} (new {catType})");
                newList?.Add(fbName);

                nextY += GapFor(catType);
            }
        }


        private void ApplyParams(XElement fb, VueOneComponent comp, string catType)
        {
            ApplyFallbackParams(fb, comp, catType);
            var rules = GetRulesForCat(catType);
            if (rules.Count > 0)
                ApplyRuleDrivenParams(fb, comp, catType, rules);
        }

        private static bool IsValidParamName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (char c in name)
            {
                if (c != '_' && !char.IsLetterOrDigit(c)) return false;
            }
            return true;
        }

        private void ApplyRuleDrivenParams(XElement fb, VueOneComponent comp, string catType,
            List<MappingRuleEntry> rules)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.IEC61499Element))
                    continue;

                string target = rule.IEC61499Element.Trim();
                string paramName;
                if (target.StartsWith("FB.", StringComparison.OrdinalIgnoreCase))
                    paramName = target.Substring(3);
                else
                    paramName = target;

                if (StandardAttributes.Contains(paramName))
                    continue;

                if (!IsValidParamName(paramName))
                    continue;

                string? sourceValue = ResolveSourceValue(rule.VueOneElement, comp, catType);

                string paramValue;
                switch (rule.Type)
                {
                    case MappingType.TRANSLATED:
                        if (sourceValue == null) continue;
                        paramValue = ApplyTransformation(sourceValue, rule.TransformationRule, comp, catType);
                        break;

                    case MappingType.ENCODED:
                        if (sourceValue == null) continue;
                        paramValue = ApplyTransformation(sourceValue, rule.TransformationRule, comp, catType);
                        break;

                    case MappingType.ASSUMED:
                        paramValue = ResolveAssumedValue(rule, comp, catType);
                        break;

                    case MappingType.HARDCODED:
                        continue;

                    default:
                        continue;
                }

                SetParam(fb, paramName, paramValue);
            }

            if (catType == ProcessCatType)
                ApplyProcessStepTableParams(fb, comp);
        }

        private void ApplyFallbackParams(XElement fb, VueOneComponent comp, string catType)
        {
            if (catType == ActuatorCatType || catType == SevenStateActuatorCatType)
            {
                var name = FbName(comp, catType);
                SetParam(fb, "actuator_name", $"'{name.ToLower()}'");
            }
            else if (catType == ProcessCatType)
            {
                ApplyProcessStepTableParams(fb, comp);
            }
        }

        private void ApplyProcessStepTableParams(XElement fb, VueOneComponent comp)
        {
            SetParam(fb, "process_name", $"'{comp.Name}'");
            SetParam(fb, "process_id", "10");

            ProcessStepTableRules? rules = null;
            if (!string.IsNullOrEmpty(_mappingRulesPath) && File.Exists(_mappingRulesPath))
            {
                var sheetName = CatTypeToSheet.TryGetValue(ProcessCatType, out var s) ? s : ProcessCatType;
                rules = ProcessStepTableRules.LoadFromSheet(_mappingRulesPath, sheetName);
            }

            var stepTable = ProcessStepTableGenerator.Generate(comp, _allComponents, rules);

            if (stepTable.Success)
            {
                SetParam(fb, "num_steps", stepTable.NumSteps.ToString());
                SetParam(fb, "num_comps", stepTable.NumComps.ToString());
                SetParam(fb, "st_type", stepTable.StepType);
                SetParam(fb, "cmd_target", stepTable.CmdTarget);
                SetParam(fb, "cmd_state", stepTable.CmdState);
                SetParam(fb, "st_wait_comp", stepTable.WaitComp);
                SetParam(fb, "st_wait_state", stepTable.WaitState);
                SetParam(fb, "st_next", stepTable.NextStep);
                SetParam(fb, "cr_name", stepTable.CompNames);
                SetParam(fb, "Text", stepTable.Text);

                if (_currentResult != null)
                {
                    _currentResult.InjectedFBs.Add(
                        $"[StepTable] {comp.Name}: {stepTable.NumSteps} steps, {stepTable.NumComps} components");
                    foreach (var desc in stepTable.StepDescriptions)
                        _currentResult.InjectedFBs.Add($"  {desc}");
                    foreach (var warn in stepTable.Warnings)
                        _currentResult.InjectedFBs.Add($"  WARN: {warn}");
                }
            }
            else
            {
                _currentResult?.UnsupportedComponents.Add(
                    $"Step table generation failed for '{comp.Name}': {stepTable.ErrorMessage}");
                SetParam(fb, "Text", BuildTextParam(comp));
            }
        }

        private static string? ResolveSourceValue(string vueOneElement, VueOneComponent comp, string catType)
        {
            if (string.IsNullOrWhiteSpace(vueOneElement))
                return null;

            var el = vueOneElement.Trim();

            if (el.Equals("Component/Name", StringComparison.OrdinalIgnoreCase)
                || el.Equals("Component Name", StringComparison.OrdinalIgnoreCase))
                return FbName(comp, catType);

            if (el.Equals("Component/Type", StringComparison.OrdinalIgnoreCase))
                return comp.Type;

            if (el.Equals("Component/Description", StringComparison.OrdinalIgnoreCase))
                return comp.Description;

            if (el.Equals("Component/ComponentID", StringComparison.OrdinalIgnoreCase)
                || el.Equals("ComponentID", StringComparison.OrdinalIgnoreCase))
                return comp.ComponentID;

            if (el.Equals("SystemID", StringComparison.OrdinalIgnoreCase)
                || el.StartsWith("System/", StringComparison.OrdinalIgnoreCase))
                return comp.ComponentID;

            if (el.StartsWith("State/", StringComparison.OrdinalIgnoreCase) && comp.States.Count > 0)
            {
                var field = el.Substring(6);
                var state = comp.States[0];
                if (field.Equals("Name", StringComparison.OrdinalIgnoreCase)) return state.Name;
                if (field.Equals("StateNumber", StringComparison.OrdinalIgnoreCase)) return state.StateNumber.ToString();
                if (field.Equals("StateID", StringComparison.OrdinalIgnoreCase)) return state.StateID;
            }

            return null;
        }

        private static string ApplyTransformation(string value, string transformRule,
            VueOneComponent comp, string catType)
        {
            if (string.IsNullOrWhiteSpace(transformRule))
                return value;

            var rule = transformRule.ToLowerInvariant();
            string result = value;

            if (rule.Contains("direct") && rule.Contains("copy"))
                return result;

            if (rule.Contains("lowercase") || rule.Contains("lower case") || rule.Contains("lower-case"))
                result = result.ToLower();
            else if (rule.Contains("uppercase") || rule.Contains("upper case") || rule.Contains("upper-case"))
                result = result.ToUpper();

            if (rule.Contains("single quote") || (rule.Contains("wrap") && rule.Contains("quote"))
                || rule.Contains("'value'") || rule.Contains("quoted"))
                result = $"'{result}'";

            if (rule.Contains("sha256") || rule.Contains("sha-256")
                || (rule.Contains("hash") && rule.Contains("hex")))
                result = MakeId(result, "encoded");

            if (rule.Contains("prefix"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(transformRule, @"'([^']+)'");
                if (match.Success) result = match.Groups[1].Value + result;
            }

            return result;
        }

        private static string ResolveAssumedValue(MappingRuleEntry rule, VueOneComponent comp, string catType)
        {
            var transform = rule.TransformationRule?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(transform) && !transform.Contains(" "))
                return transform;

            if (rule.IEC61499Element?.Contains("Type", StringComparison.OrdinalIgnoreCase) == true)
                return catType;

            if (rule.IEC61499Element?.Contains("Namespace", StringComparison.OrdinalIgnoreCase) == true)
                return "Main";

            return transform;
        }


        private static string FbName(VueOneComponent comp, string catType)
        {
            return NameMap.TryGetValue(comp.Name, out var mapped) ? mapped : comp.Name;
        }


        private static int GapFor(string catType) => catType switch
        {
            ActuatorCatType => ActuatorYGap,
            SevenStateActuatorCatType => ActuatorYGap,
            SensorCatType => SensorYGap,
            ProcessCatType => ProcessYGap,
            _ => DefaultYGap
        };

        private static int ComputeStartY(XElement net, string catType)
        {
            var ys = FbsOfType(net, catType)
                .Select(fb => ParseInt(fb.Attribute("y")?.Value, 0))
                .ToList();

            if (ys.Any()) return ys.Max() + GapFor(catType);

            return catType switch
            {
                ActuatorCatType => 2080,
                SevenStateActuatorCatType => 2080,
                SensorCatType => 1000,
                ProcessCatType => 1000,
                _ => 3000
            };
        }

        private static void WireActuators(XElement net, List<string> actuators,
            string proc, SystemInjectionResult result)
        {
            var ec = EnsureSection(net, "EventConnections");
            var dc = EnsureSection(net, "DataConnections");

            foreach (var name in actuators)
            {
                string lc = name.ToLower();
                AddConn(ec, $"{proc}.state_update", $"{name}.pst_event", result);
                AddConn(ec, $"{name}.pst_out", $"{proc}.state_change", result);
                AddConn(dc, $"{proc}.actuator_name", $"{name}.process_state_name", result);
                AddConn(dc, $"{proc}.state_val", $"{name}.state_val", result);
                AddConn(dc, $"{name}.current_state_to_process", $"{proc}.{lc}", result);
            }
        }

        // LEGACY/DORMANT: wires PHANTOM data pins absent on Process1_Generic; real command path is the stateRprtCmd ring.
        private static void WireSevenStateActuators(XElement net, List<string> actuators,
            string proc, SystemInjectionResult result)
        {
            var ec = EnsureSection(net, "EventConnections");
            var dc = EnsureSection(net, "DataConnections");

            foreach (var name in actuators)
            {
                string lc = name.ToLower();
                AddConn(ec, $"{proc}.state_update", $"{name}.pst_event", result);
                AddConn(dc, $"{proc}.actuator_name", $"{name}.process_state_name", result);
                AddConn(dc, $"{proc}.state_val", $"{name}.state_val", result);
                AddConn(dc, $"{name}.current_state_to_process", $"{proc}.{lc}", result);
            }
        }

        private static void ExtendInitChain(XElement net, List<string> newActs,
            string proc, SystemInjectionResult result)
        {
            if (!newActs.Any()) return;

            var ec = EnsureSection(net, "EventConnections");
            string procInit = $"{proc}.INIT";

            var existingConn = ec.Elements()
                .Where(e => e.Name.LocalName == "Connection")
                .FirstOrDefault(c => string.Equals(
                    c.Attribute("Destination")?.Value, procInit,
                    StringComparison.OrdinalIgnoreCase));

            string? prev = existingConn?.Attribute("Source")?.Value;
            existingConn?.Remove();

            if (!string.IsNullOrEmpty(prev))
                AddConn(ec, prev, $"{newActs[0]}.INIT", result);

            for (int i = 0; i < newActs.Count - 1; i++)
                AddConn(ec, $"{newActs[i]}.INITO", $"{newActs[i + 1]}.INIT", result);

            AddConn(ec, $"{newActs[^1]}.INITO", procInit, result);

            result.InjectedFBs.Add(
                $"INIT chain: {prev ?? "?"} → {string.Join(" → ", newActs)} → {proc}");
        }


        private static XElement? LoadNet(string path, string tag)
        {
            var doc = XDocument.Load(path);
            return doc.Root?.Element(Ns + tag);
        }

        private static IEnumerable<XElement> FbsOfType(XElement net, string type) =>
            net.Descendants()
               .Where(e => e.Name.LocalName == "FB"
                        && string.Equals(e.Attribute("Type")?.Value, type,
                               StringComparison.OrdinalIgnoreCase));

        private static XElement? FindFb(XElement net, string name, string type) =>
            net.Descendants()
               .Where(e => e.Name.LocalName == "FB")
               .FirstOrDefault(fb =>
                   string.Equals(fb.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(fb.Attribute("Type")?.Value, type, StringComparison.OrdinalIgnoreCase));

        private static XElement? FirstFbOfType(XElement net, string type) =>
            FbsOfType(net, type).FirstOrDefault();

        private static XElement EnsureSection(XElement net, string tag)
        {
            var s = net.Elements().FirstOrDefault(e => e.Name.LocalName == tag);
            if (s != null) return s;
            s = new XElement(Ns + tag);
            net.Add(s);
            return s;
        }

        private static void AddConn(XElement section, string src, string dst,
            SystemInjectionResult result)
        {
            bool exists = section.Elements()
                .Where(e => e.Name.LocalName == "Connection")
                .Any(c =>
                    string.Equals(c.Attribute("Source")?.Value, src, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Attribute("Destination")?.Value, dst, StringComparison.OrdinalIgnoreCase));
            if (exists) return;

            section.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", src),
                new XAttribute("Destination", dst)));
            result.InjectedFBs.Add($"  wire: {src} → {dst}");
        }

        private static void RewriteConnections(XElement net, Dictionary<string, string> renames)
        {
            foreach (var c in net.Descendants().Where(e => e.Name.LocalName == "Connection"))
            {
                PatchAttr(c, "Source", renames);
                PatchAttr(c, "Destination", renames);
            }
        }

        private static void PatchAttr(XElement el, string attr,
            Dictionary<string, string> renames)
        {
            var v = el.Attribute(attr)?.Value;
            if (string.IsNullOrEmpty(v)) return;
            int d = v.IndexOf('.');
            if (d < 0) return;
            if (renames.TryGetValue(v[..d], out var np))
                el.SetAttributeValue(attr, np + v[d..]);
        }

        private static string MakeId(string name, string salt)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{salt}:{name}"));
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToUpperInvariant();
        }

        private static void RecordId(XElement fb, string name, bool isSysres,
            Dictionary<string, string> syslayIds)
        {
            if (!isSysres)
                syslayIds[name] = fb.Attribute("ID")?.Value ?? MakeId(name, "syslay");
        }

        private static int ParseInt(string? s, int fallback) =>
            int.TryParse(s, out var v) ? v : fallback;

        private static void SetParam(XElement fb, string name, string value)
        {
            var el = fb.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Parameter" &&
                                     e.Attribute("Name")?.Value == name);
            if (el != null) el.SetAttributeValue("Value", value);
            else fb.Add(new XElement(Ns + "Parameter",
                new XAttribute("Name", name),
                new XAttribute("Value", value)));
        }

        private static string BuildTextParam(VueOneComponent proc)
        {
            var names = proc.States.OrderBy(s => s.StateNumber).Select(s => $"'{s.Name}'").ToList();
            int pad = Math.Max(0, 14 - names.Count);
            if (pad > 0) names.Add($"{pad}('')");
            return "[" + string.Join(",", names) + "]";
        }


        private static void PatchProcessNames(List<VueOneComponent> components, string controlXmlPath)
        {
            var procs = components.Where(c =>
                c.Type?.Equals("Process", StringComparison.OrdinalIgnoreCase) == true).ToList();
            if (!procs.Any()) return;

            try
            {
                var doc = XDocument.Load(controlXmlPath);
                var xmlComps = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Component")
                    .ToList();

                foreach (var comp in procs)
                {
                    var xmlComp = xmlComps.FirstOrDefault(c =>
                    {
                        var type = c.Elements().FirstOrDefault(e => e.Name.LocalName == "Type")?.Value;
                        return type?.Equals("Process", StringComparison.OrdinalIgnoreCase) == true;
                    });
                    if (xmlComp == null) continue;

                    var nTag = xmlComp.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "n")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(nTag))
                        comp.Name = nTag;
                }
            }
            catch
            {
            }
        }

        private static List<VueOneComponent> Actuators(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Actuator", StringComparison.OrdinalIgnoreCase) == true
                        && c.States.Count == 5).ToList();

        private static List<VueOneComponent> SevenStateActuators(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Actuator", StringComparison.OrdinalIgnoreCase) == true
                        && c.States.Count == 7).ToList();

        private static List<VueOneComponent> Sensors(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Sensor", StringComparison.OrdinalIgnoreCase) == true
                        && c.States.Count == 2).ToList();

        private static List<VueOneComponent> Processes(List<VueOneComponent> all) =>
            all.Where(c => c.Type?.Equals("Process", StringComparison.OrdinalIgnoreCase) == true).ToList();

        // Type="Robot" also covers Bearing/Shaft/CoverPnp grippers; IsRobotTaskArm selects only the real UR3e.
        private static List<VueOneComponent> Robots(List<VueOneComponent> all, MapperConfig config) =>
            all.Where(c => TemplateMap.IsRobotTaskArm(c)).ToList();

        private static List<VueOneComponent> Unsupported(List<VueOneComponent> all, MapperConfig config) =>
            all.Where(c => !Actuators(all).Contains(c)
                        && !SevenStateActuators(all).Contains(c)
                        && !Sensors(all).Contains(c)
                        && !Processes(all).Contains(c)
                        && !Robots(all, config).Contains(c)).ToList();

        public string GeneratePusherTestSyslay(string outputFolder)
        {
            if (string.IsNullOrEmpty(outputFolder))
                throw new ArgumentException("Output folder is required.", nameof(outputFolder));
            if (!Directory.Exists(outputFolder))
                throw new DirectoryNotFoundException($"Output folder does not exist: {outputFolder}");
            return GeneratePusherTestSyslayToPath(Path.Combine(outputFolder, "Pusher_Test.syslay"));
        }

        public string GeneratePusherTestSyslayToPath(string targetSyslayPath, IoBindings? bindings = null)
        {
            return GeneratePusherTestSyslayToPath(targetSyslayPath, bindings, out _);
        }

        public string GeneratePusherTestSyslayToPath(string targetSyslayPath, IoBindings? bindings,
            out BindingApplicationReport report)
        {
            if (string.IsNullOrEmpty(targetSyslayPath))
                throw new ArgumentException("Target syslay path is required.", nameof(targetSyslayPath));

            report = new BindingApplicationReport();
            var fileName = Path.GetFileName(targetSyslayPath);
            var layerId = FBIdGenerator.GenerateFBId(fileName);
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                "v1 limitations: Pusher test only. Demonstrator was cleaned of universal-architecture instances " +
                "before this generation; restore via 'git checkout' on the Demonstrator repo to revert.");

            var pusherId = FBIdGenerator.GenerateFBId("Pusher_Test_v1");
            var parameters = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString("pusher"),
                ["actuator_id"] = SyslayBuilder.FormatInt(0),
                ["WorkSensorFitted"] = SyslayBuilder.FormatBool(false),
                ["HomeSensorFitted"] = SyslayBuilder.FormatBool(false),
                ["toWorkTime"] = SyslayBuilder.FormatTimeMs(2000),
                ["toHomeTime"] = SyslayBuilder.FormatTimeMs(2000),
                ["enableToWorkFaultTimeout"] = SyslayBuilder.FormatBool(false),
                ["enableToHomeFaultTimeout"] = SyslayBuilder.FormatBool(false),
                ["faultTimeoutWork"] = SyslayBuilder.FormatTimeMs(4000),
                ["faultTimeoutHome"] = SyslayBuilder.FormatTimeMs(4000),
            };

            var pusherBinding = bindings?.Actuators.GetValueOrDefault("Pusher")
                ?? bindings?.Actuators.GetValueOrDefault("Feeder");
            if (pusherBinding != null)
                report.Bound.Add(("Pusher", DescribeBinding(pusherBinding)));
            else
                report.Missing.Add("Pusher");

            builder.AddFB(pusherId, "Pusher", "Five_State_Actuator_CAT", "Main", 1300, 2480, parameters);

            var doc = builder.Build();
            doc.Save(targetSyslayPath);
            return targetSyslayPath;
        }

        public class BindingApplicationReport
        {
            public List<(string Component, string Detail)> Bound { get; } = new();
            public List<string> Missing { get; } = new();
            public List<(string Pin, string Value)> HcfPinAssignments { get; } = new();
        }

        // SyslayBuilder.AddFB discards nested FB overrides; M262IO scope is applied in the .hcf, not here.
        private static IDictionary<string, IDictionary<string, string>>? BuildActuatorNestedOverrides(ActuatorBinding? b)
        {
            if (b == null) return null;
            var nested = new Dictionary<string, IDictionary<string, string>>(StringComparer.Ordinal);

            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(b.AthomeTag)) inputs["NAME1"] = SyslayBuilder.FormatString(b.AthomeTag);
            if (!string.IsNullOrEmpty(b.AtworkTag)) inputs["NAME2"] = SyslayBuilder.FormatString(b.AtworkTag);
            if (inputs.Count > 0) nested["Inputs"] = inputs;

            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(b.OutputToHomeTag)) outputs["NAME1"] = SyslayBuilder.FormatString(b.OutputToHomeTag);
            if (!string.IsNullOrEmpty(b.OutputToWorkTag)) outputs["NAME2"] = SyslayBuilder.FormatString(b.OutputToWorkTag);
            if (outputs.Count > 0) nested["Output"] = outputs;

            return nested.Count > 0 ? nested : null;
        }

        private static IDictionary<string, IDictionary<string, string>>? BuildSensorNestedOverrides(SensorBinding? b)
        {
            if (b == null || string.IsNullOrEmpty(b.InputTag)) return null;
            return new Dictionary<string, IDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["Input"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NAME1"] = SyslayBuilder.FormatString(b.InputTag)
                }
            };
        }

        private static string DescribeBinding(ActuatorBinding b) =>
            $"athome={b.AthomeTag ?? "-"} atwork={b.AtworkTag ?? "-"} outputToWork={b.OutputToWorkTag ?? "-"} outputToHome={b.OutputToHomeTag ?? "-"}";

        private static string DescribeBinding(SensorBinding b) =>
            $"input={b.InputTag ?? "-"}";

        public string GenerateFeedStationSyslay(string controlXmlPath, string outputFolder)
        {
            if (string.IsNullOrEmpty(outputFolder))
                throw new ArgumentException("Output folder is required.", nameof(outputFolder));
            if (!Directory.Exists(outputFolder))
                throw new DirectoryNotFoundException($"Output folder does not exist: {outputFolder}");

            var reader = new CodeGen.IO.SystemXmlReader();
            reader.ReadAllComponents(controlXmlPath);
            var projectName = !string.IsNullOrWhiteSpace(reader.SystemName) ? reader.SystemName : "FeedStation_Generated";
            var fileName = $"{SanitizeFileName(projectName)}.syslay";
            return GenerateFeedStationSyslayToPath(controlXmlPath, Path.Combine(outputFolder, fileName));
        }

        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath)
        {
            return GenerateFeedStationSyslayToPath(controlXmlPath, targetSyslayPath, null, null, out _);
        }

        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            return GenerateFeedStationSyslayToPath(controlXmlPath, targetSyslayPath, bindings, null, out report);
        }

        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath,
            IoBindings? bindings, MapperConfig? config, out BindingApplicationReport report)
        {
            report = new BindingApplicationReport();
            if (string.IsNullOrEmpty(controlXmlPath))
                throw new ArgumentException("Control.xml path is required.", nameof(controlXmlPath));
            if (!File.Exists(controlXmlPath))
                throw new FileNotFoundException($"Control.xml not found: {controlXmlPath}");
            if (string.IsNullOrEmpty(targetSyslayPath))
                throw new ArgumentException("Target syslay path is required.", nameof(targetSyslayPath));

            var reader = new CodeGen.IO.SystemXmlReader();
            var allComponents = reader.ReadAllComponents(controlXmlPath);

            // Merge the M262 Feed ring into the cross-PLC ring only when a Feed process has a cross-controller gate.
            Configuration.MapperConfig.MergeFeedRing =
                Process.Recipes.RecipeStateClassifier.FeedRingMergeNeeded(allComponents);

            var process = FindStation1Process(allComponents);
            if (process == null)
                throw new InvalidOperationException(
                    "No Process referencing a 'Feeder' actuator was found in Control.xml.");

            var grouping = new StationGroupingService();
            var fullContents = grouping.GroupStationContents(process, allComponents);

            // Sensors-first ordering here is load-bearing: it drives state_table[] index / FB id (actuator_id) / recipe Wait1Id. Absent components are skipped.
            var allowedActuators = new[]
            {
                "Feeder", "Checker", "Transfer", "Ejector",
                "Bearing_PnP",
                "Bearing_Gripper",
                "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper",
                "Clamp",
                "CoverPNP_Hr", "CoverPNP_Vr",
                "CoverPnp_Gripper",
            };
            if (MapperConfig.EnableRobotTaskTail)
                allowedActuators = allowedActuators.Append("Robot").ToArray();
            var allowedSensors = new[]
            {
                "PartInHopper", "PartAtChecker",
                "BearingSensor", "ShaftSensor",
                "TopCoverSenosr",
            };
            // Source from full Control.xml (StationGroupingService only populates Feed_Station's conditions); grippers are Type="Robot", so accept both.
            var contents = new StationContents(
                fullContents.Process,
                allowedActuators
                    .Select(n => allComponents.FirstOrDefault(c =>
                        (string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(c.Name, n, StringComparison.Ordinal)))
                    .Where(a => a != null).Select(a => a!).ToList(),
                allowedSensors
                    .Select(n => allComponents.FirstOrDefault(c =>
                        string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Name, n, StringComparison.Ordinal)))
                    .Where(s => s != null).Select(s => s!).ToList());

            var fileName = Path.GetFileName(targetSyslayPath);
            var fullPath = targetSyslayPath;

            var layerId = FBIdGenerator.GenerateFBId(fileName);
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                "Phase 1: Process1 recipe arrays are emitted as syslay Parameter values on the " +
                "Process1 instance (StepType, CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep). " +
                "Scope filter trims to the Feed Station slice — Feeder + Checker actuators, " +
                "PartInHopper + PartAtChecker sensors; out-of-scope component waits " +
                "fall back to (0,0). Sensor-to-process DataConnections still not generated. " +
                "Demonstrator was cleaned of universal-architecture instances before this generation; " +
                "restore via 'git checkout' on the Demonstrator repo to revert.");

            int sensorIdStart = 0;
            int actuatorIdStart = contents.Sensors.Count;
            // process_ids stay in [0..19] (state_table ARRAY[20]) and above the component id space (max actuator_id 16), so no Wait1Id collides with one.
            int assemblyProcessId = MapperConfig.AssemblyProcessId;
            int disassemblyProcessId = MapperConfig.DisassemblyProcessId;
            // Feed_Station keeps process_id 10 (== Shaft_Hr); harmless under MergeFeedRing (Feed only WAITs, its CMD states 1/3 ≠ Shaft_Hr targets 0/2).
            int processId = MapperConfig.FeedStationProcessId;

            // No top-level PLC_Start FB: Area_CAT/Station_CAT each hold their own plcStart; an external one double-bootstraps (EAE rejects).

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_HMI"),
                "Area_HMI", "Area_CAT", "Main", 240, 140);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area"),
                "Area", "Area", "Main", 400, 580,
                new Dictionary<string, string>
                {
                    ["AreaName"] = SyslayBuilder.FormatString("Area")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station1"),
                "Station1", "Station", "Main", 2120, 600,
                new Dictionary<string, string>
                {
                    ["StationName"] = SyslayBuilder.FormatString("Station1")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station1_HMI"),
                "Station1_HMI", "Station_CAT", "Main", 2220, 100);

            // Station 2 stack — coordinates here just need to be unique; the post-syslay CanonicalLayout pass rewrites them.
            builder.AddFB(FBIdGenerator.GenerateFBId("Station2"),
                "Station2", "Station", "Main", 12000, 600,
                new Dictionary<string, string>
                {
                    ["StationName"] = SyslayBuilder.FormatString("Station2")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station2_HMI"),
                "Station2_HMI", "Station_CAT", "Main", 12100, 100);

            // Instance name: Instance_Name_Overrides sheet, else suffix-stripping convention, else "Process1".
            var overrides = (config != null && !string.IsNullOrWhiteSpace(config.MappingRulesPath))
                ? InstanceNameOverridesLoader.Load(config.MappingRulesPath)
                : new InstanceNameOverridesLoader.Overrides();

            var processInstanceName = InstanceNameResolver.Resolve(contents.Process,
                overrides.ByComponentId, overrides.ByVueOneName);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            var (processOuter, processNested, processRecipe) = BuildProcessFbParameters(
                contents.Process, allComponents, processInstanceName, processId, contents,
                useRecipeStruct: config != null && config.UseRecipeStruct);

            // EAE rejects a Parameter not declared as an InputVar on the FBType (ERR_MEMBER_VAR_NOTFOUND).

            // SAFETY: every actuator with a work command (CmdState=1) must also have a return-to-home (CmdState=3).
            if (processRecipe != null)
            {
                var adv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < processRecipe.StepType.Count; i++)
                {
                    if (processRecipe.StepType[i] != 1) continue;
                    var t = (processRecipe.CmdTargetName[i] ?? string.Empty).Trim();
                    if (t.Length == 0) continue;
                    if (processRecipe.CmdStateArr[i] == 1) adv.Add(t);
                    else if (processRecipe.CmdStateArr[i] == 3) ret.Add(t);
                }
                var strandedAct = adv.Where(a => !ret.Contains(a)).ToList();
                if (strandedAct.Count > 0)
                    throw new InvalidOperationException(
                        $"[Recipe] Actuator '{strandedAct[0]}' has no return-to-home cmd step" +
                        (strandedAct.Count > 1
                            ? $" ({strandedAct.Count} affected: {string.Join(", ", strandedAct)})"
                            : string.Empty) +
                        " — refusing to generate code that strands an actuator at work. " +
                        "(auto-retract should have inserted it; this is a recipe-generator bug.)");
            }

            builder.AddFB(FBIdGenerator.GenerateFBId(contents.Process.ComponentID),
                processInstanceName, "Process1_Generic", "Main", 3360, 1460,
                processOuter, processNested);

            // Station 2 Process FBs reuse the SAME global sensors-first `contents` so every Wait1Id matches the global FB id;
            // commandFromCondition:true because Station-2 state names don't encode motion verbs.
            var assemblyStationProc = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, "Assembly_Station", StringComparison.Ordinal));
            var crossProcInstances = new List<string> { processInstanceName };
            if (assemblyStationProc != null)
            {
                var assemblyName = InstanceNameResolver.Resolve(assemblyStationProc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var (aOuter, aNested, aRecipe) = BuildProcessFbParameters(
                    assemblyStationProc, allComponents, assemblyName, assemblyProcessId,
                    contents: contents,
                    useRecipeStruct: config != null && config.UseRecipeStruct,
                    commandFromCondition: true);
                builder.AddFB(FBIdGenerator.GenerateFBId(assemblyStationProc.ComponentID),
                    assemblyName, "Process1_Generic", "Main", 12200, 1460,
                    aOuter, aNested);
                crossProcInstances.Add(assemblyName);
                ReportStation2Recipe(report, assemblyName, aRecipe, "M580");
                AppendProcessRecipeComment(builder, assemblyName, aRecipe);
            }
            else
            {
                report.Missing.Add(
                    "[Recipe] Assembly_Station Process not found in Control.xml — " +
                    "Station 2 (M580) frame will have actuators but no Process FB.");
            }

            // disassemblyFbName is captured so BuildStation2Wiring threads the SAME FB the sysres does; null → syslay stays Assembly-only.
            string disassemblyFbName = null;
            var disassyProc = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(c.Name, "Disassembly", StringComparison.Ordinal)
                 || string.Equals(c.Name, "Disassembly_Station", StringComparison.Ordinal)));
            if (disassyProc != null)
            {
                var disassyName = InstanceNameResolver.Resolve(disassyProc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                disassemblyFbName = disassyName;
                var (dOuter, dNested, dRecipe) = BuildProcessFbParameters(
                    disassyProc, allComponents, disassyName, disassemblyProcessId,
                    contents: contents,
                    useRecipeStruct: config != null && config.UseRecipeStruct,
                    commandFromCondition: true);
                builder.AddFB(FBIdGenerator.GenerateFBId(disassyProc.ComponentID),
                    disassyName, "Process1_Generic", "Main", 20800, 1460,
                    dOuter, dNested);
                crossProcInstances.Add(disassyName);
                ReportStation2Recipe(report, disassyName, dRecipe, "M580");
                AppendProcessRecipeComment(builder, disassyName, dRecipe);
            }
            else
            {
                report.Missing.Add(
                    "[Recipe] Disassembly Process not found in Control.xml — " +
                    "BX1 zone will have actuators but no Disassembly Process FB.");
            }

            // Bearing_PnP routes to Seven_State (13-state PARALLEL+ALTERNATIVE branched swivel).
            var bearingPnp = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, "Bearing_PnP", StringComparison.Ordinal));
            if (bearingPnp != null)
                report.Missing.Add(
                    $"[Recipe] Bearing_PnP ({bearingPnp.States.Count} states / PARALLEL+ALTERNATIVE " +
                    "branched) → Seven_State_Actuator_CAT; recipe emits settled WAITs (CMD vocabulary pending).");

            if (processRecipe != null && processRecipe.SkippedConditions.Count > 0)
            {
                var prefix = $" Recipe scope: {processRecipe.SkippedConditions.Count} " +
                             "Control.xml condition(s) were dropped because they reference " +
                             "components not present in this syslay (Button 2 filters to " +
                             $"Feeder + Checker + PartInHopper + PartAtChecker). Skipped:\n  - " +
                             string.Join("\n  - ", processRecipe.SkippedConditions);
                builder.AppendTopComment(prefix);
                foreach (var skip in processRecipe.SkippedConditions)
                    report.Missing.Add($"recipe: {skip}");
            }

            if (processRecipe != null &&
                !string.IsNullOrWhiteSpace(processRecipe.OrderingSummary))
            {
                builder.AppendTopComment(
                    " Recipe step ordering (serialised — collision-safe; each actuator " +
                    "returns home before any subsequent actuator advances; auto-retract " +
                    "is nested in place, not batched at the end): " +
                    processRecipe.OrderingSummary);
                report.Missing.Add($"recipe ordering: {processRecipe.OrderingSummary}");
            }

            // Sensors-first id map == the recipe's Wait1Id scheme, so InterlockManager.RuleSourceID and the engine read the same state_table slots.
            var scopedIds = ProcessRecipeArrayGenerator.BuildScopedComponentMap(
                contents.Sensors, contents.Actuators);

            // PLC partitioning index (name-based guess when MapperConfig is null).
            var plcIndex = config != null
                ? HcfSymbolIndex.Build(config)
                : new HcfSymbolIndex();
            var perPlcCount = new Dictionary<PlcAssignment, int>
            {
                [PlcAssignment.M262] = 0,
                [PlcAssignment.M580] = 0,
                [PlcAssignment.BX1]  = 0,
                [PlcAssignment.Unknown] = 0,
            };

            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                var actuator = contents.Actuators[i];
                int assignedId = actuatorIdStart + i;
                var fbType = ResolveActuatorFBType(actuator);
                // Force the UR3e's dedicated non-colliding slot (its positional id clashes on the M580 state_table) so CAT actuator_id == robot Wait1Id.
                if (MapperConfig.EnableRobotTaskTail && TemplateMap.IsRobotTaskArm(actuator))
                    assignedId = MapperConfig.RobotActuatorId;
                var displayName = InstanceNameResolver.Resolve(actuator,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var actPlc = plcIndex.ResolveComponent(actuator.Name, bindings);

                Dictionary<string, string> actParams;
                if (fbType == "Five_State_Actuator_CAT")
                {
                    actParams = BuildActuatorParameters(actuator, assignedId, allComponents, scopedIds);
                    // actuator_name = resolved instance name so the runtime broadcast key matches the FB instance.
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        displayName.ToLowerInvariant());

                    InterlockEmitter.GuardFiveState(actParams, actuator, allComponents, scopedIds, report.Bound);
                }
                else if (string.Equals(fbType, "Seven_State_Actuator_Centre_Home_CAT", StringComparison.Ordinal))
                {
                    actParams = BuildMinimalActuatorParameters(actuator, assignedId, fbType);
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        displayName.ToLowerInvariant());
                    InterlockEmitter.ApplyCentreHome(actParams, actuator, allComponents, scopedIds);
                    InterlockEmitter.GuardCentreHome(actParams, actuator, allComponents, scopedIds, report.Bound);
                }
                else
                {
                    actParams = BuildMinimalActuatorParameters(actuator, assignedId, fbType);
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        displayName.ToLowerInvariant());
                    report.Missing.Add(
                        $"[Phase 6] {actuator.Name} ({fbType}): minimal params only — " +
                        "data-driven Target*/Rule*/Interlock* wiring deferred to recipe phase");
                }

                ActuatorBinding? actBinding = null;
                bindings?.Actuators.TryGetValue(actuator.Name, out actBinding);
                if (actBinding != null) report.Bound.Add((actuator.Name, DescribeBinding(actBinding)));
                else if (bindings != null) report.Missing.Add(actuator.Name);

                // Placeholder position; CanonicalLayout overrides known names post-syslay.
                int colInPlc = perPlcCount[actPlc]++;
                var (zoneX, zoneY) = PlcZoneActuatorPosition(actPlc, colInPlc);

                builder.AddFB(FBIdGenerator.GenerateFBId(actuator.ComponentID),
                    displayName, fbType, "Main",
                    zoneX, zoneY, actParams);

                if (!string.Equals(displayName, actuator.Name, StringComparison.Ordinal))
                    report.Missing.Add(
                        $"[Layout] '{actuator.Name}' emitted as FB instance '{displayName}' " +
                        "(rename from Instance_Name_Overrides xlsx sheet)");
            }

            var perPlcSensorCount = new Dictionary<PlcAssignment, int>
            {
                [PlcAssignment.M262] = 0,
                [PlcAssignment.M580] = 0,
                [PlcAssignment.BX1]  = 0,
                [PlcAssignment.Unknown] = 0,
            };

            for (int i = 0; i < contents.Sensors.Count; i++)
            {
                var sensor = contents.Sensors[i];
                int assignedId = sensorIdStart + i;

                SensorBinding? senBinding = null;
                bindings?.Sensors.TryGetValue(sensor.Name, out senBinding);
                if (senBinding != null) report.Bound.Add((sensor.Name, DescribeBinding(senBinding)));
                else if (bindings != null) report.Missing.Add(sensor.Name);

                var senPlc = plcIndex.ResolveComponent(sensor.Name, bindings);
                int senCol = perPlcSensorCount[senPlc]++;
                var (sX, sY) = PlcZoneSensorPosition(senPlc, senCol);

                var senDisplayName = InstanceNameResolver.Resolve(sensor,
                    overrides.ByComponentId, overrides.ByVueOneName);

                builder.AddFB(FBIdGenerator.GenerateFBId(sensor.ComponentID),
                    senDisplayName, "Sensor_Bool_CAT", "Main",
                    sX, sY,
                    new Dictionary<string, string>
                    {
                        ["name"] = SyslayBuilder.FormatString(senDisplayName),
                        ["id"] = SyslayBuilder.FormatInt(assignedId),
                    });
            }

            // Synthesized M262 sensors: EXPLICIT ids so they never shift Feed actuator ids; off every report ring, so the Feed ring stays byte-identical.
            if (MapperConfig.EnableRobotTaskTail)
            {
                int synthY = 5200;
                string prevSynthInit = "PartInHopper";
                foreach (var (synthName, _, synthId) in MapperConfig.M262SynthSensors)
                {
                    builder.AddFB(FBIdGenerator.GenerateFBId("m262rigsensor-" + synthName),
                        synthName, "Sensor_Bool_CAT", "Main", 2000, synthY,
                        new Dictionary<string, string>
                        {
                            ["name"] = SyslayBuilder.FormatString(synthName),
                            ["id"] = SyslayBuilder.FormatInt(synthId),
                        });
                    builder.AddEventConnection($"{prevSynthInit}.INITO", $"{synthName}.INIT");
                    prevSynthInit = synthName;
                    synthY += 500;
                }
            }

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn1_Term"),
                "Stn1_Term", "CaSAdptrTerminator", "Main", 4780, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn2_Term"),
                "Stn2_Term", "CaSAdptrTerminator", "Main", 14000, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_Term"),
                "Area_Term", "CaSAdptrTerminator", "Main", 3760, 720);

            // Embedded MQTT_PUBLISH binds to a connection by matching ConnectionID value (no wire); gated so output is unchanged when off.
            if (config != null && config.MqttPublishEnabled)
            {
                string brokerUrl = config.MqttBrokerUrl;
                // Scheme follows MqttSecureTls so it can't contradict the mode: insecure→mqtt:// (needs BX1 "Insecure Application", else RC101); secure→mqtts:// (needs a TLS broker, else RC100).
                string mqttScheme = config.MqttSecureTls ? "mqtts" : "mqtt";
                brokerUrl = System.Text.RegularExpressions.Regex.Replace(
                    brokerUrl, @"^[A-Za-z][A-Za-z0-9+.\-]*://", mqttScheme + "://");

                // One MQTT_CONNECTION per PLC: UNIQUE ClientIdentifier (mosquitto evicts duplicate ids), shared ConnectionID so each resource's embedded MqttPub binds locally.
                void InjectMqttConn(string fbName, string connectionId, string clientIdentifier, int x, int y)
                {
                    if (config.UseTelemetryCat)
                    {
                        // Telemetry composite wraps the MQTT_CONNECTION with the same ConnectionID, so the embedded MqttPub still binds.
                        var cfgLit = SyslayBuilder.FormatTelemetryConfig(
                            true, connectionId, brokerUrl, clientIdentifier,
                            config.MqttSecureTls ? config.MqttValidateCert : 0,
                            config.MqttSecureTls ? (config.MqttCaCert ?? string.Empty) : string.Empty);
                        builder.AddFB(FBIdGenerator.GenerateFBId(fbName), fbName,
                            "Telemetry", "Main", x, y,
                            new Dictionary<string, string> { ["Config"] = cfgLit });
                        return;
                    }
                    var p = new Dictionary<string, string>
                    {
                        ["QI"] = SyslayBuilder.FormatBool(true),
                        ["ConnectionID"] = SyslayBuilder.FormatString(connectionId),
                        ["URL"] = SyslayBuilder.FormatString(brokerUrl),
                        ["ClientIdentifier"] = SyslayBuilder.FormatString(clientIdentifier),
                    };
                    if (config.MqttSecureTls)
                    {
                        p["ValidateCert"] = config.MqttValidateCert.ToString();
                        if (!string.IsNullOrWhiteSpace(config.MqttCaCert))
                            p["CACert"] = SyslayBuilder.FormatString(config.MqttCaCert);
                    }
                    builder.AddFB(FBIdGenerator.GenerateFBId(fbName), fbName,
                        "MQTT_CONNECTION", "Runtime.NetConnectivity", x, y, p);
                }

                bool tele = config.UseTelemetryCat;
                string bx1Name  = tele ? "Telemetry_BX1"  : "MqttConn";
                string m262Name = tele ? "Telemetry_M262" : "MqttConn_M262";
                string m580Name = tele ? "Telemetry_M580" : "MqttConn_M580";

                var mqttEntry = CodeGen.Mapping.ComponentRegistry.Get(bx1Name);
                int bx1X = mqttEntry?.X ?? 29000;
                int bx1Y = mqttEntry?.Y ?? 200;
                // Each conn is routed to its own sysres via SysresFbMirror.BucketFor; BX1 bring-up is in BuildBx1Wiring, M262/M580 below.
                InjectMqttConn(bx1Name, config.MqttConnectionName, config.MqttClientId, bx1X, bx1Y);
                InjectMqttConn(m262Name, config.MqttConnectionName, config.MqttClientM262,
                    LayoutGrid.ColumnBaseX(PlcAssignment.M262), 200);
                InjectMqttConn(m580Name, config.MqttConnectionName, config.MqttClientM580,
                    LayoutGrid.ColumnBaseX(PlcAssignment.M580), 200);
                builder.AddEventConnection($"{m262Name}.INITO", $"{m262Name}.CONNECT");
                builder.AddEventConnection($"{m580Name}.INITO", $"{m580Name}.CONNECT");
                builder.AddEventConnection("Area.INITO", $"{m262Name}.INIT");
                builder.AddEventConnection("Station2.INITO", $"{m580Name}.INIT");
                report.Missing.Add(
                    $"[MQTT] {(tele ? "Telemetry" : "MQTT_CONNECTION")} injected per resource — BX1 " +
                    $"(ClientId SMC_BX1) + M262 (SMC_M262) + M580 (SMC_M580), shared ConnectionID=" +
                    $"{config.MqttConnectionName} so each resource's embedded MqttPub binds locally; URL={brokerUrl}.");
            }


            RingWiringPlanner.BuildFeedStationWiring(builder, contents);
            RingWiringPlanner.BuildStation2Wiring(builder, contents, disassemblyFbName);
            RingWiringPlanner.BuildBx1Wiring(builder, contents, config);

            _ = config;

            // Frame widths (from LayoutGrid) MUST enclose all this PLC's FBs: EAE's MoveStyle="AnyContained" auto-grows a frame westward around any FB past its right edge, swallowing neighbours.
            builder.AddFrame("FRAME_Station1",
                LayoutGrid.FrameOriginX(PlcAssignment.M262), LayoutGrid.FrameOriginY,
                LayoutGrid.FrameWidth(PlcAssignment.M262), LayoutGrid.FrameHeight,
                "LightYellow", "Station 1   —   PLC M262", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            builder.AddFrame("FRAME_Station2_M580",
                LayoutGrid.FrameOriginX(PlcAssignment.M580), LayoutGrid.FrameOriginY,
                LayoutGrid.FrameWidth(PlcAssignment.M580), LayoutGrid.FrameHeight,
                "MediumPurple", "Station 2   —   PLC M580", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            // BX1 is the Soft dPAC host (Cover P&P) — NOT Station 2 (which is the M580 frame above).
            builder.AddFrame("FRAME_BX1",
                LayoutGrid.FrameOriginX(PlcAssignment.BX1), LayoutGrid.FrameOriginY,
                LayoutGrid.FrameWidth(PlcAssignment.BX1), LayoutGrid.FrameHeight,
                "LightGreen", "Soft dPAC   —   PLC BX1", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");

            var doc = builder.Build();
            doc.Save(fullPath);

            // EAE Solution Integrity requires an opcua.xml inside a folder named after the syslay stem.
            EnsureOpcuaXmlBesideArtefact(fullPath);

            return fullPath;
        }

        // Default fallback timing used only when Control.xml omits or zeros out State.Time.
        private static int DefaultMotionMs => GenerationConfig.Current.DefaultMotionMs;

        private static readonly HashSet<string> VacuumGripperNames =
            new(StringComparer.OrdinalIgnoreCase) { };

        // Component → emitted FB Type via TemplateMap; the 6 sites that must agree are INVARIANTS.md I-4.
        internal static string ResolveActuatorFBType(VueOneComponent actuator)
        {
            if (actuator == null) return "Five_State_Actuator_CAT";
            // Only the real UR3e (IsRobotTaskArm) → Robot_Task_CAT; Type="Robot" grippers stay Five_State/Vacuum.
            if (MapperConfig.EnableRobotTaskTail && TemplateMap.IsRobotTaskArm(actuator))
                return "Robot_Task_CAT";
            return TemplateMap.ResolveActuatorCatType(
                actuator.Name ?? string.Empty,
                actuator.States?.Count ?? 0,
                TemplateMap.IsBranchedSevenState(actuator));
        }

        // Minimal params (actuator_name + actuator_id) for actuators that are NOT plain 5-state cylinders.
        private static Dictionary<string, string> BuildMinimalActuatorParameters(
            VueOneComponent actuator, int assignedId, string fbType)
        {
            var dict = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString(actuator.Name.ToLowerInvariant()),
                ["actuator_id"]   = SyslayBuilder.FormatInt(assignedId),
            };
            // Seven_State Target Pick/Place/Home = 1/2/0 stay in lock-step with the CMD state (MapSevenStateCommandFromConditionName).
            if (string.Equals(fbType, "Seven_State_Actuator_CAT", StringComparison.OrdinalIgnoreCase))
            {
                dict["TargetPickState"]  = SyslayBuilder.FormatInt(1);
                dict["TargetPlaceState"] = SyslayBuilder.FormatInt(2);
                dict["TargetHomeState"]  = SyslayBuilder.FormatInt(0);
                // SevenStateActuator2's ECC gates every commanded transition on process_state_name = actuator_name; the ring never delivers it, so statically param it or the swivel stalls.
                dict["process_state_name"] = SyslayBuilder.FormatString(actuator.Name.ToLowerInvariant());
            }
            // Centre-home swivel settles at current_state_to_process 2=Work1 / 4=Work2 / 6=Home; Target*State feed the interlock manager at those values.
            if (string.Equals(fbType, "Seven_State_Actuator_Centre_Home_CAT", StringComparison.OrdinalIgnoreCase))
            {
                TargetEmitter.Apply(dict, work1: 2, work2: 4, home: 6);
                dict["enableToWork1FaultTimeout"] = SyslayBuilder.FormatBool(false);
                dict["enableToWork2FaultTimeout"] = SyslayBuilder.FormatBool(false);
                dict["faultTimeoutWork1"] = SyslayBuilder.FormatTimeMs(10000);
                dict["faultTimeoutWork2"] = SyslayBuilder.FormatTimeMs(10000);
                // Zeroed rule defaults; the real Bearing_PnP path overlays them via ApplyCentreHome.
                InterlockEmitter.ApplyZero(dict);
            }
            return dict;
        }

        // Placeholder placement; CanonicalLayout rewrites registered names to their ComponentRegistry coordinate post-syslay.
        private static (int X, int Y) PlcZoneActuatorPosition(PlcAssignment plc, int colIndexInPlc)
        {
            return (LayoutGrid.ColumnBaseX(plc) + colIndexInPlc * LayoutGrid.ColumnPitchX,
                    LayoutGrid.RowY(plc, LayoutRow.Actuator));
        }

        private static (int X, int Y) PlcZoneSensorPosition(PlcAssignment plc, int colIndexInPlc)
        {
            return (LayoutGrid.ColumnBaseX(plc) + colIndexInPlc * LayoutGrid.ColumnPitchX,
                    LayoutGrid.RowY(plc, LayoutRow.Sensor));
        }

        internal static bool IsBx1CoverActuator(string name) =>
            name is "CoverPNP_Hr" or "CoverPNP_Vr" or "CoverPnp_Gripper";

        public static Dictionary<string, string> BuildActuatorParameters(
            VueOneComponent actuator, int assignedId,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds = null)
        {
            int toWorkMs = ResolveStateTimeMs(actuator, stateNumber: 1, fallbackMs: DefaultMotionMs);
            int toHomeMs = ResolveStateTimeMs(actuator, stateNumber: 3, fallbackMs: DefaultMotionMs);

            var atWorkIds = ResolveAtWorkStateIds(actuator);
            var atHomeIds = ResolveAtHomeStateIds(actuator);
            bool workSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atWorkIds);
            bool homeSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atHomeIds);

            // StubSevenStateActuatorsAsFiveState (read at multiple sites that MUST agree): a Seven_State swivel emitted as Five_State has no athome/atwork, so force it sensorless.
            if (Configuration.MapperConfig.StubSevenStateActuatorsAsFiveState
                && (actuator.States.Count == 7 || TemplateMap.IsBranchedSevenState(actuator)))
            {
                workSensorFitted = false;
                homeSensorFitted = false;
            }

            // Cover actuators settle in coverMotionMs (Hr/Vr keep real DIs); the gripper has no grip/release DI, so it timer-acknowledges sensorless or the release WAIT stalls.
            if (IsBx1CoverActuator(actuator.Name))
            {
                toWorkMs = GenerationConfig.Current.CoverMotionMs;
                toHomeMs = GenerationConfig.Current.CoverMotionMs;
                if (string.Equals(actuator.Name, "CoverPnp_Gripper", StringComparison.OrdinalIgnoreCase))
                {
                    workSensorFitted = false;
                    homeSensorFitted = false;
                    int ackMs = GenerationConfig.Current.CoverGripperAckMs;
                    if (ackMs > 0) { toWorkMs = ackMs; toHomeMs = ackMs; }
                }
            }

            // M262 Ejector is open-loop (only the DO03 coil, no DIs), so force sensorless or a sensored WAIT stalls forever.
            if (MapperConfig.EnableRobotTaskTail
                && string.Equals(actuator.Name, "Ejector", StringComparison.OrdinalIgnoreCase))
            {
                workSensorFitted = false;
                homeSensorFitted = false;
            }

            // M580 shaft actuators keep real work sensors (timer-ack could release before the physical motion completes).

            var actuatorParams = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString(actuator.Name.ToLowerInvariant()),
                ["actuator_id"] = SyslayBuilder.FormatInt(assignedId),
                ["WorkSensorFitted"] = SyslayBuilder.FormatBool(workSensorFitted),
                ["HomeSensorFitted"] = SyslayBuilder.FormatBool(homeSensorFitted),
                ["toWorkTime"] = SyslayBuilder.FormatTimeMs(toWorkMs),
                ["toHomeTime"] = SyslayBuilder.FormatTimeMs(toHomeMs),
                ["faultTimeoutWork"] = SyslayBuilder.FormatTimeMs(toWorkMs * 2),
                ["faultTimeoutHome"] = SyslayBuilder.FormatTimeMs(toHomeMs * 2),
                ["enableToWorkFaultTimeout"] = SyslayBuilder.FormatBool(workSensorFitted),
                ["enableToHomeFaultTimeout"] = SyslayBuilder.FormatBool(homeSensorFitted),
            };

            // Target states feeding the embedded InterlockManager (Work1=atwork 2, Home=athome 4;
            // Five_State has no Work2).
            TargetEmitter.Apply(actuatorParams, work1: 2, work2: null, home: 4);

            InterlockEmitter.ApplyFiveState(actuatorParams, actuator, allComponents, scopedIds);

            return actuatorParams;
        }

        public static int ResolveStateTimeMs(VueOneComponent actuator, int stateNumber, int fallbackMs)
        {
            var s = actuator.States.FirstOrDefault(st => st.StateNumber == stateNumber);
            if (s == null || s.Time <= 0) return fallbackMs;
            return s.Time;
        }

        // atWork = the static state at the far end of motion (StateNumber=2).
        public static HashSet<string> ResolveAtWorkStateIds(VueOneComponent actuator)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in actuator.States.Where(st => st.StateNumber == 2 && st.StaticState))
                ids.Add(s.StateID);
            return ids;
        }

        // atHome = static states at StateNumber=0 (Initial) and =4 (post-cycle latch).
        public static HashSet<string> ResolveAtHomeStateIds(VueOneComponent actuator)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in actuator.States.Where(st =>
                (st.StateNumber == 0 || st.StateNumber == 4) && st.StaticState))
                ids.Add(s.StateID);
            return ids;
        }

        public static bool AnyComponentReferencesStates(
            IReadOnlyList<VueOneComponent> allComponents,
            VueOneComponent actuator,
            HashSet<string> stateIds)
        {
            if (stateIds.Count == 0) return false;
            foreach (var c in allComponents)
            {
                if (string.Equals(c.ComponentID, actuator.ComponentID, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var st in c.States)
                    foreach (var t in st.Transitions)
                        foreach (var cond in t.Conditions)
                            if (!string.IsNullOrEmpty(cond.ID) && stateIds.Contains(cond.ID))
                                return true;
            }
            return false;
        }

        // Legacy literal-substring lookup, no longer used by BuildActuatorParameters.
        public static bool ConditionReferences(VueOneComponent process, string actuatorName, string suffix)
        {
            var pattern = $"{actuatorName}/{suffix}";
            foreach (var state in process.States)
            {
                foreach (var trans in state.Transitions)
                {
                    foreach (var cond in trans.Conditions)
                    {
                        if (cond.Name != null &&
                            cond.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            return false;
        }

        public static string StateRprtOut(string fbType)
        {
            return string.Equals(fbType, "Process1_Generic", StringComparison.Ordinal)
                ? "stateRptCmdAdptr_out"
                : "stateRprtCmd_out";
        }

        public static string StateRprtIn(string fbType)
        {
            return string.Equals(fbType, "Process1_Generic", StringComparison.Ordinal)
                ? "stateRptCmdAdptr_in"
                : "stateRprtCmd_in";
        }

        public static string StationAdptrOut(string fbType) => "stationAdptr_out";
        public static string StationAdptrIn(string fbType) => "stationAdptr_in";

        private static readonly HashSet<string> UniversalCatTypes = new(StringComparer.Ordinal)
        {
            "Five_State_Actuator_CAT", "Sensor_Bool_CAT", "Process1_Generic",
            "Station_CAT", "Area_CAT", "CaSAdptrTerminator", "Station", "Area"
        };

        // Stripped on cleanup: PLC_RW_M262 is re-emitted every run, so a stale instance would double-declare it on the sysres.
        private static readonly HashSet<string> LegacyIoBridgeTypes = new(StringComparer.Ordinal)
        {
            "PLC_RW_M262"
        };

        public class CleanupReport
        {
            public List<string> RemovedFbs { get; } = new();
            public List<string> PreservedFbs { get; } = new();
            public List<string> Unmatched { get; } = new();
            public int RemovedConnections { get; set; }
            public List<string> DeviceCleanupLog { get; } = new();
        }

        public CleanupReport PrepareDemonstratorForGeneration(MapperConfig config)
        {
            var report = new CleanupReport();

            // Recreate the app shell (create-if-absent) BEFORE the SyslayPath2 check below.
            CodeGen.Devices.Core.ApplicationShellEmitter.EnsureApplicationShell(
                DeriveDemonstratorEaeRoot(config),
                line => report.DeviceCleanupLog.Add(line));

            if (string.IsNullOrEmpty(config.SyslayPath2) || !File.Exists(config.SyslayPath2))
                throw new FileNotFoundException(
                    $"Demonstrator syslay not configured or missing: '{config.SyslayPath2}'");

            CleanFile(config.SyslayPath2, "SubAppNetwork", report);

            // EAE renames the .sysres to the short-hex resource ID, so resolve the actual file by globbing the sysdev folder.
            foreach (var sysresPath in ResolveActualSysresPaths(config))
                CleanFile(sysresPath, "FBNetwork", report);

            CleanM262SysdevResources(config, report);

            SweepOrphanSysresPerSysdev(config, report);

            SweepBridgeFbsFromAllSysres(config, report);

            return report;
        }

        // Delete .sysres files referenced by no <Resource> in the sysdev, else EAE raises a "Repair Instances" dialog.
        private static void SweepOrphanSysresPerSysdev(MapperConfig config, CleanupReport report)
        {
            void Log(string line) => report.DeviceCleanupLog.Add($"[CleanDevice] {line}");

            string? eaeRoot = DeriveDemonstratorEaeRoot(config);
            if (string.IsNullOrEmpty(eaeRoot)) return; // harness / no project root → skip

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return;

            List<string> sysdevFiles;
            try { sysdevFiles = Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories).ToList(); }
            catch { return; }
            if (sysdevFiles.Count == 0) return; // not a real System folder

            foreach (var sysdevPath in sysdevFiles)
            {
                XDocument doc;
                try { doc = XDocument.Load(sysdevPath); }
                catch { continue; }
                var root = doc.Root;
                if (root == null) continue;
                XNamespace dns = root.GetDefaultNamespace();

                var activeIds = new HashSet<string>(
                    (root.Element(dns + "Resources")?.Elements(dns + "Resource")
                        ?? Enumerable.Empty<XElement>())
                        .Select(r => (string?)r.Attribute("ID") ?? string.Empty)
                        .Where(s => s.Length > 0),
                    StringComparer.Ordinal);
                if (activeIds.Count == 0) continue; // nothing referenced → don't touch

                // Resource files live in {sysdevFolder}/{sysdevStem}/
                var sysdevStem = Path.GetFileNameWithoutExtension(sysdevPath);
                var resDir = Path.Combine(Path.GetDirectoryName(sysdevPath)!, sysdevStem);
                if (!Directory.Exists(resDir)) continue;

                List<string> sysresFiles;
                try { sysresFiles = Directory.GetFiles(resDir, "*.sysres", SearchOption.TopDirectoryOnly).ToList(); }
                catch { continue; }
                if (sysresFiles.Count <= 1) continue; // 0 or 1 file → no possible orphan

                // Skip the whole sysdev unless every active resource has its {ID}.sysres (else filename==ID is broken).
                bool allActivePresent = activeIds.All(id =>
                    sysresFiles.Any(f => string.Equals(
                        Path.GetFileNameWithoutExtension(f), id, StringComparison.Ordinal)));
                if (!allActivePresent)
                {
                    Log($"{Path.GetFileName(sysdevPath)}: an active Resource has no matching .sysres on disk — orphan sweep skipped (filename!=ID convention not satisfied)");
                    continue;
                }

                foreach (var file in sysresFiles)
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (activeIds.Contains(stem)) continue; // active → keep
                    try
                    {
                        File.Delete(file);
                        Log($"deleted orphan sysres {Path.GetFileName(file)} under {sysdevStem} " +
                            $"(referenced by no Resource in {Path.GetFileName(sysdevPath)}; active = {string.Join(",", activeIds)})");
                    }
                    catch (Exception ex)
                    {
                        Log($"failed to delete orphan sysres {file}: {ex.Message}");
                    }
                }
            }
        }

        // Remove stale MQTT bridge FBs (MqttFmt_/MqttPub_ names only, never MqttConn) + their connections from every .sysres in place.
        private static void SweepBridgeFbsFromAllSysres(MapperConfig config, CleanupReport report)
        {
            var syslayDir = Path.GetDirectoryName(config.SyslayPath2);
            if (string.IsNullOrEmpty(syslayDir)) return;
            var sysGuidDir = Path.GetDirectoryName(syslayDir);
            if (string.IsNullOrEmpty(sysGuidDir) || !Directory.Exists(sysGuidDir)) return;

            // Guard: only act on a real EAE System folder (one with .sysdev files).
            try { if (!Directory.EnumerateFiles(sysGuidDir, "*.sysdev").Any()) return; }
            catch { return; }

            System.Xml.Linq.XNamespace ns = "https://www.se.com/LibraryElements";
            bool IsBridge(string? n) =>
                n != null && (n.StartsWith("MqttFmt_", StringComparison.Ordinal)
                           || n.StartsWith("MqttPub_", StringComparison.Ordinal));

            List<string> sysresFiles;
            try { sysresFiles = Directory.EnumerateFiles(sysGuidDir, "*.sysres", SearchOption.AllDirectories).ToList(); }
            catch { return; }

            foreach (var file in sysresFiles)
            {
                System.Xml.Linq.XDocument doc;
                try { doc = System.Xml.Linq.XDocument.Load(file, System.Xml.Linq.LoadOptions.PreserveWhitespace); }
                catch { continue; }
                var net = doc.Root?.Element(ns + "FBNetwork") ?? doc.Root?.Element(ns + "SubAppNetwork");
                if (net == null) continue;

                int removedFb = 0, removedConn = 0;
                foreach (var fb in net.Elements(ns + "FB")
                             .Where(f => IsBridge((string?)f.Attribute("Name"))).ToList())
                { fb.Remove(); removedFb++; }

                foreach (var section in new[] { "EventConnections", "DataConnections" })
                {
                    var sec = net.Element(ns + section);
                    if (sec == null) continue;
                    foreach (var c in sec.Elements(ns + "Connection").Where(c =>
                    {
                        var s = (string?)c.Attribute("Source") ?? "";
                        var d = (string?)c.Attribute("Destination") ?? "";
                        return IsBridge(s.Split('.')[0]) || IsBridge(d.Split('.')[0]);
                    }).ToList())
                    { c.Remove(); removedConn++; }
                }

                if (removedFb > 0 || removedConn > 0)
                {
                    try
                    {
                        doc.Save(file);
                        report.DeviceCleanupLog.Add(
                            $"[CleanDevice] swept {removedFb} stale bridge FB(s) + {removedConn} wire(s) " +
                            $"from {Path.GetFileName(file)}");
                    }
                    catch { /* best-effort */ }
                }
            }
        }

        // Every .sysres that actually exists in the M262 sysdev folder (SysresPath2's directory).
        private static IEnumerable<string> ResolveActualSysresPaths(MapperConfig config)
        {
            if (string.IsNullOrEmpty(config.SysresPath2)) yield break;
            var dir = Path.GetDirectoryName(config.SysresPath2);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
            foreach (var f in Directory.EnumerateFiles(dir, "*.sysres",
                         SearchOption.TopDirectoryOnly))
                yield return f;
        }

        // Dedup <Resource> entries in the M262 sysdev (first survives); each dropped Resource's sibling .sysres is deleted, the .hcf left alone.
        private static void CleanM262SysdevResources(MapperConfig config, CleanupReport report)
        {
            void Log(string line) => report.DeviceCleanupLog.Add($"[CleanDevice] {line}");

            string? eaeRoot = DeriveDemonstratorEaeRoot(config);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                Log("could not derive EAE project root from MapperConfig.SyslayPath2; sysdev dedup skipped");
                return;
            }

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir))
            {
                Log($"IEC61499/System not found under {eaeRoot}; sysdev dedup skipped");
                return;
            }

            string? sysdevPath = null;
            foreach (var candidate in Directory.EnumerateFiles(
                systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(candidate);
                    var root = doc.Root;
                    if (root == null) continue;
                    var type  = (string?)root.Attribute("Type")      ?? string.Empty;
                    var nspac = (string?)root.Attribute("Namespace") ?? string.Empty;
                    if (string.Equals(type,  "M262_dPAC", StringComparison.Ordinal) &&
                        string.Equals(nspac, "SE.DPAC",   StringComparison.Ordinal))
                    {
                        sysdevPath = candidate;
                        break;
                    }
                }
                catch { /* skip malformed; keep scanning */ }
            }
            if (sysdevPath == null)
            {
                Log($"no M262 sysdev found under {systemDir}; nothing to dedupe");
                return;
            }

            Log($"reading sysdev at {sysdevPath}");

            XDocument sysdevDoc;
            try { sysdevDoc = XDocument.Load(sysdevPath); }
            catch (Exception ex)
            {
                Log($"failed to load sysdev {sysdevPath}: {ex.Message}");
                return;
            }
            var sysdevRoot = sysdevDoc.Root;
            if (sysdevRoot == null)
            {
                Log($"sysdev {sysdevPath} has no root element; nothing to dedupe");
                return;
            }

            XNamespace ns = sysdevRoot.GetDefaultNamespace();
            var resourcesEl = sysdevRoot.Element(ns + "Resources");
            var resources = resourcesEl?.Elements(ns + "Resource").ToList()
                ?? new List<XElement>();
            int count = resources.Count;

            Log($"found {count} resources");

            // {sysdev-folder}/{sysdev-stem}/ holds the .sysres + .hcf siblings; we touch .sysres only.
            var sysdevStem = Path.GetFileNameWithoutExtension(sysdevPath);
            var sysdevDir  = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!, sysdevStem);
            int sysresCount = 0;
            if (Directory.Exists(sysdevDir))
                sysresCount = Directory.GetFiles(
                    sysdevDir, "*.sysres", SearchOption.TopDirectoryOnly).Length;

            // Fast-path: one Resource + one .sysres = canonical clean state.
            if (count == 1 && sysresCount == 1)
            {
                Log("M262 sysdev clean, no duplicates");
                return;
            }

            if (count <= 1)
            {
                Log($"M262 sysdev has {count} resource(s), nothing to dedupe");
                return;
            }

            // 2+ Resources — keep the first, drop the rest and their backing .sysres files.
            var keep = resources[0];
            var firstResourceId = (string?)keep.Attribute("ID")
                ?? (string?)keep.Attribute("Name")
                ?? "(unknown)";

            int removed = 0;
            for (int i = 1; i < resources.Count; i++)
            {
                var dup = resources[i];
                var dupId   = (string?)dup.Attribute("ID")   ?? string.Empty;
                var dupName = (string?)dup.Attribute("Name") ?? string.Empty;
                var dupIdent = !string.IsNullOrEmpty(dupId) ? dupId : dupName;

                string deletedSysresPath = string.Empty;
                if (!string.IsNullOrEmpty(dupId) && Directory.Exists(sysdevDir))
                {
                    var candidate = Path.Combine(sysdevDir, dupId + ".sysres");
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            File.Delete(candidate);
                            deletedSysresPath = candidate;
                        }
                        catch (Exception ex)
                        {
                            Log($"failed to delete sysres {candidate}: {ex.Message}");
                        }
                    }
                }

                dup.Remove();
                removed++;

                if (deletedSysresPath.Length > 0)
                    Log($"removed duplicate resource {dupIdent}, deleted sysres file {deletedSysresPath}");
                else
                    Log($"removed duplicate resource {dupIdent} (no matching .sysres file on disk)");
            }

            try
            {
                sysdevDoc.Save(sysdevPath);
            }
            catch (Exception ex)
            {
                Log($"failed to save sysdev {sysdevPath} after dedup: {ex.Message}");
                return;
            }

            Log($"removed {removed} duplicate Resource entries, kept {firstResourceId}");
            Log($"kept resource {firstResourceId}");
        }

        // Walks up from config.SyslayPath2 for the folder whose parent contains a .dfbproj.
        private static string? DeriveDemonstratorEaeRoot(MapperConfig config)
        {
            var path = config?.SyslayPath2;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dfbproj").Length > 0)
                    return Path.GetDirectoryName(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static void CleanFile(string path, string netTag, CleanupReport report)
        {
            report.DeviceCleanupLog.Add($"[Clean] file={path} root=<{netTag}>");

            XNamespace ns = "https://www.se.com/LibraryElements";
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(ns + netTag);
            if (net == null)
            {
                report.DeviceCleanupLog.Add($"[Clean] <{netTag}> not found in {Path.GetFileName(path)} — nothing to clean");
                return;
            }

            var fbsToRemove = new List<XElement>();
            var namesToRemove = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fb in net.Elements(ns + "FB").ToList())
            {
                var fbType = fb.Attribute("Type")?.Value ?? string.Empty;
                var fbName = fb.Attribute("Name")?.Value ?? string.Empty;
                var fbNs = fb.Attribute("Namespace")?.Value ?? string.Empty;

                bool isUniversal = UniversalCatTypes.Contains(fbType) ||
                    LegacyIoBridgeTypes.Contains(fbType) ||
                    (fbType == "plcStart" && fbNs == "SE.AppBase");

                if (isUniversal)
                {
                    fbsToRemove.Add(fb);
                    namesToRemove.Add(fbName);
                    report.RemovedFbs.Add($"{fbName} ({fbType})");
                    report.DeviceCleanupLog.Add($"[Clean]   FB {fbName} type={fbType} -> REMOVE");
                }
                else
                {
                    report.PreservedFbs.Add($"{fbName} ({fbType})");
                    report.DeviceCleanupLog.Add($"[Clean]   FB {fbName} type={fbType} -> PRESERVE");
                }
            }

            foreach (var fb in fbsToRemove) fb.Remove();

            int connRemovedHere = 0;
            foreach (var section in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
            {
                var s = net.Element(ns + section);
                if (s == null) continue;
                foreach (var conn in s.Elements(ns + "Connection").ToList())
                {
                    var src = conn.Attribute("Source")?.Value ?? string.Empty;
                    var dst = conn.Attribute("Destination")?.Value ?? string.Empty;
                    var srcFb = src.Split('.', 2)[0];
                    var dstFb = dst.Split('.', 2)[0];
                    if (namesToRemove.Contains(srcFb) || namesToRemove.Contains(dstFb))
                    {
                        conn.Remove();
                        report.RemovedConnections++;
                        connRemovedHere++;
                    }
                }
            }

            report.DeviceCleanupLog.Add(
                $"[Clean] {Path.GetFileName(path)}: removed {fbsToRemove.Count} FB(s), " +
                $"{connRemovedHere} connection(s)");

            doc.Save(path);
        }

        public static VueOneComponent? FindStation1Process(List<VueOneComponent> all)
        {
            var feeder = all.FirstOrDefault(c =>
                string.Equals(c.Type, "Actuator", StringComparison.Ordinal) &&
                string.Equals(c.Name, "Feeder", StringComparison.Ordinal));
            if (feeder == null) return null;

            return all.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.Ordinal) &&
                c.States.Any(s => s.Transitions.Any(t =>
                    t.Conditions.Any(cond =>
                        string.Equals(cond.ComponentID, feeder.ComponentID, StringComparison.OrdinalIgnoreCase)))));
        }

        public static (Dictionary<string, string> Outer,
                       IDictionary<string, IDictionary<string, string>> Nested,
                       RecipeArrays? Recipe)
            BuildProcessFbParameters(VueOneComponent process, List<VueOneComponent> allComponents,
                string processName, int processId,
                StationContents? contents = null, bool useRecipeStruct = false,
                bool commandFromCondition = false)
        {
            // Recipe arrays travel as Process1_Generic Parameter values; if `contents` is null, emit only the two scalars and return a null Recipe.
            var outer = new Dictionary<string, string>
            {
                ["process_name"] = SyslayBuilder.FormatString(processName),
                ["process_id"] = SyslayBuilder.FormatInt(processId)
            };

            RecipeArrays? recipe = null;
            if (contents != null)
            {
                recipe = ProcessRecipeArrayGenerator.Generate(process, contents, allComponents, processId, commandFromCondition);
                if (useRecipeStruct)
                {
                    // 6 arrays collapse into one Recipe struct; the deployer normalizers reshape the FBType to match under the same flag (else ERR_MEMBER_VAR_NOTFOUND).
                    outer["Recipe"] = SyslayBuilder.FormatRecipeTable(
                        recipe.StepType, recipe.CmdTargetName, recipe.CmdStateArr,
                        recipe.Wait1Id, recipe.Wait1State, recipe.NextStep);
                }
                else
                {
                    outer["StepType"]      = SyslayBuilder.FormatIntArray(recipe.StepType);
                    outer["CmdTargetName"] = SyslayBuilder.FormatStringArray(recipe.CmdTargetName);
                    outer["CmdStateArr"]   = SyslayBuilder.FormatIntArray(recipe.CmdStateArr);
                    outer["Wait1Id"]       = SyslayBuilder.FormatIntArray(recipe.Wait1Id);
                    outer["Wait1State"]    = SyslayBuilder.FormatIntArray(recipe.Wait1State);
                    outer["NextStep"]      = SyslayBuilder.FormatIntArray(recipe.NextStep);
                }
            }

            var nested = new Dictionary<string, IDictionary<string, string>>(StringComparer.Ordinal);
            return (outer, nested, recipe);
        }

        private static void ReportStation2Recipe(BindingApplicationReport report,
            string processName, RecipeArrays? recipe, string plcLabel)
        {
            if (recipe == null)
            {
                report.Missing.Add($"[Recipe] {processName}: no recipe built (no station contents).");
                return;
            }
            int cmd = recipe.StepType.Count(t => t == 1);
            int wait = recipe.StepType.Count(t => t == 2);
            report.Missing.Add(
                $"[Recipe] {processName} ({plcLabel}): {recipe.StepType.Count}-row recipe — " +
                $"{cmd} CMD / {wait} WAIT, {recipe.SkippedConditions.Count} condition(s) dropped, " +
                $"{recipe.Warnings.Count} generator warning(s). Cross-PLC waits resolve on the " +
                "single-ring simulator or once the M580↔BX1 broker bridge is emitted.");
            foreach (var w in recipe.Warnings)
                report.Missing.Add($"[Recipe] {processName}: {w}");
        }

        private static void AppendProcessRecipeComment(SyslayBuilder builder,
            string processName, RecipeArrays? recipe)
        {
            if (recipe == null) return;

            if (recipe.TransitionTable.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} Control.xml transition chain used for recipe:\n  - " +
                    string.Join("\n  - ", recipe.TransitionTable));
            }

            if (!string.IsNullOrWhiteSpace(recipe.OrderingSummary))
            {
                builder.AppendTopComment(
                    $" {processName} recipe ordering: " + recipe.OrderingSummary);
            }

            if (recipe.SkippedConditions.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} skipped condition(s):\n  - " +
                    string.Join("\n  - ", recipe.SkippedConditions));
            }

            if (recipe.Warnings.Count > 0)
            {
                builder.AppendTopComment(
                    $" {processName} recipe warning(s):\n  - " +
                    string.Join("\n  - ", recipe.Warnings));
            }
        }

        public string GenerateProcessFBSyslay(MapperConfig config, string controlXmlPath,
            string? processName, out BindingApplicationReport report)
        {
            report = new BindingApplicationReport();
            if (string.IsNullOrEmpty(config.SyslayPath2))
                throw new InvalidOperationException("MapperConfig.SyslayPath2 is not configured.");
            if (!File.Exists(controlXmlPath))
                throw new FileNotFoundException($"Control.xml not found: {controlXmlPath}");

            var reader = new CodeGen.IO.SystemXmlReader();
            var allComponents = reader.ReadAllComponents(controlXmlPath);

            var process = !string.IsNullOrEmpty(processName)
                ? allComponents.FirstOrDefault(c =>
                    string.Equals(c.Type, "Process", StringComparison.Ordinal) &&
                    string.Equals(c.Name, processName, StringComparison.Ordinal))
                : allComponents.FirstOrDefault(c => string.Equals(c.Type, "Process", StringComparison.Ordinal));

            if (process == null)
                throw new InvalidOperationException("No Process component found in Control.xml.");

            var fileName = Path.GetFileName(config.SyslayPath2);
            var layerId = FBIdGenerator.GenerateFBId(fileName + ":ProcessFB");
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                $"Button 1 / Process FB only. Process: {process.Name}. " +
                "Demonstrator was cleaned of universal-architecture instances before this generation.");

            // Compute the station grouping up-front so BuildProcessFbParameters can serialise the recipe.
            StationContents? contents = null;
            try
            {
                contents = new StationGroupingService().GroupStationContents(process, allComponents);
            }
            catch (Exception ex)
            {
                report.Missing.Add($"station grouping skipped: {ex.Message}");
            }

            var (outer, nested, _) = BuildProcessFbParameters(process, allComponents, process.Name, 10, contents,
                useRecipeStruct: config.UseRecipeStruct);
            builder.AddFB(FBIdGenerator.GenerateFBId(process.ComponentID),
                process.Name, "Process1_Generic", "Main", 3360, 1460, outer, nested);

            var doc = builder.Build();
            doc.Save(config.SyslayPath2);
            return config.SyslayPath2;
        }

        public string GenerateStation1TestSyslay(MapperConfig config, string controlXmlPath,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            if (string.IsNullOrEmpty(config.SyslayPath2))
                throw new InvalidOperationException("MapperConfig.SyslayPath2 is not configured.");
            // Reset SimulatorRecipeMode (the State-Transition Table preview sets it transiently) so no preview run carries over onto the rig.
            Configuration.MapperConfig.SimulatorRecipeMode = false;
            return GenerateFeedStationSyslayToPath(controlXmlPath, config.SyslayPath2, bindings, config, out report);
        }

        public string GenerateFullSystemSyslay(MapperConfig config, string controlXmlPath,
            IoBindings? bindings, out BindingApplicationReport report)
        {
            report = new BindingApplicationReport();
            if (string.IsNullOrEmpty(config.SyslayPath2))
                throw new InvalidOperationException("MapperConfig.SyslayPath2 is not configured.");
            if (!File.Exists(controlXmlPath))
                throw new FileNotFoundException($"Control.xml not found: {controlXmlPath}");

            var reader = new CodeGen.IO.SystemXmlReader();
            var allComponents = reader.ReadAllComponents(controlXmlPath);
            var processes = allComponents.Where(c => string.Equals(c.Type, "Process", StringComparison.Ordinal)).ToList();
            if (processes.Count == 0)
                throw new InvalidOperationException("No Process components found in Control.xml.");

            var fileName = Path.GetFileName(config.SyslayPath2);
            var layerId = FBIdGenerator.GenerateFBId(fileName + ":FullSystem");
            var builder = new SyslayBuilder(layerId);
            var multiProcWarning = processes.Count > 1
                ? $" WARNING: {processes.Count} Processes detected. The recipe ST in " +
                  "ProcessRuntime_Generic_v1.fbt is shared across every Process_Generic instance, " +
                  "so all Processes will run the FIRST Process's recipe until the runtime is " +
                  "split into per-Process .fbt copies."
                : string.Empty;
            builder.SetTopComment(
                $"Button 3 / Generate All. {processes.Count} stations under one Area. " +
                "Demonstrator was cleaned of universal-architecture instances before this generation." +
                multiProcWarning);
            if (processes.Count > 1)
                report.Missing.Add(
                    $"Multi-Process limitation: {processes.Count} Processes share one " +
                    "ProcessRuntime_Generic_v1.fbt; only the first Process's recipe is loaded.");

            // No top-level PLC_Start FB: Area_CAT and Station_CAT contain their own internal plcStart.
            builder.AddFB(FBIdGenerator.GenerateFBId("Area_HMI"),
                "Area_HMI", "Area_CAT", "Main", 240, 140);
            builder.AddFB(FBIdGenerator.GenerateFBId("Area"),
                "Area", "Area", "Main", 400, 580,
                new Dictionary<string, string> { ["AreaName"] = SyslayBuilder.FormatString("Area") });

            var grouping = new StationGroupingService();
            var stationNames = new List<string>();
            int xCol = 2120;
            int stationIndex = 0;
            var perStationContents = new List<(string StationName, StationContents Contents)>();

            foreach (var proc in processes)
            {
                stationIndex++;
                var stationName = $"Station{stationIndex}";
                var hmiName = $"{stationName}_HMI";
                stationNames.Add(stationName);

                StationContents contents;
                try
                {
                    contents = grouping.GroupStationContents(proc, allComponents);
                }
                catch
                {
                    report.Missing.Add($"{proc.Name} (skipped: grouping failed)");
                    continue;
                }
                perStationContents.Add((stationName, contents));

                builder.AddFB(FBIdGenerator.GenerateFBId(stationName),
                    stationName, "Station", "Main", xCol, 600,
                    new Dictionary<string, string>
                    {
                        ["StationName"] = SyslayBuilder.FormatString(stationName)
                    });
                builder.AddFB(FBIdGenerator.GenerateFBId(hmiName),
                    hmiName, "Station_CAT", "Main", xCol + 100, 100);

                var (outer, nested, _) = BuildProcessFbParameters(proc, allComponents, proc.Name, 10 + stationIndex, contents,
                    useRecipeStruct: config.UseRecipeStruct);
                var processInstanceName = $"Process{stationIndex}";
                builder.AddFB(FBIdGenerator.GenerateFBId(proc.ComponentID),
                    processInstanceName, "Process1_Generic", "Main", xCol + 1240, 1460, outer, nested);

                int sensorBase = 0;
                int actuatorBase = contents.Sensors.Count;

                for (int i = 0; i < contents.Actuators.Count; i++)
                {
                    var act = contents.Actuators[i];
                    int aid = actuatorBase + i;
                    var actParams = BuildActuatorParameters(act, aid, allComponents);

                    ActuatorBinding? ab = null;
                    bindings?.Actuators.TryGetValue(act.Name, out ab);
                    if (ab != null) report.Bound.Add((act.Name, DescribeBinding(ab)));
                    else if (bindings != null) report.Missing.Add(act.Name);

                    builder.AddFB(FBIdGenerator.GenerateFBId(act.ComponentID),
                        act.Name, "Five_State_Actuator_CAT", "Main",
                        xCol - 800 + i * 400, 2480, actParams);
                }

                for (int i = 0; i < contents.Sensors.Count; i++)
                {
                    var sen = contents.Sensors[i];
                    int sid = sensorBase + i;
                    SensorBinding? sb = null;
                    bindings?.Sensors.TryGetValue(sen.Name, out sb);
                    if (sb != null) report.Bound.Add((sen.Name, DescribeBinding(sb)));
                    else if (bindings != null) report.Missing.Add(sen.Name);

                    builder.AddFB(FBIdGenerator.GenerateFBId(sen.ComponentID),
                        sen.Name, "Sensor_Bool_CAT", "Main",
                        xCol - 560 + i * 400, 1480,
                        new Dictionary<string, string>
                        {
                            ["name"] = SyslayBuilder.FormatString(sen.Name),
                            ["id"] = SyslayBuilder.FormatInt(sid)
                        });
                }

                xCol += 2200;
            }

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_Term"),
                "Area_Term", "CaSAdptrTerminator", "Main", xCol + 200, 720);

            BuildFullSystemWiring(builder, perStationContents);

            var doc = builder.Build();
            doc.Save(config.SyslayPath2);
            return config.SyslayPath2;
        }

        private static void BuildFullSystemWiring(SyslayBuilder builder,
            List<(string StationName, StationContents Contents)> stations)
        {
            if (stations.Count == 0) return;

            var initChain = new List<string> { "Area" };
            for (int s = 0; s < stations.Count; s++)
            {
                var (stationName, contents) = stations[s];
                initChain.Add(stationName);
                foreach (var sn in contents.Sensors) initChain.Add(sn.Name);
                foreach (var a in contents.Actuators) initChain.Add(a.Name);
                initChain.Add($"Process{s + 1}");
            }
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            builder.AddAdapterConnection("Area_HMI.AreaHMIAdptrOUT", "Area.AreaHMIAdptrIN");
            for (int s = 0; s < stations.Count; s++)
            {
                var (stationName, _) = stations[s];
                builder.AddAdapterConnection($"{stationName}_HMI.StationHMIAdptrOUT", $"{stationName}.StationHMIAdptrIN");
            }

            builder.AddAdapterConnection("Area.AreaAdptrOUT", $"{stations[0].StationName}.AreaAdptrIN");
            for (int s = 0; s < stations.Count - 1; s++)
                builder.AddAdapterConnection($"{stations[s].StationName}.AreaAdptrOUT",
                    $"{stations[s + 1].StationName}.AreaAdptrIN");
            builder.AddAdapterConnection($"{stations[^1].StationName}.AreaAdptrOUT", "Area_Term.CasAdptrIN");

            for (int s = 0; s < stations.Count; s++)
            {
                var (stationName, contents) = stations[s];
                var processInstanceName = $"Process{s + 1}";

                var stationChain = new List<(string Name, string Type)>();
                foreach (var a in contents.Actuators)
                    stationChain.Add((a.Name, "Five_State_Actuator_CAT"));
                stationChain.Add((processInstanceName, "Process1_Generic"));

                if (stationChain.Count > 0)
                {
                    builder.AddAdapterConnection($"{stationName}.StationAdaptrOUT",
                        $"{stationChain[0].Name}.{StationAdptrIn(stationChain[0].Type)}");
                    for (int i = 0; i < stationChain.Count - 1; i++)
                        builder.AddAdapterConnection(
                            $"{stationChain[i].Name}.{StationAdptrOut(stationChain[i].Type)}",
                            $"{stationChain[i + 1].Name}.{StationAdptrIn(stationChain[i + 1].Type)}");
                }

                var ringComponents = new List<(string Name, string Type)>();
                foreach (var sn in contents.Sensors)
                    ringComponents.Add((sn.Name, "Sensor_Bool_CAT"));
                foreach (var a in contents.Actuators)
                    ringComponents.Add((a.Name, "Five_State_Actuator_CAT"));
                ringComponents.Add((processInstanceName, "Process1_Generic"));

                if (ringComponents.Count > 1)
                {
                    for (int i = 0; i < ringComponents.Count - 1; i++)
                        builder.AddAdapterConnection(
                            $"{ringComponents[i].Name}.{StateRprtOut(ringComponents[i].Type)}",
                            $"{ringComponents[i + 1].Name}.{StateRprtIn(ringComponents[i + 1].Type)}");
                    builder.AddAdapterConnection(
                        $"{ringComponents[^1].Name}.{StateRprtOut(ringComponents[^1].Type)}",
                        $"{ringComponents[0].Name}.{StateRprtIn(ringComponents[0].Type)}");
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // opcua.xml stub in a folder named after the artefact stem, so EAE's Solution Integrity check passes.
        public static void EnsureOpcuaXmlBesideArtefact(string artefactPath)
            => CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(artefactPath);
    }
}
