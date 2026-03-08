using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    /// <summary>
    /// Injects ONLY the components the caller supplies.  The caller (MainForm) is
    /// responsible for passing only the user-selected, validated subset.
    ///
    /// Overlap-free positioning:
    ///   nextY = max(existing y of same CAT type in network) + YGap
    ///   Each successive new FB increments nextY by another YGap.
    ///
    /// Wiring added for newly-injected ACTUATORS ONLY (syslay):
    ///   Event  Process.state_update         → Actuator.pst_event
    ///   Event  Actuator.pst_out             → Process.state_change
    ///   Data   Process.actuator_name        → Actuator.process_state_name
    ///   Data   Process.state_val            → Actuator.state_val
    ///   Data   Actuator.current_state_to_process → Process.{lowercase_name}
    ///
    /// INIT chain extended for newly-injected actuators (syslay):
    ///   Before:  X.INITO → Process.INIT
    ///   After:   X.INITO → Act1.INIT → ... → ActN.INITO → Process.INIT
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";
        private const string RobotCatType = "Robot_Task_CAT";

        private const int YGap = 1400;   // vertical gap between new FBs

        // X column per type — match reference syslay exactly
        private const int ActuatorX = 1300;
        private const int SensorX = 1560;
        private const int ProcessX = 3360;
        private const int RobotX = 5000;

        // ── Public API ────────────────────────────────────────────────────────

        public DiffReport PreviewDiff(MapperConfig config, List<VueOneComponent> components)
        {
            var report = new DiffReport();
            if (!File.Exists(config.SyslayPath))
            {
                report.Unsupported.Add($"syslay not found: {config.SyslayPath}");
                return report;
            }
            var net = LoadNet(config.SyslayPath, "SubAppNetwork");
            if (net == null) { report.Unsupported.Add("SubAppNetwork not found"); return report; }

            Classify(net, ActuatorCatType, Actuators(components), report);
            Classify(net, SensorCatType, Sensors(components), report);
            Classify(net, ProcessCatType, Processes(components), report);
            Classify(net, RobotCatType, Robots(components, config), report);

            foreach (var c in Unsupported(components, config))
                report.Unsupported.Add($"{c.Name} — {c.Type}/{c.States.Count} states: no template this phase");

            return report;
        }

        public SystemInjectionResult Inject(MapperConfig config, List<VueOneComponent> components)
        {
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

                foreach (var c in Unsupported(components, config))
                    result.UnsupportedComponents.Add($"{c.Name} ({c.Type}, {c.States.Count} states)");

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

        // ── Syslay ────────────────────────────────────────────────────────────

        private void InjectSyslay(MapperConfig config, List<VueOneComponent> components,
            SystemInjectionResult result, Dictionary<string, string> syslayIds)
        {
            var doc = XDocument.Load(config.SyslayPath);
            var net = doc.Root?.Element(Ns + "SubAppNetwork")
                ?? throw new Exception("SubAppNetwork not found in syslay");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var newActuators = new List<string>(); // ONLY actuators — used for wiring + INIT

            // Each group gets its own column and tracks only its own newly injected FBs
            InjectGroup(net, Processes(components), ProcessCatType, ProcessX, false, renames, result, syslayIds, null);
            InjectGroup(net, Sensors(components), SensorCatType, SensorX, false, renames, result, syslayIds, null);
            InjectGroup(net, Actuators(components), ActuatorCatType, ActuatorX, false, renames, result, syslayIds, newActuators);
            InjectGroup(net, Robots(components, config), RobotCatType, RobotX, false, renames, result, syslayIds, null);

            if (renames.Any())
                RewriteConnections(net, renames);

            if (newActuators.Any())
            {
                string? proc = FirstFbOfType(net, ProcessCatType)?.Attribute("Name")?.Value;
                if (proc != null)
                {
                    WireActuators(net, newActuators, proc, result);
                    ExtendInitChain(net, newActuators, proc, result);
                }
                else
                {
                    result.UnsupportedComponents.Add("No Process1_CAT found — wiring skipped");
                }
            }

            doc.Save(config.SyslayPath);
        }

        // ── Sysres ────────────────────────────────────────────────────────────

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
            InjectGroup(net, Robots(components, config), RobotCatType, RobotX, true, renames, result, syslayIds, null);

            if (renames.Any())
                RewriteConnections(net, renames);

            doc.Save(config.SysresPath);
        }

        // ── Group injection ───────────────────────────────────────────────────

        private void InjectGroup(
            XElement net,
            List<VueOneComponent> group,
            string catType,
            int columnX,
            bool isSysres,
            Dictionary<string, string> renames,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds,
            List<string>? newList)   // non-null only for actuators in syslay
        {
            if (!group.Any()) return;

            var groupNames = new HashSet<string>(
                group.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            var spares = FbsOfType(net, catType)
                .Where(fb => !groupNames.Contains(fb.Attribute("Name")?.Value ?? ""))
                .ToList();
            int spareIdx = 0;

            // Start Y cursor: one YGap below the current lowest of this type
            int nextY = ComputeStartY(net, catType);

            foreach (var comp in group)
            {
                // 1. Exact match already present
                var present = FindFb(net, comp.Name, catType);
                if (present != null)
                {
                    ApplyParams(present, comp, catType);
                    RecordId(present, comp.Name, isSysres, syslayIds);
                    result.SkippedFBs.Add($"{comp.Name} — already present, params updated");
                    continue;
                }

                // 2. Spare slot — remap
                if (spareIdx < spares.Count)
                {
                    var slot = spares[spareIdx++];
                    var old = slot.Attribute("Name")?.Value ?? "";
                    renames[old] = comp.Name;
                    slot.SetAttributeValue("Name", comp.Name);
                    ApplyParams(slot, comp, catType);
                    RecordId(slot, comp.Name, isSysres, syslayIds);
                    result.InjectedFBs.Add($"{comp.Name} (remapped from {old})");
                    newList?.Add(comp.Name);
                    continue;
                }

                // 3. New FB insert
                string id = isSysres ? MakeId(comp.Name, "sysres") : MakeId(comp.Name, "syslay");

                var fb = new XElement(Ns + "FB",
                    new XAttribute("ID", id),
                    new XAttribute("Name", comp.Name),
                    new XAttribute("Type", catType),
                    new XAttribute("Namespace", "Main"),
                    new XAttribute("x", columnX),
                    new XAttribute("y", nextY));

                if (isSysres && syslayIds.TryGetValue(comp.Name, out var slId))
                    fb.SetAttributeValue("Mapping", slId);

                ApplyParams(fb, comp, catType);
                net.Add(fb);

                RecordId(fb, comp.Name, isSysres, syslayIds);
                result.InjectedFBs.Add($"{comp.Name} → x={columnX}, y={nextY} (new {catType})");
                newList?.Add(comp.Name);

                nextY += YGap;   // next new FB of this type goes further down
            }
        }

        // ── Position helper ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the Y coordinate for the first new FB of a given type.
        /// = max(existing y of same type) + YGap
        /// Falls back to type-specific defaults if no existing FBs.
        /// </summary>
        private static int ComputeStartY(XElement net, string catType)
        {
            var ys = FbsOfType(net, catType)
                .Select(fb => ParseInt(fb.Attribute("y")?.Value, 0))
                .ToList();

            if (ys.Any()) return ys.Max() + YGap;

            return catType switch
            {
                ActuatorCatType => 2480,
                SensorCatType => 1480,
                ProcessCatType => 1460,
                _ => 3000
            };
        }

        // ── Actuator wiring ───────────────────────────────────────────────────

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

        // ── INIT chain ────────────────────────────────────────────────────────

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

        // ── XML helpers ───────────────────────────────────────────────────────

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
            result.InjectedFBs.Add($"  conn: {src} → {dst}");
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

        // ── Parameter helpers ─────────────────────────────────────────────────

        private static void ApplyParams(XElement fb, VueOneComponent comp, string catType)
        {
            if (catType == ActuatorCatType)
                SetParam(fb, "actuator_name", $"'{comp.Name.ToLower()}'");
            else if (catType == ProcessCatType)
                SetParam(fb, "Text", BuildTextParam(comp));
        }

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

        // ── Diff classification ───────────────────────────────────────────────

        private static void Classify(XElement net, string catType,
            List<VueOneComponent> group, DiffReport report)
        {
            var existing = FbsOfType(net, catType)
                .Select(fb => fb.Attribute("Name")?.Value ?? "").ToList();
            var spares = existing.Where(n =>
                !group.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            int si = 0;
            foreach (var c in group)
            {
                if (existing.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase)))
                    report.AlreadyPresent.Add($"{c.Name} ({catType})");
                else if (si < spares.Count)
                    report.ToBeInjected.Add($"{spares[si++]} → {c.Name} ({catType} remap)");
                else
                    report.ToBeInjected.Add($"{c.Name} (new {catType})");
            }
        }

        // ── Type filters ──────────────────────────────────────────────────────

        private static bool IsActuator(VueOneComponent c) =>
            string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase)
            && c.States.Count == 5;

        private static bool IsSensor(VueOneComponent c) =>
            string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase)
            && c.States.Count == 2;

        private static bool IsProcess(VueOneComponent c) =>
            string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase);

        private static bool IsRobot(VueOneComponent c, MapperConfig cfg) =>
            string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(cfg.RobotTemplatePath);

        private static bool IsSupported(VueOneComponent c, MapperConfig cfg) =>
            IsActuator(c) || IsSensor(c) || IsProcess(c) || IsRobot(c, cfg);

        private static List<VueOneComponent> Actuators(List<VueOneComponent> all) =>
            all.Where(IsActuator).ToList();
        private static List<VueOneComponent> Sensors(List<VueOneComponent> all) =>
            all.Where(IsSensor).ToList();
        private static List<VueOneComponent> Processes(List<VueOneComponent> all) =>
            all.Where(IsProcess).ToList();
        private static List<VueOneComponent> Robots(List<VueOneComponent> all, MapperConfig cfg) =>
            all.Where(c => IsRobot(c, cfg)).ToList();
        private static List<VueOneComponent> Unsupported(List<VueOneComponent> all, MapperConfig cfg) =>
            all.Where(c => !IsSupported(c, cfg)).ToList();

        // ── Result types ──────────────────────────────────────────────────────

        public class DiffReport
        {
            public List<string> AlreadyPresent { get; } = new();
            public List<string> ToBeInjected { get; } = new();
            public List<string> Unsupported { get; } = new();
        }
    }
}