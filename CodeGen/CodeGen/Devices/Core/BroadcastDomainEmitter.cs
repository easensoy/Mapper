using System;
using System.IO;
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
    }
}
