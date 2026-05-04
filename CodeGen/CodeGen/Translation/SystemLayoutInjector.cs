using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation;

namespace MapperUI.Services
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


        public DiffReport PreviewDiff(MapperConfig config, List<VueOneComponent> components,
            string? controlXmlPath = null)
        {
            var report = new DiffReport();
            if (!File.Exists(config.SyslayPath))
            {
                report.Unsupported.Add($"syslay not found: {config.SyslayPath}");
                return report;
            }

            if (controlXmlPath != null && File.Exists(controlXmlPath))
                PatchProcessNames(components, controlXmlPath);

            var net = LoadNet(config.SyslayPath, "SubAppNetwork");
            if (net == null) { report.Unsupported.Add("SubAppNetwork not found"); return report; }

            Classify(net, ActuatorCatType, Actuators(components), report);
            Classify(net, SevenStateActuatorCatType, SevenStateActuators(components), report);
            Classify(net, SensorCatType, Sensors(components), report);
            Classify(net, ProcessCatType, Processes(components), report);
            Classify(net, RobotCatType, Robots(components, config), report);

            foreach (var c in Unsupported(components, config))
                report.Unsupported.Add($"{c.Name} {c.Type}/{c.States.Count} states: no template this phase");

            return report;
        }

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


        private static void Classify(XElement net, string catType,
            List<VueOneComponent> group, DiffReport report)
        {
            var existing = FbsOfType(net, catType)
                .Select(fb => fb.Attribute("Name")?.Value ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var wanted = group.Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var n in wanted)
                (existing.Contains(n) ? report.AlreadyPresent : report.ToBeInjected).Add(n);

            foreach (var n in existing.Where(e => !wanted.Contains(e)))
                report.Spare.Add(n);
        }

        public class DiffReport
        {
            public List<string> AlreadyPresent { get; } = new();
            public List<string> ToBeInjected { get; } = new();
            public List<string> Spare { get; } = new();
            public List<string> Unsupported { get; } = new();
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

        private static List<VueOneComponent> Robots(List<VueOneComponent> all, MapperConfig config) =>
            all.Where(c => c.Type?.Equals("Robot", StringComparison.OrdinalIgnoreCase) == true).ToList();

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
            var nested = BuildActuatorNestedOverrides(pusherBinding);

            if (pusherBinding != null)
                report.Bound.Add(("Pusher", DescribeBinding(pusherBinding)));
            else
                report.Missing.Add("Pusher");

            builder.AddFB(pusherId, "Pusher", "Five_State_Actuator_CAT", "Main", 1300, 2480, parameters, nested);

            var doc = builder.Build();
            doc.Save(targetSyslayPath);
            return targetSyslayPath;
        }

        public class BindingApplicationReport
        {
            public List<(string Component, string Detail)> Bound { get; } = new();
            public List<string> Missing { get; } = new();
        }

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
            return GenerateFeedStationSyslayToPath(controlXmlPath, targetSyslayPath, null, out _);
        }

        public string GenerateFeedStationSyslayToPath(string controlXmlPath, string targetSyslayPath,
            IoBindings? bindings, out BindingApplicationReport report)
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

            var process = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.Ordinal) &&
                string.Equals(c.Name, "Feed_Station", StringComparison.Ordinal));
            if (process == null)
                throw new InvalidOperationException("No Process named 'Feed_Station' found in Control.xml.");

            var grouping = new StationGroupingService();
            var contents = grouping.GroupStationContents(process, allComponents);

            var fileName = Path.GetFileName(targetSyslayPath);
            var fullPath = targetSyslayPath;

            var layerId = FBIdGenerator.GenerateFBId(fileName);
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                "v1 limitations: Process1 recipe arrays are default-empty; sensor-to-process " +
                "DataConnections not generated; manual recipe loading required in " +
                "ProcessRuntime_Generic_v1.initialize before Process1 will sequence. " +
                "Demonstrator was cleaned of universal-architecture instances before this generation; " +
                "restore via 'git checkout' on the Demonstrator repo to revert.");

            int sensorIdStart = 0;
            int actuatorIdStart = contents.Sensors.Count;
            const int processId = 10;

            builder.AddFB(FBIdGenerator.GenerateFBId("PLC_Start"),
                "PLC_Start", "plcStart", "SE.AppBase", 80, 580,
                new Dictionary<string, string>
                {
                    ["Prio"] = SyslayBuilder.FormatInt(35),
                    ["Delay"] = SyslayBuilder.FormatTimeMs(0)
                });

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

            builder.AddFB(FBIdGenerator.GenerateFBId(contents.Process.ComponentID),
                "Process1", "Process1_Generic", "Main", 3360, 1460,
                new Dictionary<string, string>
                {
                    ["process_name"] = SyslayBuilder.FormatString("Process1"),
                    ["process_id"] = SyslayBuilder.FormatInt(processId)
                });

            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                var actuator = contents.Actuators[i];
                int assignedId = actuatorIdStart + i;
                var actParams = BuildActuatorParameters(actuator, assignedId, contents.Process);

                ActuatorBinding? actBinding = null;
                bindings?.Actuators.TryGetValue(actuator.Name, out actBinding);
                if (actBinding != null) report.Bound.Add((actuator.Name, DescribeBinding(actBinding)));
                else if (bindings != null) report.Missing.Add(actuator.Name);

                var nestedAct = BuildActuatorNestedOverrides(actBinding);

                builder.AddFB(FBIdGenerator.GenerateFBId(actuator.ComponentID),
                    actuator.Name, "Five_State_Actuator_CAT", "Main",
                    1300 + i * 400, 2480, actParams, nestedAct);
            }

            for (int i = 0; i < contents.Sensors.Count; i++)
            {
                var sensor = contents.Sensors[i];
                int assignedId = sensorIdStart + i;

                SensorBinding? senBinding = null;
                bindings?.Sensors.TryGetValue(sensor.Name, out senBinding);
                if (senBinding != null) report.Bound.Add((sensor.Name, DescribeBinding(senBinding)));
                else if (bindings != null) report.Missing.Add(sensor.Name);

                var nestedSen = BuildSensorNestedOverrides(senBinding);

                builder.AddFB(FBIdGenerator.GenerateFBId(sensor.ComponentID),
                    sensor.Name, "Sensor_Bool_CAT", "Main",
                    1560 + i * 400, 1480,
                    new Dictionary<string, string>
                    {
                        ["name"] = SyslayBuilder.FormatString(sensor.Name),
                        ["id"] = SyslayBuilder.FormatInt(assignedId)
                    }, nestedSen);
            }

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn1_Term"),
                "Stn1_Term", "CaSAdptrTerminator", "Main", 4780, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_Term"),
                "Area_Term", "CaSAdptrTerminator", "Main", 3760, 720);

            BuildFeedStationWiring(builder, contents);

            var doc = builder.Build();
            doc.Save(fullPath);
            return fullPath;
        }

        private static Dictionary<string, string> BuildActuatorParameters(
            VueOneComponent actuator, int assignedId, VueOneComponent process)
        {
            bool workSensorFitted = ConditionReferences(process, actuator.Name, "atWork");
            bool homeSensorFitted = ConditionReferences(process, actuator.Name, "atHome");

            int toWorkMs = 2000;
            int toHomeMs = 2000;

            return new Dictionary<string, string>
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
                ["enableToHomeFaultTimeout"] = SyslayBuilder.FormatBool(homeSensorFitted)
            };
        }

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

        private static void BuildFeedStationWiring(SyslayBuilder builder, StationContents contents)
        {
            var initChain = new List<string>();
            initChain.Add("Area");
            initChain.Add("Station1");
            foreach (var s in contents.Sensors) initChain.Add(s.Name);
            foreach (var a in contents.Actuators) initChain.Add(a.Name);
            initChain.Add("Process1");

            builder.AddEventConnection("PLC_Start.FIRST_INIT", "Area.INIT");
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");
            builder.AddEventConnection("Process1.INITO", "PLC_Start.ACK_FIRST");

            builder.AddAdapterConnection("Area_HMI.AreaHMIAdptrOUT", "Area.AreaHMIAdptrIN");
            builder.AddAdapterConnection("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN");
            builder.AddAdapterConnection("Area.AreaAdptrOUT", "Station1.AreaAdptrIN");
            builder.AddAdapterConnection("Station1.AreaAdptrOUT", "Area_Term.CasAdptrIN");

            // v1-assumption: Sensor_Bool_CAT lacks stationAdptr ports per .fbt verification.
            // CaSBus chain skips sensors and includes only actuators + Process1.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
                stationChain.Add((a.Name, "Five_State_Actuator_CAT"));
            stationChain.Add(("Process1", "Process1_Generic"));

            if (stationChain.Count > 0)
            {
                builder.AddAdapterConnection("Station1.StationAdaptrOUT",
                    $"{stationChain[0].Name}.{StationAdptrIn(stationChain[0].Type)}");
                for (int i = 0; i < stationChain.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{stationChain[i].Name}.{StationAdptrOut(stationChain[i].Type)}",
                        $"{stationChain[i + 1].Name}.{StationAdptrIn(stationChain[i + 1].Type)}");
                builder.AddAdapterConnection(
                    $"{stationChain[^1].Name}.{StationAdptrOut(stationChain[^1].Type)}",
                    "Stn1_Term.CasAdptrIN");
            }

            var ringComponents = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                ringComponents.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                ringComponents.Add((a.Name, "Five_State_Actuator_CAT"));
            ringComponents.Add(("Process1", "Process1_Generic"));

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

        public class CleanupReport
        {
            public List<string> RemovedFbs { get; } = new();
            public List<string> PreservedFbs { get; } = new();
            public List<string> Unmatched { get; } = new();
            public int RemovedConnections { get; set; }
        }

        public CleanupReport PrepareDemonstratorForGeneration(MapperConfig config)
        {
            var report = new CleanupReport();

            if (string.IsNullOrEmpty(config.SyslayPath2) || !File.Exists(config.SyslayPath2))
                throw new FileNotFoundException(
                    $"Demonstrator syslay not configured or missing: '{config.SyslayPath2}'");

            CleanFile(config.SyslayPath2, "SubAppNetwork", report);

            if (!string.IsNullOrEmpty(config.SysresPath2) && File.Exists(config.SysresPath2))
                CleanFile(config.SysresPath2, "FBNetwork", report);

            return report;
        }

        private static void CleanFile(string path, string netTag, CleanupReport report)
        {
            XNamespace ns = "https://www.se.com/LibraryElements";
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(ns + netTag);
            if (net == null) return;

            var fbsToRemove = new List<XElement>();
            var namesToRemove = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fb in net.Elements(ns + "FB").ToList())
            {
                var fbType = fb.Attribute("Type")?.Value ?? string.Empty;
                var fbName = fb.Attribute("Name")?.Value ?? string.Empty;
                var fbNs = fb.Attribute("Namespace")?.Value ?? string.Empty;

                bool isUniversal = UniversalCatTypes.Contains(fbType) ||
                    (fbType == "plcStart" && fbNs == "SE.AppBase");

                if (isUniversal)
                {
                    fbsToRemove.Add(fb);
                    namesToRemove.Add(fbName);
                    report.RemovedFbs.Add($"{fbName} ({fbType})");
                }
                else
                {
                    report.PreservedFbs.Add($"{fbName} ({fbType})");
                }
            }

            foreach (var fb in fbsToRemove) fb.Remove();

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
                    }
                }
            }

            doc.Save(path);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
