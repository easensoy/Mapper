using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;

namespace MapperUI.Services
{
    /// <summary>
    /// Phase 2 – Inject CAT instances into an existing EAE .syslay and .sysres.
    ///
    /// Rules:
    ///   Actuator  + 5 states  → Five_State_Actuator_CAT
    ///   Sensor    + 2 states  → Sensor_Bool_CAT
    ///   Process   (any)       → Process1_CAT
    ///   Everything else       → skipped with a warning
    ///
    /// IDs are derived deterministically from component name so the operation
    /// is fully idempotent (safe to run multiple times).
    /// </summary>
    public class SystemInjector
    {
        // EAE XML namespace – confirmed from the real syslay / sysres files
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";

        public SystemInjectionResult Inject(MapperConfig config)
        {
            var result = new SystemInjectionResult
            {
                SyslayPath = config.SyslayPath,
                SysresPath = config.SysresPath
            };

            try
            {
                // ── 1. Validate paths ─────────────────────────────────────────
                if (!File.Exists(config.SystemXmlPath))
                    throw new FileNotFoundException($"System XML not found: {config.SystemXmlPath}");
                if (!File.Exists(config.SyslayPath))
                    throw new FileNotFoundException($"syslay not found: {config.SyslayPath}");
                if (!File.Exists(config.SysresPath))
                    throw new FileNotFoundException($"sysres not found: {config.SysresPath}");

                // ── 2. Parse VueOne system XML ────────────────────────────────
                var reader = new SystemXmlReader();
                var components = reader.ReadAllComponents(config.SystemXmlPath);

                if (components.Count == 0)
                    throw new Exception("No components found in system XML. Ensure it is Type='System'.");

                // ── 3. Categorise components ──────────────────────────────────
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

                // We need exactly one Process FB to wire into
                var processComponent = processes.FirstOrDefault();

                // ── 4. Patch .syslay ──────────────────────────────────────────
                PatchSyslay(config.SyslayPath, actuators, sensors, processComponent, result);

                // ── 5. Patch .sysres ──────────────────────────────────────────
                PatchSysres(config.SysresPath, actuators, sensors, processComponent, result);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // SYSLAY
        // ─────────────────────────────────────────────────────────────────────

        private void PatchSyslay(
            string path,
            List<VueOneComponent> actuators,
            List<VueOneComponent> sensors,
            VueOneComponent? processComp,
            SystemInjectionResult result)
        {
            var doc = XDocument.Load(path);
            var subAppNetwork = doc.Root?.Element(Ns + "SubAppNetwork")
                ?? throw new Exception("SubAppNetwork element not found in syslay.");

            int xPos = 1200;
            int yPos = 500;

            // ── Inject Process FB ─────────────────────────────────────────────
            if (processComp != null)
            {
                var textParam = BuildTextParam(processComp);
                EnsureFbInNetwork(subAppNetwork, processComp.Name, ProcessCatType,
                    SyslayId(processComp.Name), xPos + 2000, yPos + 1000,
                    new[] { ("Text", textParam) },
                    result);
            }

            // ── Inject Sensor FBs ─────────────────────────────────────────────
            foreach (var sensor in sensors)
            {
                EnsureFbInNetwork(subAppNetwork, sensor.Name, SensorCatType,
                    SyslayId(sensor.Name), xPos, yPos,
                    Array.Empty<(string, string)>(),
                    result);
                yPos += 500;
            }

            // ── Inject Actuator FBs ───────────────────────────────────────────
            foreach (var actuator in actuators)
            {
                EnsureFbInNetwork(subAppNetwork, actuator.Name, ActuatorCatType,
                    SyslayId(actuator.Name), xPos, yPos,
                    new[] { ("actuator_name", $"'{actuator.Name.ToLower()}'") },
                    result);
                yPos += 500;
            }

            // ── Inject Connections ────────────────────────────────────────────
            if (processComp != null)
            {
                var evtConns = EnsureOrAddElement(subAppNetwork, "EventConnections");
                var dataConns = EnsureOrAddElement(subAppNetwork, "DataConnections");

                foreach (var sensor in sensors)
                {
                    // sensor.pst_out → Process.state_change  (event)
                    EnsureConnection(evtConns, $"{sensor.Name}.pst_out", $"{processComp.Name}.state_change", "60");
                }

                foreach (var actuator in actuators)
                {
                    // Actuator.pst_out → Process.state_change  (event – feedback)
                    EnsureConnection(evtConns, $"{actuator.Name}.pst_out", $"{processComp.Name}.state_change", "383");
                    // Process.state_update → Actuator.pst_event  (event – command)
                    EnsureConnection(evtConns, $"{processComp.Name}.state_update", $"{actuator.Name}.pst_event", "100");

                    // Actuator.current_state_to_process → Process.<actuatorVar>  (data)
                    EnsureConnection(dataConns, $"{actuator.Name}.current_state_to_process",
                        $"{processComp.Name}.{ToCamelCase(actuator.Name)}", "463");
                    // Process.actuator_name → Actuator.process_state_name  (data)
                    EnsureConnection(dataConns, $"{processComp.Name}.actuator_name",
                        $"{actuator.Name}.process_state_name", "110");
                    // Process.state_val → Actuator.state_val  (data)
                    EnsureConnection(dataConns, $"{processComp.Name}.state_val",
                        $"{actuator.Name}.state_val", "60");
                }

                foreach (var sensor in sensors)
                {
                    // Sensor.Status → Process.<sensorVar>  (data)
                    EnsureConnection(dataConns, $"{sensor.Name}.Status",
                        $"{processComp.Name}.{ToCamelCase(sensor.Name)}", "206");
                }
            }

            // ── Build INIT chain among injected FBs ───────────────────────────
            if (processComp != null && (sensors.Count > 0 || actuators.Count > 0))
            {
                var evtConns = EnsureOrAddElement(subAppNetwork, "EventConnections");
                var chain = new List<string>();
                chain.AddRange(sensors.Select(s => s.Name));
                chain.AddRange(actuators.Select(a => a.Name));
                chain.Add(processComp.Name);

                for (int i = 0; i < chain.Count - 1; i++)
                    EnsureConnection(evtConns, $"{chain[i]}.INITO", $"{chain[i + 1]}.INIT", "60");
            }

            doc.Save(path);
        }

        // ─────────────────────────────────────────────────────────────────────
        // SYSRES
        // ─────────────────────────────────────────────────────────────────────

        private void PatchSysres(
            string path,
            List<VueOneComponent> actuators,
            List<VueOneComponent> sensors,
            VueOneComponent? processComp,
            SystemInjectionResult result)
        {
            var doc = XDocument.Load(path);
            // sysres root container is <FBNetwork> inside <Resource>
            var fbNetwork = doc.Root?.Element(Ns + "FBNetwork")
                ?? throw new Exception("FBNetwork element not found in sysres.");

            int xPos = 2500;
            int yPos = 2000;

            // ── Inject Process FB ─────────────────────────────────────────────
            if (processComp != null)
            {
                var textParam = BuildTextParam(processComp);
                EnsureFbInNetworkRes(fbNetwork, processComp.Name, ProcessCatType,
                    SysresId(processComp.Name), SyslayId(processComp.Name),
                    xPos + 2000, yPos + 1000,
                    new[] { ("Text", textParam) });
            }

            // ── Inject Sensor FBs ─────────────────────────────────────────────
            foreach (var sensor in sensors)
            {
                EnsureFbInNetworkRes(fbNetwork, sensor.Name, SensorCatType,
                    SysresId(sensor.Name), SyslayId(sensor.Name),
                    xPos, yPos,
                    Array.Empty<(string, string)>());
                yPos += 500;
            }

            // ── Inject Actuator FBs ───────────────────────────────────────────
            foreach (var actuator in actuators)
            {
                EnsureFbInNetworkRes(fbNetwork, actuator.Name, ActuatorCatType,
                    SysresId(actuator.Name), SyslayId(actuator.Name),
                    xPos, yPos,
                    new[] { ("actuator_name", $"'{actuator.Name.ToLower()}'") });
                yPos += 500;
            }

            // ── Inject Connections (mirror syslay) ────────────────────────────
            if (processComp != null)
            {
                var evtConns = EnsureOrAddElement(fbNetwork, "EventConnections");
                var dataConns = EnsureOrAddElement(fbNetwork, "DataConnections");

                foreach (var sensor in sensors)
                    EnsureConnection(evtConns, $"{sensor.Name}.pst_out", $"{processComp.Name}.state_change", "60");

                foreach (var actuator in actuators)
                {
                    EnsureConnection(evtConns, $"{actuator.Name}.pst_out", $"{processComp.Name}.state_change", "383");
                    EnsureConnection(evtConns, $"{processComp.Name}.state_update", $"{actuator.Name}.pst_event", "100");
                    EnsureConnection(dataConns, $"{actuator.Name}.current_state_to_process",
                        $"{processComp.Name}.{ToCamelCase(actuator.Name)}", "463");
                    EnsureConnection(dataConns, $"{processComp.Name}.actuator_name",
                        $"{actuator.Name}.process_state_name", "110");
                    EnsureConnection(dataConns, $"{processComp.Name}.state_val",
                        $"{actuator.Name}.state_val", "60");
                }

                foreach (var sensor in sensors)
                    EnsureConnection(dataConns, $"{sensor.Name}.Status",
                        $"{processComp.Name}.{ToCamelCase(sensor.Name)}", "206");
            }

            if (processComp != null && (sensors.Count > 0 || actuators.Count > 0))
            {
                var evtConns = EnsureOrAddElement(fbNetwork, "EventConnections");
                var chain = new List<string>();
                chain.AddRange(sensors.Select(s => s.Name));
                chain.AddRange(actuators.Select(a => a.Name));
                chain.Add(processComp.Name);

                for (int i = 0; i < chain.Count - 1; i++)
                    EnsureConnection(evtConns, $"{chain[i]}.INITO", $"{chain[i + 1]}.INIT", "60");
            }

            doc.Save(path);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Add FB to SubAppNetwork if it doesn't already exist by Name.</summary>
        private void EnsureFbInNetwork(
            XElement network,
            string name,
            string type,
            string id,
            int x,
            int y,
            IEnumerable<(string Key, string Value)> parameters,
            SystemInjectionResult result)
        {
            bool exists = network.Elements(Ns + "FB")
                .Any(fb => (string?)fb.Attribute("Name") == name);

            if (exists)
            {
                result.SkippedFBs.Add($"{name} (already present)");
                return;
            }

            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id),
                new XAttribute("Name", name),
                new XAttribute("Type", type),
                new XAttribute("Namespace", "Main"),
                new XAttribute("x", x.ToString()),
                new XAttribute("y", y.ToString()));

            foreach (var (key, value) in parameters)
                fb.Add(new XElement(Ns + "Parameter",
                    new XAttribute("Name", key),
                    new XAttribute("Value", value)));

            // Insert before first connection element so FBs stay grouped at top
            var firstConn = network.Elements()
                .FirstOrDefault(e => e.Name.LocalName is "EventConnections" or "DataConnections" or "AdapterConnections");

            if (firstConn != null)
                firstConn.AddBeforeSelf(fb);
            else
                network.Add(fb);

            result.InjectedFBs.Add($"{name} ({type})");
        }

        /// <summary>Add FB to FBNetwork (sysres) with Mapping= attribute.</summary>
        private void EnsureFbInNetworkRes(
            XElement network,
            string name,
            string type,
            string id,
            string mappingId,
            int x,
            int y,
            IEnumerable<(string Key, string Value)> parameters)
        {
            bool exists = network.Elements(Ns + "FB")
                .Any(fb => (string?)fb.Attribute("Name") == name);

            if (exists) return;

            var fb = new XElement(Ns + "FB",
                new XAttribute("ID", id),
                new XAttribute("Name", name),
                new XAttribute("Type", type),
                new XAttribute("Namespace", "Main"),
                new XAttribute("Mapping", mappingId),
                new XAttribute("x", x.ToString()),
                new XAttribute("y", y.ToString()));

            foreach (var (key, value) in parameters)
                fb.Add(new XElement(Ns + "Parameter",
                    new XAttribute("Name", key),
                    new XAttribute("Value", value)));

            var firstConn = network.Elements()
                .FirstOrDefault(e => e.Name.LocalName is "EventConnections" or "DataConnections" or "AdapterConnections");

            if (firstConn != null)
                firstConn.AddBeforeSelf(fb);
            else
                network.Add(fb);
        }

        /// <summary>Add connection if not already present (matched on Source+Destination).</summary>
        private void EnsureConnection(XElement connGroup, string source, string destination, string dx1)
        {
            bool exists = connGroup.Elements(Ns + "Connection")
                .Any(c => (string?)c.Attribute("Source") == source
                       && (string?)c.Attribute("Destination") == destination);

            if (exists) return;

            connGroup.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", source),
                new XAttribute("Destination", destination),
                new XAttribute("dx1", dx1)));
        }

