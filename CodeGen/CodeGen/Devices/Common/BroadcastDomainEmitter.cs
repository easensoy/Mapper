using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // A broadcast domain's subnet/gateway must match the device it binds to, or EAE's
    // connect-to-device verification flags those rows.
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

        // An Equipment referencing a domain UUID that no BroadcastDomain_*.json declares fails EAE's
        // topology import. Only writes BroadcastDomain JSON; never touches Equipment or device state.
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

            var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bd in Directory.EnumerateFiles(topologyDir, "BroadcastDomain_*.json"))
            {
                string text;
                try { text = File.ReadAllText(bd); } catch { continue; }
                var m = defRx.Match(text);
                if (m.Success) defined.Add(m.Groups[1].Value);
            }

            // Undefined domains are the ones the dPACs reference, so they take the M262 rig
            // subnet, not DefaultNetwork.
            var dev = DeviceConfig.Current.M262;
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
                  "ipV4Address": "{{dev.SubnetAddress}}",
                  "ipV4Mask": "{{dev.SubnetMask}}",
                  "ipV4Gateway": "{{dev.Gateway}}"
                }
                """;
                File.WriteAllText(path, json);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, path));

                // Some EAE import paths honour only REGISTERED topology items.
                var topoProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
                if (File.Exists(topoProj))
                {
                    try
                    {
                        EaeProjectLayout.RegisterInTopologyProj(
                            topoProj, new[] { Path.GetFileName(path) });
                    }
                    catch { /* registration best-effort */ }
                }

                result.Warnings.Add(
                    $"Created + registered missing BroadcastDomain '{name}' (uuid {uuid}) — an " +
                    $"Equipment referenced it but no file declared it (dangling domain → topology " +
                    $"import failure). Pinned to 192.168.1.0/24.");
            }

            // EAE's Archive packs only files REGISTERED in TopologyManager.topologyproj, so an unregistered
            // domain is dropped from the .sln and the unarchived solution fails topology import entirely.
            var projPath = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(projPath))
            {
                var allDomains = new List<string>();
                foreach (var bd in Directory.EnumerateFiles(topologyDir, "BroadcastDomain_*.json"))
                {
                    var fn = Path.GetFileName(bd);
                    if (!string.IsNullOrEmpty(fn)) allDomains.Add(fn);
                }
                if (allDomains.Count > 0)
                {
                    try
                    {
                        EaeProjectLayout.RegisterInTopologyProj(
                            projPath, allDomains.ToArray());
                    }
                    catch { /* registration best-effort; the files on disk remain the primary fix */ }
                }
            }
            return result;
        }
    }
}
