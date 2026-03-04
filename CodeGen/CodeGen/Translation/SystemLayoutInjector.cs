using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using MapperUI;

namespace MapperUI.Services
{
    /// <summary>
    /// Remaps existing EAE CAT instances to match a new VueOne system.
    /// 
    /// PHILOSOPHY:
    ///   The baseline project already has the right number of CAT instances
    ///   with established IDs, positions, connections and OPC UA bindings.
    ///   This injector does NOT add new FBs — it remaps existing ones:
    ///     - Renames the FB Name attribute to the new component name
    ///     - Updates parameters (actuator_name, Text)
    ///     - Rewrites all Connection Source/Destination references to the new name
    ///   IDs, positions, and wiring topology are preserved entirely.
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";

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

            var existing = GetExistingByType(net);

            MapperLogger.Diff($"Reading syslay: {Path.GetFileName(config.SyslayPath)}");
            MapperLogger.Diff($"Found {existing[ProcessCatType].Count} Process1_CAT, {existing[SensorCatType].Count} Sensor_Bool_CAT, {existing[ActuatorCatType].Count} Five_State_Actuator_CAT in baseline");

            var actuators = components.Where(c => IsActuator(c)).ToList();
            var sensors = components.Where(c => IsSensor(c)).ToList();
            var processes = components.Where(c => IsProcess(c)).ToList();

            // Processes
            for (int i = 0; i < processes.Count; i++)
            {
                if (i < existing[ProcessCatType].Count)
                    report.ToBeInjected.Add(
                        $"{existing[ProcessCatType][i]} → {processes[i].Name} (Process1_CAT remap)");
                else
                    report.Unsupported.Add($"{processes[i].Name} — no spare Process1_CAT slot in baseline");
            }

            // Sensors
            for (int i = 0; i < sensors.Count; i++)
            {
                if (i < existing[SensorCatType].Count)
                    report.ToBeInjected.Add(
                        $"{existing[SensorCatType][i]} → {sensors[i].Name} (Sensor_Bool_CAT remap)");
                else
                    report.Unsupported.Add($"{sensors[i].Name} — no spare Sensor_Bool_CAT slot in baseline");
            }

            // Actuators
            for (int i = 0; i < actuators.Count; i++)
            {
                if (i < existing[ActuatorCatType].Count)
                    report.ToBeInjected.Add(
                        $"{existing[ActuatorCatType][i]} → {actuators[i].Name} (Five_State_Actuator_CAT remap)");
                else
                    report.Unsupported.Add($"{actuators[i].Name} — no spare Five_State_Actuator_CAT slot in baseline");
            }

            // Components with no matching CAT type
            foreach (var c in components.Where(c => !IsActuator(c) && !IsSensor(c) && !IsProcess(c)))
                report.Unsupported.Add($"{c.Name} ({c.Type}, {c.States.Count} states — no CAT type)");

            MapperLogger.Diff($"Reading syslay: {Path.GetFileName(config.SyslayPath)}");
            MapperLogger.Diff($"Found {existing[ProcessCatType].Count} Process1_CAT, {existing[SensorCatType].Count} Sensor_Bool_CAT, {existing[ActuatorCatType].Count} Five_State_Actuator_CAT in baseline");
            
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

                var actuators = components.Where(c => IsActuator(c)).ToList();
                var sensors = components.Where(c => IsSensor(c)).ToList();
                var processes = components.Where(c => IsProcess(c)).ToList();

                foreach (var c in components.Where(c => !IsActuator(c) && !IsSensor(c) && !IsProcess(c)))
                    result.UnsupportedComponents.Add($"{c.Name} ({c.Type}, {c.States.Count} states)");

                RemapFile(config.SyslayPath, isSysres: false,
                    actuators, sensors, processes, result);

                RemapFile(config.SysresPath, isSysres: true,
                    actuators, sensors, processes, result);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ── Core remap logic ──────────────────────────────────────────────────

