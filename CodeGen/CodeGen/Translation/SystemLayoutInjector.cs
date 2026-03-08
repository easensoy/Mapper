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
    /// STRATEGY per component (in priority order):
    ///   1. Correct Name + Type already present → update parameters only (idempotent)
    ///   2. Spare slot of correct Type (wrong Name) exists → rename + update parameters
    ///   3. No slot → INSERT a new FB element with deterministic SHA256 ID
    ///
    /// NEW INSERTION LIMIT:
    ///   MapperConfig.MaxNewInsertionsPerRun (default 3) caps how many brand-new FBs
    ///   may be written in a single run.  Remapping existing slots is unlimited.
    ///   Set to 0 in mapper_config.json to disable the cap and inject everything at once.
    /// </summary>
    public class SystemInjector
    {
        private static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        private const string ActuatorCatType = "Five_State_Actuator_CAT";
        private const string SensorCatType = "Sensor_Bool_CAT";
        private const string ProcessCatType = "Process1_CAT";

        // Auto-layout positions for newly inserted FBs
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

            var existingByType = GetExistingByType(net);
            var existingNames = GetAllExistingNames(net);

            ClassifyGroup(ProcessCatType, components.Where(IsProcess).ToList(), existingByType, existingNames, report);
            ClassifyGroup(SensorCatType, components.Where(IsSensor).ToList(), existingByType, existingNames, report);
            ClassifyGroup(ActuatorCatType, components.Where(IsActuator).ToList(), existingByType, existingNames, report);

            foreach (var c in components.Where(c => !IsActuator(c) && !IsSensor(c) && !IsProcess(c)))
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

                var actuators = components.Where(IsActuator).ToList();
                var sensors = components.Where(IsSensor).ToList();
                var processes = components.Where(IsProcess).ToList();

                foreach (var c in components.Where(c => !IsActuator(c) && !IsSensor(c) && !IsProcess(c)))
                    result.UnsupportedComponents.Add($"{c.Name} ({c.Type}, {c.States.Count} states)");

                // Shared new-insertion counter across both files and all component types.
                // Only incremented when a brand-new <FB> element is written (not on remaps).
                int newInsertionCount = 0;
                int maxNew = config.MaxNewInsertionsPerRun; // 0 = unlimited

                var syslayIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ProcessFile(config.SyslayPath, "SubAppNetwork", isSysres: false,
                    actuators, sensors, processes, result, syslayIdMap, maxNew, ref newInsertionCount);

                ProcessFile(config.SysresPath, "FBNetwork", isSysres: true,
                    actuators, sensors, processes, result, syslayIdMap, maxNew, ref newInsertionCount);

                if (maxNew > 0 && newInsertionCount >= maxNew)
                    result.LimitReached = true;

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        // ── Core per-file logic ───────────────────────────────────────────────

        private void ProcessFile(
            string path, string networkTag, bool isSysres,
            List<VueOneComponent> actuators,
            List<VueOneComponent> sensors,
            List<VueOneComponent> processes,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIdMap,
            int maxNew, ref int newCount)
        {
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(Ns + networkTag)
                ?? throw new Exception($"<{networkTag}> not found in {Path.GetFileName(path)}");

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int newActIdx = 0, newSnsIdx = 0, newPrcIdx = 0;

            InjectGroup(net, processes, ProcessCatType, isSysres, renames,
                ref newPrcIdx, ProcessStartX, ProcessStartY, result, syslayIdMap, maxNew, ref newCount);

            InjectGroup(net, sensors, SensorCatType, isSysres, renames,
                ref newSnsIdx, SensorStartX, SensorStartY, result, syslayIdMap, maxNew, ref newCount);

            InjectGroup(net, actuators, ActuatorCatType, isSysres, renames,
                ref newActIdx, ActuatorStartX, ActuatorStartY, result, syslayIdMap, maxNew, ref newCount);

            if (renames.Any())
                RewriteConnections(net, renames);

            doc.Save(path);
        }

        private void InjectGroup(
            XElement net,
            List<VueOneComponent> components,
            string catType,
            bool isSysres,
            Dictionary<string, string> renames,
            ref int newFbIndex,
            int startX, int startY,
            SystemInjectionResult result,
            Dictionary<string, string> syslayIdMap,
            int maxNew, ref int newCount)
        {
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
                // 1. Already present with correct name+type → update params only
                var existing = FindFbByNameAndType(net, component.Name, catType);
                if (existing != null)
                {
                    UpdateParams(existing, component, catType);
                    if (!isSysres)
                    {
                        syslayIdMap[component.Name] = existing.Attribute("ID")?.Value ?? MakeId(component.Name, "syslay");
                        result.InjectedFBs.Add($"{component.Name} (present — params updated)");
                    }
                    continue;
                }

                // 2. Spare slot → remap (does not count toward new limit)
                if (spareIdx < spareSlots.Count)
                {
                    var slot = spareSlots[spareIdx++];
                    var oldName = slot.Attribute("Name")?.Value ?? "";
                    renames[oldName] = component.Name;
                    slot.SetAttributeValue("Name", component.Name);
                    UpdateParams(slot, component, catType);

                    if (!isSysres)
                    {
                        syslayIdMap[component.Name] = slot.Attribute("ID")?.Value ?? MakeId(component.Name, "syslay");
                        result.InjectedFBs.Add($"{oldName} → {component.Name} ({catType} remap)");
                    }
                    continue;
                }

                // 3. Insert new FB — check batch limit first
                if (maxNew > 0 && newCount >= maxNew)
                {
                    result.UnsupportedComponents.Add(
                        $"{component.Name} — batch limit ({maxNew}) reached; run Generate Code again to inject next batch");
                    continue;
                }

                var id = isSysres ? MakeId(component.Name, "sysres") : MakeId(component.Name, "syslay");
                int x = startX;
                int y = startY + (newFbIndex * LayoutStepY);
                newFbIndex++;
                newCount++;

                var newFb = new XElement(Ns + "FB",
                    new XAttribute("ID", id),
                    new XAttribute("Name", component.Name),
                    new XAttribute("Type", catType),
                    new XAttribute("Namespace", "Main"),
                    new XAttribute("x", x),
                    new XAttribute("y", y));

                if (isSysres && syslayIdMap.TryGetValue(component.Name, out var slId))
                    newFb.SetAttributeValue("Mapping", slId);

                UpdateParams(newFb, component, catType);
                net.Add(newFb);

                if (!isSysres)
                {
                    syslayIdMap[component.Name] = id;
                    result.InjectedFBs.Add($"{component.Name} (new {catType} inserted — {newCount}/{(maxNew > 0 ? maxNew.ToString() : "∞")})");
                }
            }
        }

        // ── Parameter helpers ─────────────────────────────────────────────────

        private static void UpdateParams(XElement fb, VueOneComponent component, string catType)
        {
            if (catType == ActuatorCatType)
                SetOrAddParameter(fb, "actuator_name", $"'{component.Name.ToLower()}'");
            else if (catType == ProcessCatType)
                SetOrAddParameter(fb, "Text", BuildTextParam(component));
        }

        private static void SetOrAddParameter(XElement fb, string paramName, string value)
        {
            var existing = fb.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Parameter" && e.Attribute("Name")?.Value == paramName);
            if (existing != null)
                existing.SetAttributeValue("Value", value);
            else
                fb.Add(new XElement(Ns + "Parameter",
                    new XAttribute("Name", paramName), new XAttribute("Value", value)));
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
                RenameAttrPrefix(conn, "Source", renames);
                RenameAttrPrefix(conn, "Destination", renames);
            }
        }

        private static void RenameAttrPrefix(XElement el, string attr, Dictionary<string, string> renames)
        {
            var val = el.Attribute(attr)?.Value;
            if (string.IsNullOrEmpty(val)) return;
            int dot = val.IndexOf('.');
            if (dot < 0) return;
            if (renames.TryGetValue(val[..dot], out var newPrefix))
                el.SetAttributeValue(attr, newPrefix + val[dot..]);
        }

        // ── DiffReport helpers ────────────────────────────────────────────────

        private static void ClassifyGroup(
            string catType, List<VueOneComponent> components,
            Dictionary<string, List<string>> existingByType,
            HashSet<string> existingNames, DiffReport report)
        {
            var slots = existingByType.GetValueOrDefault(catType, new());
            var spares = slots.Where(n => !components.Any(c =>
                string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            int spareIdx = 0;

            foreach (var c in components)
            {
                bool alreadyPresent = existingNames.Contains(c.Name)
                    && slots.Contains(c.Name, StringComparer.OrdinalIgnoreCase);

                if (alreadyPresent)
                    report.AlreadyPresent.Add($"{c.Name} ({catType})");
                else if (spareIdx < spares.Count)
                    report.ToBeInjected.Add($"{spares[spareIdx++]} → {c.Name} ({catType} remap)");
                else
                    report.ToBeInjected.Add($"{c.Name} (new {catType} — will be inserted)");
            }
        }

        // ── XML helpers ───────────────────────────────────────────────────────

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

        // ── Type guards ───────────────────────────────────────────────────────

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