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
            /// <summary>Each <c>(Pin, Value)</c> entry rewritten in the .hcf —
            /// e.g. <c>("DI00", "RES0.M262IO.PusherAtHome")</c>. Surfaced into
            /// the Activity panel as one <c>[Hcf]</c> line per pin.</summary>
            public List<(string Pin, string Value)> HcfPinAssignments { get; } = new();
        }

        // M262IO scope is applied in the .hcf by M262HwConfigCopier.OverwriteHcfParameterValuesInMemory,
        // not here. SyslayBuilder.AddFB discards nested FB overrides anyway.
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

            var process = FindStation1Process(allComponents);
            if (process == null)
                throw new InvalidOperationException(
                    "No Process referencing a 'Feeder' actuator was found in Control.xml.");

            var grouping = new StationGroupingService();
            var fullContents = grouping.GroupStationContents(process, allComponents);

            // Button 2 scope: smallest deployable Tuesday slice — only Feeder actuator and
            // PartInHopper sensor. Other components in the Process's reference set are out of
            // scope for this button; Button 3 emits the full grouping.
            var contents = new StationContents(
                fullContents.Process,
                fullContents.Actuators
                    .Where(a => string.Equals(a.Name, "Feeder", StringComparison.Ordinal))
                    .ToList(),
                fullContents.Sensors
                    .Where(s => string.Equals(s.Name, "PartInHopper", StringComparison.Ordinal))
                    .ToList());

            var fileName = Path.GetFileName(targetSyslayPath);
            var fullPath = targetSyslayPath;

            var layerId = FBIdGenerator.GenerateFBId(fileName);
            var builder = new SyslayBuilder(layerId);
            builder.SetTopComment(
                "Phase 1: Process1 recipe arrays are emitted as syslay Parameter values on the " +
                "Process1 instance (StepType, CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep). " +
                "Scope filter still trims to Feeder + PartInHopper; out-of-scope component waits " +
                "fall back to (0,0). Sensor-to-process DataConnections still not generated. " +
                "Demonstrator was cleaned of universal-architecture instances before this generation; " +
                "restore via 'git checkout' on the Demonstrator repo to revert.");

            int sensorIdStart = 0;
            int actuatorIdStart = contents.Sensors.Count;
            const int processId = 10;

            // No top-level PLC_Start FB: Area_CAT and Station_CAT each contain their own
            // internal plcStart bootstrap, so an external one would double-bootstrap and
            // EAE flags it as a duplicate.

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

            // Resolve the Process FB instance name via InstanceNameResolver. The resolver
            // checks the optional Instance_Name_Overrides sheet first (if MappingRulesPath
            // is set on the config) and otherwise applies the default convention of stripping
            // a trailing "_process" suffix. Falls back to "Process1" only if both the override
            // sheet AND the component's Name are blank (defensive).
            var overrides = (config != null && !string.IsNullOrWhiteSpace(config.MappingRulesPath))
                ? InstanceNameOverridesLoader.Load(config.MappingRulesPath)
                : new InstanceNameOverridesLoader.Overrides();

            var processInstanceName = InstanceNameResolver.Resolve(contents.Process,
                overrides.ByComponentId, overrides.ByVueOneName);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            var (processOuter, processNested, processRecipe) = BuildProcessFbParameters(
                contents.Process, allComponents, processInstanceName, processId, contents);
            builder.AddFB(FBIdGenerator.GenerateFBId(contents.Process.ComponentID),
                processInstanceName, "Process1_Generic", "Main", 3360, 1460,
                processOuter, processNested);

            // Surface every out-of-scope condition the recipe generator dropped so the
            // .syslay file self-documents what's missing. Without this, an operator
            // reading the syslay has no clue that Checker / Transfer / Assembly_Station
            // references in Control.xml were silently filtered out by Button 2's scope.
            if (processRecipe != null && processRecipe.SkippedConditions.Count > 0)
            {
                var prefix = $" Recipe scope: {processRecipe.SkippedConditions.Count} " +
                             "Control.xml condition(s) were dropped because they reference " +
                             "components not present in this syslay (Button 2 filters to " +
                             $"Feeder + PartInHopper). Skipped:\n  - " +
                             string.Join("\n  - ", processRecipe.SkippedConditions);
                builder.AppendTopComment(prefix);
                foreach (var skip in processRecipe.SkippedConditions)
                    report.Missing.Add($"recipe: {skip}");
            }

            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                var actuator = contents.Actuators[i];
                int assignedId = actuatorIdStart + i;
                var actParams = BuildActuatorParameters(actuator, assignedId, allComponents);

                ActuatorBinding? actBinding = null;
                bindings?.Actuators.TryGetValue(actuator.Name, out actBinding);
                if (actBinding != null) report.Bound.Add((actuator.Name, DescribeBinding(actBinding)));
                else if (bindings != null) report.Missing.Add(actuator.Name);

                builder.AddFB(FBIdGenerator.GenerateFBId(actuator.ComponentID),
                    actuator.Name, "Five_State_Actuator_CAT", "Main",
                    1300 + i * 400, 2480, actParams);
            }

            for (int i = 0; i < contents.Sensors.Count; i++)
            {
                var sensor = contents.Sensors[i];
                int assignedId = sensorIdStart + i;

                SensorBinding? senBinding = null;
                bindings?.Sensors.TryGetValue(sensor.Name, out senBinding);
                if (senBinding != null) report.Bound.Add((sensor.Name, DescribeBinding(senBinding)));
                else if (bindings != null) report.Missing.Add(sensor.Name);

                builder.AddFB(FBIdGenerator.GenerateFBId(sensor.ComponentID),
                    sensor.Name, "Sensor_Bool_CAT", "Main",
                    1560 + i * 400, 1480,
                    new Dictionary<string, string>
                    {
                        ["name"] = SyslayBuilder.FormatString(sensor.Name),
                        ["id"] = SyslayBuilder.FormatInt(assignedId)
                    });
            }

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn1_Term"),
                "Stn1_Term", "CaSAdptrTerminator", "Main", 4780, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_Term"),
                "Area_Term", "CaSAdptrTerminator", "Main", 3760, 720);

            BuildFeedStationWiring(builder, contents);

            // Phase 1: recipe arrays now ride on the Process1 syslay Parameter values written
            // by BuildProcessFbParameters above. The deployed ProcessRuntime_Generic_v1.fbt is
            // no longer mutated at generation time. The MapperConfig parameter is retained for
            // call-site compatibility but unused here.
            _ = config;

            var doc = builder.Build();
            doc.Save(fullPath);

            // .hcf patching lives in the MapperUI layer (HcfPatchService) — CodeGen.dll
            // does not reference MapperUI.dll so we can't load M262HcfDocument here.
            // MainForm.btnTestStation1_Click calls HcfPatchService.PatchDeployed(...) after
            // GenerateStation1TestSyslay returns and consumes report.HcfPinAssignments.

            return fullPath;
        }

        // Default fallback timing used only when Control.xml omits or zeros out State.Time.
        private const int DefaultMotionMs = 2000;

        /// <summary>
        /// Builds Five_State_Actuator_CAT parameters straight from Control.xml.
        /// - toWorkTime / toHomeTime come from the actuator's State_Number=1 (Advancing)
        ///   and State_Number=3 (Returning) <Time> values.
        /// - faultTimeout is 2x the corresponding motion time (watchdog factor).
        /// - WorkSensorFitted / HomeSensorFitted are TRUE iff any other component's
        ///   Sequence_Condition references the actuator's atWork (StateNumber=2) or
        ///   atHome (StateNumber=0 or 4) StateID. This replaces the old fragile
        ///   "actuator.Name + /atWork" literal substring lookup.
        /// </summary>
        public static Dictionary<string, string> BuildActuatorParameters(
            VueOneComponent actuator, int assignedId,
            IReadOnlyList<VueOneComponent> allComponents)
        {
            int toWorkMs = ResolveStateTimeMs(actuator, stateNumber: 1, fallbackMs: DefaultMotionMs);
            int toHomeMs = ResolveStateTimeMs(actuator, stateNumber: 3, fallbackMs: DefaultMotionMs);

            var atWorkIds = ResolveAtWorkStateIds(actuator);
            var atHomeIds = ResolveAtHomeStateIds(actuator);
            bool workSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atWorkIds);
            bool homeSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atHomeIds);

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

        /// <summary>Returns the actuator's <Time> for the given State_Number, or fallback.</summary>
        public static int ResolveStateTimeMs(VueOneComponent actuator, int stateNumber, int fallbackMs)
        {
            var s = actuator.States.FirstOrDefault(st => st.StateNumber == stateNumber);
            if (s == null || s.Time <= 0) return fallbackMs;
            return s.Time;
        }

        /// <summary>atWork = the static state at the far end of motion (5-state pattern: StateNumber=2).</summary>
        public static HashSet<string> ResolveAtWorkStateIds(VueOneComponent actuator)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in actuator.States.Where(st => st.StateNumber == 2 && st.StaticState))
                ids.Add(s.StateID);
            return ids;
        }

        /// <summary>atHome = static states at StateNumber=0 (Initial) and =4 (post-cycle latch).</summary>
        public static HashSet<string> ResolveAtHomeStateIds(VueOneComponent actuator)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in actuator.States.Where(st =>
                (st.StateNumber == 0 || st.StateNumber == 4) && st.StaticState))
                ids.Add(s.StateID);
            return ids;
        }

        /// <summary>
        /// True iff any component (other than the actuator itself) has a Condition whose
        /// referenced state ID is in the supplied set — evidence that a sensor on that
        /// state is being read by some Process or peer state machine.
        /// </summary>
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

        /// <summary>
        /// Legacy literal-substring lookup. Retained for backward compatibility but no longer
        /// used by BuildActuatorParameters (replaced by Control.xml-driven derivation above).
        /// </summary>
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
            // Process FB instance name resolved via InstanceNameResolver — must match
            // what GenerateFeedStationSyslayToPath emitted upstream so the wire endpoints
            // line up. Without the config we can't reach the Excel overrides here, so
            // we rely on the resolver's default convention (strip "_process" suffix);
            // for explicit overrides the upstream emit and this wiring agree because
            // both go through the same resolver code path.
            var processInstanceName = InstanceNameResolver.Resolve(contents.Process);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            var initChain = new List<string>();
            initChain.Add("Area");
            initChain.Add("Station1");
            foreach (var s in contents.Sensors) initChain.Add(s.Name);
            foreach (var a in contents.Actuators) initChain.Add(a.Name);
            initChain.Add(processInstanceName);

            // No PLC_Start bootstrap edges: Area_CAT contains its own internal plcStart
            // which fires Area.INITO via INIT, propagating through this chain.
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            builder.AddAdapterConnection("Area_HMI.AreaHMIAdptrOUT", "Area.AreaHMIAdptrIN");
            builder.AddAdapterConnection("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN");
            builder.AddAdapterConnection("Area.AreaAdptrOUT", "Station1.AreaAdptrIN");
            builder.AddAdapterConnection("Station1.AreaAdptrOUT", "Area_Term.CasAdptrIN");

            // v1-assumption: Sensor_Bool_CAT lacks stationAdptr ports per .fbt verification.
            // CaSBus chain skips sensors and includes only actuators + the Process instance.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
                stationChain.Add((a.Name, "Five_State_Actuator_CAT"));
            stationChain.Add((processInstanceName, "Process1_Generic"));

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

        /// <summary>
        /// I/O-bridge FB types that must also be stripped on the demonstrator
        /// cleanup pass. PLC_RW_M262 (instance "M262IO") is re-emitted fresh
        /// every run by M262SysdevEmitter.EnsureSystemFb, so leaving a stale
        /// instance behind double-declares it on the sysres FBNetwork. Not in
        /// UniversalCatTypes because it is not a CAT and is owned by the
        /// device layer, but it still has to clear on regeneration.
        /// </summary>
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
            /// <summary>Per-action [CleanDevice] log lines emitted by the
            /// M262 sysdev Resource-dedup step. Mirrored to the Activity panel
            /// by callers — see MainForm.LogCleanup.</summary>
            public List<string> DeviceCleanupLog { get; } = new();
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

            CleanM262SysdevResources(config, report);

            return report;
        }

        /// <summary>
        /// Detect and remove duplicate <c>&lt;Resource&gt;</c> entries from the
        /// M262 sysdev so EAE's Devices tree only renders one M262_RES /
        /// RES0 row under the device. The FIRST Resource in document order is
        /// the survivor — its attributes are left untouched. Every removed
        /// Resource also has its sibling <c>{resourceId}.sysres</c> file
        /// deleted from the sysdev folder. The <c>.hcf</c>, BMTM3
        /// declarations, network profiles and IP addresses, and the kept
        /// Resource's FBNetwork contents are all left alone.
        /// </summary>
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

            // Sysdev folder layout: {sysdev-folder}/{sysdev-stem}/ holds the
            // .sysres + .hcf siblings. We touch .sysres only — .hcf is left
            // alone per spec (it carries the IO bindings).
            var sysdevStem = Path.GetFileNameWithoutExtension(sysdevPath);
            var sysdevDir  = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!, sysdevStem);
            int sysresCount = 0;
            if (Directory.Exists(sysdevDir))
                sysresCount = Directory.GetFiles(
                    sysdevDir, "*.sysres", SearchOption.TopDirectoryOnly).Length;

            // Fast-path guard: exactly one Resource AND exactly one .sysres
            // file = canonical clean state, log + return.
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

            // 2+ Resources — keep the first in document order, drop the rest
            // and their backing .sysres files.
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

                // Delete the sibling .sysres file. The .hcf is explicitly
                // NOT touched even when this Resource is removed — per spec
                // the .hcf carries the IO bindings.
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

        /// <summary>
        /// Walks up from <c>config.SyslayPath2</c> looking for the folder
        /// whose immediate parent contains a <c>.dfbproj</c> — same
        /// derivation M262SysdevEmitter.DeriveEaeProjectRoot uses, kept
        /// local here so CodeGen.dll doesn't take a back-reference on
        /// MapperUI.dll.
        /// </summary>
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
                    LegacyIoBridgeTypes.Contains(fbType) ||
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
                StationContents? contents = null)
        {
            // Phase 1+: recipe arrays travel as syslay Parameter values on the
            // Process1_Generic instance; Process1_Generic.fbt exposes 8 InputVars
            // (process_name, process_id, plus the 6 array inputs).
            //
            // The Recipe return slot exposes the in-scope ComponentRegistry,
            // SkippedConditions and Warnings so the caller (typically
            // GenerateFeedStationSyslayToPath) can surface them in the .syslay
            // top comment for self-documentation.
            //
            // If `contents` is null (defensive — caller should always pass it now)
            // we emit only the two scalar parameters and Recipe comes back null.
            var outer = new Dictionary<string, string>
            {
                ["process_name"] = SyslayBuilder.FormatString(processName),
                ["process_id"] = SyslayBuilder.FormatInt(processId)
            };

            RecipeArrays? recipe = null;
            if (contents != null)
            {
                recipe = ProcessRecipeArrayGenerator.Generate(process, contents, allComponents, processId);
                outer["StepType"]      = SyslayBuilder.FormatIntArray(recipe.StepType);
                outer["CmdTargetName"] = SyslayBuilder.FormatStringArray(recipe.CmdTargetName);
                outer["CmdStateArr"]   = SyslayBuilder.FormatIntArray(recipe.CmdStateArr);
                outer["Wait1Id"]       = SyslayBuilder.FormatIntArray(recipe.Wait1Id);
                outer["Wait1State"]    = SyslayBuilder.FormatIntArray(recipe.Wait1State);
                outer["NextStep"]      = SyslayBuilder.FormatIntArray(recipe.NextStep);
            }

            var nested = new Dictionary<string, IDictionary<string, string>>(StringComparer.Ordinal);
            return (outer, nested, recipe);
        }

        // Phase 1: ResolveProcessRuntimeFbtPath / RewriteProcessRuntimeRecipe deleted.
        // Recipes now ride on syslay Parameter values (see BuildProcessFbParameters above).
        // FbtRewriter remains in the codebase but is no longer invoked from the recipe path.

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

            // Phase 1: recipe arrays travel as syslay Parameter values. Compute the station
            // grouping up-front so BuildProcessFbParameters can serialise the recipe.
            StationContents? contents = null;
            try
            {
                contents = new StationGroupingService().GroupStationContents(process, allComponents);
            }
            catch (Exception ex)
            {
                report.Missing.Add($"station grouping skipped: {ex.Message}");
            }

            var (outer, nested, _) = BuildProcessFbParameters(process, allComponents, process.Name, 10, contents);
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

            // No top-level PLC_Start FB: Area_CAT and Station_CAT each contain their own
            // internal plcStart bootstrap.
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

                var (outer, nested, _) = BuildProcessFbParameters(proc, allComponents, proc.Name, 10 + stationIndex, contents);
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

            // Phase 1: recipe arrays now travel as syslay Parameter values per Process FB
            // instance. The deployed ProcessRuntime_Generic_v1.fbt is no longer mutated.
            // (Previously this rewrote the FBT once using the first Process's recipe, which
            // did not generalise to multi-Process projects anyway.)

            var doc = builder.Build();
            doc.Save(config.SyslayPath2);
            return config.SyslayPath2;
        }

        private static void BuildFullSystemWiring(SyslayBuilder builder,
            List<(string StationName, StationContents Contents)> stations)
        {
            if (stations.Count == 0) return;

            // No PLC_Start bootstrap edge: Area_CAT contains its own internal plcStart.

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
            // No closing PLC_Start.ACK_FIRST edge: bootstrap is internal to Area_CAT.

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
    }
}
