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

            // Button 2 scope: the Feed Station slice — Feeder + Checker +
            // Transfer actuators and PartInHopper + PartAtChecker sensors.
            // Transfer moves a part Station1→Station2; its Control.xml wait
            // conditions (TransferAdvancing/Returning/Returned) reference only
            // the Transfer actuator itself, so no extra sensor is required to
            // unlock those recipe rows. Process/Assembly_Station references
            // (WaitingReleaseSt2, HandShake) stay out of scope; Button 3 emits
            // the full grouping.
            //
            // Order is FIXED by these arrays (sensors: PartInHopper then
            // PartAtChecker; actuators: Feeder, Checker, Transfer) — not by
            // Control.xml appearance order — so the combined sensors-first
            // component map is deterministic. With PartAtChecker present:
            // PartInHopper=0, PartAtChecker=1, Feeder=2, Checker=3, Transfer=4;
            // without it (today's rig build): PartInHopper=0, Feeder=1,
            // Checker=2, Transfer=3. The FB id/actuator_id params
            // (sensorIdStart=0, actuatorIdStart=Sensors.Count) and the recipe's
            // Wait1Id (ProcessRecipeArrayGenerator.BuildScopedComponentMap)
            // both derive from this same ordered list, so they stay in
            // lock-step with the runtime state_table. Components absent from
            // Control.xml are skipped gracefully (whatever subset is present).
            // Scope for the Test Runtime button — Feed Station (M262) +
            // Assembly Station (M580 + BX1). Ordering matters: this list
            // dictates state_table[] index allocation (sensors first, then
            // actuators), the FB id parameter, and the recipe Wait1Id lookup.
            // Disassembly Process is intentionally NOT scoped yet.
            //
            // Station 1 (M262): Feeder, Checker, Transfer + PartInHopper, PartAtChecker
            // Station 2 (M580): Bearing_PnP, Bearing_Gripper, Shaft_Hr, Shaft_Vr,
            //                   Shaft_Gripper, Clamp + BearingSensor, ShaftSensor
            // Station 2 (BX1):  CoverPNP_Hr, CoverPNP_Vr, CoverPnp_Gripper + TopCoverSenosr
            var allowedActuators = new[]
            {
                // Station 1 (M262)
                "Feeder", "Checker", "Transfer",
                // Station 2 (M580)
                "Bearing_PnP",
                "Bearing_Gripper",
                "Shaft_Hr", "Shaft_Vr", "Shaft_Gripper",
                "Clamp",
                // Station 2 (BX1)
                "CoverPNP_Hr", "CoverPNP_Vr",
                "CoverPnp_Gripper",
            };
            var allowedSensors = new[]
            {
                "PartInHopper", "PartAtChecker",
                "BearingSensor", "ShaftSensor",
                "TopCoverSenosr",
            };
            // BUG-FIX 2026-05-21: previous code drew from
            // fullContents.Actuators / fullContents.Sensors, which
            // StationGroupingService populates from ONLY the Feed_Station
            // Process's own transition conditions. Station 2 actuators
            // (Bearing_PnP, Shaft_Hr/Vr, grippers, Clamp) are referenced by
            // Assembly_Station's transitions, so they were silently dropped
            // and the purple M580 / green BX1 frames came out empty.
            //
            // Source the allowed components from `allComponents` (the full
            // Control.xml) instead. Feed_Station Process itself stays
            // Feed_Station — we're only widening the COMPONENT scope, not
            // the Process scope. Assembly_Station's recipe lives in its own
            // Process FB instance which is emitted by a later phase.
            // Grippers (Bearing_Gripper / Shaft_Gripper / CoverPnp_Gripper) carry
            // VueOne Type="Robot", not "Actuator" — a strict Type=="Actuator"
            // filter silently dropped all three, leaving the assembly recipe with
            // no grasp/release step and the M580/BX1 frames missing their grippers.
            // Accept Actuator OR Robot here; ResolveActuatorFBType still routes the
            // 5-state mechanical grippers to Five_State_Actuator_CAT and the 7-state
            // Robot arm to Seven_State. Sensor ids are unaffected, and the M262
            // actuator ids (Feeder=…, Checker, Transfer) are unchanged because the
            // grippers sort after them, so Feed_Station's recipe is byte-identical.
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
            const int processId = 10;
            // Station-2 process_ids sit ABOVE the component id space (sensors+
            // actuators occupy 0..N-1, N≈16) so a recipe Wait1Id can never collide
            // with a Process FB's own process_id — the collision
            // ProcessRecipeArrayGenerator.ValidateProcessIdInvariant throws on.
            // Feed_Station keeps process_id 10 (no M262 component reaches id 10),
            // so the proven M262 recipe is unchanged.
            const int assemblyProcessId = 101;
            const int disassemblyProcessId = 102;

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

            // Station 2 structural stack — same shape as Station 1, parented
            // under the same Area FB. Mirrors SMC_Rig_Expo_withClamp's reference
            // layout (Station + Station_HMI + Station_Term per station). The
            // post-syslay CanonicalLayout pass rewrites coordinates; the
            // values here just need to be unique so two FBs don't share an
            // initial position. M580 frame holds Station2 graphically.
            builder.AddFB(FBIdGenerator.GenerateFBId("Station2"),
                "Station2", "Station", "Main", 12000, 600,
                new Dictionary<string, string>
                {
                    ["StationName"] = SyslayBuilder.FormatString("Station2")
                });

            builder.AddFB(FBIdGenerator.GenerateFBId("Station2_HMI"),
                "Station2_HMI", "Station_CAT", "Main", 12100, 100);

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
                contents.Process, allComponents, processInstanceName, processId, contents,
                useRecipeStruct: config != null && config.SimulatorFullSystem);

            // (Removed: processOuter["BackgroundColor"] = "PaleGreen" — EAE's
            // FB-type compiler rejects any Parameter Name that is not declared
            // as an InputVar on the FBType. BackgroundColor is not an InputVar
            // on Process1_Generic, so emitting it raises ERR_MEMBER_VAR_NOTFOUND
            // at compile time. Confirmed via Schneider's nxtLibraryElement.dtd
            // and the renderer binary EngineeringEditors.dll — FB colouring is
            // not exposed via syslay parameters.)

            // Change 4: deploy-time recipe-completeness validation (SAFETY).
            // Refuse to emit a syslay that strands an actuator atwork — every
            // actuator with a work command (CmdState=1) must also have a
            // return-to-home command (CmdState=3). Change 2's auto-retract
            // makes this hold in normal operation; this is the hard backstop
            // (the strict test: we never deploy code that leaves a cylinder
            // out, which on the rig required killing air to recover).
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

                // Interlock-rule validation is now a HARD per-actuator check
                // in the actuator loop below (abort if an actuator has an
                // in-scope Control.xml interlock but RuleCount=0). No longer a
                // deferred TODO — interlock rules are emitted from Control.xml.
            }

            builder.AddFB(FBIdGenerator.GenerateFBId(contents.Process.ComponentID),
                processInstanceName, "Process1_Generic", "Main", 3360, 1460,
                processOuter, processNested);

            // Station 2 — Assembly_Station + Disassembly Process FBs. Same FBType
            // (Process1_Generic) as Feed_Station; the FBT is data-driven, so each
            // instance carries its own recipe arrays. The reference
            // SMC_Rig_Expo_withClamp hand-wires bespoke Process2_CAT/Process3_CAT;
            // our single data-driven FBT replaces all of them.
            //
            // Both recipes are now built on the HARDWARE path too (Assembly was an
            // empty placeholder, Disassembly was sim-only). They reuse the SAME
            // global, sensors-first `contents` registry as Feed_Station, so every
            // Wait1Id matches the global FB id stamped on the actuator/sensor
            // instances and mirrored onto the M580/BX1 resources (this is the
            // global-id registry that closes deferred task #25). Commands are
            // derived from the transition CONDITIONS (commandFromCondition: true) —
            // "command actuator X to state Y, then wait until it settles" — because
            // the Station-2 state names (Cover_PnP_Down, Clamping_Part, …) don't
            // encode the motion verbs Feed_Station's do. Distinct out-of-range
            // process_ids (101/102) avoid the ValidateProcessIdInvariant collision.
            //
            // Cross-PLC caveat: Assembly references BX1 cover components and
            // Disassembly references BX1 (+ skipped M262 Ejector/Robot). Those
            // waits carry the correct global id but only resolve on a single ring
            // (the simulator) or once the M580↔BX1 broker bridge is emitted — the
            // same outstanding piece HcfSymbolBinder flags. Reported per process.
            var assemblyStationProc = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, "Assembly_Station", StringComparison.Ordinal));
            // Track the Process FB instance names we emit so we can wire
            // cross-process state_update → state_change events below.
            var crossProcInstances = new List<string> { processInstanceName };
            if (assemblyStationProc != null)
            {
                var assemblyName = InstanceNameResolver.Resolve(assemblyStationProc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var (aOuter, aNested, aRecipe) = BuildProcessFbParameters(
                    assemblyStationProc, allComponents, assemblyName, assemblyProcessId,
                    contents: contents,        // global registry → global Wait1Id ids
                    useRecipeStruct: config != null && config.SimulatorFullSystem,
                    commandFromCondition: true);
                builder.AddFB(FBIdGenerator.GenerateFBId(assemblyStationProc.ComponentID),
                    assemblyName, "Process1_Generic", "Main", 12200, 1460,
                    aOuter, aNested);
                crossProcInstances.Add(assemblyName);
                ReportStation2Recipe(report, assemblyName, aRecipe, "M580");
            }
            else
            {
                report.Missing.Add(
                    "[Recipe] Assembly_Station Process not found in Control.xml — " +
                    "Station 2 (M580) frame will have actuators but no Process FB.");
            }

            // Disassembly Process FB. Same FBType (Process1_Generic) with its own
            // recipe arrays. Sits in a third X column under the BX1 zone so the
            // three Process FBs form a left→right Station1/Station2/Station3 row at
            // y=1460. Now emitted on the hardware path too (was sim-only).
            var disassyProc = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(c.Name, "Disassembly", StringComparison.Ordinal)
                 || string.Equals(c.Name, "Disassembly_Station", StringComparison.Ordinal)));
            if (disassyProc != null)
            {
                var disassyName = InstanceNameResolver.Resolve(disassyProc,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var (dOuter, dNested, dRecipe) = BuildProcessFbParameters(
                    disassyProc, allComponents, disassyName, disassemblyProcessId,
                    contents: contents,        // global registry → global Wait1Id ids
                    useRecipeStruct: config != null && config.SimulatorFullSystem,
                    commandFromCondition: true);
                builder.AddFB(FBIdGenerator.GenerateFBId(disassyProc.ComponentID),
                    disassyName, "Process1_Generic", "Main", 20800, 1460,
                    dOuter, dNested);
                crossProcInstances.Add(disassyName);
                ReportStation2Recipe(report, disassyName, dRecipe, "M580");
            }
            else
            {
                report.Missing.Add(
                    "[Recipe] Disassembly Process not found in Control.xml — " +
                    "BX1 zone will have actuators but no Disassembly Process FB.");
            }

            // Cross-process synchronisation REMOVED 2026-05-26: the prior code
            // emitted Process[i].state_update → Process[j].state_change event
            // wires across every Process pair. The deployed Process1_Generic.fbt
            // declares ONLY INIT/INITO events — no state_update (EVENT_OUTPUT) or
            // state_change (EVENT_INPUT) — so EAE rejected those wires with
            // ERR_NO_SUCH_EVENT, the whole project failed to compile, and the
            // runtime never started (Pusher / Feeder did not actuate on the rig).
            // The reference-rig wires (SMC_Rig audit E.2) presumed a Process1_CAT
            // variant that did expose those events; this Mapper's
            // Process1_Generic doesn't, so the wires can't exist here. Removing
            // them clears the three ERR_NO_SUCH_EVENT compile errors. Cross-
            // process HandShake conditions in the recipe were already dropped as
            // out-of-scope Process refs (the recipe ends at row 15 and loops),
            // so no recipe behaviour changes — only the dead, dotted wires go.

            // Bearing_PnP routes to Seven_State_Actuator_CAT (13-state PARALLEL+
            // ALTERNATIVE branched swivel → 7-state ECC). Its recipe rows are
            // settled WAITs only — the Seven_State toWork/toHome command vocabulary
            // is the remaining per-CAT step (flagged by the recipe generator).
            var bearingPnp = allComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, "Bearing_PnP", StringComparison.Ordinal));
            if (bearingPnp != null)
                report.Missing.Add(
                    $"[Recipe] Bearing_PnP ({bearingPnp.States.Count} states / PARALLEL+ALTERNATIVE " +
                    "branched) → Seven_State_Actuator_CAT; recipe emits settled WAITs (CMD vocabulary pending).");

            // Surface every out-of-scope condition the recipe generator dropped so the
            // .syslay file self-documents what's missing. Without this, an operator
            // reading the syslay has no clue that Checker / Transfer / Assembly_Station
            // references in Control.xml were silently filtered out by Button 2's scope.
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

            // Defect 3: self-document the FINAL serialised recipe ordering so
            // the collision-safe sequence (each actuator returns home before
            // any subsequent actuator advances — Transfer never advances while
            // Checker/Feeder is atwork) is verifiable straight from the syslay
            // comment, without hand-decoding the six parallel arrays.
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

            // Sensors-first state_table id map (PartInHopper=0, PartAtChecker=1,
            // Feeder=2, Checker=3, Transfer=4) — identical to the recipe's
            // Wait1Id scheme so InterlockManager.RuleSourceID and the engine
            // read the same state_table slots.
            var scopedIds = ProcessRecipeArrayGenerator.BuildScopedComponentMap(
                contents.Sensors, contents.Actuators);

            // PLC partitioning index — used to position Station 2 components in
            // the purple M580 / green BX1 zones while leaving Station 1 (M262)
            // components untouched in the existing yellow zone. Falls back to
            // a name-based guess when MapperConfig is null (legacy callers).
            var plcIndex = config != null
                ? HcfSymbolIndex.Build(config)
                : new HcfSymbolIndex();
            // Per-PLC counters used for column index within each zone.
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
                // Resolve FB instance name via the same path Process FB names use:
                //   1. Instance_Name_Overrides xlsx sheet (ComponentID, then VueOne Name)
                //   2. Default convention (suffix stripping)
                //   3. Component's raw Name as the final fallback.
                // This is the data-driven Feeder → Pusher rename — add a row to
                // the xlsx (VueOne Name="Feeder", IEC Instance Name="Pusher") and
                // it lands here. No hardcoded aliases.
                var displayName = InstanceNameResolver.Resolve(actuator,
                    overrides.ByComponentId, overrides.ByVueOneName);
                var actPlc = plcIndex.ResolveComponent(actuator.Name, bindings);

                // Five-state actuators (Feeder/Checker/Transfer + Shaft/Cover
                // single-acting cylinders + Clamp + mechanical grippers) take
                // the full data-driven parameter block built from Control.xml.
                // Non-5-state actuators (Bearing_PnP's branched 7+6, the
                // vacuum gripper, Ejector's 4-state no-sensor cylinder) carry
                // only the minimal name+id pair — their data-driven inputs
                // (Target* / Rule* / Interlock*) will be wired in a later
                // phase once the per-type recipe vocabulary lands.
                Dictionary<string, string> actParams;
                if (fbType == "Five_State_Actuator_CAT")
                {
                    actParams = BuildActuatorParameters(actuator, assignedId, allComponents, scopedIds,
                        dropInterlockConstants: config != null && config.SimulatorFullSystem);
                    // Override actuator_name so the runtime broadcast key uses
                    // the resolved instance name (Pusher), not the Control.xml
                    // raw name (Feeder). Without this, state_table.source_name
                    // would be 'feeder' while the FB instance is 'Pusher' and
                    // cross-FB lookups (interlock, recipe wait) would silently miss.
                    actParams["actuator_name"] = SyslayBuilder.FormatString(
                        displayName.ToLowerInvariant());

                    // Change 2 validation (SAFETY): if Control.xml gives this
                    // actuator an in-scope interlock (a NOT-condition referencing
                    // an in-scope component) but no rule was emitted, the safety
                    // net would be silently theoretical — refuse to generate.
                    int inScopeInterlocks = CountInScopeInterlockConds(actuator, scopedIds);
                    int emittedRuleCount = int.Parse(actParams["RuleCount"],
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (inScopeInterlocks > 0 && emittedRuleCount == 0)
                        throw new InvalidOperationException(
                            $"[Recipe] Actuator '{actuator.Name}' has {inScopeInterlocks} in-scope " +
                            "Control.xml interlock condition(s) but emitted RuleCount=0 — refusing to " +
                            "generate code whose InterlockManager passes everything through (false " +
                            "safety net). Interlock rule translation failed for this actuator.");
                    if (emittedRuleCount > 0)
                        report.Bound.Add((actuator.Name,
                            $"interlock RuleCount={emittedRuleCount}"));
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

                // Position the FB inside its PLC zone. The post-syslay
                // CanonicalLayout pass (M262SysresWireEmitter) will override
                // these coordinates for known names; the initial values here
                // just need to be inside-frame so a misnamed entry doesn't
                // float into negative-X territory before the rewrite.
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
                        // 'name' Parameter mirrors the resolved instance name so
                        // the runtime broadcast (state_table.source_name) uses the
                        // same identifier as the FB instance — keeps the rename
                        // end-to-end consistent.
                        ["name"] = SyslayBuilder.FormatString(senDisplayName),
                        ["id"] = SyslayBuilder.FormatInt(assignedId),
                        // (Removed: ["BackgroundColor"] = "LightSteelBlue" —
                        // EAE compiler rejects with ERR_MEMBER_VAR_NOTFOUND
                        // because BackgroundColor is not an InputVar on
                        // Sensor_Bool_CAT. FB colouring is not exposed via
                        // syslay parameters in EAE 24.1.)
                    });
            }

            builder.AddFB(FBIdGenerator.GenerateFBId("Stn1_Term"),
                "Stn1_Term", "CaSAdptrTerminator", "Main", 4780, 2360);

            // Station 2 chain terminator — mirrors Stn1_Term but sits inside
            // the M580 frame at the right edge of the Station 2 row. The
            // post-syslay CanonicalLayout rewrite places it at (19500, 2900).
            builder.AddFB(FBIdGenerator.GenerateFBId("Stn2_Term"),
                "Stn2_Term", "CaSAdptrTerminator", "Main", 14000, 2360);

            builder.AddFB(FBIdGenerator.GenerateFBId("Area_Term"),
                "Area_Term", "CaSAdptrTerminator", "Main", 3760, 720);

            // MQTT event-buffer: inject the ONE shared MQTT_CONNECTION when
            // enabled. Every embedded MQTT_PUBLISH (patched into the CATs by
            // TemplateLibraryDeployer) binds to it by matching ConnectionID
            // value — no wire between them. INIT then CONNECT fire at startup
            // so the link is up before the first publish; the connection's
            // QueueDepth buffers messages while the broker is unreachable.
            // Gated by MqttPublishEnabled so the hardware/sim output is
            // unchanged when MQTT is off.
            if (config != null && config.MqttPublishEnabled)
            {
                var mqttParams = new Dictionary<string, string>
                {
                    ["QI"] = SyslayBuilder.FormatBool(true),
                    // ConnectionID is a STRING (working MQTT_CONNECTION uses
                    // $ConnectionID='SoftdPAC'); the embedded MqttPub FBs carry
                    // the SAME string to bind. Quote the configured value.
                    ["ConnectionID"] = SyslayBuilder.FormatString(config.MqttConnectionId.ToString()),
                    ["URL"] = SyslayBuilder.FormatString(config.MqttBrokerUrl),
                    ["ClientIdentifier"] = SyslayBuilder.FormatString(config.MqttClientId),
                    ["CleanSession"] = SyslayBuilder.FormatBool(config.MqttCleanSession),
                    ["QueueDepth"] = SyslayBuilder.FormatInt(config.MqttQueueDepth),
                    // KeepAlive / ConnectionRetryTime are TIME-typed ports on
                    // MQTT_CONNECTION — giving them integers caused
                    // ERR_CAST_CONSTANT (ANY_INT -> TIME). The working
                    // TrainingIIoT connection sets none of these, so we leave
                    // KeepAlive / ConnectionRetryCount / ConnectionRetryTime at
                    // EAE defaults. QueueDepth (INT) + CleanSession (BOOL) stay
                    // — they are the offline-buffer essentials and are not TIME.
                };
                builder.AddFB(FBIdGenerator.GenerateFBId("MqttConn"),
                    "MqttConn", "MQTT_CONNECTION", "Runtime.NetConnectivity", 3760, 200, mqttParams);
                // Bring the link up at startup: INIT (off the Area init) then
                // CONNECT (off its own INITO). CONN_STATE_IND/retry keep it up.
                builder.AddEventConnection("Area.INITO", "MqttConn.INIT");
                builder.AddEventConnection("MqttConn.INITO", "MqttConn.CONNECT");
                report.Missing.Add(
                    $"[MQTT] MqttConn (MQTT_CONNECTION) injected at app scope — " +
                    $"ConnectionID={config.MqttConnectionId}, URL={config.MqttBrokerUrl}, " +
                    $"QueueDepth={config.MqttQueueDepth}. Embedded MqttPub FBs bind by ConnectionID.");
            }

            BuildFeedStationWiring(builder, contents);

            // Phase 1: recipe arrays now ride on the Process1 syslay Parameter values written
            // by BuildProcessFbParameters above. The deployed ProcessRuntime_Generic_v1.fbt is
            // no longer mutated at generation time. The MapperConfig parameter is retained for
            // call-site compatibility but unused here.
            _ = config;

            // Visual organisation. Two nested frames:
            //   1. FRAME_Station1 (white outer) — full canvas envelope.
            //   2. FRAME_M262   (light-yellow)  — PLC partition inside Station 1.
            // Frame bounds enclose the actual FB bounding box emitted above:
            //   HMI row at Y=2000, Area/Station1/Area_Term row at Y=2900,
            //   Sensors + Process at Y=4000, Actuators + Stn1_Term at Y=5400
            //   (visible bottom edge ~Y=6000 with default FB body height).
            //   X spans 2000..9500 (Stn1_Term right edge ~10000 with default FB width).
            // Outer  bounds (1800,1700)→(10200,6800), H=5100.
            // Inner  bounds (1880,1880)→(10120,6720), H=4840 — inset 80 on
            //   left/right/bottom, 180 on top to leave room for the Station 1
            //   36pt title above the M262 18pt title.
            // NOTE: EAE's FB type-label strip (the blue band that shows the
            // type name on each FB) is rendered by EAE's library renderer and
            // is NOT controllable via syslay parameters. The Schneider
            // reference syslays (SMC_Rig_Expo_withClamp) confirm this — they
            // set BackgroundColor only on <Frame>, never on <FB>. So we do not
            // attempt to recolour the type strip here.
            // Frame layout — three independent station frames, no combined
            // outer envelope. Each PLC zone is self-titled and self-coloured.
            //   Station 1 (M262)  — yellow,    holds Feed_Station logic
            //   Station 2 (M580)  — purple,    holds swivel-arm + shaft column
            //   Station 2 (BX1)   — green,     holds cover pick-and-place
            // The two "Station 2" frames are visually distinct rather than
            // wrapped in a single outer, matching the SMC_Rig_Expo_withClamp
            // reference which colour-codes per PLC.
            builder.AddFrame("FRAME_Station1", 1800, 1700, 8400, 5100,
                "LightYellow", "Station 1   —   PLC M262", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            builder.AddFrame("FRAME_Station2_M580", 12000, 1700, 8300, 5100,
                "MediumPurple", "Station 2   —   PLC M580", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            builder.AddFrame("FRAME_Station2_BX1", 20400, 1700, 6800, 5100,
                "LightGreen", "Station 2   —   PLC BX1", "TopCenter",
                "Microsoft Sans Serif, 36pt, style=Bold");
            // Empirical observation 2026-05-21: EAE always renders <FB> z-order
            // above <Frame> z-order, regardless of XML document order. Verified
            // by writing TYPE_STRIP Frames at the exact (FB.x, FB.y, 580, 50)
            // coordinates and observing the FB still painted on top. So Frames
            // can only colour areas not covered by an FB body — they cannot
            // recolour the EAE-rendered blue type-name strip.

            var doc = builder.Build();
            doc.Save(fullPath);

            // EAE Solution Integrity check requires an opcua.xml inside a
            // folder named after the syslay file's stem. Emit a minimal one
            // (no OPCUAAttribute entries — they're only needed when specific
            // FB attributes are exposed to OPC UA; for a regular project the
            // root-only file passes the integrity check).
            EnsureOpcuaXmlBesideArtefact(fullPath);

            // .hcf patching lives in the MapperUI layer (HcfPatchService) — CodeGen.dll
            // does not reference MapperUI.dll so we can't load M262HcfDocument here.
            // MainForm.btnTestStation1_Click calls HcfPatchService.PatchDeployed(...) after
            // GenerateStation1TestSyslay returns and consumes report.HcfPinAssignments.

            return fullPath;
        }

        // Default fallback timing used only when Control.xml omits or zeros out State.Time.
        private const int DefaultMotionMs = 2000;

        /// <summary>
        /// Vacuum-driven gripper instance names (suction cup, single coil, no
        /// athome/atwork sensor pair). Drives FB type dispatch in
        /// <see cref="ResolveActuatorFBType"/>. Mirrors MainForm's
        /// _vacuumGripperNames so the syslay emit and the Mapping Information
        /// grid agree on which gripper is vacuum vs mechanical.
        /// </summary>
        // Vacuum_Gripper_CAT is not in the Template Library yet; CoverPnp_Gripper
        // falls through to Five_State_Actuator_CAT until that CAT is deployed.
        private static readonly HashSet<string> VacuumGripperNames =
            new(StringComparer.OrdinalIgnoreCase) { };

        /// <summary>
        /// Maps a Control.xml &lt;Component&gt; to the FB Type= attribute the
        /// syslay should emit. Same logic as MainForm.Validate: 7-state and
        /// PARALLEL+ALTERNATIVE-branched actuators go to Seven_State, vacuum
        /// gripper instance names go to Vacuum_Gripper_CAT, 4-state
        /// no-sensors (Ejector) goes to Five_State_Actuator_No_Sensors_CAT,
        /// everything else falls through to Five_State_Actuator_CAT.
        /// </summary>
        private static string ResolveActuatorFBType(VueOneComponent actuator)
        {
            if (actuator == null) return "Five_State_Actuator_CAT";
            var name = actuator.Name ?? string.Empty;
            if (VacuumGripperNames.Contains(name)) return "Vacuum_Gripper_CAT";
            // Seven_State_Actuator_CAT routing restored 2026-05-21 for
            // Bearing_PnP. Two detection paths:
            //   1. States.Count == 7 — the simple "seven-state actuator"
            //      (turn-pick / pick / turn-place / place / turn-home / home).
            //   2. PARALLEL+ALTERNATIVE branched (Bearing_PnP's 13-state
            //      pattern: assembly path leaves the resting state via PARALLEL,
            //      disassembly path leaves the same state via ALTERNATIVE).
            //      The physical actuator has 3 positions + 2 coils so it
            //      collapses onto Seven_State's ECC regardless of how many
            //      logical Control.xml states it carries.
            if (actuator.States.Count == 7 || IsBranchedSevenState(actuator))
                return "Seven_State_Actuator_CAT";
            if (actuator.States.Count == 4) return "Five_State_Actuator_No_Sensors_CAT";
            return "Five_State_Actuator_CAT";
        }

        /// <summary>
        /// Mirrors <c>MainForm.IsBranchedSevenStateActuator</c> — detects the
        /// 13-state "branched swivel" pattern where the resting state has at
        /// least one outgoing PARALLEL transition AND at least one outgoing
        /// ALTERNATIVE transition. Bearing_PnP fits this pattern. The CAT
        /// itself runs as a 7-state ECC; the additional Control.xml states
        /// are absorbed into the same physical motion vocabulary.
        /// </summary>
        private static bool IsBranchedSevenState(VueOneComponent comp)
        {
            if (comp?.States == null) return false;
            foreach (var st in comp.States)
            {
                bool hasParallel = false;
                bool hasAlternative = false;
                foreach (var tr in st.Transitions)
                {
                    if (string.Equals(tr.TransitionType, "PARALLEL", StringComparison.OrdinalIgnoreCase))
                        hasParallel = true;
                    else if (string.Equals(tr.TransitionType, "ALTERNATIVE", StringComparison.OrdinalIgnoreCase))
                        hasAlternative = true;
                }
                if (hasParallel && hasAlternative) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns minimal Parameter values for actuators that are NOT plain
        /// 5-state cylinders — they only need actuator_name + actuator_id at
        /// this phase. Their Target* / Rule* / Interlock* inputs will be
        /// populated by a per-process recipe generator later. Without this,
        /// the Seven_State / Vacuum_Gripper / NoSensors FBs would either
        /// trip BuildActuatorParameters' state-number assumptions or emit
        /// stale 5-state parameters that the new FBType rejects.
        /// </summary>
        private static Dictionary<string, string> BuildMinimalActuatorParameters(
            VueOneComponent actuator, int assignedId, string fbType)
        {
            var dict = new Dictionary<string, string>
            {
                ["actuator_name"] = SyslayBuilder.FormatString(actuator.Name.ToLowerInvariant()),
                ["actuator_id"]   = SyslayBuilder.FormatInt(assignedId),
            };
            // Seven_State_Actuator_CAT-specific block removed 2026-05-21.
            // Vacuum_Gripper_CAT + Five_State_Actuator_No_Sensors_CAT (the other
            // non-5-state cases that still route through this minimal path)
            // only need the scalar actuator_name + actuator_id pair declared above.
            return dict;
        }

        /// <summary>
        /// PLC zone X/Y for actuator FB initial placement. The post-syslay
        /// CanonicalLayout rewrite (M262SysresWireEmitter) overrides these
        /// for known names; the initial coordinates here only need to land
        /// inside the right colored frame so an unrecognised name still
        /// renders visibly.
        /// Column pitch 2500 dxa, base X chosen so the column sits inside
        /// the matching coloured frame (FRAME_M262 ends at X=10120;
        /// FRAME_M580 spans 12000..20300; FRAME_BX1 spans 20400..27200).
        /// Actuators land at Y=5400 (the existing convention).
        /// </summary>
        private static (int X, int Y) PlcZoneActuatorPosition(PlcAssignment plc, int colIndexInPlc)
        {
            const int Y = 5400;
            int baseX = plc switch
            {
                PlcAssignment.M580 => 12200,
                PlcAssignment.BX1  => 20600,
                _ /* M262 / Unknown */ => 2000,
            };
            return (baseX + colIndexInPlc * 2500, Y);
        }

        /// <summary>Sensor counterpart of <see cref="PlcZoneActuatorPosition"/>.</summary>
        private static (int X, int Y) PlcZoneSensorPosition(PlcAssignment plc, int colIndexInPlc)
        {
            const int Y = 4000;
            int baseX = plc switch
            {
                PlcAssignment.M580 => 12200,
                PlcAssignment.BX1  => 20600,
                _ /* M262 / Unknown */ => 2000,
            };
            return (baseX + colIndexInPlc * 2500, Y);
        }

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
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds = null,
            bool dropInterlockConstants = false)
        {
            int toWorkMs = ResolveStateTimeMs(actuator, stateNumber: 1, fallbackMs: DefaultMotionMs);
            int toHomeMs = ResolveStateTimeMs(actuator, stateNumber: 3, fallbackMs: DefaultMotionMs);

            var atWorkIds = ResolveAtWorkStateIds(actuator);
            var atHomeIds = ResolveAtHomeStateIds(actuator);
            bool workSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atWorkIds);
            bool homeSensorFitted = AnyComponentReferencesStates(allComponents, actuator, atHomeIds);

            // InterlockManager rule arrays from this actuator's own Control.xml
            // NOT-conditions. scopedIds==null only for legacy/test callers →
            // pass-through (RuleCount=0). The real Button 2 / Button 4 path
            // passes the sensors-first map so RuleSourceID matches the recipe's
            // Wait1Id state_table indices.
            var (ruleCount, ruleFrom, ruleTo, ruleSrc, ruleBlk) =
                scopedIds != null
                    ? BuildInterlockRules(actuator, allComponents, scopedIds)
                    : (0, new int[10], new int[10], new int[10], new int[10]);

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

                // InterlockManager (CommonInterlockEvaluator) InputVars — the
                // new Five_State_Actuator_CAT embeds an InterlockManager FB
                // that needs these wired on every instance. TargetWork1State/
                // TargetHomeState follow the OSDA / Five_State convention and
                // match the recipe's Wait1State semantics (2 = atwork, 4 =
                // athome).
                ["TargetWork1State"] = SyslayBuilder.FormatInt(2),
                ["TargetHomeState"] = SyslayBuilder.FormatInt(4),
                // InterlockManager rule arrays — Control.xml NOT-conditions on
                // this actuator's transitions. RuleSourceID is the sensors-
                // first state_table index (same scheme as the recipe Wait1Id).
                ["RuleCount"] = SyslayBuilder.FormatInt(ruleCount),
                ["RuleFromState"] = SyslayBuilder.FormatIntArray(ruleFrom),
                ["RuleToState"] = SyslayBuilder.FormatIntArray(ruleTo),
                ["RuleSourceID"] = SyslayBuilder.FormatIntArray(ruleSrc),
                ["RuleBlockedState"] = SyslayBuilder.FormatIntArray(ruleBlk),

                // (Removed: ["BackgroundColor"] = "Plum" — EAE's FB-type
                // compiler rejects any <Parameter Name="..."> whose name is
                // not declared as an InputVar on the FBType. BackgroundColor
                // is not an InputVar on Five_State_Actuator_CAT, so emitting
                // it raises ERR_MEMBER_VAR_NOTFOUND at compile time. FB
                // colouring is not controllable via syslay in EAE 24.1.)
            };

            // Simulator-only interface reduction (Test Simulator button). When
            // SimulatorFullSystem is on, TemplateLibraryDeployer
            // .NormalizeFiveStateInterlockConstants bakes TargetWork1State (=2)
            // and TargetHomeState (=4) directly onto the embedded
            // InterlockManager FB inside the CAT type and DELETES the two
            // boundary InputVars. An instance <Parameter> for a var the type no
            // longer declares raises ERR_MEMBER_VAR_NOTFOUND, so drop the two
            // keys here. The exact same flag drives both halves (the deployer
            // reshapes the type, this reshapes the instance) so they can never
            // disagree. Hardware (Test Runtime, flag off) keeps emitting them.
            if (dropInterlockConstants)
            {
                actuatorParams.Remove("TargetWork1State");
                actuatorParams.Remove("TargetHomeState");

                // Simulator interface reduction: collapse the 4 parallel Rule
                // arrays into one InterlockRule[10] (RuleTable). The CAT and the
                // CommonInterlockEvaluator are reshaped to a single RuleTable
                // input by TemplateLibraryDeployer's normalizers under this same
                // flag, so the instance must emit RuleTable — emitting a
                // now-removed array input would raise ERR_MEMBER_VAR_NOTFOUND.
                // RuleCount stays separate (it's the Evaluate loop bound). The
                // named-field literal syntax was verified by the StructLiteralProbe
                // spike. Logic identical: the same numbers reach
                // InterlockManager.Evaluate, just packaged as struct fields.
                actuatorParams.Remove("RuleFromState");
                actuatorParams.Remove("RuleToState");
                actuatorParams.Remove("RuleSourceID");
                actuatorParams.Remove("RuleBlockedState");
                actuatorParams["RuleTable"] = SyslayBuilder.FormatRuleTable(
                    ruleFrom, ruleTo, ruleSrc, ruleBlk, ruleCount);

                // Derived fault-enable flags. The Mapper always sets
                // enableToWorkFaultTimeout = WorkSensorFitted and
                // enableToHomeFaultTimeout = HomeSensorFitted. Inside the CAT,
                // FB17/FB14 already AND the enable with the same sensor-fitted
                // input, so AND(fitted, fitted) = fitted. The simulator
                // normalizer re-points FB17.IN2/FB14.IN2 at the sensor-fitted
                // lines and drops these two boundary inputs, so the instance
                // must not emit them (emitting a removed InputVar would raise
                // ERR_MEMBER_VAR_NOTFOUND). Value and behaviour are identical.
                actuatorParams.Remove("enableToWorkFaultTimeout");
                actuatorParams.Remove("enableToHomeFaultTimeout");
            }

            return actuatorParams;
        }

        // Rule* InputVars are ArraySize=10 in Five_State_Actuator_CAT.fbt.
        private const int InterlockRuleCap = 10;

        /// <summary>
        /// Translate an actuator's Control.xml interlock conditions into the
        /// InterlockManager rule arrays. VueOne stores these in a STATE-level
        /// <c>&lt;Interlock_Condition&gt;</c> element (parsed into
        /// <see cref="VueOneState.InterlockConditions"/>) — NOT in the
        /// transition's Sequence_Condition. Each listed condition on a state
        /// becomes one rule: the state's transition (FromState = the state's
        /// State_Number; ToState = its &lt;Destination_State&gt; State_Number,
        /// e.g. Advancing#1 → Advanced#2) is blocked while component
        /// <c>cond.ComponentID</c> sits in the state whose StateID == cond.ID
        /// (e.g. "NOT Checker/Down"). RuleSourceID uses the sensors-first
        /// scoped map (== recipe Wait1Id scheme) so the runtime
        /// InterlockManager reads the same state_table slot the recipe waits
        /// on. Conditions referencing an OUT-of-scope component are skipped
        /// (no state_table slot exists — same invariant as the recipe's
        /// Wait1Id scope filter). Capped at 10.
        /// </summary>
        public static (int Count, int[] From, int[] To, int[] Src, int[] Blocked)
            BuildInterlockRules(VueOneComponent actuator,
                IReadOnlyList<VueOneComponent> allComponents,
                IReadOnlyDictionary<string, int> scopedIds)
        {
            var from = new int[InterlockRuleCap];
            var to = new int[InterlockRuleCap];
            var src = new int[InterlockRuleCap];
            var blk = new int[InterlockRuleCap];
            int n = 0;

            foreach (var st in actuator.States)
            {
                if (st.InterlockConditions.Count == 0) continue;

                // ToState = where this state's transition leads (the
                // <Interlock_Condition> blocks THAT transition, e.g.
                // Advancing#1 -> Advanced#2 via <Destination_State>).
                int toState = -1;
                foreach (var tr in st.Transitions)
                {
                    var dest = (tr.DestinationStateID ?? string.Empty).Trim();
                    if (dest.Length == 0) continue;
                    var ds = actuator.States.FirstOrDefault(s =>
                        string.Equals((s.StateID ?? string.Empty).Trim(), dest,
                            StringComparison.OrdinalIgnoreCase));
                    if (ds != null) { toState = ds.StateNumber; break; }
                }

                foreach (var c in st.InterlockConditions)
                {
                    var key = (c.ComponentID ?? string.Empty).Trim();
                    if (key.Length == 0) continue;
                    // Out-of-scope blocker → no state_table slot; cannot emit a
                    // valid rule (same scoping invariant as recipe Wait1Id).
                    if (!scopedIds.TryGetValue(key, out var srcId)) continue;

                    var srcComp = allComponents.FirstOrDefault(x =>
                        string.Equals((x.ComponentID ?? string.Empty).Trim(), key,
                            StringComparison.OrdinalIgnoreCase));
                    int blockedState = srcComp?.States.FirstOrDefault(s =>
                        string.Equals((s.StateID ?? string.Empty).Trim(),
                            (c.ID ?? string.Empty).Trim(),
                            StringComparison.OrdinalIgnoreCase))?.StateNumber ?? -1;

                    // Skip rather than emit a wrong safety rule if either end
                    // is unresolved.
                    if (toState < 0 || blockedState < 0) continue;
                    if (n >= InterlockRuleCap) break;

                    // RuleFromState (root-cause fix for the inert safety net).
                    // Per the CommonInterlockEvaluator Mapper Guide, a rule
                    // matches ONLY when CurrentRawState == RuleFromState AND
                    // the requested target == RuleToState. At REQ_WORK1 /
                    // REQ_HOME time the actuator is at its RESTING state
                    // (CurrentRawState=0 AtHomeInit before an advance,
                    // CurrentRawState=2 AtWork before a retract), NOT the
                    // in-transit owning state number (1 Advancing, 3
                    // Returning). The earlier code emitted FromState=
                    // st.StateNumber which made every rule inert because
                    // 0 never equals 1. Walk the actuator's own state
                    // machine to find the predecessor state (the one whose
                    // transition Destination is this owning state); that
                    // predecessor's State_Number is the resting value the
                    // FB will see. If no predecessor exists (owning state
                    // is itself the start of the chain) fall back to its
                    // own number.
                    var ownStateId = (st.StateID ?? string.Empty).Trim();
                    int fromState = st.StateNumber;
                    if (ownStateId.Length > 0)
                    {
                        var predecessor = actuator.States.FirstOrDefault(p =>
                            p.Transitions.Any(t =>
                                string.Equals(
                                    (t.DestinationStateID ?? string.Empty).Trim(),
                                    ownStateId, StringComparison.OrdinalIgnoreCase)));
                        if (predecessor != null)
                            fromState = predecessor.StateNumber;
                    }

                    // RuleBlockedState: the value the SOURCE component
                    // actually publishes on the ring. A Five_State actuator
                    // stably holds AtHomeInit=0 and AtWork=2; AtHomeEnd=4
                    // is the momentary publish during the ToHome ->
                    // AtHomeInit ECC arc (data-only guard, fires in the
                    // same run-to-stable tick), so the InterlockManager
                    // never sees state_table[srcId].state == 4 at any
                    // useful moment. A Control.xml reference to a home-
                    // finished family state on an actuator (State_Number
                    // 4: ReturnedFinished / RisingFinished etc.) must remap
                    // to 0 to fire reliably. Sensors are unchanged because
                    // Sensor_Bool publishes its Control.xml number.
                    int blockedStateRuntime = blockedState;
                    if (blockedState == 4 && srcComp != null &&
                        string.Equals(srcComp.Type, "Actuator",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        blockedStateRuntime = 0;
                    }

                    from[n] = fromState;
                    to[n] = toState;
                    src[n] = srcId;
                    blk[n] = blockedStateRuntime;
                    n++;
                }
            }
            return (n, from, to, src, blk);
        }

        /// <summary>
        /// Count of in-scope Control.xml interlock conditions on this
        /// actuator's STATE &lt;Interlock_Condition&gt; elements (blocking
        /// component present in the sensors-first scoped map). Used by the
        /// deploy-time validation: if this is &gt; 0 but the emitted
        /// RuleCount is 0, the safety net would be silently theoretical and
        /// generation must abort.
        /// </summary>
        public static int CountInScopeInterlockConds(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds)
        {
            int n = 0;
            foreach (var st in actuator.States)
            foreach (var c in st.InterlockConditions)
            {
                var key = (c.ComponentID ?? string.Empty).Trim();
                if (key.Length > 0 && scopedIds.ContainsKey(key)) n++;
            }
            return n;
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

            // PER-PLC FILTER (added 2026-05-26 after Pusher would not actuate on the rig).
            // Feed_Station lives on M262 only — Station1, Stn1_Term and the
            // Area/Station HMI adapters all belong to the M262 frame. When
            // contents.Sensors / contents.Actuators expanded to whole-system
            // scope (Phase-1 task #9), this function began stitching M580 +
            // BX1 components into a single cross-PLC chain on the syslay:
            //   ... ShaftSensor (M580) → TopCoverSenosr (BX1) → Feeder (M262) ...
            // EAE renders direct event/adapter wires that cross a resource
            // boundary as DASHED ("unresolved") because they cannot be deployed
            // — a wire on M262's resource cannot signal an FB that runs on
            // M580. Result: every M262 actuator (Feeder, Checker, Transfer)
            // showed dashed inputs on >>stationAdptr_in / >>stateRprtCmd_in,
            // EAE blocked deploy, and the Pusher never received its CMD.
            // Each PLC's sysres still gets its own contained chain via
            // ResourceWireEmitter.EmitForResource (M580 + BX1 chain themselves
            // through Station2 / their own components), so dropping non-M262
            // names here only removes the cross-PLC syslay wires — every
            // resource's runtime chain is unaffected.
            static bool IsM262(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.M262;

            var initChain = new List<string>();
            initChain.Add("Area");
            initChain.Add("Station1");
            foreach (var s in contents.Sensors)
                if (IsM262(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (IsM262(a.Name)) initChain.Add(a.Name);
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
            // M262-only filter (same rationale as the init chain above) — pre-filter
            // keeps Station1.StationAdaptrOUT -> Feeder -> Checker -> Transfer -> Feed_Station
            // -> Stn1_Term, with no cross-PLC actuators stitched in.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
                if (IsM262(a.Name))
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

            // Report-ring is M262-only too — keeps PartInHopper -> Feeder -> Checker
            // -> Transfer -> Feed_Station -> PartInHopper (closed). The M580 / BX1
            // resources each close their own ring in their own sysres.
            var ringComponents = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (IsM262(s.Name)) ringComponents.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                if (IsM262(a.Name)) ringComponents.Add((a.Name, "Five_State_Actuator_CAT"));
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

            // The configured SysresPath2 holds the *pre-rename* filename
            // (zero-GUID stem). EAE renames the .sysres on its side to the
            // short-hex resource ID (e.g. 1459BCD12760907D.sysres), so a
            // bare File.Exists(config.SysresPath2) check is almost always
            // false and the FBNetwork cleanup silently never ran — leaving
            // a stale M262IO (and every other FB) on the sysres. Resolve
            // the ACTUAL .sysres by globbing the sysdev folder and clean
            // every one found.
            foreach (var sysresPath in ResolveActualSysresPaths(config))
                CleanFile(sysresPath, "FBNetwork", report);

            CleanM262SysdevResources(config, report);

            return report;
        }

        /// <summary>
        /// Return every <c>.sysres</c> that actually exists under the M262
        /// sysdev folder. Prefers the directory of
        /// <see cref="MapperConfig.SysresPath2"/>; if that exact file is
        /// present it is included, plus any sibling <c>.sysres</c> EAE may
        /// have renamed it to. Empty if the folder can't be resolved.
        /// </summary>
        private static IEnumerable<string> ResolveActualSysresPaths(MapperConfig config)
        {
            if (string.IsNullOrEmpty(config.SysresPath2)) yield break;
            var dir = Path.GetDirectoryName(config.SysresPath2);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
            foreach (var f in Directory.EnumerateFiles(dir, "*.sysres",
                         SearchOption.TopDirectoryOnly))
                yield return f;
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
                recipe = ProcessRecipeArrayGenerator.Generate(process, contents, allComponents, processId, commandFromCondition);
                if (useRecipeStruct)
                {
                    // Simulator interface reduction: the 6 parallel recipe arrays
                    // collapse into one Recipe : ARRAY OF RecipeStep. The deployer
                    // normalizers reshape Process1_Generic + ProcessRuntime to a
                    // single Recipe input under the same flag, so the instance must
                    // emit Recipe (emitting a removed array input -> ERR_MEMBER_VAR_
                    // NOTFOUND). Same values, one struct field per array column.
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

        /// <summary>
        /// Surfaces a Station-2 process recipe into the binding report: row count,
        /// CMD/WAIT split, dropped (out-of-scope) conditions and generator warnings,
        /// plus the standing cross-PLC caveat. Keeps the Assembly_Station /
        /// Disassembly call sites terse and identical.
        /// </summary>
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

        // Phase 1: ResolveProcessRuntimeFbtPath / RewriteProcessRuntimeRecipe / FbtRewriter
        // were all deleted. Recipes now ride on syslay Parameter values (see
        // BuildProcessFbParameters above).

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

            var (outer, nested, _) = BuildProcessFbParameters(process, allComponents, process.Name, 10, contents,
                useRecipeStruct: config.SimulatorFullSystem);
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

                var (outer, nested, _) = BuildProcessFbParameters(proc, allComponents, proc.Name, 10 + stationIndex, contents,
                    useRecipeStruct: config.SimulatorFullSystem);
                var processInstanceName = $"Process{stationIndex}";
                builder.AddFB(FBIdGenerator.GenerateFBId(proc.ComponentID),
                    processInstanceName, "Process1_Generic", "Main", xCol + 1240, 1460, outer, nested);

                int sensorBase = 0;
                int actuatorBase = contents.Sensors.Count;

                for (int i = 0; i < contents.Actuators.Count; i++)
                {
                    var act = contents.Actuators[i];
                    int aid = actuatorBase + i;
                    var actParams = BuildActuatorParameters(act, aid, allComponents,
                        dropInterlockConstants: config.SimulatorFullSystem);

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

        /// <summary>
        /// Ensures EAE's Solution Integrity check passes by creating an
        /// <c>opcua.xml</c> stub inside a folder named after the syslay (or
        /// sysres) file's stem. EAE expects every artefact at
        /// <c>IEC61499/System/{sysGuid}/{containerGuid}/{stem}.syslay</c> to
        /// have a sibling folder <c>{stem}/</c> containing an
        /// <c>opcua.xml</c> — the same convention the reference
        /// <c>SMC_Rig_Expo_withClamp</c> project uses for every sysres + the
        /// syslay. The file's <c>UID</c> attribute equals the parent folder
        /// GUID (i.e. <c>{containerGuid}</c>), matching the reference's
        /// pattern. The file body lists OPCUAAttribute entries only when
        /// specific FB attributes are exposed via OPC UA; for a regular
        /// Mapper-emitted artefact the root-only file is sufficient and
        /// passes integrity. Idempotent — overwrites any existing file.
        /// </summary>
        public static void EnsureOpcuaXmlBesideArtefact(string artefactPath)
            => CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(artefactPath);
    }
}
