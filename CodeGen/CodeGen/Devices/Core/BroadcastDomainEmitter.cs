using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Writes <c>Topology/BroadcastDomain_Default Network.json</c> so its
    /// subnet + gateway match the live rig network. EAE 24.1 ships a template
    /// default of 192.168.0.0/24 gateway 192.168.0.254; the SMC rig is on
    /// 192.168.1.0/24 with no gateway. When the M580 endpoint binds to this
    /// broadcast domain (see <c>cfg.M580BroadcastDomainUuid</c>) and the domain
    /// subnet disagrees with the device, EAE's connect-to-device verification
    /// dialog flags the gateway / subnet rows in red.
    ///
    /// <para>Idempotent — overwrites the file unconditionally so a manual
    /// EAE edit / a partial reload cannot leave a stale subnet behind. The
    /// uuid is pinned to <c>cfg.DefaultNetworkUuid</c> so the cross-reference
    /// from the M580 endpoint stays intact across re-emits.</para>
    /// </summary>
    public static class BroadcastDomainEmitter
    {
        public sealed class EmitResult
        {
            public System.Collections.Generic.List<string> FilesWritten { get; } = new();
            public System.Collections.Generic.List<string> Warnings { get; } = new();
        }

        public static EmitResult Emit(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("EAE project root not derivable — BroadcastDomain not emitted.");
                return result;
            }
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir))
            {
                result.Warnings.Add($"Topology folder missing at {topologyDir}.");
                return result;
            }

            var path = Path.Combine(topologyDir, "BroadcastDomain_Default Network.json");
            var json = $$"""
            {
              "uuid": "{{cfg.DefaultNetworkUuid}}",
              "identifier": "Default Network",
              "ipV4Address": "{{cfg.DefaultNetworkSubnetAddress}}",
              "ipV4Mask": "{{cfg.DefaultNetworkSubnetMask}}",
              "ipV4Gateway": "{{cfg.DefaultNetworkGateway}}"
            }
            """;
            File.WriteAllText(path, json);
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, path));
            return result;
        }

        /// <summary>
        /// Topology self-consistency guard. Scans every <c>Equipment_*.json</c>
        /// in the Topology folder for <c>"domain": "&lt;uuid&gt;"</c> bindings
        /// and, for any referenced UUID that has NO matching
        /// <c>BroadcastDomain_*.json</c> on disk, creates one at
        /// <c>192.168.1.0/24</c>. This fixes EAE's "Unable to import topology /
        /// Internal Server Error" caused by a DANGLING broadcast-domain
        /// reference — e.g. a DeviceNetwork_1 created in EAE's Logical Networks
        /// Editor that got a UUID but was never persisted as a file, leaving an
        /// Equipment pointing at a domain that doesn't exist. The null-sentinel
        /// domain (<c>00000000-…0000</c> = NOCONF) is ignored. Only writes
        /// BroadcastDomain JSON files — never touches Equipment / sysdev /
        /// sysres / device-trust state, so it is safe on any path.
        /// </summary>
        public static EmitResult EnsureReferencedDomains(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("EAE project root not derivable — domain consistency check skipped.");
                return result;
            }
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir)) return result;

            const string NullDomain = "00000000-0000-0000-0000-000000000000";
            var uuidRx = new Regex("\"domain\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
            var defRx  = new Regex("\"uuid\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");

            // 1. Domain UUIDs referenced by any Equipment.
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var eq in Directory.EnumerateFiles(topologyDir, "Equipment_*.json"))
            {
                string text;
                try { text = File.ReadAllText(eq); } catch { continue; }
                foreach (Match m in uuidRx.Matches(text))
                {
                    var uuid = m.Groups[1].Value;
                    if (!string.Equals(uuid, NullDomain, StringComparison.OrdinalIgnoreCase))
                        referenced.Add(uuid);
                }
            }

            // 2. Domain UUIDs already defined by a BroadcastDomain file.
            var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bd in Directory.EnumerateFiles(topologyDir, "BroadcastDomain_*.json"))
            {
                string text;
                try { text = File.ReadAllText(bd); } catch { continue; }
                var m = defRx.Match(text);
                if (m.Success) defined.Add(m.Groups[1].Value);
            }

            // 3. Create any referenced-but-undefined domain at 192.168.1.0/24.
            int n = 1;
            foreach (var uuid in referenced)
            {
                if (defined.Contains(uuid)) continue;
                string name;
                do { name = $"DeviceNetwork_{n++}"; }
                while (File.Exists(Path.Combine(topologyDir, $"BroadcastDomain_{name}.json")));

                var path = Path.Combine(topologyDir, $"BroadcastDomain_{name}.json");
                var json = $$"""
                {
                  "uuid": "{{uuid}}",
                  "identifier": "{{name}}",
                  "ipV4Address": "192.168.1.0",
                  "ipV4Mask": "255.255.255.0",
                  "ipV4Gateway": "192.168.1.254"
                }
                """;
                File.WriteAllText(path, json);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, path));
                result.Warnings.Add(
                    $"Created missing BroadcastDomain '{name}' (uuid {uuid}) — an Equipment " +
                    $"referenced it but no file existed (dangling domain → topology import failure). " +
                    $"Pinned to 192.168.1.0/24.");
            }
            return result;
        }
    }
}
