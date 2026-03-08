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
    /// Injects the CALLER-SUPPLIED list of VueOne components into EAE .syslay and .sysres.
    ///
    /// This class never decides which components to include — the UI passes
    /// only the user-selected rows.  It operates in three phases:
    ///
    ///   Phase A — FB elements (syslay + sysres)
    ///     1. Correct Name + Type already present  → update parameters only (idempotent)
    ///     2. Spare slot of correct Type (wrong Name) → rename + update parameters
    ///     3. No slot at all → INSERT new FB with deterministic SHA-256 ID
    ///     Positions are computed dynamically: new FBs are placed 1400 units below the
    ///     lowest existing FB of the same type, so NOTHING OVERLAPS.
    ///
    ///   Phase B — Connections (syslay only)
    ///     For every newly inserted/remapped actuator:
    ///       EventConnection  Process1.state_update   → Actuator.pst_event
    ///       EventConnection  Actuator.pst_out        → Process1.state_change
    ///       DataConnection   Process1.actuator_name  → Actuator.process_state_name
    ///       DataConnection   Process1.state_val      → Actuator.state_val
    ///       DataConnection   Actuator.current_state_to_process → Process1.{lc_name}
    ///     All connections are idempotent (skipped if already present).
    ///
    ///   Phase C — INIT chain extension (syslay only)
    ///     Finds the existing connection whose Destination ends in ".INIT" pointing
    ///     to the process FB (e.g. "Pusher.INITO → Process1.INIT"), removes it,
    ///     threads the new actuators through the chain, and restores the terminal
    ///     "lastNew.INITO → Process1.INIT".
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";
        private const string RobotCatType = "Robot_Task_CAT";

        // Vertical gap between consecutive new FBs of the same type (EAE units)
        private const int FbGap = 1400;

        // Default x-columns per type (matching the reference project layout)
        private const int ActuatorColumnX = 1300;
        private const int SensorColumnX = 1560;
        private const int ProcessColumnX = 3360;
        private const int RobotColumnX = 5000;

        // ── Public API ────────────────────────────────────────────────────────

        public DiffReport PreviewDiff(MapperConfig config, List<VueOneComponent> components)
        {
            var report = new DiffReport();

            if (!File.Exists(config.SyslayPath))
            {
                report.Unsupported.Add($"syslay not found: {config.SyslayPath}");
                return report;
            }

            var doc = XDocument.Load(config.SyslayPath);
            var net = doc.Root?.Element(Ns + "SubAppNetwork");
            if (net == null) { report.Unsupported.Add("SubAppNetwork not found"); return report; }

            var byType = GetExistingByType(net);
            var allNames = GetAllNames(net);

            ClassifyGroup(ProcessCatType, Processes(components), byType, allNames, report);
            ClassifyGroup(SensorCatType, Sensors(components), byType, allNames, report);
            ClassifyGroup(ActuatorCatType, Actuators(components), byType, allNames, report);
            ClassifyGroup(RobotCatType, Robots(components, config), byType, allNames, report);

            foreach (var c in Unsupported(components, config))
                report.Unsupported.Add($"{c.Name} ({c.Type}, {c.States.Count} states — no CAT type)");

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

                // ── Phase A+B+C: syslay ───────────────────────────────────────
                var newlyInjectedActuators = ProcessSyslay(config.SyslayPath, config, components, result, syslayIds);

                // ── Phase A only: sysres ──────────────────────────────────────
                ProcessSysres(config.SysresPath, config, components, result, syslayIds);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ── Syslay: FBs + connections + INIT chain ────────────────────────────

        private List<string> ProcessSyslay(
            string path,
            MapperConfig config,
            List<VueOneComponent> components,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + "SubAppNetwork")
                ?? throw new Exception("SubAppNetwork not found in syslay");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var newlyInjectedActuators = new List<string>();

            // Phase A — FB elements
            InjectGroup(net, Processes(components), ProcessCatType, false, ActuatorColumnX, renames, result, syslayIds, newlyInjectedActuators);
            InjectGroup(net, Sensors(components), SensorCatType, false, SensorColumnX, renames, result, syslayIds, newlyInjectedActuators);
            InjectGroup(net, Actuators(components), ActuatorCatType, false, ActuatorColumnX, renames, result, syslayIds, newlyInjectedActuators);
            InjectGroup(net, Robots(components, config), RobotCatType, false, RobotColumnX, renames, result, syslayIds, newlyInjectedActuators);

            if (renames.Any())
                RewriteConnections(net, renames);

            // Phase B — wire actuators to their process
            if (newlyInjectedActuators.Any())
            {
                var processName = FindProcessFbName(net);
                if (processName != null)
                {
                    WireActuatorsToProcess(net, newlyInjectedActuators, processName, result);
                    // Phase C — extend INIT chain
                    ExtendInitChain(net, newlyInjectedActuators, processName, result);
                }
                else
                {
                    result.UnsupportedComponents.Add(
                        "No Process1_CAT found in syslay — skipped wiring and INIT chain");
                }
            }

            doc.Save(path);
            return newlyInjectedActuators;
        }

        // ── Sysres: FB elements only ──────────────────────────────────────────

        private void ProcessSysres(
            string path,
            MapperConfig config,
            List<VueOneComponent> components,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + "FBNetwork")
                ?? throw new Exception("FBNetwork not found in sysres");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dummy = new List<string>(); // sysres doesn't track injected names

            InjectGroup(net, Processes(components), ProcessCatType, true, ProcessColumnX, renames, result, syslayIds, dummy);
            InjectGroup(net, Sensors(components), SensorCatType, true, SensorColumnX, renames, result, syslayIds, dummy);
            InjectGroup(net, Actuators(components), ActuatorCatType, true, ActuatorColumnX, renames, result, syslayIds, dummy);
            InjectGroup(net, Robots(components, config), RobotCatType, true, RobotColumnX, renames, result, syslayIds, dummy);

            if (renames.Any())
                RewriteConnections(net, renames);

            doc.Save(path);
        }

        // ── Phase A: FB group injection ───────────────────────────────────────

        private void InjectGroup(
            XElement net,
            List<VueOneComponent> group,
            string catType,
            bool isSysres,
            int columnX,
            Dictionary<string, string> renames,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds,
            List<string> newlyInjected)
        {
            if (!group.Any()) return;

            var groupNames = new HashSet<string>(
                group.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            // Spare slots: correct type, name not in the incoming group
            var spares = AllFbsOfType(net, catType)
                .Where(fb => !groupNames.Contains(fb.Attribute("Name")?.Value ?? ""))
                .ToList();

            int spareIdx = 0;

            foreach (var comp in group)
            {
                // 1. Already present with correct name + type
                var existing = FindByNameAndType(net, comp.Name, catType);
                if (existing != null)
                {
                    ApplyParams(existing, comp, catType);
                    Track(existing, comp.Name, isSysres, syslayIds, result,
                        "params updated", newlyInjected);
                    continue;
                }

                // 2. Spare slot → remap
                if (spareIdx < spares.Count)
                {
                    var slot = spares[spareIdx++];
                    var oldName = slot.Attribute("Name")?.Value ?? "";
                    renames[oldName] = comp.Name;
                    slot.SetAttributeValue("Name", comp.Name);
                    ApplyParams(slot, comp, catType);
                    Track(slot, comp.Name, isSysres, syslayIds, result,
                        $"← {oldName} remap", newlyInjected);
                    continue;
                }

                // 3. New insert — position below the lowest existing FB of this type
                var id = isSysres ? MakeId(comp.Name, "sysres") : MakeId(comp.Name, "syslay");
                int x = columnX;
                int y = ComputeNextY(net, catType, columnX);

                var fb = new XElement(Ns + "FB",
                    new XAttribute("ID", id),
                    new XAttribute("Name", comp.Name),
                    new XAttribute("Type", catType),
                    new XAttribute("Namespace", "Main"),
                    new XAttribute("x", x),
                    new XAttribute("y", y));

                if (isSysres && syslayIds.TryGetValue(comp.Name, out var slId))
                    fb.SetAttributeValue("Mapping", slId);

                ApplyParams(fb, comp, catType);
                net.Add(fb);

                Track(fb, comp.Name, isSysres, syslayIds, result,
                    $"new insert at ({x},{y})", newlyInjected);
            }
        }

        // ── Phase B: Actuator ↔ Process wiring ───────────────────────────────

        /// <summary>
        /// Wires each newly-injected actuator to the first Process1_CAT instance
        /// found in the syslay.  Pattern taken directly from the reference syslay:
        ///
        ///   EventConnections
        ///     ProcessName.state_update  →  Actuator.pst_event
        ///     Actuator.pst_out          →  ProcessName.state_change
        ///
        ///   DataConnections
        ///     ProcessName.actuator_name →  Actuator.process_state_name
        ///     ProcessName.state_val     →  Actuator.state_val
        ///     Actuator.current_state_to_process  →  ProcessName.{lc_actuator_name}
        ///
        /// All connections are idempotent.
        /// </summary>
        private static void WireActuatorsToProcess(
            XElement net,
            List<string> actuatorNames,
            string processName,
            SystemInjectionResult result)
        {
            var ec = EnsureSection(net, "EventConnections");
            var dc = EnsureSection(net, "DataConnections");

            foreach (var name in actuatorNames)
            {
                string lc = name.ToLower();

                // ── Event connections ─────────────────────────────────────────
                AddConn(ec, $"{processName}.state_update", $"{name}.pst_event", result);
                AddConn(ec, $"{name}.pst_out", $"{processName}.state_change", result);

                // ── Data connections ──────────────────────────────────────────
                AddConn(dc, $"{processName}.actuator_name", $"{name}.process_state_name", result);
                AddConn(dc, $"{processName}.state_val", $"{name}.state_val", result);
                AddConn(dc, $"{name}.current_state_to_process", $"{processName}.{lc}", result);
            }
        }

        // ── Phase C: INIT chain extension ─────────────────────────────────────

        /// <summary>
        /// Inserts new actuators into the INIT chain just before the process FB.
        ///
        /// Before:  ...SomeFB.INITO  →  ProcessName.INIT
        /// After:   ...SomeFB.INITO  →  Checker.INIT
        ///          Checker.INITO    →  Transfer.INIT
        ///          Transfer.INITO   →  Ejector.INIT
        ///          Ejector.INITO    →  ProcessName.INIT
        ///
        /// If the connection "→ ProcessName.INIT" is not found, each new actuator
        /// is still chained together (open-ended — user can connect the start manually).
        /// </summary>
        private static void ExtendInitChain(
            XElement net,
            List<string> newActuators,
            string processName,
            SystemInjectionResult result)
        {
            if (!newActuators.Any()) return;

            var ec = EnsureSection(net, "EventConnections");

            // Find the existing connection whose Destination is "ProcessName.INIT"
            var processInitConn = ec.Elements()
                .Where(e => e.Name.LocalName == "Connection")
                .FirstOrDefault(c => string.Equals(
                    c.Attribute("Destination")?.Value,
                    $"{processName}.INIT",
                    StringComparison.OrdinalIgnoreCase));

            string? chainEntry = processInitConn?.Attribute("Source")?.Value;

            // Remove the old direct link to Process.INIT
            processInitConn?.Remove();

            // Re-attach: chainEntry → first new actuator
            if (!string.IsNullOrEmpty(chainEntry))
                AddConn(ec, chainEntry, $"{newActuators[0]}.INIT", result);
            else
                result.UnsupportedComponents.Add(
                    $"INIT chain: could not find '→ {processName}.INIT' — " +
                    $"connect {newActuators[0]}.INIT manually");

            // Chain through remaining new actuators
            for (int i = 0; i < newActuators.Count - 1; i++)
                AddConn(ec, $"{newActuators[i]}.INITO", $"{newActuators[i + 1]}.INIT", result);

            // Last new actuator → Process.INIT
            AddConn(ec, $"{newActuators[^1]}.INITO", $"{processName}.INIT", result);

            result.InjectedFBs.Add(
                $"INIT chain extended: ...→ {newActuators[0]} → ... → {newActuators[^1]} → {processName}");
        }

        // ── Layout helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Finds the maximum Y coordinate of all existing FBs of the given type,
        /// then returns that + FbGap.  Falls back to a sensible default if no
        /// FBs of that type exist yet.
        /// </summary>
        private static int ComputeNextY(XElement net, string catType, int columnX)
        {
            var existing = AllFbsOfType(net, catType)
                .Select(fb => TryParseInt(fb.Attribute("y")?.Value, 0))
                .ToList();

            if (existing.Any())
                return existing.Max() + FbGap;

            // No FBs of this type yet — find max Y of ANY FB in this column,
            // fall back to sensible defaults per type.
            var anyInColumn = net.Descendants()
                .Where(e => e.Name.LocalName == "FB"
                         && TryParseInt(e.Attribute("x")?.Value, -1) == columnX)
                .Select(fb => TryParseInt(fb.Attribute("y")?.Value, 0))
                .ToList();

            if (anyInColumn.Any())
                return anyInColumn.Max() + FbGap;

            // Absolute fallback
            return catType switch
            {
                ActuatorCatType => 2480,
                SensorCatType => 1480,
                ProcessCatType => 1460,
                _ => 3000
            };
        }

        private static int TryParseInt(string? s, int fallback) =>
            int.TryParse(s, out var v) ? v : fallback;

        // ── XML helpers ───────────────────────────────────────────────────────

        private static string? FindProcessFbName(XElement net) =>
            AllFbsOfType(net, ProcessCatType)
                .FirstOrDefault()?.Attribute("Name")?.Value;

        private static IEnumerable<XElement> AllFbsOfType(XElement net, string type) =>
            net.Descendants()
               .Where(e => e.Name.LocalName == "FB"
                        && string.Equals(e.Attribute("Type")?.Value, type,
                               StringComparison.OrdinalIgnoreCase));

        private static XElement? FindByNameAndType(XElement net, string name, string type) =>
            net.Descendants()
               .Where(e => e.Name.LocalName == "FB")
               .FirstOrDefault(fb =>
                   string.Equals(fb.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(fb.Attribute("Type")?.Value, type, StringComparison.OrdinalIgnoreCase));

        private static Dictionary<string, List<string>> GetExistingByType(XElement net)
        {
            var d = new Dictionary<string, List<string>>
            {
                [ProcessCatType] = new(),
                [SensorCatType] = new(),
                [ActuatorCatType] = new(),
                [RobotCatType] = new()
            };
            foreach (var fb in net.Descendants().Where(e => e.Name.LocalName == "FB"))
            {
                var t = fb.Attribute("Type")?.Value ?? "";
                var n = fb.Attribute("Name")?.Value ?? "";
                if (d.ContainsKey(t)) d[t].Add(n);
            }
            return d;
        }

        private static HashSet<string> GetAllNames(XElement net) =>
            new(net.Descendants()
                   .Where(e => e.Name.LocalName == "FB")
                   .Select(fb => fb.Attribute("Name")?.Value ?? ""),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns the EventConnections or DataConnections section, creating it if absent.</summary>
        private static XElement EnsureSection(XElement net, string sectionName)
        {
            var section = net.Elements()
                .FirstOrDefault(e => e.Name.LocalName == sectionName);
            if (section != null) return section;

            section = new XElement(Ns + sectionName);
            net.Add(section);
            return section;
        }

        /// <summary>
        /// Adds a Connection element if the same Source+Destination pair is not
        /// already present in the section.
        /// </summary>
        private static void AddConn(
            XElement section, string source, string destination,
            SystemInjectionResult result)
        {
            bool exists = section.Elements()
                .Where(e => e.Name.LocalName == "Connection")
                .Any(c =>
                    string.Equals(c.Attribute("Source")?.Value, source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Attribute("Destination")?.Value, destination, StringComparison.OrdinalIgnoreCase));

            if (exists) return;

            section.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", source),
                new XAttribute("Destination", destination)));

            result.InjectedFBs.Add($"  wire: {source} → {destination}");
        }

        private static void RewriteConnections(XElement net, Dictionary<string, string> renames)
        {
            foreach (var conn in net.Descendants().Where(e => e.Name.LocalName == "Connection"))
            {
                PatchPrefix(conn, "Source", renames);
                PatchPrefix(conn, "Destination", renames);
            }
        }

        private static void PatchPrefix(XElement el, string attr, Dictionary<string, string> renames)
        {
            var val = el.Attribute(attr)?.Value;
            if (string.IsNullOrEmpty(val)) return;
            int dot = val.IndexOf('.');
            if (dot < 0) return;
            if (renames.TryGetValue(val[..dot], out var np))
                el.SetAttributeValue(attr, np + val[dot..]);
        }

        private static string MakeId(string name, string salt)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{salt}:{name}"));
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToUpperInvariant();
        }

        // ── Parameter helpers ─────────────────────────────────────────────────

        private static void ApplyParams(XElement fb, VueOneComponent comp, string catType)
        {
            switch (catType)
            {
                case ActuatorCatType:
                    SetParam(fb, "actuator_name", $"'{comp.Name.ToLower()}'");
                    break;
                case ProcessCatType:
                    SetParam(fb, "Text", BuildTextParam(comp));
                    break;
            }
        }

        private static void SetParam(XElement fb, string name, string value)
        {
            var el = fb.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Parameter" && e.Attribute("Name")?.Value == name);
            if (el != null)
                el.SetAttributeValue("Value", value);
            else
                fb.Add(new XElement(Ns + "Parameter",
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

        private static void Track(
            XElement fb, string name, bool isSysres,
            Dictionary<string, string> syslayIds,
            SystemInjectionResult result,
            string note,
            List<string> newlyInjected)
        {
            if (isSysres) return;
            syslayIds[name] = fb.Attribute("ID")?.Value ?? MakeId(name, "syslay");
            result.InjectedFBs.Add($"{name} ({note})");
            newlyInjected.Add(name);
        }

        // ── Diff classification ───────────────────────────────────────────────

        private static void ClassifyGroup(
            string catType, List<VueOneComponent> group,
            Dictionary<string, List<string>> byType,
            HashSet<string> allNames, DiffReport report)
        {
            var slots = byType.GetValueOrDefault(catType, new());
            var spares = slots.Where(n => !group.Any(c =>
                string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            int si = 0;

            foreach (var c in group)
            {
                bool present = allNames.Contains(c.Name)
                            && slots.Contains(c.Name, StringComparer.OrdinalIgnoreCase);
                if (present)
                    report.AlreadyPresent.Add($"{c.Name} ({catType})");
                else if (si < spares.Count)
                    report.ToBeInjected.Add($"{spares[si++]} → {c.Name} ({catType} remap)");
                else
                    report.ToBeInjected.Add($"{c.Name} (new {catType})");
            }
        }

        // ── Component type filters ────────────────────────────────────────────

        private static bool IsActuator(VueOneComponent c) =>
            string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) && c.States.Count == 5;

        private static bool IsSensor(VueOneComponent c) =>
            string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) && c.States.Count == 2;

        private static bool IsProcess(VueOneComponent c) =>
            string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase);

        private static bool IsRobot(VueOneComponent c, MapperConfig config) =>
            string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(config.RobotTemplatePath);

        private static bool IsSupported(VueOneComponent c, MapperConfig config) =>
            IsActuator(c) || IsSensor(c) || IsProcess(c) || IsRobot(c, config);

        private static List<VueOneComponent> Actuators(List<VueOneComponent> all) =>
            all.Where(IsActuator).ToList();
        private static List<VueOneComponent> Sensors(List<VueOneComponent> all) =>
            all.Where(IsSensor).ToList();
        private static List<VueOneComponent> Processes(List<VueOneComponent> all) =>
            all.Where(IsProcess).ToList();
        private static List<VueOneComponent> Robots(List<VueOneComponent> all, MapperConfig config) =>
            all.Where(c => IsRobot(c, config)).ToList();
        private static List<VueOneComponent> Unsupported(List<VueOneComponent> all, MapperConfig config) =>
            all.Where(c => !IsSupported(c, config)).ToList();

        // ── Result types ──────────────────────────────────────────────────────

        public class DiffReport
        {
            public List<string> AlreadyPresent { get; } = new();
            public List<string> ToBeInjected { get; } = new();
            public List<string> Unsupported { get; } = new();
        }
    }
}