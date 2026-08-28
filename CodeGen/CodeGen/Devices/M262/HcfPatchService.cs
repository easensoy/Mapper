using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Translation;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Mapping;

namespace CodeGen.Devices.M262
{
    public static class HcfPatchService
    {
        public static void PatchDeployed(GenerationContext ctx, PlcAssignment target, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report)
        {
            PatchDeployed(ctx.Cfg, target, ctx.Profile, bindings, report);
        }

        // The device whose channels this binds is the one whose backend is running, read from its own
        // descriptor: matched by a name it bound whichever device happened to carry that type.
        public static void PatchDeployed(Configuration.CompilerConfiguration? config, PlcAssignment target,
            DeploymentProfile profile,
            IoBindings? bindings,
            SystemInjector.BindingApplicationReport report)
        {
            if (config == null)
            {
                report.Missing.Add("[Hcf] skipped, no MapperConfig available");
                return;
            }

            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
                if (eaeRoot == null)
                {
                    report.Missing.Add("[Hcf] skipped, could not derive EAE project root");
                    return;
                }

                var loc = LocateSysdevAndResource(config, target, eaeRoot);
                if (loc == null)
                {
                    report.Missing.Add(
                        "[Hcf] skipped, no SE.DPAC.M262_dPAC sysdev with M262_RES resource found");
                    return;
                }
                var (sysdevDir, resourceId, sysresPath) = loc.Value;

                // TM3 channels bind straight to the consumer FB instance: there is no broker FB, the CATs
                // are the I/O.
                var fbIdByName = ReadFbIdByName(sysresPath);
                if (fbIdByName.Count == 0)
                {
                    report.Missing.Add(
                        "[Hcf] ERROR: sysres FBNetwork has no FB instances — cannot resolve component IDs");
                    return;
                }

                // .hcf file STEM = sysdev guid (folder name), NOT the resource guid.
                var sysdevGuid = Path.GetFileName(sysdevDir);
                var hcfPath = Path.Combine(sysdevDir, sysdevGuid + ".hcf");

                report.Missing.Add($"[Hcf] resource_guid={resourceId} components={fbIdByName.Count}");
                report.Missing.Add($"[Hcf] writing → {hcfPath}");

                foreach (var stale in Directory.EnumerateFiles(sysdevDir, "*.hcf"))
                {
                    if (!string.Equals(stale, hcfPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(stale); } catch { /* best-effort */ }
                    }
                }
                var sensorNames = ReadSensorNames(sysresPath, config);
                WriteHcfMerged(config, target, profile, hcfPath, resourceId, bindings, fbIdByName,
                    sensorNames, report);

                report.Missing.Add($"[Hcf] wrote   ← {hcfPath}");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static (string sysdevDir, string resourceId, string sysresPath)? LocateSysdevAndResource(
            Configuration.CompilerConfiguration cfg, PlcAssignment target, string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(sysdev);
                    var root = doc.Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    var type = (string?)root.Attribute("Type") ?? string.Empty;
                    var nspace = (string?)root.Attribute("Namespace") ?? string.Empty;
                    if (type != cfg.Targets.Of(target).DeviceType || nspace != TargetDescriptor.DeviceNamespace) continue;
                    XNamespace ns = root.GetDefaultNamespace();
                    var resources = root.Element(ns + "Resources");
                    var m262Res = resources?.Elements(ns + "Resource").FirstOrDefault();
                    if (m262Res == null) continue;

                    // .hcf and .sysres live one level deeper under {sysdev-guid}/.
                    var sysdevStem = Path.GetFileNameWithoutExtension(sysdev);
                    var sysdevDir = Path.Combine(Path.GetDirectoryName(sysdev)!, sysdevStem);
                    Directory.CreateDirectory(sysdevDir);
                    var sysresPath = Directory.EnumerateFiles(sysdevDir, "*.sysres").FirstOrDefault()
                        ?? Path.Combine(sysdevDir, "RES0.sysres");

                    // EAE's .hcf ResourceId is a 16-char hex matching the sysres root ID. A zero/empty
                    // sysdev ID is minted and persisted to sysdev + sysres so all three agree.
                    var resourceId = (string?)m262Res.Attribute("ID") ?? string.Empty;
                    if (IsZeroOrEmptyId(resourceId))
                    {
                        resourceId = NewShortHexId("RES0|" + sysdev);
                        m262Res.SetAttributeValue("ID", resourceId);
                        SaveXml(doc, sysdev);
                        PropagateResourceIdToSysres(sysresPath, resourceId);
                    }
                    return (sysdevDir, resourceId, sysresPath);
                }
                catch { /* skip malformed */ }
            }
            return null;
        }

