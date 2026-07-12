using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Translation;

namespace CodeGen.Devices.M262
{
    public static class HcfPatchService
    {
        public static void PatchDeployed(MapperConfig? config,
            string syslayPath, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report)
        {
            var syslayFbNames = ReadSyslayFbNames(syslayPath);
            PatchDeployed(config, syslayFbNames, bindings, report);
        }

        public static void PatchDeployed(MapperConfig? config,
            HashSet<string> syslayFbNames,
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

                var loc = LocateM262SysdevAndResource(eaeRoot);
                if (loc == null)
                {
                    report.Missing.Add(
                        "[Hcf] skipped, no SE.DPAC.M262_dPAC sysdev with M262_RES resource found");
                    return;
                }
                var (sysdevDir, resourceId, sysresPath) = loc.Value;

                // TM3 channels bind their symlink directly to the consumer FB instance
                // (e.g. "Feeder.athome") — no M262IO/PLC_RW_M262 broker FB; the CATs are the I/O.
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
                var sensorNames = ReadSensorNames(sysresPath);
                WriteHcfMerged(hcfPath, resourceId, bindings, fbIdByName, sensorNames, report);

                report.Missing.Add($"[Hcf] wrote   ← {hcfPath}");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EnsureDeployedHcfPopulated(string hcfPath, MapperConfig config,
            string eaeRoot, SystemInjector.BindingApplicationReport report)
        {
            try
            {
                var doc = XDocument.Load(hcfPath);
                int tmCount = doc.Descendants()
                    .Count(e => e.Name.LocalName == "Name" &&
                                (e.Value == "TM3DI16_G" || e.Value == "TM3DQ16T_G"));
                if (tmCount > 0) return; // already populated

                var baseline = config.M262HardwareConfigBaselinePath;
                if (string.IsNullOrWhiteSpace(baseline) || !Directory.Exists(baseline))
                {
                    report.Missing.Add(
                        "[Hcf] deployed .hcf is empty and M262HardwareConfigBaselinePath is not set — " +
                        "cannot reseed. EAE Hardware Configurator will stay empty.");
                    return;
                }

                var srcHcf = PickRichestBaselineHcf(baseline);
                if (srcHcf == null)
                {
                    report.Missing.Add("[Hcf] deployed .hcf is empty and no usable baseline .hcf found.");
                    return;
                }

                var seed = XDocument.Load(srcHcf);
                var sysresId = M262HwConfigCopier.ReadTargetSysresId(eaeRoot);
                if (!string.IsNullOrEmpty(sysresId))
                {
                    var item = seed.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
                    item?.SetAttributeValue("ResourceId", sysresId);
                }
                seed.Save(hcfPath);
                report.Missing.Add(
                    $"[Hcf] deployed .hcf was empty — reseeded from baseline '{Path.GetFileName(srcHcf)}'.");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] reseed failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string? PickRichestBaselineHcf(string baselineRoot)
        {
            string? best = null;
            int bestCount = 0;
            foreach (var path in Directory.EnumerateFiles(baselineRoot, "*.hcf", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(path);
                    int count = doc.Descendants()
                        .Count(e => e.Name.LocalName == "Name" &&
                                    (e.Value == "TM3DI16_G" || e.Value == "TM3DQ16T_G"));
                    if (count > bestCount) { bestCount = count; best = path; }
                }
                catch { /* skip malformed */ }
            }
            return best;
        }

        private static (string sysdevDir, string resourceId, string sysresPath)? LocateM262SysdevAndResource(string eaeRoot)
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
                    if (type != "M262_dPAC" || nspace != "SE.DPAC") continue;
                    XNamespace ns = root.GetDefaultNamespace();
                    var resources = root.Element(ns + "Resources");
                    // The M262 device always has exactly one resource child.
                    var m262Res = resources?.Elements(ns + "Resource").FirstOrDefault();
                    if (m262Res == null) continue;

                    // .hcf and .sysres live one level deeper under {sysdev-guid}/.
                    var sysdevStem = Path.GetFileNameWithoutExtension(sysdev);
                    var sysdevDir = Path.Combine(Path.GetDirectoryName(sysdev)!, sysdevStem);
                    Directory.CreateDirectory(sysdevDir);
                    var sysresPath = Directory.EnumerateFiles(sysdevDir, "*.sysres").FirstOrDefault()
                        ?? Path.Combine(sysdevDir, "RES0.sysres");

                    // EAE's .hcf ResourceId is a 16-char hex matching the sysres root ID; if the
                    // sysdev ID is zero/empty, mint one and persist it to sysdev + sysres so all
                    // three carry the same non-zero GUID.
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

        private static string EnsureM262IoFb(string sysresPath, string resourceId,
            SystemInjector.BindingApplicationReport report)
        {
            try
            {
                if (!File.Exists(sysresPath)) return string.Empty;
                var doc = XDocument.Load(sysresPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return string.Empty;
                XNamespace ns = root.GetDefaultNamespace();
                var fbNet = root.Element(ns + "FBNetwork");
                if (fbNet == null) return string.Empty;

                var m262Io = fbNet.Elements(ns + "FB")
                    .FirstOrDefault(e => (string?)e.Attribute("Type") == "PLC_RW_M262");
                if (m262Io == null) return string.Empty;

                var fbId = (string?)m262Io.Attribute("ID") ?? string.Empty;
                if (IsZeroOrEmptyId(fbId))
                {
                    fbId = NewShortHexId("M262IO|" + resourceId);
                    m262Io.SetAttributeValue("ID", fbId);
                    SaveXml(doc, sysresPath);
                }
                return fbId;
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] EnsureM262IoFb failed: {ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }

        private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

        // Idempotent merge into the deployed .hcf. ParameterValue targets use the symlink
        // convention {resourceId}.{componentFbId}.{port}.
        private static void WriteHcfMerged(string hcfPath, string resourceId,
            IoBindings? bindings, Dictionary<string, string> fbIdByName,
            List<string> sensorNames,
            SystemInjector.BindingApplicationReport report)
        {
            // Effective pin -> (component, port). Seed from the xlsx actuator PinAssignments,
            // then auto-assign each Sensor_Bool_CAT "Input" to the next free DI (xlsx has no
            // sensor pin column); sensors fill the lowest free DI slots in name order.
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
            // The xlsx pre-seeds DI/DO channels with EMPTY values, so a pin is truly free to
            // bind in code when it is absent, empty, OR points at a component not on this sysres.
            bool PinBlank(string p) => !effective.TryGetValue(p, out var v)
                || string.IsNullOrEmpty(v.Comp) || !fbIdByName.ContainsKey(v.Comp);
            // UR3e task arm (only when the Robot FB is on this resource's sysres): DO04 = start-task
            // pulse, DI10 = task-complete.
            if (MapperConfig.EnableRobotTaskTail && fbIdByName.ContainsKey("Robot"))
            {
                if (!effective.ContainsKey("DO04"))
                    effective["DO04"] = ("Robot", "RobotCommands_StartTask");
                if (!effective.ContainsKey("DI10"))
                {
                    effective["DI10"] = ("Robot", "RobotStatus_Task_Complete");
                    usedDi.Add(10);
                }
                report.Missing.Add("[Hcf][5b] bound DO04=Robot.RobotCommands_StartTask, DI10=Robot.RobotStatus_Task_Complete");
            }
            // M262 Ejector is open-loop (coil only, no DIs): bind DO03 = OutputToWork, only when
            // the Ejector FB is on this resource's sysres.
            if (MapperConfig.EnableRobotTaskTail && fbIdByName.ContainsKey("Ejector")
                && PinBlank("DO03"))
            {
                effective["DO03"] = ("Ejector", "OutputToWork");
                report.Missing.Add("[Hcf][5b] bound DO03=Ejector.OutputToWork (open-loop, no sensor DIs)");
            }
            // Synthesized M262 rig proximity sensors (not in the twin): bind each to its fixed
            // physical DI channel, only when the synthesized FB is on this resource's sysres.
            foreach (var (synthName, synthPin, _) in MapperConfig.M262SynthSensors)
            {
                if (!MapperConfig.EnableRobotTaskTail) break;
                if (!fbIdByName.ContainsKey(synthName) || !PinBlank(synthPin)) continue;
                effective[synthPin] = (synthName, "Input");
                if (synthPin.Length == 4 && int.TryParse(synthPin.Substring(2), out var diCh)) usedDi.Add(diCh);
                report.Missing.Add($"[Hcf][5b] bound {synthPin}={synthName}.Input (synthesized M262 rig sensor, not in twin)");
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

            // INVARIANT (user: "by default Feeder/Checker belong to M262; only touch their IO if the user moves
            // that component to RevPi"). Every Feed component that has an expected M262 channel and is NOT
            // explicitly on RevPi MUST be on this sysres, else its M262 IO silently blanks. If one is missing
            // it is a stale partial-RevPi leftover (a component routed off M262 with no RevPi device to host it)
            // -> flag it LOUDLY so it is never silently shipped. A plain Clean + re-Generate restores it (pure
            // M262 puts the component back on the M262 sysres and this HCF re-binds it).
            var expectedM262 = new HashSet<string>(
                (bindings?.PinAssignments.Values.Select(v => v.ComponentName) ?? Enumerable.Empty<string>())
                    .Concat(sensorNames)
                    .Where(c => !string.IsNullOrEmpty(c)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var comp in expectedM262)
            {
                if (MapperConfig.RevPiComponents.Contains(comp)) continue;   // explicitly on RevPi -> blank is correct
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

            UpsertConfigurationBaseItem(devItem, "BMTM3", BuildBmtm3Shell(), report);
            var bmtm3 = FindChildBlock(devItem, "BMTM3")!;
            var items = bmtm3.Elements().FirstOrDefault(e => e.Name.LocalName == "Items");
            if (items == null)
            {
                items = new XElement("Items");
                bmtm3.Add(items);
            }

            UpsertConfigurationBaseItem(items, "TM262L01MDESE8T", BuildTm262Block(), report);

            UpsertModuleWithPins(items, "TM3DI16_G", BuildTm3Di16Shell, "DI", Sym, report);
            UpsertModuleWithPins(items, "TM3DQ16T_G", BuildTm3Dq16Shell, "DO", Sym, report);

            SaveHcfWithRetry(doc, hcfPath, report);
        }

        // Load the existing .hcf, or mint a skeleton with the xmlns:xsd/xmlns:xsi prefix
        // declarations EAE expects on the root (also patched onto a loaded file if missing).
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

                // Remove any same-Name ParameterValue before re-adding, for idempotency.
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

        // Block builders — each returns a detached XElement matching the .hcf shape EAE accepts.

        private static XElement BuildBmtm3Shell() => new XElement("ConfigurationBaseItem",
            new XElement("Name", "BMTM3"),
            new XElement("ID", "9510AF594EA1EDD1"),
            new XElement("Type",
                new XElement("Name", "BMTM3"),
                new XElement("Namespace", "SE.IoTMx")),
            new XElement("ItemProperties",
                ItemPropertyStr("busid", "TM3Config", "BUS_ID"),
                ItemPropertyByte("powerConsumption", "0", null),
                ItemPropertyStr("buscycletime", "T#80ms", "busCycleTime"),
                ItemPropertyStr("buscycletolerance", "30", "busCycleTolerance"),
                ItemPropertyStr("buscycleactionwhenmissed", "1", "busCycleActionWhenMissed"),
                ItemPropertyStr("enableSymlinkOC", "TRUE", "enableSymlinkOC")),
            new XElement("ParameterValues",
                new XElement("ParameterValue",
                    new XAttribute("Name", "busId"), new XAttribute("Value", "'BMTM3'")),
                new XElement("ParameterValue",
                    new XAttribute("Name", "enableSymlinkOC"), new XAttribute("Value", "TRUE")),
                new XElement("ParameterValue",
                    new XAttribute("Name", "phase"), new XAttribute("Value", "T#0ms")),
                new XElement("ParameterValue",
                    new XAttribute("Name", "busCycleTime"), new XAttribute("Value", "T#80ms")),
                new XElement("ParameterValue",
                    new XAttribute("Name", "busCycleTolerance"), new XAttribute("Value", "30")),
                new XElement("ParameterValue",
                    new XAttribute("Name", "busCycleActionWhenMissed"), new XAttribute("Value", "1")),
                new XElement("ParameterValue",
                    new XAttribute("Name", "busStatusSymlink"), new XAttribute("Value", ""))),
            new XElement("MasterConfigFileName",
                @"${ProjectDir}\${SystemName}\RuntimeData\${DeviceName}\boot\${busid}.xml"),
            new XElement("Items"));

        private static XElement BuildTm262Block() => new XElement("ConfigurationBaseItem",
            new XElement("Name", "TM262L01MDESE8T"),
            new XElement("ID", "E2B036F9B0A5B0A4"),
            new XElement("Type",
                new XElement("Name", "TM262L01MDESE8T"),
                new XElement("Namespace", "SE.IoTMx")),
            new XElement("ItemProperties"),
            new XElement("ParameterValues"),
            new XElement("PreviousItem",
                new XElement("Name", "BMTM3"),
                new XElement("PortName", "BusOut")),
            new XElement("Items"));

        private static XElement BuildTm3Di16Shell()
        {
            var itemProps = new XElement("ItemProperties",
                ItemPropertyByte("OptionalModule", "0", null));
            for (int ch = 0; ch < 16; ch++)
            {
                itemProps.Add(ItemPropertyByte($"Channel_{ch}.Latch", "32", null));
                itemProps.Add(ItemPropertyByte($"Channel_{ch}.Filter", "4", null));
            }
            return new XElement("ConfigurationBaseItem",
                new XElement("Name", "TM3DI16_G"),
                new XElement("ID", "52DB1E4920A80F90"),
                new XElement("Type",
                    new XElement("Name", "TM3DI16_G"),
                    new XElement("Namespace", "SE.IoTMx")),
                itemProps,
                new XElement("ParameterValues"),
                new XElement("PreviousItem",
                    new XElement("Name", "TM262L01MDESE8T"),
                    new XElement("PortName", "BusOut")),
                new XElement("Items"));
        }

        private static XElement BuildTm3Dq16Shell() => new XElement("ConfigurationBaseItem",
            new XElement("Name", "TM3DQ16T_G"),
            new XElement("ID", "1256CB09958B4E27"),
            new XElement("Type",
                new XElement("Name", "TM3DQ16T_G"),
                new XElement("Namespace", "SE.IoTMx")),
            new XElement("ItemProperties",
                ItemPropertyByte("OptionalModule", "0", null)),
            new XElement("ParameterValues"),
            new XElement("PreviousItem",
                new XElement("Name", "TM3DI16_G"),
                new XElement("PortName", "BusOut")),
            new XElement("Items"));

        private static XElement ItemPropertyStr(string name, string value, string? hwParam)
        {
            var el = new XElement("ItemProperty",
                new XElement("Name", name),
                new XElement("Value",
                    new XAttribute(XsiNs + "type", "xsd:string"), value));
            if (hwParam != null)
                el.Add(new XElement("HWParameters", new XElement("string", hwParam)));
            return el;
        }

        private static XElement ItemPropertyByte(string name, string value, string? hwParam)
        {
            var el = new XElement("ItemProperty",
                new XElement("Name", name),
                new XElement("Value",
                    new XAttribute(XsiNs + "type", "xsd:unsignedByte"), value));
            if (hwParam != null)
                el.Add(new XElement("HWParameters", new XElement("string", hwParam)));
            return el;
        }

        // UTF-8 no BOM (EAE requirement); retries up to 8 times if EAE briefly holds a write lock.
        private static void SaveHcfWithRetry(XDocument doc, string hcfPath,
            SystemInjector.BindingApplicationReport report)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(false),
                NewLineHandling = System.Xml.NewLineHandling.Replace,
            };

            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(hcfPath,
                        FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    if (attempt > 1)
                        report.Missing.Add(
                            $"[Hcf] write succeeded on attempt {attempt} (EAE briefly held a lock).");
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
            }
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

        // FB instance names on the sysres whose Type is Sensor_Bool_CAT.
        private static List<string> ReadSensorNames(string sysresPath)
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
                        t.StartsWith("Sensor_Bool_CAT", StringComparison.Ordinal))
                        list.Add(n);
                }
            }
            catch { /* best-effort */ }
            return list;
        }

        private static HashSet<string> ReadSyslayFbNames(string syslayPath)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return names;
                var doc = XDocument.Load(syslayPath);
                foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                {
                    var name = (string?)fb.Attribute("Name");
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            catch { /* best-effort */ }
            return names;
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

        // EAE resolves symlinks by ID equality across sysdev -> sysres -> .hcf; all three must
        // carry the same non-zero ID or the Symbolic Links view goes red.
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

        // Deterministic 16-char uppercase hex ID matching the format EAE writes into baseline
        // .sysres/.hcf files; same seed -> same ID so the ResourceId stays stable across runs.
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
