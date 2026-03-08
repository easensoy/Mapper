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
    /// Injects VueOne components as CAT FB instances into EAE .syslay and .sysres files.
    ///
    /// STRATEGY (in priority order for each component):
    ///   1. FB with correct Name + correct Type already exists → update parameters only (idempotent)
    ///   2. FB with correct Type but wrong Name exists (spare slot) → rename + update parameters
    ///   3. No available slot → INSERT a new &lt;FB&gt; element with a deterministic ID
    ///
    /// IDs for new elements are derived deterministically from the component name using SHA256
    /// so repeated runs produce identical XML and EAE sees no spurious changes.
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";

        // Auto-layout starting positions for newly inserted FBs.
        // Each new FB of the same type is offset downward by LayoutStepY.
        private const int ActuatorStartX = 1300;
        private const int ActuatorStartY = 3200;
        private const int SensorStartX = 1700;
        private const int SensorStartY = 3200;
        private const int ProcessStartX = 3600;
        private const int ProcessStartY = 3200;
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

            // Catalogue what is already in the syslay
            var existingByType = GetExistingByType(net);
            var existingNames = GetAllExistingNames(net);

            var actuators = components.Where(IsActuator).ToList();
            var sensors = components.Where(IsSensor).ToList();
            var processes = components.Where(IsProcess).ToList();

            ClassifyComponents(ProcessCatType, processes, existingByType, existingNames, report);
            ClassifyComponents(SensorCatType, sensors, existingByType, existingNames, report);
            ClassifyComponents(ActuatorCatType, actuators, existingByType, existingNames, report);

            // Anything not actuator/sensor/process is unsupported
            foreach (var c in components.Where(c => !IsActuator(c) && !IsSensor(c) && !IsProcess(c)))
                report.Unsupported.Add($"{c.Name} ({c.Type}, {c.States.Count} states — no CAT type)");

            return report;
        }

        /// <summary>
        /// Writes component instances into the syslay and sysres files.
        /// Idempotent: running twice produces the same result.
        /// </summary>
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

                var actuators = components.Where(IsActuator).ToList();
                var sensors = components.Where(IsSensor).ToList();
                var processes = components.Where(IsProcess).ToList();

                foreach (var c in components.Where(c => !IsActuator(c) && !IsSensor(c) && !IsProcess(c)))
                    result.UnsupportedComponents.Add($"{c.Name} ({c.Type}, {c.States.Count} states)");

                // syslayIdMap: componentName → the FB's ID in syslay (needed for sysres Mapping=)
                var syslayIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ProcessNetworkFile(
                    path: config.SyslayPath,
                    networkTag: "SubAppNetwork",
                    isSysres: false,
                    actuators: actuators,
                    sensors: sensors,
                    processes: processes,
                    result: result,
                    syslayIdMap: syslayIdMap);

                ProcessNetworkFile(
                    path: config.SysresPath,
                    networkTag: "FBNetwork",
                    isSysres: true,
                    actuators: actuators,
                    sensors: sensors,
                    processes: processes,
                    result: result,
                    syslayIdMap: syslayIdMap);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ── Core per-file processing ──────────────────────────────────────────

        private void ProcessNetworkFile(
            string path,
            string networkTag,
            bool isSysres,
            List<VueOneComponent> actuators,
            List<VueOneComponent> sensors,
            List<VueOneComponent> processes,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIdMap)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + networkTag)
                ?? throw new Exception($"<{networkTag}> not found in {Path.GetFileName(path)}");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Counters for auto-layout of newly inserted FBs
            int newActuatorIdx = 0;
            int newSensorIdx = 0;
            int newProcessIdx = 0;

            InjectGroup(net, processes, ProcessCatType, isSysres, renames, usedNames,
                ref newProcessIdx, ProcessStartX, ProcessStartY, result, syslayIdMap);

            InjectGroup(net, sensors, SensorCatType, isSysres, renames, usedNames,
                ref newSensorIdx, SensorStartX, SensorStartY, result, syslayIdMap);

            InjectGroup(net, actuators, ActuatorCatType, isSysres, renames, usedNames,
                ref newActuatorIdx, ActuatorStartX, ActuatorStartY, result, syslayIdMap);

            // Rewrite all Connection Source/Destination references
            if (renames.Any())
                RewriteConnections(net, renames);

            doc.Save(path);
        }

        /// <summary>
        /// Processes one component group (e.g. all actuators) against the network XML.
        /// Strategy per component:
        ///   1. Already present (correct Name + Type) → update params
        ///   2. Spare slot (correct Type, wrong Name) → rename + update params
        ///   3. No slot → INSERT new &lt;FB&gt; element
        /// </summary>
        private void InjectGroup(
            XElement net,
            List<VueOneComponent> components,
            string catType,
            bool isSysres,
            Dictionary<string, string> renames,
            HashSet<string> usedNames,
            ref int newFbIndex,
            int startX, int startY,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIdMap)
        {
            // Spare slots: FBs of this CAT type whose name is not claimed by any component
            var componentNames = new HashSet<string>(
                components.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            var spareSlots = net.Descendants()
                .Where(e => e.Name.LocalName == "FB"
                         && string.Equals(e.Attribute("Type")?.Value, catType, StringComparison.OrdinalIgnoreCase)
                         && !componentNames.Contains(e.Attribute("Name")?.Value ?? ""))
                .ToList();

            int spareIdx = 0;

            foreach (var component in components)
            {
                // ── 1. Already present? ───────────────────────────────────────
                var existing = FindFbByNameAndType(net, component.Name, catType);
                if (existing != null)
                {
                    UpdateParams(existing, component, catType);
                    usedNames.Add(component.Name);

                    // Record syslay ID for sysres Mapping= attribute
                    if (!isSysres)
                        syslayIdMap[component.Name] = existing.Attribute("ID")?.Value ?? MakeId(component.Name, "syslay");

                    if (!isSysres)
                        result.InjectedFBs.Add($"{component.Name} (present — params updated)");
                    continue;
                }

                // ── 2. Spare slot available? ──────────────────────────────────
                if (spareIdx < spareSlots.Count)
                {
                    var slot = spareSlots[spareIdx++];
                    var oldName = slot.Attribute("Name")?.Value ?? "";
                    var newName = component.Name;

                    renames[oldName] = newName;
                    slot.SetAttributeValue("Name", newName);
                    UpdateParams(slot, component, catType);
                    usedNames.Add(newName);

                    if (!isSysres)
                    {
                        syslayIdMap[newName] = slot.Attribute("ID")?.Value ?? MakeId(newName, "syslay");
                        result.InjectedFBs.Add($"{oldName} → {newName} ({catType} remap)");
                    }
                    continue;
                }

                // ── 3. Insert new FB ──────────────────────────────────────────
                var insertId = isSysres
                    ? MakeId(component.Name, "sysres")
                    : MakeId(component.Name, "syslay");

                var x = startX;
                var y = startY + (newFbIndex * LayoutStepY);
                newFbIndex++;

                var newFb = new XElement(Ns + "FB",
                    new XAttribute("ID", insertId),
                    new XAttribute("Name", component.Name),
                    new XAttribute("Type", catType),
                    new XAttribute("Namespace", "Main"),
                    new XAttribute("x", x),
                    new XAttribute("y", y));

                if (isSysres && syslayIdMap.TryGetValue(component.Name, out var syslayId))
                    newFb.SetAttributeValue("Mapping", syslayId);

                UpdateParams(newFb, component, catType);
                net.Add(newFb);
                usedNames.Add(component.Name);

                if (!isSysres)
                {
                    syslayIdMap[component.Name] = insertId;
                    result.InjectedFBs.Add($"{component.Name} (new {catType} inserted)");
                }
            }
        }

        // ── Parameter helpers ─────────────────────────────────────────────────

        private static void UpdateParams(XElement fb, VueOneComponent component, string catType)
        {
            if (catType == ActuatorCatType)
            {
                // actuator_name must match the ECC guard in Actuator.fbt: lowercase component name
                SetOrAddParameter(fb, "actuator_name", $"'{component.Name.ToLower()}'");
            }
            else if (catType == ProcessCatType)
            {
                SetOrAddParameter(fb, "Text", BuildTextParam(component));
            }
            // Sensors have no parameters that need updating
        }

        private static void SetOrAddParameter(XElement fb, string paramName, string value)
        {
            var existing = fb.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Parameter"
                                  && e.Attribute("Name")?.Value == paramName);
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

        // ── Connection rewriting ──────────────────────────────────────────────

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

            var dot = val.IndexOf('.');
            if (dot < 0) return;

            var prefix = val[..dot];
            if (renames.TryGetValue(prefix, out var newPrefix))
                el.SetAttributeValue(attrName, newPrefix + val[dot..]);
        }

        // ── DiffReport classification ─────────────────────────────────────────

        private static void ClassifyComponents(
            string catType,
            List<VueOneComponent> components,
            Dictionary<string, List<string>> existingByType,
            HashSet<string> existingNames,
            DiffReport report)
        {
            var spareSlots = existingByType.GetValueOrDefault(catType, new())
                                             .Where(n => !components.Any(c =>
                                                 string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
                                             .ToList();
            int spareIdx = 0;

            foreach (var component in components)
            {
                bool alreadyPresent = existingNames.Contains(component.Name)
                    && (existingByType.GetValueOrDefault(catType, new())
                                       .Contains(component.Name, StringComparer.OrdinalIgnoreCase));

                if (alreadyPresent)
                {
                    report.AlreadyPresent.Add($"{component.Name} ({catType})");
                }
                else if (spareIdx < spareSlots.Count)
                {
                    report.ToBeInjected.Add($"{spareSlots[spareIdx++]} → {component.Name} ({catType} remap)");
                }
                else
                {
                    report.ToBeInjected.Add($"{component.Name} (new {catType} — will be inserted)");
                }
            }
        }

        // ── XML query helpers ─────────────────────────────────────────────────

        private static XElement? FindFbByNameAndType(XElement net, string name, string type) =>
            net.Descendants()
               .Where(e => e.Name.LocalName == "FB")
               .FirstOrDefault(fb =>
                   string.Equals(fb.Attribute("Name")?.Value, name, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(fb.Attribute("Type")?.Value, type, StringComparison.OrdinalIgnoreCase));

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

        private static HashSet<string> GetAllExistingNames(XElement net) =>
            new HashSet<string>(
                net.Descendants()
                   .Where(e => e.Name.LocalName == "FB")
                   .Select(fb => fb.Attribute("Name")?.Value ?? ""),
                StringComparer.OrdinalIgnoreCase);

        // ── Deterministic ID generation ───────────────────────────────────────

        /// <summary>
        /// Generates a deterministic 16-character hex ID from a component name.
        /// Using SHA256 so repeated runs produce identical IDs — EAE won't see spurious changes.
        /// </summary>
        private static string MakeId(string componentName, string salt)
        {
            var input = $"{salt}:{componentName}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            // Take first 8 bytes → 16 hex chars, matching EAE's existing ID format
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToUpperInvariant();
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