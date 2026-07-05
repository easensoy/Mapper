using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class SysresFbMirror
    {
        const string LibElNs = "https://www.se.com/LibraryElements";

        // SINGLE SOURCE OF TRUTH for the syslay->sysres projection: MirrorFbsIntoSysres and
        // SyslaySysresParityValidator both read this set so they can never drift.
        public static readonly IReadOnlySet<string> MirroredCatTypes =
            new HashSet<string>(StringComparer.Ordinal)
        {
            "Five_State_Actuator_CAT",
            "Five_State_Actuator_No_Sensors_CAT",
            "Seven_State_Actuator_CAT",
            "Seven_State_Actuator_Centre_Home_CAT",
            "Sensor_Bool_CAT",
            "PLC_RW_M262",
            "Area",
            "Area_CAT",
            "Station",
            "Station_CAT",
            "Process1_Generic",
            "CaSAdptrTerminator",
            "Robot_Task_CAT",
            "MQTT_CONNECTION",
            "Telemetry",
            "MqttStateFormatter",
            "MQTT_PUBLISH_115480E69E664F878",
        };

        public static List<string> ReadSyslayTopLevelFbNames(string syslayPath)
        {
            return ReadSyslayTopLevelFbs(syslayPath).Select(fb => fb.Name).ToList();
        }

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
            return net.Elements(ns + "FB")
                .Select(e => new SyslayFb(
                    Id:        (string?)e.Attribute("ID")        ?? string.Empty,
                    Name:      (string?)e.Attribute("Name")      ?? string.Empty,
                    Type:      (string?)e.Attribute("Type")      ?? string.Empty,
                    Namespace: (string?)e.Attribute("Namespace") ?? "Main",
                    X:         (string?)e.Attribute("x")         ?? "0",
                    Y:         (string?)e.Attribute("y")         ?? "0",
                    Parameters: e.Elements(ns + "Parameter")
                        .Select(p => new SyslayFbParameter(
                            (string?)p.Attribute("Name")  ?? string.Empty,
                            (string?)p.Attribute("Value") ?? string.Empty))
                        .Where(p => !string.IsNullOrEmpty(p.Name))
                        .ToList(),
                    Attributes: e.Elements(ns + "Attribute")
                        .Select(a => new SyslayFbAttribute(
                            (string?)a.Attribute("Name")  ?? string.Empty,
                            (string?)a.Attribute("Value") ?? string.Empty))
                        .Where(a => !string.IsNullOrEmpty(a.Name))
                        .ToList()))
                .Where(fb => !string.IsNullOrWhiteSpace(fb.Name))
                .ToList();
        }

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
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "FB")
                .Select(e => new SyslayFb(
                    Id:        (string?)e.Attribute("ID")        ?? string.Empty,
                    Name:      (string?)e.Attribute("Name")      ?? string.Empty,
                    Type:      (string?)e.Attribute("Type")      ?? string.Empty,
                    Namespace: (string?)e.Attribute("Namespace") ?? "Main",
                    X:         (string?)e.Attribute("x")         ?? "0",
                    Y:         (string?)e.Attribute("y")         ?? "0",
                    Parameters: e.Elements()
                        .Where(p => p.Name.LocalName == "Parameter")
                        .Select(p => new SyslayFbParameter(
                            (string?)p.Attribute("Name")  ?? string.Empty,
                            (string?)p.Attribute("Value") ?? string.Empty))
                        .Where(p => !string.IsNullOrEmpty(p.Name))
                        .ToList(),
                    Attributes: e.Elements()
                        .Where(a => a.Name.LocalName == "Attribute")
                        .Select(a => new SyslayFbAttribute(
                            (string?)a.Attribute("Name")  ?? string.Empty,
                            (string?)a.Attribute("Value") ?? string.Empty))
                        .Where(a => !string.IsNullOrEmpty(a.Name))
                        .ToList()))
                .Where(fb => !string.IsNullOrWhiteSpace(fb.Name))
                .ToList();
        }

        public const string M262IoFbId        = "E786D6371CF444F9";
        public const string DpacFullInitFbId  = "593A8F4FDEA0A668";
        public const string PlcStartFbId      = "3DB1FB0F578E5F1E";

        public static int MirrorFbsIntoSysres(string sysresPath, List<SyslayFb> syslayFbs) =>
            MirrorFbsIntoSysres(sysresPath, syslayFbs, DpacFullInitFbId, PlcStartFbId);

        // Boot-ID-parameterized so each PLC resource gets its OWN DPAC_FULLINIT + plcStart (EAE FB IDs
        // must be unique across resources).
        public static int MirrorFbsIntoSysres(string sysresPath, List<SyslayFb> syslayFbs,
            string dpacFullInitId, string plcStartId)
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

            // M262IO (PLC_RW_M262) is NOT emitted onto the sysres — the .hcf channels publish symlinks
            // direct to the consumer FBs, so an M262IO instance would be dead weight.
            EnsureSystemFb(network, ns,
                id: dpacFullInitId, name: "FB1", type: "DPAC_FULLINIT", nsAttr: "SE.DPAC",
                mapping: null, x: 1900, y: 140,
                loaded: true);
            EnsureSystemFb(network, ns,
                id: plcStartId, name: "FB2", type: "plcStart", nsAttr: "SE.AppBase",
                mapping: null, x: 820, y: 660,
                loaded: true,
                parameters: new[] { ("Prio", "10"), ("Delay", "T#1000ms") });

            // DEDUP the id-flip: a component's sysres FB id can flip between regens (mirror id = syslay
            // id with its top hex bit flipped), leaving a stale previous-id copy that declares the
            // component TWICE and turns all M262 I/O red. Name-scoped so FB1/FB2 are never touched.
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
                    // Same-named FB with a stale mapping (the id-flip dup).
                    fb.Remove();
                    deduped++;
                }
                else if (mirrored && !syslayNames.Contains(nm))
                {
                    // A previously-mirrored FB whose Name is no longer in the syslay.
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

            var keepTypes = MirroredCatTypes;

            // Name -> existing sysres FB, so an already-mirrored FB is UPDATED (params replaced), not
            // skipped with stale values. Its ID/Mapping/x/y stay unchanged (stable instance handle).
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
                    // Keep ID/Mapping/x/y but SYNC Type/Namespace to the syslay — a component's CAT
                    // type can change between regens (Bearing_PnP Five_State stub <-> Seven_State), and
                    // a stale Type trips EAE's "Found References to Missing Instances".
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

                // Carry <Attribute> children (a mirrored MQTT_PUBLISH keeps its channel-count config,
                // else EAE rejects the FB on import).
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

        public static int SyncProcessRecipesFromSyslay(string syslayPath, string sysresPath)
        {
            if (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)) return 0;
            if (string.IsNullOrWhiteSpace(sysresPath) || !File.Exists(sysresPath)) return 0;

            var doc = XDocument.Load(sysresPath);
            var changed = SyncProcessRecipesFromSyslay(syslayPath, doc);
            if (changed > 0) doc.Save(sysresPath);
            return changed;
        }

        public static int SyncProcessRecipesFromSyslay(string syslayPath, XDocument sysresDoc)
        {
            if (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)) return 0;
            var root = sysresDoc.Root;
            if (root == null) return 0;

            var sourceByName = ReadTopLevelFbsWithSystemModelFallback(syslayPath)
                .Where(f => string.Equals(f.Type, "Process1_Generic", StringComparison.Ordinal))
                .Select(f => new
                {
                    f.Id,
                    f.Name,
                    Parameters = f.Parameters.ToArray()
                })
                .Where(f => f.Parameters.Length > 0)
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
                         .Where(f => string.Equals((string?)f.Attribute("Type"),
                             "Process1_Generic", StringComparison.Ordinal)))
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

                var expected = source.Parameters
                    .Select(p => (p.Name, p.Value))
                    .ToArray();

                if (!existing.SequenceEqual(expected))
                {
                    fb.Elements()
                        .Where(e => e.Name.LocalName == "Parameter")
                        .Remove();
                    foreach (var p in source.Parameters)
                    {
                        fb.Add(new XElement(ns + "Parameter",
                            new XAttribute("Name", p.Name),
                            new XAttribute("Value", p.Value)));
                    }
                    changed++;
                }
            }

            return changed;
        }

        public static int SyncMirroredFbParametersFromSyslay(string syslayPath, string sysresPath)
        {
            if (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)) return 0;
            if (string.IsNullOrWhiteSpace(sysresPath) || !File.Exists(sysresPath)) return 0;

            var doc = XDocument.Load(sysresPath);
            var changed = SyncMirroredFbParametersFromSyslay(syslayPath, doc);
            if (changed > 0) doc.Save(sysresPath);
            return changed;
        }

        public static int SyncMirroredFbParametersFromSyslay(string syslayPath, XDocument sysresDoc)
        {
            if (string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)) return 0;
            var root = sysresDoc.Root;
            if (root == null) return 0;

            var sourceByName = ReadTopLevelFbsWithSystemModelFallback(syslayPath)
                .Where(f => f.Parameters.Count > 0)
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
            foreach (var fb in network.Elements().Where(e => e.Name.LocalName == "FB"))
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

                var expected = source.Parameters
                    .Select(p => (p.Name, p.Value))
                    .ToArray();

                if (existing.SequenceEqual(expected))
                    continue;

                fb.Elements()
                    .Where(e => e.Name.LocalName == "Parameter")
                    .Remove();
                foreach (var p in source.Parameters)
                {
                    fb.Add(new XElement(ns + "Parameter",
                        new XAttribute("Name", p.Name),
                        new XAttribute("Value", p.Value)));
                }
                changed++;
            }

            return changed;
        }

        static void EnsureSystemFb(XElement network, XNamespace ns,
            string id, string name, string type, string nsAttr,
            string? mapping, int x, int y, bool loaded,
            (string Name, string Value)[]? parameters = null)
        {
            foreach (var stale in network.Elements(ns + "FB")
                .Where(e => string.Equals((string?)e.Attribute("ID"), id, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                stale.Remove();
            }

            var fb = new XElement(ns + "FB",
                new XAttribute("ID",        id),
                new XAttribute("Name",      name),
                new XAttribute("Type",      type),
                new XAttribute("Namespace", nsAttr));
            if (!string.IsNullOrEmpty(mapping)) fb.SetAttributeValue("Mapping", mapping);
            fb.SetAttributeValue("x", x);
            fb.SetAttributeValue("y", y);
            if (loaded) fb.SetAttributeValue("Loaded", "true");

            if (parameters != null)
            {
                foreach (var (pn, pv) in parameters)
                {
                    fb.Add(new XElement(ns + "Parameter",
                        new XAttribute("Name",  pn),
                        new XAttribute("Value", pv)));
                }
            }

            network.Add(fb);
        }

        // Which PLC resource a syslay FB belongs on: the ControllerMap partition, plus the MQTT/legacy
        // special cases below. Unknown falls back to M262 so nothing is dropped.
        public static PlcAssignment BucketFor(string fbName)
        {
            if (string.IsNullOrEmpty(fbName)) return PlcAssignment.Unknown;

            // One MQTT connection per resource ("MqttConn"=BX1, "_M262"=M262, "_M580"=M580); each
            // routes to its own sysres so the embedded MqttPub binds the LOCAL connection. Telemetry_*
            // wraps MQTT_CONNECTION and routes to the SAME resource as the MqttConn* it replaces.
            if (string.Equals(fbName, "MqttConn", StringComparison.Ordinal) ||
                string.Equals(fbName, "Telemetry_BX1", StringComparison.Ordinal))
                return PlcAssignment.BX1;
            // The Feed controller's MQTT connection follows the Feed station onto M262 or RevPi.
            if (string.Equals(fbName, "MqttConn_M262", StringComparison.Ordinal) ||
                string.Equals(fbName, "Telemetry_M262", StringComparison.Ordinal))
                return MapperConfig.FeedStationController == FeedController.RevPi
                    ? PlcAssignment.RevPi : PlcAssignment.M262;
            if (string.Equals(fbName, "MqttConn_M580", StringComparison.Ordinal) ||
                string.Equals(fbName, "Telemetry_M580", StringComparison.Ordinal))
                return PlcAssignment.M580;

            // Standalone MQTT bridge publishers (MqttFmt_<comp>/MqttPub_<comp>) live on BX1.
            if (fbName.StartsWith("MqttPub_", StringComparison.Ordinal) ||
                fbName.StartsWith("MqttFmt_", StringComparison.Ordinal))
                return PlcAssignment.BX1;

            if (string.Equals(fbName, "M580_CoverRingGate", StringComparison.Ordinal))
                return PlcAssignment.M580;
            if (string.Equals(fbName, "BX1_CoverRingGate", StringComparison.Ordinal))
                return PlcAssignment.BX1;

            // Legacy structural-FB name variant not in ComponentRegistry.
            if (string.Equals(fbName, "Disassembly_Station", StringComparison.Ordinal))
                return PlcAssignment.M580;

            var p = ControllerMap.PlcOf(fbName);
            // Unknown falls back to whichever controller currently hosts the Feed station (M262 or RevPi),
            // so nothing is dropped and no FB lands on a non-emitted device.
            return p == PlcAssignment.Unknown
                ? (MapperConfig.FeedStationController == FeedController.RevPi ? PlcAssignment.RevPi : PlcAssignment.M262)
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
