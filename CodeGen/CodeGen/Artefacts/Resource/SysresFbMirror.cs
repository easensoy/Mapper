using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using System.IO;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class SysresFbMirror
    {
        const string LibElNs = Station2DeviceEmitter.LibElNs;



        public record SyslayFbParameter(string Name, string Value);
        public record SyslayFbAttribute(string Name, string Value);
        public record SyslayFb(string Id, string Name, string Type, string Namespace,
            string X, string Y, List<SyslayFbParameter> Parameters,
            List<SyslayFbAttribute> Attributes);

        public static List<SyslayFb> ReadSyslayTopLevelFbs(string syslayPath)
        {
            var doc = XDocument.Load(syslayPath);
            var root = doc.Root;
            if (root == null) return new List<SyslayFb>();
            XNamespace ns = root.GetDefaultNamespace();
            var net = root.Element(ns + "SubAppNetwork") ?? root.Element(ns + "FBNetwork");
            if (net == null) return new List<SyslayFb>();
            return Project(net.Elements(ns + "FB"));
        }

        // Children matched by local name, so this serves the namespaced .syslay and System.hash alike.
        static List<SyslayFb> Project(IEnumerable<XElement> fbs) =>
            fbs.Select(e => new SyslayFb(
                    Id:        (string?)e.Attribute("ID")        ?? string.Empty,
                    Name:      (string?)e.Attribute("Name")      ?? string.Empty,
                    Type:      (string?)e.Attribute("Type")      ?? string.Empty,
                    Namespace: (string?)e.Attribute("Namespace") ?? "Main",
                    X:         (string?)e.Attribute("x")         ?? "0",
                    Y:         (string?)e.Attribute("y")         ?? "0",
                    Parameters: Named(e, "Parameter")
                        .Select(p => new SyslayFbParameter(Attr(p, "Name"), Attr(p, "Value")))
                        .Where(p => !string.IsNullOrEmpty(p.Name))
                        .ToList(),
                    Attributes: Named(e, "Attribute")
                        .Select(a => new SyslayFbAttribute(Attr(a, "Name"), Attr(a, "Value")))
                        .Where(a => !string.IsNullOrEmpty(a.Name))
                        .ToList()))
                .Where(fb => !string.IsNullOrWhiteSpace(fb.Name))
                .ToList();

        static IEnumerable<XElement> Named(XElement parent, string localName) =>
            parent.Elements().Where(c => c.Name.LocalName == localName);

        static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? string.Empty;

        public static List<SyslayFb> ReadTopLevelFbsWithSystemModelFallback(string syslayPath)
        {
            if (!string.IsNullOrWhiteSpace(syslayPath) && File.Exists(syslayPath))
            {
                var direct = ReadSyslayTopLevelFbs(syslayPath);
                if (direct.Count > 0) return direct;
            }

            var systemHash = FindSystemHashBeside(syslayPath);
            return systemHash == null ? new List<SyslayFb>() : ReadSystemHashFbs(systemHash);
        }

        static string? FindSystemHashBeside(string syslayPath)
        {
            if (string.IsNullOrWhiteSpace(syslayPath)) return null;

            var dir = Path.GetDirectoryName(syslayPath);
            while (!string.IsNullOrWhiteSpace(dir))
            {
                var candidate = Path.Combine(dir, "obj", "System.hash");
                if (File.Exists(candidate)) return candidate;
                dir = Directory.GetParent(dir)?.FullName;
            }

            return null;
        }

        static List<SyslayFb> ReadSystemHashFbs(string systemHashPath)
        {
            var doc = XDocument.Load(systemHashPath);
            return Project(doc.Descendants().Where(e => e.Name.LocalName == "FB"));
        }

        public static int MirrorFbsIntoSysres(string sysresPath, List<SyslayFb> syslayFbs,
            IReadOnlyList<SystemFbSpec> systemFbs)
        {
            if (!File.Exists(sysresPath)) return 0;
            var doc = XDocument.Load(sysresPath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty sysres: {sysresPath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            var network = root.Element(ns + "FBNetwork");
            if (network == null)
            {
                network = new XElement(ns + "FBNetwork");
                root.Add(network);
            }

            foreach (var spec in systemFbs) EnsureSystemFb(network, ns, spec);

            // DEDUP the id-flip: a sysres FB id can flip between regens (mirror id = syslay id with its
            // top hex bit flipped), leaving a stale copy that declares the component TWICE. Name-scoped,
            // so FB1/FB2 are never touched.
            var currentSyslayIds = new HashSet<string>(
                syslayFbs.Where(f => !string.IsNullOrEmpty(f.Id)).Select(f => f.Id),
                StringComparer.Ordinal);
            var syslayNames = new HashSet<string>(
                syslayFbs.Select(f => f.Name).Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.Ordinal);
            int deduped = 0;
            foreach (var fb in network.Elements(ns + "FB").ToList())
            {
                var nm = (string?)fb.Attribute("Name") ?? string.Empty;
                var map = (string?)fb.Attribute("Mapping") ?? string.Empty;
                bool mirrored = !string.IsNullOrEmpty(map);   // mirrored FBs carry a Mapping; FB1/FB2 do not
                if (syslayNames.Contains(nm) && !currentSyslayIds.Contains(map))
                {
                    fb.Remove();
                    deduped++;
                }
                else if (mirrored && !syslayNames.Contains(nm))
                {
                    fb.Remove();
                    deduped++;
                }
            }

            var existingMappings = new HashSet<string>(
                network.Elements(ns + "FB")
                    .Select(e => (string?)e.Attribute("Mapping") ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);
            var existingNames = new HashSet<string>(
                network.Elements(ns + "FB")
                    .Select(e => (string?)e.Attribute("Name") ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);

            var keepTypes = TemplateManifest.Mirrored;

            // An already-mirrored FB is UPDATED, not skipped; its ID/Mapping/x/y stay put as its handle.
            var existingByName = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var fb in network.Elements(ns + "FB"))
            {
                var nm = (string?)fb.Attribute("Name") ?? string.Empty;
                if (!string.IsNullOrEmpty(nm)) existingByName[nm] = fb;
            }

            int added = 0, updated = 0;
            foreach (var fb in syslayFbs)
            {
                if (string.IsNullOrEmpty(fb.Id)) continue;
                if (!keepTypes.Contains(fb.Type)) continue;

                if (existingByName.TryGetValue(fb.Name, out var existing))
                {
                    // SYNC Type/Namespace to the syslay: a component's CAT type can change between
                    // regens, and a stale Type trips EAE's "Found References to Missing Instances".
                    existing.SetAttributeValue("Type",      fb.Type);
                    existing.SetAttributeValue("Namespace", fb.Namespace);
                    // Upsert <Attribute> children (don't blanket-remove — EAE may add its own).
                    foreach (var a in fb.Attributes)
                    {
                        var existingAttr = existing.Elements(ns + "Attribute")
                            .FirstOrDefault(x => (string?)x.Attribute("Name") == a.Name);
                        if (existingAttr != null) existingAttr.SetAttributeValue("Value", a.Value);
                        else existing.Add(new XElement(ns + "Attribute",
                            new XAttribute("Name", a.Name), new XAttribute("Value", a.Value)));
                    }
                    existing.Elements(ns + "Parameter").Remove();
                    foreach (var p in fb.Parameters)
                    {
                        existing.Add(new XElement(ns + "Parameter",
                            new XAttribute("Name",  p.Name),
                            new XAttribute("Value", p.Value)));
                    }
                    updated++;
                    continue;
                }

                if (existingMappings.Contains(fb.Id)) continue;

                var mirrorId = ComputeMirrorId(fb.Id);
                var fbElement = new XElement(ns + "FB",
                    new XAttribute("ID",        mirrorId),
                    new XAttribute("Name",      fb.Name),
                    new XAttribute("Type",      fb.Type),
                    new XAttribute("Namespace", fb.Namespace),
                    new XAttribute("Mapping",   fb.Id),
                    new XAttribute("x",         fb.X),
                    new XAttribute("y",         fb.Y));

                // A mirrored MQTT_PUBLISH must keep its channel-count Attribute, else EAE rejects it.
                foreach (var a in fb.Attributes)
                {
                    fbElement.Add(new XElement(ns + "Attribute",
                        new XAttribute("Name",  a.Name),
                        new XAttribute("Value", a.Value)));
                }

                foreach (var p in fb.Parameters)
                {
                    fbElement.Add(new XElement(ns + "Parameter",
                        new XAttribute("Name",  p.Name),
                        new XAttribute("Value", p.Value)));
                }

                network.Add(fbElement);
                added++;
            }

            if (added > 0 || updated > 0) doc.Save(sysresPath);
            return added + updated;
        }

        // Refresh a sysres FB's Parameters from the syslay, which is the authority: a resource keeping
        // its old parameters would deploy a stale recipe with no error. Matched by Name, then by the
        // Mapping attribute (I-9: an FB's Mapping is a separate GUID carrying the syslay id).
        public static int SyncProcessRecipesFromSyslay(string syslayPath, string sysresPath) =>
            SyncFromSyslay(syslayPath, sysresPath, IsProcessEngine);

        public static int SyncProcessRecipesFromSyslay(string syslayPath, XDocument sysresDoc) =>
            SyncFromSyslay(syslayPath, sysresDoc, IsProcessEngine);

        public static int SyncMirroredFbParametersFromSyslay(string syslayPath, string sysresPath) =>
            SyncFromSyslay(syslayPath, sysresPath, _ => true);

        public static int SyncMirroredFbParametersFromSyslay(string syslayPath, XDocument sysresDoc) =>
            SyncFromSyslay(syslayPath, sysresDoc, _ => true);

        private static bool IsProcessEngine(string type) =>
            string.Equals(type, "Process1_Generic", StringComparison.Ordinal);

        private static int SyncFromSyslay(string syslayPath, string sysresPath, Func<string, bool> selects)
        {
            if (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)) return 0;
            if (string.IsNullOrWhiteSpace(sysresPath) || !File.Exists(sysresPath)) return 0;

            var doc = XDocument.Load(sysresPath);
            var changed = SyncFromSyslay(syslayPath, doc, selects);
            if (changed > 0) doc.Save(sysresPath);
            return changed;
        }

        private static int SyncFromSyslay(string syslayPath, XDocument sysresDoc, Func<string, bool> selects)
        {
            if (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)) return 0;
            var root = sysresDoc.Root;
            if (root == null) return 0;

            var sourceByName = ReadTopLevelFbsWithSystemModelFallback(syslayPath)
                .Where(f => selects(f.Type) && f.Parameters.Count > 0)
                .ToDictionary(f => f.Name, StringComparer.Ordinal);
            var sourceById = sourceByName.Values
                .Where(f => !string.IsNullOrWhiteSpace(f.Id))
                .ToDictionary(f => f.Id, StringComparer.Ordinal);
            if (sourceByName.Count == 0) return 0;

            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            var network = root.Elements().FirstOrDefault(e => e.Name.LocalName == "FBNetwork");
            if (network == null) return 0;

            int changed = 0;
            foreach (var fb in network.Elements()
                         .Where(e => e.Name.LocalName == "FB")
                         .Where(f => selects((string?)f.Attribute("Type") ?? string.Empty)))
            {
                var name = (string?)fb.Attribute("Name") ?? string.Empty;
                var mapping = (string?)fb.Attribute("Mapping") ?? string.Empty;

                if (!sourceByName.TryGetValue(name, out var source) &&
                    !sourceById.TryGetValue(mapping, out source))
                    continue;

                var existing = fb.Elements()
                    .Where(e => e.Name.LocalName == "Parameter")
                    .Select(p => (
                        Name: (string?)p.Attribute("Name") ?? string.Empty,
                        Value: (string?)p.Attribute("Value") ?? string.Empty))
                    .ToArray();
                var expected = source.Parameters.Select(p => (p.Name, p.Value)).ToArray();
                if (existing.SequenceEqual(expected)) continue;

                fb.Elements().Where(e => e.Name.LocalName == "Parameter").Remove();
                foreach (var p in source.Parameters)
                    fb.Add(new XElement(ns + "Parameter",
                        new XAttribute("Name", p.Name), new XAttribute("Value", p.Value)));
                changed++;
            }
            return changed;
        }

        static void EnsureSystemFb(XElement network, XNamespace ns, SystemFbSpec spec)
        {
            foreach (var stale in network.Elements(ns + "FB")
                .Where(e => string.Equals((string?)e.Attribute("ID"), spec.Id, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                stale.Remove();
            }

            var fb = new XElement(ns + "FB",
                new XAttribute("ID",        spec.Id),
                new XAttribute("Name",      spec.Name),
                new XAttribute("Type",      spec.Type),
                new XAttribute("Namespace", spec.Namespace));
            fb.SetAttributeValue("x", spec.X);
            fb.SetAttributeValue("y", spec.Y);
            fb.SetAttributeValue("Loaded", "true");

            foreach (var (pn, pv) in spec.Parameters)
                fb.Add(new XElement(ns + "Parameter",
                    new XAttribute("Name",  pn),
                    new XAttribute("Value", pv)));

            network.Add(fb);
        }

        // Which PLC resource a syslay FB belongs on: the ControllerMap partition plus the MQTT cases below.
        public static PlcAssignment BucketFor(string fbName, ControllerAllocation allocation)
        {
            if (string.IsNullOrEmpty(fbName)) return PlcAssignment.Unknown;

            // One MQTT connection per resource, so the embedded MqttPub binds the LOCAL one. A partial
            // swap emits RevPi's explicitly, since it keeps M262 as the controller.
            if (string.Equals(fbName, "MqttConn_RevPi", StringComparison.Ordinal) ||
                string.Equals(fbName, "Telemetry_RevPi", StringComparison.Ordinal))
                return PlcAssignment.RevPi;

            // Standalone MQTT bridge publishers (MqttFmt_<comp>/MqttPub_<comp>) live on BX1.
            if (fbName.StartsWith("MqttPub_", StringComparison.Ordinal) ||
                fbName.StartsWith("MqttFmt_", StringComparison.Ordinal))
                return PlcAssignment.BX1;

            if (string.Equals(fbName, "M580_CoverRingGate", StringComparison.Ordinal))
                return PlcAssignment.M580;
            if (string.Equals(fbName, "BX1_CoverRingGate", StringComparison.Ordinal))
                return PlcAssignment.BX1;

            var p = allocation.Of(fbName);
            // Unknown falls back to whichever controller hosts the Feed station, so nothing is dropped.
            return p == PlcAssignment.Unknown
                ? PlcAssignment.M262
                : p;
        }

        static string ComputeMirrorId(string syslayId)
        {
            if (syslayId.Length >= 16)
            {
                var first = syslayId[0];
                int v = Convert.ToInt32(first.ToString(), 16);
                var flipped = (v ^ 0x8).ToString("X");
                return flipped + syslayId.Substring(1, 15);
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mirror:" + syslayId));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }
    }
}
