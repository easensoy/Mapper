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
        private const string ProcessCatType = "Process1_CAT";
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

        // Standard FB XML attributes — never set via Parameter elements
        private static readonly HashSet<string> StandardAttributes =
            new(StringComparer.OrdinalIgnoreCase)
            { "Name", "Type", "ID", "Namespace", "x", "y", "Mapping" };

        // ── Instance-level name translations (VueOne name → IEC 61499 name) ──
        private static readonly Dictionary<string, string> NameMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "Bearing_PnP", "swivel" },
            };

        // ── Rule cache per injection run ──
        private string? _mappingRulesPath;
        private Dictionary<string, List<MappingRuleEntry>>? _ruleCache;

        // ── Public API ─────────────────────────────────────────────────────

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

        /// <summary>
        /// Injects FB instances into syslay + sysres, driven by Excel mapping rules.
        /// </summary>
        /// <param name="mappingRulesPath">
        /// Path to the VueOne_IEC61499_Mapping.xlsx file.
        /// When provided, the per-CAT sheet rules drive parameter mapping.
        /// When null, falls back to hardcoded parameter logic.
        /// </param>
        public SystemInjectionResult Inject(MapperConfig config, List<VueOneComponent> components,
            string? controlXmlPath = null, string? mappingRulesPath = null)
        {
            _mappingRulesPath = mappingRulesPath;
            _ruleCache = null;

            var result = new SystemInjectionResult
            {
                SyslayPath = config.SyslayPath,
                SysresPath = config.SysresPath
            };
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

        // ── Rule loading ───────────────────────────────────────────────────

        private List<MappingRuleEntry> GetRulesForCat(string catType)
        {
            if (string.IsNullOrEmpty(_mappingRulesPath) || !File.Exists(_mappingRulesPath))
                return new List<MappingRuleEntry>();

            _ruleCache ??= new Dictionary<string, List<MappingRuleEntry>>(StringComparer.OrdinalIgnoreCase);

            if (!_ruleCache.TryGetValue(catType, out var rules))
            {
                rules = MappingRuleEngine.GetActiveRulesForCat(_mappingRulesPath, catType);
                _ruleCache[catType] = rules;
            }
            return rules;
        }

        // ── syslay / sysres injection ──────────────────────────────────────

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
                // TEMP: OLD BUGGY WIRING — both five-state and seven-state use same feedback loop
                var allActuators = FbsOfType(net, ActuatorCatType)
                    .Concat(FbsOfType(net, SevenStateActuatorCatType))
                    .Select(fb => fb.Attribute("Name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList()!;

                WireActuators(net, allActuators!, proc, result);

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

        // ── Rule-driven parameter application ──────────────────────────────

        private void ApplyParams(XElement fb, VueOneComponent comp, string catType)
        {
            // Always apply essential parameters first
            ApplyFallbackParams(fb, comp, catType);

            // Then overlay any rule-driven parameters from the Excel
            var rules = GetRulesForCat(catType);
            if (rules.Count > 0)
                ApplyRuleDrivenParams(fb, comp, catType, rules);
        }

        /// <summary>
        /// Returns true if the string is a valid IEC 61499 parameter name
        /// (identifier-like: no spaces, no parentheses, no slashes).
        /// </summary>
        private static bool IsValidParamName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            // Valid param names are identifier-like: letters, digits, underscores
            // Reject anything with spaces, parens, slashes, commas — those are descriptions
            foreach (char c in name)
            {
                if (c != '_' && !char.IsLetterOrDigit(c)) return false;
            }
            return true;
        }

        /// <summary>
        /// Applies FB parameters based on Excel mapping rules.
        /// Only sets parameters where the IEC 61499 Element is a valid parameter name
        /// (not a human-readable description like "GUID (FBType)").
        /// </summary>
        private void ApplyRuleDrivenParams(XElement fb, VueOneComponent comp, string catType,
            List<MappingRuleEntry> rules)
        {
            foreach (var rule in rules)
            {
                // Only process rules that target an IEC 61499 element
                if (string.IsNullOrWhiteSpace(rule.IEC61499Element))
                    continue;

                // Parse target: expect "FB.paramName" or just "paramName"
                string target = rule.IEC61499Element.Trim();
                string paramName;
                if (target.StartsWith("FB.", StringComparison.OrdinalIgnoreCase))
                    paramName = target.Substring(3);
                else
                    paramName = target;

                // Skip standard FB attributes — they're set during element creation
                if (StandardAttributes.Contains(paramName))
                    continue;

                // Skip descriptive text that isn't a valid parameter name
                // e.g. "GUID (FBType)", "FBType Name (in system file)"
                if (!IsValidParamName(paramName))
                    continue;

                // Resolve the source value from the VueOne component
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

            // Also apply process-specific Text param if this is a process CAT
            // (state-to-text aggregation is structural, not a single-field rule)
            if (catType == ProcessCatType)
                SetParam(fb, "Text", BuildTextParam(comp));
        }

        /// <summary>
        /// Fallback for CAT types with no Excel rules (e.g., Process1_CAT, Robot_Task_CAT).
        /// Preserves original hardcoded behavior.
        /// </summary>
        private static void ApplyFallbackParams(XElement fb, VueOneComponent comp, string catType)
        {
            if (catType == ActuatorCatType || catType == SevenStateActuatorCatType)
            {
                var name = FbName(comp, catType);
                SetParam(fb, "actuator_name", $"'{name.ToLower()}'");
            }
            else if (catType == ProcessCatType)
                SetParam(fb, "Text", BuildTextParam(comp));
        }

        /// <summary>
        /// Resolves a VueOne element reference to the actual value from the component.
        /// </summary>
        private static string? ResolveSourceValue(string vueOneElement, VueOneComponent comp, string catType)
        {
            if (string.IsNullOrWhiteSpace(vueOneElement))
                return null;

            var el = vueOneElement.Trim();

            // Component-level fields
            if (el.Equals("Component/Name", StringComparison.OrdinalIgnoreCase)
                || el.Equals("Component Name", StringComparison.OrdinalIgnoreCase))
            {
                // Apply instance-level name mapping (e.g. Bearing_PnP → swivel)
                return FbName(comp, catType);
            }

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

            // State-level fields — return first state's value as representative
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

        /// <summary>
        /// Applies the transformation described in the Excel rule to a source value.
        /// Interprets human-readable transformation descriptions.
        /// </summary>
        private static string ApplyTransformation(string value, string transformRule,
            VueOneComponent comp, string catType)
        {
            if (string.IsNullOrWhiteSpace(transformRule))
                return value;

            var rule = transformRule.ToLowerInvariant();
            string result = value;

            // Direct copy — no transformation
            if (rule.Contains("direct") && rule.Contains("copy"))
                return result;

            // Apply casing transformations
            if (rule.Contains("lowercase") || rule.Contains("lower case") || rule.Contains("lower-case"))
                result = result.ToLower();
            else if (rule.Contains("uppercase") || rule.Contains("upper case") || rule.Contains("upper-case"))
                result = result.ToUpper();

            // Apply wrapping
            if (rule.Contains("single quote") || (rule.Contains("wrap") && rule.Contains("quote"))
                || rule.Contains("'value'") || rule.Contains("quoted"))
                result = $"'{result}'";

            // SHA256 hashing
            if (rule.Contains("sha256") || rule.Contains("sha-256")
                || (rule.Contains("hash") && rule.Contains("hex")))
                result = MakeId(result, "encoded");

            // Prefix/suffix patterns
            if (rule.Contains("prefix"))
            {
                // Try to extract prefix from rule like "Prefix with 'xyz'"
                var match = System.Text.RegularExpressions.Regex.Match(transformRule, @"'([^']+)'");
                if (match.Success) result = match.Groups[1].Value + result;
            }

            return result;
        }

        /// <summary>
        /// Resolves an ASSUMED value — the value is generated or constant, not from VueOne data.
        /// </summary>
        private static string ResolveAssumedValue(MappingRuleEntry rule, VueOneComponent comp, string catType)
        {
            // For ASSUMED rules, the TransformationRule often contains the literal value
            // or a description of what to generate
            var transform = rule.TransformationRule?.Trim() ?? string.Empty;

            // If the rule text looks like a literal value (e.g., "Main", "Seven_State_Actuator_CAT")
            if (!string.IsNullOrEmpty(transform) && !transform.Contains(" "))
                return transform;

            // Otherwise try to derive from context
            if (rule.IEC61499Element?.Contains("Type", StringComparison.OrdinalIgnoreCase) == true)
                return catType;

            if (rule.IEC61499Element?.Contains("Namespace", StringComparison.OrdinalIgnoreCase) == true)
                return "Main";

            return transform;
        }

        // ── Name resolution ────────────────────────────────────────────────

        private static string FbName(VueOneComponent comp, string catType)
        {
            return NameMap.TryGetValue(comp.Name, out var mapped) ? mapped : comp.Name;
        }

        // ── Layout helpers ─────────────────────────────────────────────────

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

        // ── Wiring ─────────────────────────────────────────────────────────

        /// <summary>
        /// Wires five-state actuators with full bidirectional event flow.
        /// </summary>
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

        /// <summary>
        /// Wires seven-state actuators: command-only, NO pst_out feedback to process.
        /// The seven-state ECC has unconditional transitions (timerStart→ToHome, timerEnd→athome)
        /// and sensor-less guard conditions (atwork1=FALSE AND atwork2=FALSE) that always pass.
        /// Wiring pst_out → state_change creates an infinite event loop:
        ///   handshake→START→timerStart→ToHome→timerEnd→athome→handshake (repeats).
        /// Instead, the process sends commands via state_update/pst_event (one-way),
        /// and the actuator reports state via data connections only.
        /// </summary>
        private static void WireSevenStateActuators(XElement net, List<string> actuators,
            string proc, SystemInjectionResult result)
        {
            var ec = EnsureSection(net, "EventConnections");
            var dc = EnsureSection(net, "DataConnections");

            foreach (var name in actuators)
            {
                string lc = name.ToLower();
                // Process commands the actuator (one-way event)
                AddConn(ec, $"{proc}.state_update", $"{name}.pst_event", result);
                // NO pst_out → state_change — this is what caused the loop
                // Data connections for name matching and state reporting
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

        // ── XML helpers ────────────────────────────────────────────────────

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

        // ── Component classification ───────────────────────────────────────

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
    }
}