        /// <summary>Find or create a child element (EventConnections / DataConnections).</summary>
        private XElement EnsureOrAddElement(XElement parent, string localName)
        {
            var existing = parent.Element(Ns + localName);
            if (existing != null) return existing;

            var newEl = new XElement(Ns + localName);
            parent.Add(newEl);
            return newEl;
        }

        /// <summary>Build Process1_CAT Text parameter from process state names.</summary>
        private string BuildTextParam(VueOneComponent processComp)
        {
            var names = processComp.States
                .OrderBy(s => s.StateNumber)
                .Select(s => $"'{s.Name}'")
                .ToList();

            int padding = Math.Max(0, 14 - names.Count);
            if (padding > 0)
                names.Add($"{padding}('')");

            return "[" + string.Join(",", names) + "]";
        }

        /// <summary>Deterministic 16-char uppercase hex ID for syslay (based on component name).</summary>
        private static string SyslayId(string name)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("SYSLAY:" + name.ToUpperInvariant()));
            return BitConverter.ToString(hash).Replace("-", "")[..16].ToUpper();
        }

        /// <summary>Deterministic 16-char uppercase hex ID for sysres.</summary>
        private static string SysresId(string name)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("SYSRES:" + name.ToUpperInvariant()));
            return BitConverter.ToString(hash).Replace("-", "")[..16].ToUpper();
        }

        /// <summary>Convert "PartInHopper" → "partInHopper" (first letter lower).</summary>
        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return char.ToLower(name[0]) + name[1..];
        }
    }
}