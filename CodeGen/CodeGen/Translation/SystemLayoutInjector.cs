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
    /// Phase 2 – Injects CAT instances into an existing EAE .syslay and .sysres.
    /// Receives pre-loaded components directly — does NOT re-read any Control.xml.
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";

        /// <summary>
        /// READ-ONLY diff. Shows what would change without modifying any files.
        /// Also fixes the duplicate-injection bug: uses LocalName to match FB elements,
        /// ignoring xmlns namespace mismatches that caused EnsureFb to miss existing FBs.
        /// </summary>
        public DiffReport PreviewDiff(MapperConfig config, List<VueOneComponent> components)
        {
            var report = new DiffReport();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(config.SyslayPath))
            {
                var doc = XDocument.Load(config.SyslayPath);
                // LocalName ignores xmlns — this is the fix for the duplicate injection bug
                foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                {
                    var n = fb.Attribute("Name")?.Value;
                    if (!string.IsNullOrEmpty(n)) existing.Add(n);
                }
            }

            foreach (var c in components)
            {
                bool actuator = string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) && c.States.Count == 5;
                bool sensor = string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) && c.States.Count == 2;
                bool process = string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase);

                if (!actuator && !sensor && !process)
                { report.Unsupported.Add($"{c.Name} ({c.Type}, {c.States.Count} states)"); continue; }

                if (existing.Contains(c.Name))
                    report.AlreadyPresent.Add($"{c.Name} ({c.Type})");
                else
                    report.ToBeInjected.Add($"{c.Name} ({c.Type})");
            }
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
                if (components == null || components.Count == 0)
                    throw new Exception("No components loaded. Use Browse to load a Control.xml first.");

                var actuators = new List<VueOneComponent>();
                var sensors = new List<VueOneComponent>();
                var processes = new List<VueOneComponent>();

                foreach (var c in components)
                {
                    if (string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) && c.States.Count == 5)
                        actuators.Add(c);
                    else if (string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) && c.States.Count == 2)
                        sensors.Add(c);
                    else if (string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase))
                        processes.Add(c);
                    else
                        result.UnsupportedComponents.Add($"{c.Name} ({c.Type}, {c.States.Count} states)");
                }

                var processComp = processes.FirstOrDefault();

                PatchSyslay(config.SyslayPath, actuators, sensors, processComp, result);
                PatchSysres(config.SysresPath, actuators, sensors, processComp, result);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public class DiffReport
        {
            public List<string> AlreadyPresent { get; } = new();   // will be skipped
            public List<string> ToBeInjected { get; } = new();   // will be injected
            public List<string> Unsupported { get; } = new();   // no CAT mapping
        }

        // ── SYSLAY ────────────────────────────────────────────────────────────

        private void PatchSyslay(string path, List<VueOneComponent> actuators,
            List<VueOneComponent> sensors, VueOneComponent? processComp, SystemInjectionResult result)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + "SubAppNetwork")
                ?? throw new Exception("SubAppNetwork not found in syslay.");

            int x = 1200, y = 500;

            if (processComp != null)
                EnsureFb(net, processComp.Name, ProcessCatType, SyslayId(processComp.Name),
                    x + 2000, y + 1000, new[] { ("Text", BuildTextParam(processComp)) }, result);

            foreach (var s in sensors)
            { EnsureFb(net, s.Name, SensorCatType, SyslayId(s.Name), x, y, Array.Empty<(string, string)>(), result); y += 500; }

            foreach (var a in actuators)
            { EnsureFb(net, a.Name, ActuatorCatType, SyslayId(a.Name), x, y, new[] { ("actuator_name", $"'{a.Name.ToLower()}'") }, result); y += 500; }

            if (processComp != null)
                AddWiring(net, actuators, sensors, processComp);

            doc.Save(path);
        }

        // ── SYSRES ────────────────────────────────────────────────────────────

        private void PatchSysres(string path, List<VueOneComponent> actuators,
            List<VueOneComponent> sensors, VueOneComponent? processComp, SystemInjectionResult result)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + "FBNetwork")
                ?? throw new Exception("FBNetwork not found in sysres.");

            int x = 2500, y = 2000;

            if (processComp != null)
                EnsureFbRes(net, processComp.Name, ProcessCatType,
                    SysresId(processComp.Name), SyslayId(processComp.Name),
                    x + 2000, y + 1000, new[] { ("Text", BuildTextParam(processComp)) });

            foreach (var s in sensors)
            { EnsureFbRes(net, s.Name, SensorCatType, SysresId(s.Name), SyslayId(s.Name), x, y, Array.Empty<(string, string)>()); y += 500; }

            foreach (var a in actuators)
            { EnsureFbRes(net, a.Name, ActuatorCatType, SysresId(a.Name), SyslayId(a.Name), x, y, new[] { ("actuator_name", $"'{a.Name.ToLower()}'") }); y += 500; }

            if (processComp != null)
                AddWiring(net, actuators, sensors, processComp);

            doc.Save(path);
        }

        // ── Wiring (shared for both syslay and sysres) ───────────────────────

        private void AddWiring(XElement net, List<VueOneComponent> actuators,
            List<VueOneComponent> sensors, VueOneComponent proc)
        {
            var evt = EnsureOrAddElement(net, "EventConnections");
            var data = EnsureOrAddElement(net, "DataConnections");

            foreach (var s in sensors)
            {
                EnsureConn(evt, $"{s.Name}.pst_out", $"{proc.Name}.state_change", "60");
                EnsureConn(data, $"{s.Name}.Status", $"{proc.Name}.{ToCamel(s.Name)}", "206");
            }

            foreach (var a in actuators)
            {
                EnsureConn(evt, $"{a.Name}.pst_out", $"{proc.Name}.state_change", "383");
                EnsureConn(evt, $"{proc.Name}.state_update", $"{a.Name}.pst_event", "100");
                EnsureConn(data, $"{a.Name}.current_state_to_process", $"{proc.Name}.{ToCamel(a.Name)}", "463");
                EnsureConn(data, $"{proc.Name}.actuator_name", $"{a.Name}.process_state_name", "110");
                EnsureConn(data, $"{proc.Name}.state_val", $"{a.Name}.state_val", "60");
            }

            // INIT chain: sensor(s) → actuator(s) → process
            var chain = sensors.Select(s => s.Name)
                .Concat(actuators.Select(a => a.Name))
                .Concat(new[] { proc.Name })
                .ToList();

            for (int i = 0; i < chain.Count - 1; i++)
                EnsureConn(evt, $"{chain[i]}.INITO", $"{chain[i + 1]}.INIT", "60");
        }

        // ── XML helpers ───────────────────────────────────────────────────────

        private void EnsureFb(XElement net, string name, string type, string id, int x, int y,
            IEnumerable<(string Key, string Value)> parms, SystemInjectionResult result)
        {
            if (net.Descendants().Where(e => e.Name.LocalName == "FB")
                   .Any(fb => string.Equals(fb.Attribute("Name")?.Value, name,
                              StringComparison.OrdinalIgnoreCase)))
            { result.SkippedFBs.Add($"{name} (already present)"); return; }

            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id), new XAttribute("Name", name),
                new XAttribute("Type", type), new XAttribute("Namespace", "Main"),
                new XAttribute("x", x), new XAttribute("y", y));

            foreach (var (k, v) in parms)
                fb.Add(new XElement(Ns + "Parameter", new XAttribute("Name", k), new XAttribute("Value", v)));

            InsertBeforeConnections(net, fb);
            result.InjectedFBs.Add($"{name} ({type})");
        }

        private void EnsureFbRes(XElement net, string name, string type, string id, string mapping,
            int x, int y, IEnumerable<(string Key, string Value)> parms)
        {
            if (net.Elements(Ns + "FB").Any(fb => (string?)fb.Attribute("Name") == name)) return;

            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id), new XAttribute("Name", name),
                new XAttribute("Type", type), new XAttribute("Namespace", "Main"),
                new XAttribute("Mapping", mapping),
                new XAttribute("x", x), new XAttribute("y", y));

            foreach (var (k, v) in parms)
                fb.Add(new XElement(Ns + "Parameter", new XAttribute("Name", k), new XAttribute("Value", v)));

            InsertBeforeConnections(net, fb);
        }

        private void EnsureConn(XElement grp, string src, string dst, string dx1)
        {
            if (grp.Elements(Ns + "Connection")
                    .Any(c => (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst))
                return;
            grp.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", src), new XAttribute("Destination", dst), new XAttribute("dx1", dx1)));
        }

        private XElement EnsureOrAddElement(XElement parent, string localName)
        {
            var el = parent.Element(Ns + localName);
            if (el != null) return el;
            el = new XElement(Ns + localName);
            parent.Add(el);
            return el;
        }

        private void InsertBeforeConnections(XElement net, XElement fb)
        {
            var anchor = net.Elements().FirstOrDefault(
                e => e.Name.LocalName is "EventConnections" or "DataConnections" or "AdapterConnections");
            if (anchor != null) anchor.AddBeforeSelf(fb);
            else net.Add(fb);
        }

        private string BuildTextParam(VueOneComponent proc)
        {
            var names = proc.States.OrderBy(s => s.StateNumber).Select(s => $"'{s.Name}'").ToList();
            int pad = Math.Max(0, 14 - names.Count);
            if (pad > 0) names.Add($"{pad}('')");
            return "[" + string.Join(",", names) + "]";
        }

        private static string SyslayId(string name)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(
                Encoding.UTF8.GetBytes("SYSLAY:" + name.ToUpperInvariant()))).Replace("-", "")[..16].ToUpper();
        }

        private static string SysresId(string name)
        {
            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(
                Encoding.UTF8.GetBytes("SYSRES:" + name.ToUpperInvariant()))).Replace("-", "")[..16].ToUpper();
        }

        private static string ToCamel(string name) =>
            string.IsNullOrEmpty(name) ? name : char.ToLower(name[0]) + name[1..];
    }
}