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
    /// Injects a supplied list of VueOne components as CAT FB instances into EAE
    /// .syslay and .sysres files.
    ///
    /// The caller (MainForm) passes only the SELECTED components — this class
    /// never decides which components to include or exclude.
    ///
    /// Strategy per component (priority order):
    ///   1. Correct Name + Type already present → update parameters only (idempotent)
    ///   2. Spare slot of correct Type (wrong Name) exists → rename + update parameters
    ///   3. No slot at all → INSERT a new FB with deterministic SHA256-derived ID
    ///
    /// Template paths come exclusively from MapperConfig.
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";
        private const string RobotCatType = "Robot_Task_CAT";

        private static readonly (int x, int y) ActuatorLayout = (1300, 3200);
        private static readonly (int x, int y) SensorLayout = (1700, 3200);
        private static readonly (int x, int y) ProcessLayout = (3600, 3200);
        private static readonly (int x, int y) RobotLayout = (5000, 3200);
        private const int LayoutStepY = 600;

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

                ProcessFile(config.SyslayPath, "SubAppNetwork", isSysres: false,
                    config, components, result, syslayIds);

                ProcessFile(config.SysresPath, "FBNetwork", isSysres: true,
                    config, components, result, syslayIds);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ── Per-file processing ───────────────────────────────────────────────

        private void ProcessFile(
            string path, string networkTag, bool isSysres,
            MapperConfig config,
            List<VueOneComponent> components,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + networkTag)
                ?? throw new Exception($"<{networkTag}> not found in {Path.GetFileName(path)}");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int pi = 0, si = 0, ai = 0, ri = 0;

            InjectGroup(net, Processes(components), ProcessCatType, isSysres, ProcessLayout, renames, ref pi, result, syslayIds);
            InjectGroup(net, Sensors(components), SensorCatType, isSysres, SensorLayout, renames, ref si, result, syslayIds);
            InjectGroup(net, Actuators(components), ActuatorCatType, isSysres, ActuatorLayout, renames, ref ai, result, syslayIds);
            InjectGroup(net, Robots(components, config), RobotCatType, isSysres, RobotLayout, renames, ref ri, result, syslayIds);

            if (renames.Any())
                RewriteConnections(net, renames);

            doc.Save(path);
        }

        // ── Group injection ───────────────────────────────────────────────────

        private void InjectGroup(
            XElement net,
            List<VueOneComponent> group,
            string catType,
            bool isSysres,
            (int x, int y) layout,
            Dictionary<string, string> renames,
            ref int groupIdx,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIds)
        {
            var groupNames = new HashSet<string>(group.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            var spares = net.Descendants()
                .Where(e => e.Name.LocalName == "FB"
                         && string.Equals(e.Attribute("Type")?.Value, catType, StringComparison.OrdinalIgnoreCase)
                         && !groupNames.Contains(e.Attribute("Name")?.Value ?? ""))
                .ToList();

            int spareIdx = 0;

            foreach (var comp in group)
            {
                // 1. Already present with correct name + type
                var existing = FindByNameAndType(net, comp.Name, catType);
                if (existing != null)
                {
                    ApplyParams(existing, comp, catType);
                    TrackSyslay(existing, comp.Name, isSysres, syslayIds, result, "params updated");
                    continue;
                }

                // 2. Spare slot — remap
                if (spareIdx < spares.Count)
                {
                    var slot = spares[spareIdx++];
                    var oldName = slot.Attribute("Name")?.Value ?? "";
                    renames[oldName] = comp.Name;
                    slot.SetAttributeValue("Name", comp.Name);
                    ApplyParams(slot, comp, catType);
                    TrackSyslay(slot, comp.Name, isSysres, syslayIds, result, $"← {oldName} remap");
                    continue;
                }

                // 3. New insert
                var id = isSysres ? MakeId(comp.Name, "sysres") : MakeId(comp.Name, "syslay");
                var fb = new XElement(Ns + "FB",
                    new XAttribute("ID", id),
                    new XAttribute("Name", comp.Name),
                    new XAttribute("Type", catType),
                    new XAttribute("Namespace", "Main"),
                    new XAttribute("x", layout.x),
                    new XAttribute("y", layout.y + (groupIdx * LayoutStepY)));

                if (isSysres && syslayIds.TryGetValue(comp.Name, out var slId))
                    fb.SetAttributeValue("Mapping", slId);

                ApplyParams(fb, comp, catType);
                net.Add(fb);
                groupIdx++;

                TrackSyslay(fb, comp.Name, isSysres, syslayIds, result, "new insert");
            }
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
                    // Sensor and Robot_Task_CAT: no injected parameters at this phase
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

        // ── Connection rewriting ──────────────────────────────────────────────

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
            if (renames.TryGetValue(val[..dot], out var newPrefix))
                el.SetAttributeValue(attr, newPrefix + val[dot..]);
        }

        // ── Diff helpers ──────────────────────────────────────────────────────

        private static void ClassifyGroup(
            string catType, List<VueOneComponent> group,
            Dictionary<string, List<string>> byType,
            HashSet<string> allNames, DiffReport report)
        {
            var slots = byType.GetValueOrDefault(catType, new());
            var spares = slots.Where(n => !group.Any(c =>
                string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            int spareIdx = 0;

            foreach (var c in group)
            {
                bool present = allNames.Contains(c.Name)
                            && slots.Contains(c.Name, StringComparer.OrdinalIgnoreCase);
                if (present)
                    report.AlreadyPresent.Add($"{c.Name} ({catType})");
                else if (spareIdx < spares.Count)
                    report.ToBeInjected.Add($"{spares[spareIdx++]} → {c.Name} ({catType} remap)");
                else
                    report.ToBeInjected.Add($"{c.Name} (new {catType})");
            }
        }

        // ── XML helpers ───────────────────────────────────────────────────────

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

        private static string MakeId(string name, string salt)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{salt}:{name}"));
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToUpperInvariant();
        }

        private static void TrackSyslay(
            XElement fb, string name, bool isSysres,
            Dictionary<string, string> syslayIds,
            SystemInjectionResult result, string note)
        {
            if (isSysres) return;
            syslayIds[name] = fb.Attribute("ID")?.Value ?? MakeId(name, "syslay");
            result.InjectedFBs.Add($"{name} ({note})");
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