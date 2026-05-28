using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    public static class SysresFbMirror
    {
        const string LibElNs = "https://www.se.com/LibraryElements";

        public static List<string> ReadSyslayTopLevelFbNames(string syslayPath)
        {
            return ReadSyslayTopLevelFbs(syslayPath).Select(fb => fb.Name).ToList();
        }

        public record SyslayFbParameter(string Name, string Value);
        public record SyslayFb(string Id, string Name, string Type, string Namespace,
            string X, string Y, List<SyslayFbParameter> Parameters);

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
                        .ToList()))
                .Where(fb => !string.IsNullOrWhiteSpace(fb.Name))
                .ToList();
        }

        public const string M262IoFbId        = "E786D6371CF444F9";
        public const string DpacFullInitFbId  = "593A8F4FDEA0A668";
        public const string PlcStartFbId      = "3DB1FB0F578E5F1E";

        public static int MirrorFbsIntoSysres(string sysresPath, List<SyslayFb> syslayFbs) =>
            MirrorFbsIntoSysres(sysresPath, syslayFbs, DpacFullInitFbId, PlcStartFbId);

        /// <summary>
        /// Boot-ID-parameterized mirror so each PLC resource (M262 / M580 / BX1)
        /// gets its OWN DPAC_FULLINIT + plcStart instance with a distinct ID
        /// (EAE FB IDs must be unique across resources).
        /// </summary>
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

            // M262IO (PLC_RW_M262) is NOT emitted onto the sysres. Under the
            // Option-A .hcf binding the TM3 channels publish symlinks directly
            // to the consumer FB instances (Feeder.athome, PartInHopper.Input,
            // …) — M262IO is no longer the routing bridge, so a top-level
            // M262IO instance plus its INIT / PusherEvent / REQ_INT_BOOL
            // event wires are dead weight that EAE flags. Removing the
            // EnsureSystemFb(M262IO …) call here is what actually makes
            // M262IO disappear from the generated .sysres; the cleanup pass
            // in PrepareDemonstratorForGeneration only clears stale copies.
            EnsureSystemFb(network, ns,
                id: dpacFullInitId, name: "FB1", type: "DPAC_FULLINIT", nsAttr: "SE.DPAC",
                mapping: null, x: 1900, y: 140,
                loaded: true);
            EnsureSystemFb(network, ns,
                id: plcStartId, name: "FB2", type: "plcStart", nsAttr: "SE.AppBase",
                mapping: null, x: 820, y: 660,
                loaded: true,
                parameters: new[] { ("Prio", "10"), ("Delay", "T#1000ms") });

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

            // Mirror every CAT/composite/HMI type that EAE expects to see
            // mapped to M262.M262_RES. Each mirrored FB carries a Mapping
            // attribute pointing back at the syslay FB, which is how EAE
            // shows it under Devices > M262 > M262_RES > Local.
            var keepTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "Five_State_Actuator_CAT",
                "Five_State_Actuator_No_Sensors_CAT",
                "Seven_State_Actuator_CAT",
                "Sensor_Bool_CAT",
                "PLC_RW_M262",
                "Area",
                "Area_CAT",
                "Station",
                "Station_CAT",
                "Process1_Generic",
                "CaSAdptrTerminator",
                "Robot_Task_CAT",
                // MQTT_CONNECTION — the single shared event-buffer FB injected
                // by SystemLayoutInjector when MqttPublishEnabled is true. Without
                // a mirror entry it stays only on the syslay (no Mapping="…"
                // pointer) and EAE never deploys it to a resource, so the
                // embedded MQTT_PUBLISH FBs inside every CAT have nothing to bind
                // their ConnectionID to and publishes silently drop. Routed to
                // M262 via BucketFor's default fallback (name guess → Unknown
                // → M262), matching where the embedded publishes physically run.
                "MQTT_CONNECTION",
            };

            // Build a Name → existing sysres FB element index so we can UPDATE
            // parameters on an FB that was mirrored on a previous run, instead
            // of skipping it and leaving stale parameter values behind. Added
            // 2026-05-26 after this exact bug: SystemLayoutInjector started
            // emitting four new MqttConn timing parameters
            //   KeepAlive / ConnectionTimeout
            //   ConnectionRetryCount / ConnectionRetryTime
            // and they reached the syslay, but the MqttConn FB instance was
            // already on the sysres from an earlier deploy, so the mirror's
            // existingNames/Mappings guard short-circuited and the four new
            // <Parameter> children never got written into the resource. The
            // M262 firmware then applied T#0s defaults for the missing TIME
            // ports, MQTT_CONNECTION aborted every connect before the
            // SYN-ACK could complete (ReturnCode=50), and mosquitto logged
            // zero connection attempts from 192.168.1.10. The fix: when the
            // FB is already present, REPLACE its <Parameter> children with
            // whatever the syslay now carries — keep the FB element's ID /
            // Mapping / x / y unchanged so EAE's stable-instance tracking
            // does not see it as a new FB on every regen.
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
                    // Replace parameter children only; keep ID / Mapping / x / y
                    // so EAE keeps its stable handle on the instance across regens.
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

        /// <summary>
        /// Decides which PLC resource a syslay FB belongs on. Component FBs use
        /// <see cref="HcfSymbolIndex.NameBasedPlcGuess"/>; the Station-2 structural
        /// FBs (which have no IO bindings) are hard-routed to M580. Anything the
        /// name guess can't place falls back to M262 so nothing is ever dropped.
        /// </summary>
        public static PlcAssignment BucketFor(string fbName)
        {
            switch (fbName)
            {
                // Area2 / Area2_HMI are the M580's OWN local Area driver (the
                // run-command source for Station2). They have no IO bindings, so
                // NameBasedPlcGuess can't place them — hard-route to M580 here,
                // exactly like Station2/Stn2_Term, so the mirror copies them onto
                // the M580 sysres instead of letting them fall back to the M262.
                case "Area2":
                case "Area2_HMI":
                case "Station2":
                case "Station2_HMI":
                case "Assembly_Station":
                case "Disassembly":
                case "Disassembly_Station":
                case "Stn2_Term":
                    return PlcAssignment.M580;
            }
            var p = HcfSymbolIndex.NameBasedPlcGuess(fbName);
            return p == PlcAssignment.Unknown ? PlcAssignment.M262 : p;
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