        private void RemapFile(string path, bool isSysres,
            List<VueOneComponent> actuators,
            List<VueOneComponent> sensors,
            List<VueOneComponent> processes,
            SystemInjectionResult result)
        {
            var doc = XDocument.Load(path);

            MapperLogger.Remap($"Processing {Path.GetFileName(path)} (isSysres={isSysres})");
            // syslay uses SubAppNetwork, sysres uses FBNetwork
            var net = isSysres
                ? doc.Root?.Element(Ns + "FBNetwork")
                : doc.Root?.Element(Ns + "SubAppNetwork");

            if (net == null)
                throw new Exception($"{(isSysres ? "FBNetwork" : "SubAppNetwork")} not found in {Path.GetFileName(path)}");

            var existing = GetExistingByType(net);
            var renames = new Dictionary<string, string>(); // oldName → newName

            // ── Remap Process ─────────────────────────────────────────────────
            for (int i = 0; i < processes.Count && i < existing[ProcessCatType].Count; i++)
            {
                var oldName = existing[ProcessCatType][i];
                var newName = processes[i].Name;
                renames[oldName] = newName;

                var fb = FindFbByName(net, oldName)!;
                fb.SetAttributeValue("Name", newName);

                MapperLogger.Remap($"  {oldName} → {newName} [Process1_CAT] ID preserved");

                // Update Text parameter with new state names
                SetOrAddParameter(fb, "Text", BuildTextParam(processes[i]));

                if (!isSysres) result.InjectedFBs.Add($"{oldName} → {newName} (Process1_CAT)");
            }

            // ── Remap Sensors ─────────────────────────────────────────────────
            for (int i = 0; i < sensors.Count && i < existing[SensorCatType].Count; i++)
            {
                var oldName = existing[SensorCatType][i];
                var newName = sensors[i].Name;
                renames[oldName] = newName;

                var fb = FindFbByName(net, oldName)!;
                fb.SetAttributeValue("Name", newName);

                MapperLogger.Remap($"  {oldName} → {newName} [Sensor_Bool_CAT] ID preserved");

                if (!isSysres) result.InjectedFBs.Add($"{oldName} → {newName} (Sensor_Bool_CAT)");
            }

            // ── Remap Actuators ───────────────────────────────────────────────
            for (int i = 0; i < actuators.Count && i < existing[ActuatorCatType].Count; i++)
            {
                var oldName = existing[ActuatorCatType][i];
                var newName = actuators[i].Name;
                renames[oldName] = newName;

                var fb = FindFbByName(net, oldName)!;
                fb.SetAttributeValue("Name", newName);

                MapperLogger.Remap($"  {oldName} → {newName} [Five_State_Actuator_CAT] ID preserved");

                SetOrAddParameter(fb, "actuator_name", $"'{newName.ToLower()}'");

                if (!isSysres) result.InjectedFBs.Add($"{oldName} → {newName} (Five_State_Actuator_CAT)");
            }

            // ── Rewrite all connection name references ────────────────────────
            if (renames.Any())
                RewriteConnections(net, renames);

            MapperLogger.Remap($"  Rewrote {renames.Count} name prefix(es) in connections");

            doc.Save(path);
            MapperLogger.Write($"Saved: {Path.GetFileName(path)}");
        }

        /// <summary>
        /// Rewrites Source and Destination attributes in all Connection elements.
        /// e.g. "hopper.pst_out" → "PartInHopper.pst_out"
        /// </summary>
        private static void RewriteConnections(XElement net, Dictionary<string, string> renames)
        {
            foreach (var conn in net.Descendants().Where(e => e.Name.LocalName == "Connection"))
            {
                RenameAttrPrefix(conn, "Source", renames);
                RenameAttrPrefix(conn, "Destination", renames);
            }
        }

        private static void RenameAttrPrefix(XElement el, string attrName,
            Dictionary<string, string> renames)
        {
            var val = el.Attribute(attrName)?.Value;
            if (string.IsNullOrEmpty(val)) return;

            // val is e.g. "hopper.pst_out" — split on first dot
            var dot = val.IndexOf('.');
            if (dot < 0) return;

            var prefix = val[..dot];
            if (renames.TryGetValue(prefix, out var newPrefix))
                el.SetAttributeValue(attrName, newPrefix + val[dot..]);
        }

        // ── XML helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns existing FB instance names grouped by CAT type.
        /// </summary>
        private static Dictionary<string, List<string>> GetExistingByType(XElement net)
        {
            var result = new Dictionary<string, List<string>>
            {
                [ProcessCatType] = new(),
                [SensorCatType] = new(),
                [ActuatorCatType] = new()
            };

            foreach (var fb in net.Descendants().Where(e => e.Name.LocalName == "FB"))
            {
                var type = fb.Attribute("Type")?.Value ?? "";
                var name = fb.Attribute("Name")?.Value ?? "";
                if (result.ContainsKey(type)) result[type].Add(name);
            }

            return result;
        }

        private static XElement? FindFbByName(XElement net, string name) =>
            net.Descendants()
               .Where(e => e.Name.LocalName == "FB")
               .FirstOrDefault(fb => string.Equals(
                   fb.Attribute("Name")?.Value, name,
                   StringComparison.OrdinalIgnoreCase));

        private static void SetOrAddParameter(XElement fb, string paramName, string value)
        {
            var existing = fb.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Parameter" &&
                                     e.Attribute("Name")?.Value == paramName);
            if (existing != null)
                existing.SetAttributeValue("Value", value);
            else
                fb.Add(new XElement(Ns + "Parameter",
                    new XAttribute("Name", paramName),
                    new XAttribute("Value", value)));
        }

        private static string BuildTextParam(VueOneComponent proc)
        {
            var names = proc.States.OrderBy(s => s.StateNumber)
                            .Select(s => $"'{s.Name}'").ToList();
            int pad = Math.Max(0, 14 - names.Count);
            if (pad > 0) names.Add($"{pad}('')");
            return "[" + string.Join(",", names) + "]";
        }

        // ── Component type guards ─────────────────────────────────────────────

        private static bool IsActuator(VueOneComponent c) =>
            string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) && c.States.Count == 5;

        private static bool IsSensor(VueOneComponent c) =>
            string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) && c.States.Count == 2;

        private static bool IsProcess(VueOneComponent c) =>
            string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase);

        // ── Result types ──────────────────────────────────────────────────────

        public class DiffReport
        {
            public List<string> AlreadyPresent { get; } = new();
            public List<string> ToBeInjected { get; } = new();
            public List<string> Unsupported { get; } = new();
        }
    }
}