        private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

        // Idempotent merge into the deployed .hcf; ParameterValue targets are {resourceId}.{fbId}.{port}.
        private static void WriteHcfMerged(Configuration.CompilerConfiguration cfg,
            PlcAssignment target,
            DeploymentProfile profile, string hcfPath, string resourceId,
            IoBindings? bindings, Dictionary<string, string> fbIdByName,
            List<string> sensorNames,
            SystemInjector.BindingApplicationReport report)
        {
            // Seeded from the xlsx actuator PinAssignments. The xlsx has no sensor pin column, so each
            // Sensor_Bool_CAT "Input" takes the next free DI in name order.
            var effective = new Dictionary<string, (string Comp, string Port)>(StringComparer.OrdinalIgnoreCase);
            var usedDi = new HashSet<int>();
            if (bindings != null)
            {
                foreach (var kv in bindings.PinAssignments)
                {
                    effective[kv.Key] = (kv.Value.ComponentName, kv.Value.Port);
                    if (kv.Key.StartsWith("DI", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(kv.Key.Substring(2), out var di)) usedDi.Add(di);
                }
            }
            // The xlsx pre-seeds DI/DO channels with EMPTY values, so a pin is only truly free when it is
            // absent, empty, OR points at a component not on this sysres.
            bool PinBlank(string p) => !effective.TryGetValue(p, out var v)
                || string.IsNullOrEmpty(v.Comp) || !fbIdByName.ContainsKey(v.Comp);
            // The discharge tail's physical channels, from Config/smc-rig.yml. The binder and the parity
            // validator read this one list, so an edit there changes what is emitted AND what is checked.
            {
                foreach (var dc in cfg.Rig.DischargeChannels)
                {
                    if (!fbIdByName.ContainsKey(dc.Component) || !PinBlank(dc.Channel)) continue;
                    effective[dc.Channel] = (dc.Component, dc.Port);
                    if (dc.IsInput && int.TryParse(dc.Channel.Substring(2), out var diCh)) usedDi.Add(diCh);
                    report.Missing.Add($"[Hcf][5b] bound {dc.Channel}={dc.Meaning}");
                }
            }
            var alreadyBoundSensors = new HashSet<string>(
                effective.Values
                    .Where(v => string.Equals(v.Port, "Input", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.Comp), StringComparer.Ordinal);
            int nextDi = 0;
            foreach (var sensor in sensorNames)
            {
                if (alreadyBoundSensors.Contains(sensor)) continue;
                if (!fbIdByName.ContainsKey(sensor)) continue;       // not on sysres → skip
                while (nextDi < 16 && usedDi.Contains(nextDi)) nextDi++;
                if (nextDi >= 16)
                {
                    report.Missing.Add($"[Hcf] no free DI channel for sensor '{sensor}.Input' — TM3DI16 full");
                    break;
                }
                var pin = $"DI{nextDi:D2}";
                effective[pin] = (sensor, "Input");
                usedDi.Add(nextDi);
                report.Missing.Add($"[Hcf] auto-bound {pin} = {sensor}.Input (sensor not in xlsx pin columns)");
            }

            // INVARIANT: a Feed component with an expected M262 channel that is not explicitly on RevPi
            // MUST be on this sysres, else its M262 IO silently blanks. Flag it loudly rather than ship it.
            var expectedM262 = new HashSet<string>(
                (bindings?.PinAssignments.Values.Select(v => v.ComponentName) ?? Enumerable.Empty<string>())
                    .Concat(sensorNames)
                    .Where(c => !string.IsNullOrEmpty(c)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var comp in expectedM262)
            {
                if (profile.AssignedTarget(comp) != null) continue;   // moved elsewhere -> blank is correct
                if (fbIdByName.ContainsKey(comp)) continue;                  // present -> it will bind
                report.Missing.Add($"[Hcf][M262][ORPHAN] '{comp}' is M262-default but MISSING from the M262 " +
                    "sysres, so its M262 IO is left blank. This is a stale partial-RevPi leftover — Clean " +
                    "Demonstrator and re-Generate (M262 keeps Feeder/Checker/Hopper unless you set them to RevPi).");
            }

            string Sym(string pin)
            {
                if (!effective.TryGetValue(pin, out var pa)) return string.Empty;
                if (!fbIdByName.TryGetValue(pa.Comp, out var compFbId))
                {
                    report.Missing.Add(
                        $"[Hcf] {pin} skipped: component '{pa.Comp}' not on sysres FBNetwork");
                    return string.Empty;
                }
                var value = $"{resourceId}.{compFbId}.{pa.Port}";
                report.HcfPinAssignments.Add((pin, value));
                return value;
            }

            var doc = LoadOrCreateHcf(hcfPath);
            var root = doc.Root!;

            // DeviceHwConfigurationItem carries the ResourceId every nested ParameterValue resolves against.
            var devItem = root.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
            if (devItem == null)
            {
                devItem = new XElement("DeviceHwConfigurationItem",
                    new XAttribute("ResourceId", resourceId));
                root.Add(devItem);
            }
            else
            {
                devItem.SetAttributeValue("ResourceId", resourceId);
            }

            // The bus this device carries, module by module, in the order device.yml declares it. The
            // XML SHAPE is EAE's schema and is written by ModuleBlock; WHICH modules there are, their
            // frozen ids, their properties and which of them takes channel bindings are all declared.
            var modules = cfg.Targets.Of(target).HardwareModules;
            XElement into = devItem;
            string? previous = null;
            foreach (var m in modules)
            {
                var block = ModuleBlock(cfg, m, previous);
                if (string.IsNullOrEmpty(m.PinPrefix))
                    UpsertConfigurationBaseItem(into, m.Name, block, report);
                else
                    UpsertModuleWithPins(into, m.Name, () => ModuleBlock(cfg, m, previous),
                        m.PinPrefix!, Sym, report);
                previous = m.Name;
                if (!m.Nest) continue;

                // A bus master holds the modules after it, so the chain continues inside it.
                var master = FindChildBlock(into, m.Name)!;
                into = master.Elements().FirstOrDefault(e => e.Name.LocalName == "Items")
                       ?? Added(master, new XElement("Items"));
            }

            SaveHcfWithRetry(cfg.Generation.FileWriteRetries, doc, hcfPath, report);
        }

        // The root must carry the xmlns:xsd/xmlns:xsi prefixes EAE expects, minted or loaded alike.
        private static XDocument LoadOrCreateHcf(string hcfPath)
        {
            XDocument? doc = null;
            if (File.Exists(hcfPath))
            {
                try
                {
                    doc = XDocument.Load(hcfPath);
                    if (doc.Root?.Name.LocalName != "DeviceHwConfigurationItems")
                        doc = null;
                }
                catch { doc = null; }
            }
            if (doc == null)
            {
                doc = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("DeviceHwConfigurationItems",
                        new XAttribute(XNamespace.Xmlns + "xsd",
                            "http://www.w3.org/2001/XMLSchema"),
                        new XAttribute(XNamespace.Xmlns + "xsi",
                            "http://www.w3.org/2001/XMLSchema-instance")));
            }
            var root = doc.Root!;
            if (root.Attribute(XNamespace.Xmlns + "xsd") == null)
                root.SetAttributeValue(XNamespace.Xmlns + "xsd",
                    "http://www.w3.org/2001/XMLSchema");
            if (root.Attribute(XNamespace.Xmlns + "xsi") == null)
                root.SetAttributeValue(XNamespace.Xmlns + "xsi",
                    "http://www.w3.org/2001/XMLSchema-instance");
            return doc;
        }

        private static void UpsertConfigurationBaseItem(XElement parent, string blockName,
            XElement freshBlock, SystemInjector.BindingApplicationReport report)
        {
            var existing = parent.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "ConfigurationBaseItem" &&
                e.Elements().Any(c =>
                    c.Name.LocalName == "Name" &&
                    (c.Value ?? string.Empty).Trim() == blockName));
            if (existing != null)
            {
                existing.ReplaceWith(freshBlock);
                report.Missing.Add($"[Hcf] replaced existing {blockName} block");
            }
            else
            {
                parent.Add(freshBlock);
                report.Missing.Add($"[Hcf] appended new {blockName} block");
            }
        }

        private static XElement Added(XElement parent, XElement child)
        {
            parent.Add(child);
            return child;
        }

        // ONE module block, from ONE declaration. Element and attribute ORDER here is the serialised
        // order EAE reads, so it is the schema's, not a preference - the four hand-written shells this
        // replaces each spelled the same shape and could each drift from it separately.
        private static XElement ModuleBlock(Configuration.CompilerConfiguration cfg,
            Configuration.HardwareModule m, string? previous)
        {
            var itemProps = new XElement("ItemProperties",
                m.ItemProperties.Select(p => ItemProperty(cfg, p)));
            for (int ch = 0; ch < m.Channels; ch++)
                foreach (var p in m.ChannelProperties)
                    itemProps.Add(ItemProperty(cfg, p, $"Channel_{ch}.{p.Name}"));

            var block = new XElement("ConfigurationBaseItem",
                new XElement("Name", m.Name),
                new XElement("ID", m.Id),
                new XElement("Type",
                    new XElement("Name", m.Name),
                    new XElement("Namespace", m.TypeNamespace)),
                itemProps,
                new XElement("ParameterValues",
                    m.ParameterValues.Select(p => new XElement("ParameterValue",
                        new XAttribute("Name", p.Name),
                        new XAttribute("Value", DeclaredValue(cfg, p))))));

            if (previous != null)
                block.Add(new XElement("PreviousItem",
                    new XElement("Name", previous),
                    new XElement("PortName", "BusOut")));
            if (!string.IsNullOrEmpty(m.MasterConfigFile))
                block.Add(new XElement("MasterConfigFileName", m.MasterConfigFile));
            block.Add(new XElement("Items"));
            return block;
        }

        // A declared value names something config.yaml owns; anything else is the literal it states.
        private static string DeclaredValue(Configuration.CompilerConfiguration cfg,
            Configuration.HardwareModuleProperty p) =>
            !string.Equals(p.Kind, "declared", StringComparison.OrdinalIgnoreCase) ? p.Value
            : p.Value switch
            {
                "busCycleTime" => BusCycleTime(cfg),
                "busCycleTolerance" => BusCycleTolerance(cfg),
                "busCycleActionWhenMissed" => BusCycleActionWhenMissed(cfg),
                _ => throw new InvalidOperationException(
                    $"[Hcf] device.yml module property '{p.Name}' declares value '{p.Value}', which " +
                    "config.yaml does not own. A declared value must name one this generator can supply."),
            };

        private static XElement ItemProperty(Configuration.CompilerConfiguration cfg,
            Configuration.HardwareModuleProperty p, string? name = null)
        {
            var kind = string.Equals(p.Kind, "unsignedByte", StringComparison.OrdinalIgnoreCase)
                ? "xsd:unsignedByte" : "xsd:string";
            var el = new XElement("ItemProperty",
                new XElement("Name", name ?? p.Name),
                new XElement("Value", new XAttribute(XsiNs + "type", kind), DeclaredValue(cfg, p)));
            if (p.HwParam != null)
                el.Add(new XElement("HWParameters", new XElement("string", p.HwParam)));
            return el;
        }

        private static XElement? FindChildBlock(XElement parent, string blockName) =>
            parent.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "ConfigurationBaseItem" &&
                e.Elements().Any(c =>
                    c.Name.LocalName == "Name" &&
                    (c.Value ?? string.Empty).Trim() == blockName));

