using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace MapperUI.Services
{
    /// <summary>
    /// Patches the deployed M262 <c>.hcf</c> after Button-2 / Generate so EAE
    /// picks up the symbolic-link bindings on reload.
    ///
    /// Reads the EXISTING .hcf on disk (NOT the baseline — that's the Deploy
    /// Universal Architecture flow's job via <see cref="M262HwConfigCopier.Copy"/>),
    /// resolves <c>resourceId</c> + <c>m262IoFbId</c> from the deployed M262
    /// sysres, rewrites the TM3 module ParameterValues against the in-scope
    /// syslay FB names + IO bindings, and saves.
    ///
    /// Lives in MapperUI.Services because it cross-references M262HcfDocument /
    /// M262HwConfigCopier / M262SysdevEmitter, none of which CodeGen.dll can see.
    /// </summary>
    public static class HcfPatchService
    {
        /// <summary>
        /// Run the patch. Each pin actually rewritten is appended to
        /// <paramref name="report"/>.<see cref="SystemInjector.BindingApplicationReport.HcfPinAssignments"/>;
        /// every skip reason / warning is appended to
        /// <see cref="SystemInjector.BindingApplicationReport.Missing"/>.
        /// </summary>
        /// <summary>
        /// Convenience overload — reads in-scope FB names from the just-emitted
        /// syslay file (every &lt;FB Name="..."/&gt; inside &lt;SubAppNetwork&gt;).
        /// Suitable for MainForm.btnTestStation1_Click which already knows the
        /// syslay path it asked the injector to write.
        /// </summary>
        public static void PatchDeployed(MapperConfig? config,
            string syslayPath, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report)
        {
            var syslayFbNames = ReadSyslayFbNames(syslayPath);
            PatchDeployed(config, syslayFbNames, bindings, report);
        }

        /// <summary>Core overload — caller supplies the in-scope FB-name set directly.</summary>
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
                var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(config);
                if (eaeRoot == null)
                {
                    report.Missing.Add("[Hcf] skipped, could not derive EAE project root");
                    return;
                }

                // 1. Walk Demonstrator System tree, find the M262 sysdev
                //    (root Device with Type="M262_dPAC" Namespace="SE.DPAC")
                //    and read the embedded M262_RES resource GUID from
                //    <Resources><Resource Name="M262_RES" ID="..."/></Resources>.
                var loc = LocateM262SysdevAndResource(eaeRoot);
                if (loc == null)
                {
                    report.Missing.Add(
                        "[Hcf] skipped, no SE.DPAC.M262_dPAC sysdev with M262_RES resource found");
                    return;
                }
                var (sysdevDir, resourceId, sysresPath) = loc.Value;

                // 2. Ensure the .sysres FBNetwork carries an M262IO PLC_RW_M262
                //    FB. Inject one with a deterministic short-hex ID if missing
                //    — without this, EAE has no symlink target and the Hardware
                //    Configurator view stays blank.
                // 2. Build a {component-name → fb-id} map from the M262 sysres
                //    FBNetwork. Option A: each TM3 channel publishes its
                //    symlink directly to the consumer's FB instance (e.g.
                //    "Feeder.athome", "PartInHopper.Input"), matching the
                //    actuator/sensor CATs' SYMLINKMULTIVARDST $${PATH}<port>
                //    expansion. The M262IO FB ID is no longer in the
                //    ParameterValue — it's a passive bridge.
                var fbIdByName = ReadFbIdByName(sysresPath);
                if (fbIdByName.Count == 0)
                {
                    report.Missing.Add(
                        "[Hcf] ERROR: sysres FBNetwork has no FB instances — cannot resolve component IDs");
                    return;
                }

                // 3. Target file: {Demonstrator}/IEC61499/System/{system-guid}/
                //    {sysdev-guid}/{sysdev-guid}.hcf — file STEM = sysdev guid
                //    (folder name), NOT the resource guid.
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
                WriteHcfMerged(hcfPath, resourceId, bindings, fbIdByName, report);

                report.Missing.Add($"[Hcf] wrote   ← {hcfPath}");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// If the deployed <c>.hcf</c> has no TM3 modules (e.g. empty shell left by
        /// DemonstratorWiper), pick the richest <c>.hcf</c> from the baseline —
        /// the one with the most <c>TM3DI16_G</c>/<c>TM3DQ16T_G</c> module
        /// entries — and copy it over the deployed path. Sets the
        /// <c>ResourceId</c> attribute to match the deployed sysres so EAE's
        /// IO Mapping table resolves. Best-effort; logs to <c>report.Missing</c>.
        /// </summary>
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

        /// <summary>
        /// Scan every <c>.hcf</c> under the baseline tree, count
        /// TM3DI16_G/TM3DQ16T_G module names, and return the file with the
        /// highest count. Avoids picking a sibling .hcf that belongs to a
        /// different (non-M262) device.
        /// </summary>
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

        /// <summary>
        /// Walk <c>{eaeRoot}/IEC61499/System/</c> for a <c>.sysdev</c> whose
        /// root <c>Device</c> element has <c>Type="M262_dPAC"</c> and
        /// <c>Namespace="SE.DPAC"</c>, find the embedded
        /// <c>&lt;Resource Name="M262_RES"&gt;</c>, and return the sysdev
        /// folder, the resource's GUID, plus the sibling <c>.sysres</c> file
        /// path (named <c>{resourceId}.sysres</c>). Returns <c>null</c> if
        /// nothing matches.
        /// </summary>
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
                    // Take whichever Resource lives inside the M262 sysdev —
                    // M262SysdevEmitter renames it per cfg.ResourceName (RES0
                    // by default, but historically also M262_RES / EcoRT_0).
                    // Filtering by name brittle; the M262 device always has
                    // exactly one resource child.
                    var m262Res = resources?.Elements(ns + "Resource").FirstOrDefault();
                    if (m262Res == null) continue;

                    // Sysdev sits at {sys-guid}/{sysdev-guid}.sysdev; the .hcf
                    // and .sysres live one level deeper under {sysdev-guid}/.
                    var sysdevStem = Path.GetFileNameWithoutExtension(sysdev);
                    var sysdevDir = Path.Combine(Path.GetDirectoryName(sysdev)!, sysdevStem);
                    Directory.CreateDirectory(sysdevDir);
                    var sysresPath = Directory.EnumerateFiles(sysdevDir, "*.sysres").FirstOrDefault()
                        ?? Path.Combine(sysdevDir, "RES0.sysres");

                    // The sysdev's <Resource ID> ships as zeros (or as a
                    // long-dashed GUID). EAE's .hcf ResourceId convention is
                    // a 16-char hex value matching the sysres root ID. If the
                    // sysdev's ID is zero/empty, mint a deterministic short
                    // hex from the sysdev path and persist it to BOTH the
                    // sysdev <Resource> and the sysres root <Resource ID="">
                    // so all three carry the same non-zero GUID.
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

        /// <summary>
        /// Look up the M262IO FB by <c>Type="PLC_RW_M262"</c> on the sysres
        /// FBNetwork (there is exactly one) and return its <c>ID</c>. Does
        /// NOT inject — if the FB is missing, returns empty so the caller
        /// can abort the .hcf write with a hard error log. Filtering by Type
        /// (not Name) avoids ever picking up an actuator/sensor FB ID by
        /// accident even if someone renamed the M262IO instance.
        /// </summary>
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

        /// <summary>
        /// Idempotent merge into the deployed .hcf — BMTM3 + TM262L01MDESE8T +
        /// TM3DI16_G (all 16 channels with Latch=32/Filter=4) + TM3DQ16T_G.
        /// Each top-level <c>ConfigurationBaseItem</c> block is replaced
        /// in-place if it already exists, appended once if not. Inside
        /// TM3DI16_G/TM3DQ16T_G, each <c>ParameterValue</c> (DI00..DI15,
        /// DO00..DO15) is removed by Name attribute before being re-added so
        /// running Button-2 twice yields a byte-identical .hcf. ParameterValue
        /// targets use the component-style symlink convention
        /// <c>{resourceId}.{componentFbId}.{port}</c>.
        /// </summary>
        private static void WriteHcfMerged(string hcfPath, string resourceId,
            IoBindings? bindings, Dictionary<string, string> fbIdByName,
            SystemInjector.BindingApplicationReport report)
        {
            string Sym(string pin)
            {
                if (bindings == null) return string.Empty;
                if (!bindings.PinAssignments.TryGetValue(pin, out var pa)) return string.Empty;
                if (!fbIdByName.TryGetValue(pa.ComponentName, out var compFbId))
                {
                    report.Missing.Add(
                        $"[Hcf] {pin} skipped: component '{pa.ComponentName}' not on sysres FBNetwork");
                    return string.Empty;
                }
                var value = $"{resourceId}.{compFbId}.{pa.Port}";
                report.HcfPinAssignments.Add((pin, value));
                return value;
            }

            var doc = LoadOrCreateHcf(hcfPath);
            var root = doc.Root!;

            // DeviceHwConfigurationItem is the single child of the root that
            // carries the ResourceId attribute every nested ParameterValue
            // symlink ultimately resolves against.
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

            // BMTM3 (outer ConfigurationBaseItem). Replace-or-append, then
            // re-locate the live element so its <Items/> child can host the
            // three nested blocks.
            UpsertConfigurationBaseItem(devItem, "BMTM3", BuildBmtm3Shell(), report);
            var bmtm3 = FindChildBlock(devItem, "BMTM3")!;
            var items = bmtm3.Elements().FirstOrDefault(e => e.Name.LocalName == "Items");
            if (items == null)
            {
                items = new XElement("Items");
                bmtm3.Add(items);
            }

            // TM262L01MDESE8T — wholesale replace-or-append, no per-pin pass.
            UpsertConfigurationBaseItem(items, "TM262L01MDESE8T", BuildTm262Block(), report);

            // TM3 modules: replace-or-append the shell, then upsert the 16
            // DI/DO ParameterValues per the user's per-pin spec.
            UpsertModuleWithPins(items, "TM3DI16_G", BuildTm3Di16Shell, "DI", Sym, report);
            UpsertModuleWithPins(items, "TM3DQ16T_G", BuildTm3Dq16Shell, "DO", Sym, report);

            SaveHcfWithRetry(doc, hcfPath, report);
        }

        /// <summary>
        /// Load the existing .hcf, or mint a fresh skeleton with the
        /// xmlns:xsd / xmlns:xsi prefix declarations EAE expects on the root.
        /// Also patches the root if either prefix decl is missing on a loaded
        /// file so subsequent <c>xsi:type</c> attributes serialise cleanly.
        /// </summary>
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

        /// <summary>
        /// Find the immediate <c>ConfigurationBaseItem</c> child of
        /// <paramref name="parent"/> whose child <c>&lt;Name&gt;</c> equals
        /// <paramref name="blockName"/>. If present, replace it in place via
        /// <see cref="XElement.ReplaceWith(object)"/>; otherwise append the
        /// fresh element once. Logs <c>[Hcf] replaced existing X block</c> or
        /// <c>[Hcf] appended new X block</c> to <paramref name="report"/>.
        /// </summary>
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

        /// <summary>
        /// Replace-or-append the TM3 module block, then for each pin
        /// (DI00..DI15 or DO00..DO15) remove any existing same-Name
        /// <c>ParameterValue</c> from the block's <c>ParameterValues</c>
        /// container before adding the fresh entry. The "replaced" vs "new"
        /// classification in the log line is computed from a snapshot of the
        /// PRE-replace pin set so the user can see which pins were already
        /// configured on disk.
        /// </summary>
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

            // Locate the freshly-inserted block's <ParameterValues> container
            // (shellFactory always emits an empty one between ItemProperties
            // and PreviousItem so insertion order is canonical).
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

                // Per the Bug 2 spec: defensively remove any same-Name
                // ParameterValue from this container before adding the fresh
                // entry. The freshly-built shell is empty on the first run,
                // so this is a no-op then — but it guarantees idempotency
                // even if a future caller starts from a pre-populated shell.
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

        // ---- Block builders. Each returns a fresh detached XElement matching
        //      the canonical Alex-shaped .hcf template that EAE accepts. ----

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

        /// <summary>
        /// Save with EAE-friendly settings (UTF-8 no BOM, 2-space indent) and
        /// retry up to 8 times if EAE briefly holds a write lock.
        /// </summary>
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

        /// <summary>
        /// Read every <c>&lt;FB Name="..." ID="..."/&gt;</c> on the sysres
        /// FBNetwork into a Name → ID map so .hcf ParameterValues can resolve
        /// component instance IDs (Feeder, PartInHopper, …) by name.
        /// </summary>
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

        /// <summary>Reads &lt;FB Name="..." /&gt; values from a syslay file's
        /// &lt;SubAppNetwork&gt; root. Returns an empty set if the file can't
        /// be parsed.</summary>
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

        /// <summary>
        /// Reads the M262 sysres root's <c>ID</c> attribute (resource GUID) and
        /// the <c>M262IO</c> FB's <c>ID</c> attribute. Returns blanks if anything
        /// is missing — caller treats blanks as a skip signal.
        /// </summary>
        private static (string resourceId, string m262IoFbId) EnsureSysresAndM262IoIds(
            string eaeRoot, SystemInjector.BindingApplicationReport report)
        {
            try
            {
                var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
                if (!Directory.Exists(systemDir)) return (string.Empty, string.Empty);

                var sysresPath = Directory
                    .EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (sysresPath == null) return (string.Empty, string.Empty);

                var doc = XDocument.Load(sysresPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return (string.Empty, string.Empty);
                XNamespace ns = root.GetDefaultNamespace();

                bool dirty = false;

                // 1. Ensure resource has a non-zero short-hex ID. EAE accepts both
                //    long GUIDs and 16-char hex; baselines use the latter.
                var rawId = (string?)root.Attribute("ID") ?? string.Empty;
                if (IsZeroOrEmptyId(rawId))
                {
                    rawId = NewShortHexId("RES0|" + sysresPath);
                    root.SetAttributeValue("ID", rawId);
                    dirty = true;
                    report.Missing.Add($"[Hcf] sysres ID was zero — assigned {rawId}");
                }

                // 2. Ensure FBNetwork exists and contains an M262IO FB instance.
                //    Without it, .hcf ParameterValues cannot resolve and the EAE
                //    Symbolic Links view stays blank.
                var fbNetwork = root.Element(ns + "FBNetwork");
                if (fbNetwork == null)
                {
                    fbNetwork = new XElement(ns + "FBNetwork");
                    root.Add(fbNetwork);
                    dirty = true;
                }
                var m262Io = fbNetwork.Elements(ns + "FB")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "M262IO" &&
                                         (string?)e.Attribute("Type") == "PLC_RW_M262");
                string m262IoFbId;
                if (m262Io == null)
                {
                    m262IoFbId = NewShortHexId("M262IO|" + rawId);
                    var mappingId = NewShortHexId("M262IO_MAP|" + rawId);
                    m262Io = new XElement(ns + "FB",
                        new XAttribute("ID", m262IoFbId),
                        new XAttribute("Name", "M262IO"),
                        new XAttribute("Type", "PLC_RW_M262"),
                        new XAttribute("Namespace", "Main"),
                        new XAttribute("Mapping", mappingId),
                        new XAttribute("x", "3760"),
                        new XAttribute("y", "1020"));
                    fbNetwork.Add(m262Io);
                    dirty = true;
                    report.Missing.Add(
                        $"[Hcf] sysres had no M262IO FB — injected with ID {m262IoFbId}");
                }
                else
                {
                    m262IoFbId = (string?)m262Io.Attribute("ID") ?? string.Empty;
                    if (IsZeroOrEmptyId(m262IoFbId))
                    {
                        m262IoFbId = NewShortHexId("M262IO|" + rawId);
                        m262Io.SetAttributeValue("ID", m262IoFbId);
                        dirty = true;
                    }
                }

                if (dirty)
                {
                    var settings = new System.Xml.XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Indent = true,
                        Encoding = new System.Text.UTF8Encoding(false),
                    };
                    using var fs = new FileStream(sysresPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                }

                return (rawId, m262IoFbId);
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] EnsureSysres failed: {ex.GetType().Name}: {ex.Message}");
                return (string.Empty, string.Empty);
            }
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

        /// <summary>Rewrite the .sysres root <c>ID</c> attribute so it matches
        /// the resource GUID we just minted on the sysdev. EAE resolves
        /// symlinks by ID equality across sysdev → sysres → .hcf; if any of
        /// the three carries zeros while the others don't, the lookup fails
        /// and the Symbolic Links view goes red.</summary>
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

        /// <summary>
        /// Deterministic 16-char uppercase hex ID derived from a seed string,
        /// matching the format EAE writes into baseline .sysres / .hcf files
        /// (e.g. <c>54EB0B3D5D16444D</c>). Same input → same ID across runs,
        /// so the .hcf ResourceId stays stable between Button-2 invocations.
        /// </summary>
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
