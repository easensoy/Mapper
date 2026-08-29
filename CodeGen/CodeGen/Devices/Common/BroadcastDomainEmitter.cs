using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Services;

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

        // One document, rendered twice: the default network, and any domain an Equipment references
        // that nothing declares. They differ only in which subnet they carry, so a second copy of the
        // body could only drift from this one.
        private static string Domain(Configuration.CompilerConfiguration cfg, string uuid, string identifier,
            string subnetAddress, string subnetMask, string gateway) =>
            TemplateDocument.Load(cfg, @"Topology\BroadcastDomain.json",
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["Uuid"] = uuid,
                    ["Identifier"] = identifier,
                    ["SubnetAddress"] = subnetAddress,
                    ["SubnetMask"] = subnetMask,
                    ["Gateway"] = gateway,
                });

        public static EmitResult Emit(Configuration.CompilerConfiguration cfg)
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
            File.WriteAllText(path, Domain(cfg,
                cfg.Devices.DefaultNetwork.DomainUuid, "Default Network",
                cfg.Devices.DefaultNetwork.SubnetAddress, cfg.Devices.DefaultNetwork.SubnetMask, cfg.Devices.DefaultNetwork.Gateway));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, path));
            return result;
        }

        // An Equipment referencing a domain UUID that no BroadcastDomain_*.json declares fails EAE's
        // topology import. Only writes BroadcastDomain JSON; never touches Equipment or device state.
        public static EmitResult EnsureReferencedDomains(Configuration.CompilerConfiguration cfg)
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
                    if (!string.Equals(uuid, Artefacts.EaeAbi.NoBroadcastDomain, StringComparison.OrdinalIgnoreCase))
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

            // A domain the devices reference but nothing declares is a DEVICE network, so it takes a
            // declared target's subnet rather than DefaultNetwork - which is the engineering-PC side
            // and would put the controllers on the wrong network.
            //
            // WHICH target's is read from backendEmitOrder, i.e. the first controller this run drives.
            // It used to be `cfg.Devices.M262` unconditionally, so a deployment that declares no M262
            // took the addresses of a target it does not have.
            var firstDriven = cfg.Devices.BackendEmitOrder.FirstOrDefault();
            if (firstDriven == default)
                throw new InvalidOperationException(
                    "[Topology] a broadcast domain is referenced but undeclared, and device.yml names " +
                    "no target in backendEmitOrder to take a subnet from.");
            var dev = cfg.Devices.NetworkOf(firstDriven.ToString());
            int n = 1;
            foreach (var uuid in referenced)
            {
                if (defined.Contains(uuid)) continue;
                string name;
                do { name = $"DeviceNetwork_{n++}"; }
                while (File.Exists(Path.Combine(topologyDir, $"BroadcastDomain_{name}.json")));

                var path = Path.Combine(topologyDir, $"BroadcastDomain_{name}.json");
                File.WriteAllText(path, Domain(cfg,
                    uuid, name, dev.SubnetAddress, dev.SubnetMask, dev.Gateway));
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
                    $"import failure). On {dev.SubnetAddress}/{dev.SubnetMask}, the subnet the " +
                    "referencing device declares.");
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
