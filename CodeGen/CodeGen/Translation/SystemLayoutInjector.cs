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
    /// Layout (matches reference SMC syslay image):
    ///
    ///   Column 1 — Actuators  x=1300   (stacked vertically, YGap=900)
    ///   Column 2 — Sensors    x=2600   (clearly to the right of actuators)
    ///   Column 3 — Process    x=4200   (to the right of sensors)
    ///   Column 4 — Robot      x=5800
    ///
    ///   All columns start at the same Y=1480 so FBs align horizontally.
    ///
    /// Wiring added for newly-injected ACTUATORS (syslay only):
    ///   Event  Process.state_update              → Actuator.pst_event
    ///   Event  Actuator.pst_out                  → Process.state_change
    ///   Data   Process.actuator_name             → Actuator.process_state_name
    ///   Data   Process.state_val                 → Actuator.state_val
    ///   Data   Actuator.current_state_to_process → Process.{lowercase_name}
    ///
    /// INIT chain extended for newly-injected actuators (syslay):
    ///   Before:  X.INITO → Process.INIT
    ///   After:   X.INITO → Act1.INIT → ... → ActN.INITO → Process.INIT
    ///
    /// Process name is taken directly from the VueOne component name
    /// (e.g. "Feed_Station") — never hard-coded.
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";
        private const string RobotCatType = "Robot_Task_CAT";

        // ── Layout constants ──────────────────────────────────────────────────
        // Reduced vertical gap: actuators are clearly separated but not sprawling.
        private const int YGap = 900;

        // Horizontal columns: Actuators | Sensors | Process | Robot
        // Sensors sit clearly to the right of actuators.
        // Process sits clearly to the right of sensors.
        private const int ActuatorX = 1300;
        private const int SensorX = 2600;
        private const int ProcessX = 4200;
        private const int RobotX = 5800;

        // All types start at the same Y so rows align across columns.
        private const int StartY = 1480;

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

                MapperLogger.Parse($"Parsing syslay: {System.IO.Path.GetFileName(config.SyslayPath)}");
                MapperLogger.Parse($"Parsing sysres: {System.IO.Path.GetFileName(config.SysresPath)}");

                int nAct = components.Count(c => string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase));
                int nSns = components.Count(c => string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase));
                int nPrc = components.Count(c => string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase));
                MapperLogger.Validate($"Components to inject: {components.Count} total  ({nAct} Actuator / {nSns} Sensor / {nPrc} Process)");
                foreach (var c in components)
                    MapperLogger.Validate($"  + {c.Name,-20} ({c.Type}, {c.States.Count} states)");

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
            var newActuators = new List<string>();

            // ── Rename any legacy "Process1" instance to match Control.xml name ──
            // If syslay already has a Process1_CAT named "Process1" but Control.xml
            // provides a component named "Feed_Station", rename it so wiring uses
            // the correct instance name throughout.
            RenameExistingProcessIfNeeded(net, Processes(components), renames, result);

            // Inject order: Process first (so wiring can find it by name),
            // then Sensors, then Actuators, then Robots.
            InjectGroup(net, Processes(components), ProcessCatType, ProcessX, false, renames, result, syslayIds, null);
            InjectGroup(net, Sensors(components), SensorCatType, SensorX, false, renames, result, syslayIds, null);
            InjectGroup(net, Actuators(components), ActuatorCatType, ActuatorX, false, renames, result, syslayIds, newActuators);
            InjectGroup(net, Robots(components, config), RobotCatType, RobotX, false, renames, result, syslayIds, null);

            if (renames.Any())
                RewriteConnections(net, renames);

            if (newActuators.Any())
            {
                // Use the injected/remapped process name (e.g. "Feed_Station"), not "Process1".
                string? proc = FirstFbOfType(net, ProcessCatType)?.Attribute("Name")?.Value;
                if (proc != null)
                {
                    WireActuators(net, newActuators, proc, result);
                    ExtendInitChain(net, newActuators, proc, result);
                }
                else
                {
                    result.UnsupportedComponents.Add("No Process1_CAT found in syslay — wiring skipped");
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
            List<string>? newList)
        {
            if (!group.Any()) return;

            var groupNames = new HashSet<string>(
                group.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            var spares = FbsOfType(net, catType)
                .Where(fb => !groupNames.Contains(fb.Attribute("Name")?.Value ?? ""))
                .ToList();
            int spareIdx = 0;

            int nextY = ComputeStartY(net, catType);

            foreach (var comp in group)
            {
                // 1. Exact match already present — update params only
                var present = FindFb(net, comp.Name, catType);
                if (present != null)
                {
                    ApplyParams(present, comp, catType);
                    RecordId(present, comp.Name, isSysres, syslayIds);
                    result.SkippedFBs.Add($"{comp.Name} — already present, params updated");
                    continue;
                }

                // 2. Spare slot (different name, same type) — remap
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

                // 3. Brand-new FB
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

                nextY += YGap;
            }
        }

        // ── Position helper ───────────────────────────────────────────────────

        /// <summary>
        /// Returns the Y for the first new FB of a given type.
        /// If existing FBs of this type are present → place below the lowest one.
        /// If none → use the shared StartY constant (all types align on the same row).
        /// </summary>
        private static int ComputeStartY(XElement net, string catType)
        {
            var ys = FbsOfType(net, catType)
                .Select(fb => ParseInt(fb.Attribute("y")?.Value, 0))
                .ToList();

            return ys.Any() ? ys.Max() + YGap : StartY;
        }

        // ── Actuator wiring ───────────────────────────────────────────────────

        private static void WireActuators(XElement net, List<string> actuators,
            string proc, SystemInjectionResult result)
        {
            var ec = EnsureSection(net, "EventConnections");
            var dc = EnsureSection(net, "DataConnections");

            MapperLogger.Write($"── Wiring table: Process='{proc}', Actuators={actuators.Count} ──");
            MapperLogger.Write($"  {"Source",-45} → Destination");
            MapperLogger.Write($"  {"──────────────────────────────────────────────",-45}   ─────────────────────────────────────────────");

            foreach (var name in actuators)
            {
                string lc = name.ToLower();

                // Event connections
                AddConn(ec, $"{proc}.state_update", $"{name}.pst_event", result);
                AddConn(ec, $"{name}.pst_out", $"{proc}.state_change", result);
                // Data connections
                AddConn(dc, $"{proc}.actuator_name", $"{name}.process_state_name", result);
                AddConn(dc, $"{proc}.state_val", $"{name}.state_val", result);
                AddConn(dc, $"{name}.current_state_to_process", $"{proc}.{lc}", result);

                // Log wiring table row
                MapperLogger.Write($"  {proc}.state_update,-45} → {name}.pst_event");
                MapperLogger.Write($"  {$"{name}.pst_out",-45} → {proc}.state_change");
                MapperLogger.Write($"  {$"{proc}.actuator_name",-45} → {name}.process_state_name  [DATA]");
                MapperLogger.Write($"  {$"{proc}.state_val",-45} → {name}.state_val  [DATA]");
                MapperLogger.Write($"  {$"{name}.current_state_to_process",-45} → {proc}.{lc}  [DATA]");
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

        // ── Component filter helpers ──────────────────────────────────────────

        private static List<VueOneComponent> Actuators(List<VueOneComponent> all) =>
            all.Where(c => string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
               .ToList();

        private static List<VueOneComponent> Sensors(List<VueOneComponent> all) =>
            all.Where(c => string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
               .ToList();

        private static List<VueOneComponent> Processes(List<VueOneComponent> all) =>
            all.Where(c => string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase))
               .ToList();

        private static List<VueOneComponent> Robots(List<VueOneComponent> all, MapperConfig cfg) =>
            string.IsNullOrWhiteSpace(cfg.RobotTemplatePath)
                ? new List<VueOneComponent>()
                : all.Where(c => string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase))
                     .ToList();

        private static List<VueOneComponent> Unsupported(List<VueOneComponent> all, MapperConfig cfg)
        {
            var supported = new HashSet<VueOneComponent>(
                Actuators(all).Concat(Sensors(all)).Concat(Processes(all)).Concat(Robots(all, cfg)));
            return all.Except(supported).ToList();
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

        /// <summary>
        /// If the syslay contains a Process1_CAT instance whose Name does NOT match
        /// any process component in Control.xml (e.g. the legacy "Process1" stub),
        /// rename it in-place to the Control.xml component name (e.g. "Feed_Station")
        /// and record the rename in the renames dictionary so connections are updated.
        /// 
        /// This handles the case where EAE's baseline project ships with "Process1"
        /// but VueOne Control.xml names the process "Feed_Station".
        /// </summary>
        private static void RenameExistingProcessIfNeeded(
            XElement net,
            IEnumerable<VueOneComponent> processComps,
            Dictionary<string, string> renames,
            SystemInjectionResult result)
        {
            var targets = processComps.ToList();
            if (!targets.Any()) return;

            var existing = FbsOfType(net, ProcessCatType).ToList();
            foreach (var fb in existing)
            {
                string oldName = fb.Attribute("Name")?.Value ?? "";
                // Skip if name already matches one of our components
                if (targets.Any(c => string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Rename to first unmatched process component
                var match = targets.FirstOrDefault(c =>
                    !existing.Any(f => string.Equals(f.Attribute("Name")?.Value, c.Name,
                                                     StringComparison.OrdinalIgnoreCase)));
                if (match == null) continue;

                fb.SetAttributeValue("Name", match.Name);
                renames[oldName] = match.Name;
                MapperLogger.Remap($"Renamed Process1_CAT instance \"{oldName}\" → \"{match.Name}\"");
                result.InjectedFBs.Add($"[RENAMED] {oldName} → {match.Name} (Process1_CAT)");
            }
        }

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

        private static void PatchAttr(XElement el, string attr, Dictionary<string, string> renames)
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
                .Select(fb => fb.Attribute("Name")?.Value ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var c in group)
            {
                if (existing.Contains(c.Name))
                    report.AlreadyPresent.Add($"{c.Name} ({catType})");
                else
                    report.ToBeInjected.Add($"{c.Name} → {catType}");
            }
        }

        // ── Diff report ───────────────────────────────────────────────────────

        public class DiffReport
        {
            public List<string> AlreadyPresent { get; } = new();
            public List<string> ToBeInjected { get; } = new();
            public List<string> Unsupported { get; } = new();
        }
    }
}