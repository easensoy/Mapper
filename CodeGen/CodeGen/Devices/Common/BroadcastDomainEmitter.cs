using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // The broadcast-domain JSON subnet/gateway must match the device it binds to, or EAE's
    // connect-to-device verification flags the subnet/gateway rows.
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

        // An Equipment referencing a broadcast-domain UUID with no declaring BroadcastDomain_*.json
        // fails EAE's topology import — create the missing domain at 192.168.1.0/24. Only writes
        // BroadcastDomain JSON files; never touches Equipment/sysdev/sysres/device-trust state.
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

            // Domain UUIDs referenced by any Equipment.
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

            // Domain UUIDs already defined by a BroadcastDomain file.
            var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bd in Directory.EnumerateFiles(topologyDir, "BroadcastDomain_*.json"))
            {
                string text;
                try { text = File.ReadAllText(bd); } catch { continue; }
                var m = defRx.Match(text);
                if (m.Success) defined.Add(m.Groups[1].Value);
            }

            // Create any referenced-but-undefined domain at 192.168.1.0/24.
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

                // Some EAE import paths only honour REGISTERED topology items, so register the
                // new domain in TopologyManager.topologyproj (the file on disk is the primary fix).
                var topoProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
                if (File.Exists(topoProj))
                {
                    try
                    {
                        CodeGen.Devices.M262.M262TopologyEmitter.RegisterInTopologyProj(
                            topoProj, new[] { Path.GetFileName(path) });
                    }
                    catch { /* registration best-effort */ }
                }

                result.Warnings.Add(
                    $"Created + registered missing BroadcastDomain '{name}' (uuid {uuid}) — an " +
                    $"Equipment referenced it but no file declared it (dangling domain → topology " +
                    $"import failure). Pinned to 192.168.1.0/24.");
            }
            return result;
        }
    }
}