        private static void UpsertModuleWithPins(XElement items, string blockName,
            Func<XElement> shellFactory, string pinPrefix,
            Func<string, string> sym, SystemInjector.BindingApplicationReport report)
        {
            var existingPins = new HashSet<string>(StringComparer.Ordinal);
            var existing = items.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "ConfigurationBaseItem" &&
                e.Elements().Any(c =>
                    c.Name.LocalName == "Name" &&
                    (c.Value ?? string.Empty).Trim() == blockName));
            if (existing != null)
            {
                var oldPv = existing.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "ParameterValues");
                if (oldPv != null)
                {
                    foreach (var pv in oldPv.Elements()
                        .Where(e => e.Name.LocalName == "ParameterValue"))
                    {
                        var n = (string?)pv.Attribute("Name");
                        if (!string.IsNullOrEmpty(n)) existingPins.Add(n);
                    }
                }
            }

            var fresh = shellFactory();
            if (existing != null)
            {
                existing.ReplaceWith(fresh);
                report.Missing.Add($"[Hcf] replaced existing {blockName} block");
            }
            else
            {
                items.Add(fresh);
                report.Missing.Add($"[Hcf] appended new {blockName} block");
            }

            var freshPv = fresh.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ParameterValues");
            if (freshPv == null)
            {
                freshPv = new XElement("ParameterValues");
                var anchor = fresh.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "ItemProperties");
                if (anchor != null) anchor.AddAfterSelf(freshPv);
                else fresh.Add(freshPv);
            }

            for (int i = 0; i < 16; i++)
            {
                var pin = $"{pinPrefix}{i:D2}";
                var value = sym(pin);

                freshPv.Elements()
                    .Where(e => e.Name.LocalName == "ParameterValue" &&
                                (string?)e.Attribute("Name") == pin)
                    .Remove();

                freshPv.Add(new XElement("ParameterValue",
                    new XAttribute("Name", pin),
                    new XAttribute("Value", value)));

                var status = existingPins.Contains(pin) ? "replaced" : "new";
                report.Missing.Add($"[Hcf] {pin} = {value} ({status})");
            }
        }


        // The TM3 bus contract, read once from config.yaml. It used to be spelled twice in this
        // file - the module shell and the item-property block - which is two owners of one fact.
        static string BusCycleTime(Configuration.CompilerConfiguration cfg) =>
            Configuration.GenerationConfig.Duration(cfg.Generation.M262BusCycleMs);
        static string BusCycleTolerance(Configuration.CompilerConfiguration cfg) =>
            cfg.Generation.M262BusCycleTolerance.ToString();
        static string BusCycleActionWhenMissed(Configuration.CompilerConfiguration cfg) =>
            cfg.Generation.M262BusCycleActionWhenMissed.ToString();


        // UTF-8 no BOM (EAE requirement); retries up to 8 times if EAE briefly holds a write lock.
        private static void SaveHcfWithRetry(int retries, XDocument doc, string hcfPath,
            SystemInjector.BindingApplicationReport report)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(false),
                NewLineHandling = System.Xml.NewLineHandling.Replace,
            };

            int attempt = Services.FbtXmlEditor.SaveXmlRetrying(retries, hcfPath, settings, doc.Save);
            if (attempt > 1)
                report.Missing.Add(
                    $"[Hcf] write succeeded on attempt {attempt} (EAE briefly held a lock).");
        }

        private static Dictionary<string, string> ReadFbIdByName(string sysresPath)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(sysresPath)) return map;
                var doc = XDocument.Load(sysresPath);
                var root = doc.Root;
                if (root == null) return map;
                XNamespace ns = root.GetDefaultNamespace();
                var fbNet = root.Element(ns + "FBNetwork");
                if (fbNet == null) return map;
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var n = (string?)fb.Attribute("Name") ?? string.Empty;
                    var id = (string?)fb.Attribute("ID") ?? string.Empty;
                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(id))
                        map[n] = id;
                }
            }
            catch { /* best-effort */ }
            return map;
        }

        private static List<string> ReadSensorNames(string sysresPath, Configuration.CompilerConfiguration cfg)
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(sysresPath)) return list;
                var doc = XDocument.Load(sysresPath);
                var root = doc.Root;
                if (root == null) return list;
                XNamespace ns = root.GetDefaultNamespace();
                var fbNet = root.Element(ns + "FBNetwork");
                if (fbNet == null) return list;
                foreach (var fb in fbNet.Elements(ns + "FB"))
                {
                    var t = (string?)fb.Attribute("Type") ?? string.Empty;
                    var n = (string?)fb.Attribute("Name") ?? string.Empty;
                    if (!string.IsNullOrEmpty(n) &&
                        t.StartsWith(cfg.Manifest.SensorType.Name, StringComparison.Ordinal))
                        list.Add(n);
                }
            }
            catch { /* best-effort */ }
            return list;
        }


        private static void SaveXml(XDocument doc, string path)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(false),
            };
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var w = System.Xml.XmlWriter.Create(fs, settings);
            doc.Save(w);
        }

        // EAE resolves symlinks by ID equality across sysdev -> sysres -> .hcf; all three need the same
        // non-zero ID or the Symbolic Links view goes red.
        private static void PropagateResourceIdToSysres(string sysresPath, string newId)
        {
            try
            {
                if (!File.Exists(sysresPath)) return;
                var doc = XDocument.Load(sysresPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                var current = (string?)root.Attribute("ID") ?? string.Empty;
                if (string.Equals(current, newId, StringComparison.Ordinal)) return;
                root.SetAttributeValue("ID", newId);
                SaveXml(doc, sysresPath);
            }
            catch { /* best-effort */ }
        }

        private static bool IsZeroOrEmptyId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return true;
            foreach (var c in id) if (c != '0' && c != '-') return false;
            return true;
        }

        // Deterministic 16-char uppercase hex, the format EAE writes; same seed -> same stable ID.
        private static string NewShortHexId(string seed)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
            var sb = new System.Text.StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("X2"));
            return sb.ToString();
        }
    }
}
