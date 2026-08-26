using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Services;
using System.IO;
using System.Xml.Linq;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M262
{
    public static class M262TopologyEmitter
    {
        const string FallbackSolutionUuid      = "00000000-0000-0000-0000-000000000000";
        static Configuration.DeviceIdentity M262Id =>
            Configuration.DeviceConfig.Identity(CodeGen.Translation.PlcAssignment.Named("M262"));

        internal static string DefaultM262Uuid => M262Id.Equipment;
        static string DefaultRuntimeUuid => M262Id.Runtime;
        static string RuntimeDeoTypeId => M262Id.RuntimeType;

        // Zero UUID = EAE Topology "NOCONF": the IP endpoint is statically bound but not associated
        // with any BroadcastDomain, so the M262dPAC shows its IP in Physical Views with no network.
        const string NoConfDomainUuid          = "00000000-0000-0000-0000-000000000000";

        public static TopologyEmitResult Emit(Configuration.CompilerConfiguration cfg, string sysdevId)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new TopologyEmitResult();

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                result.Warnings.Add("Cannot derive EAE project root — topology not emitted.");
                return result;
            }

            string solutionId = EaeProjectLayout.ReadProjectGuid(eaeRoot)
                ?? FallbackSolutionUuid;
            if (solutionId == FallbackSolutionUuid)
                result.Warnings.Add(
                    "Could not read project Guid from General/ProjectInfo.xml; using zero UUID. " +
                    "EAE will reject the security domain unless ProjectInfo.xml is restored.");

            var topologyDir = Path.Combine(eaeRoot, "Topology");
            Directory.CreateDirectory(topologyDir);

            int scrubbed = 0;
            foreach (var stale in Directory.EnumerateFiles(topologyDir, "*.solutionData"))
            {
                var keepName = solutionId + ".solutionData";
                if (!string.Equals(Path.GetFileName(stale), keepName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(stale); scrubbed++; } catch { }
                }
            }
            if (scrubbed > 0)
                result.Warnings.Add($"Removed {scrubbed} stale .solutionData file(s) with foreign SolutionId.");

            var equipmentFile     = Path.Combine(topologyDir, "Equipment_M262dPAC_1.json");
            var solutionDataFile  = Path.Combine(topologyDir, $"{solutionId}.solutionData");

            // Equipment JSON (visual placement) carries no trust info, so it is safe and necessary to
            // rewrite every Test Runtime — else the M262 never appears after the Demonstrator wipe.
            File.WriteAllText(equipmentFile, BuildEquipmentJson(cfg, sysdevId, solutionId));
            result.FilesWritten.Add(Path.GetFileName(equipmentFile));

            // solutionData carries the CsConfHash + CertThumbprint trust binding; preserve an existing
            // one byte-for-byte — overwriting invalidates the trust on the next deploy.
            if (!File.Exists(solutionDataFile))
            {
                File.WriteAllText(solutionDataFile, BuildSolutionDataJson(cfg, solutionId));
                result.FilesWritten.Add(Path.GetFileName(solutionDataFile));
            }

            // NOCONF mode: no BroadcastDomain_*.json is written; remove any pre-existing one from disk
            // and de-register it from topologyproj.
            DeleteEmittedBroadcastDomain(topologyDir, cfg);

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded = EaeProjectLayout.RegisterInTopologyProj(topologyProj, new[]
                {
                    Path.GetFileName(equipmentFile),
                    Path.GetFileName(solutionDataFile),
                });

                UnregisterBroadcastDomainFromTopologyProj(topologyProj, cfg);
            }
            else
            {
                result.Warnings.Add(
                    "Topology\\TopologyManager.topologyproj missing — Equipment JSON " +
                    "written but not registered with TopologyManager build target.");
            }

            return result;
        }

        static void DeleteEmittedBroadcastDomain(string topologyDir, Configuration.CompilerConfiguration cfg)
        {
            try
            {
                var domainFile = Path.Combine(topologyDir,
                    $"BroadcastDomain_{cfg.Paths.M262LogicalNetworkName}.json");
                if (File.Exists(domainFile)) File.Delete(domainFile);
            }
            catch { /* file lock; harmless — EAE will see the topologyproj has no reference */ }
        }

        static void UnregisterBroadcastDomainFromTopologyProj(string topologyProjPath, Configuration.CompilerConfiguration cfg)
        {
            try
            {
                var doc = XDocument.Load(topologyProjPath);
                var ns = doc.Root!.GetDefaultNamespace();
                var staleName = $"BroadcastDomain_{cfg.Paths.M262LogicalNetworkName}.json";
                var nodesToRemove = doc.Descendants(ns + "None")
                    .Where(e =>
                    {
                        var inc = (string?)e.Attribute("Include") ?? string.Empty;
                        return inc.StartsWith("BroadcastDomain_", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
                if (nodesToRemove.Count == 0) return;
                foreach (var node in nodesToRemove) node.Remove();
                doc.Save(topologyProjPath);
            }
            catch { /* topologyproj malformed; not fatal */ }
        }

        static string BuildEquipmentJson(Configuration.CompilerConfiguration cfg, string sysdevId, string solutionId) =>
            TemplateDocument.Load(cfg, @"Topology\Equipment_M262dPAC.json",
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["EquipmentUuid"] = DefaultM262Uuid,
                    ["RuntimeUuid"] = DefaultRuntimeUuid,
                    ["RuntimeTypeId"] = RuntimeDeoTypeId,
                    ["NoConfDomain"] = NoConfDomainUuid,
                    ["SysdevId"] = sysdevId,
                    ["SolutionId"] = solutionId,
                    ["TargetIp"] = cfg.Paths.M262TargetIp,
                });

        static string BuildSolutionDataJson(Configuration.CompilerConfiguration cfg, string solutionId)
        {
            const string Q = "\\u0022";

            const string CsConfHash         = "f0916269882ea2879f122ff1d3066e32efbf54856420312a16cbebab4a6a3b83";
            const string AnonCsConfHash     = "a2b76b73c2ef2047823fd066d51eb2daf2cf813f9ec1e9c35255f4d325126cb9";
            const string CertThumbprintChain =
                "8449F2BD01B8FD9456C76774479DC419867161C5;" +
                "6772E25CF62EF2011DFC22AD268BC9BD8DC690EA;" +
                "E1136C66DBA76781956DE186296D4A45C5F2C2C4;" +
                "93D07395A2FC29498BBBE6BD54FF7BB7EDBCB90C;" +
                "A7F7DE0AF53A55B277C978EE08917BC31DDD3767;" +
                "F640A64FFBC94A70FA30359207FA2D1746078BF8;" +
                "04C57C9F793980D4B647D3E3BD39E0BF206292DF;" +
                "04C57C9F793980D4B647D3E3BD39E0BF206292DF;" +
                "494A5814A9A24A02B06F1AC8D3D3850F349308B8;" +
                "93D07395A2FC29498BBBE6BD54FF7BB7EDBCB90C;";
            const string AsgPwHash          = "$1$A1C337A6652A9ABCCE903AD7FD5F8F3559FC4544100BC4A17291866BB80258E9$DFD5A7DEA0BD092D78E99A4B2BDDB03A1D30F1192D6745A807AB8F4E4D5F0AD4";
            const string AnonPwHash         = "cb366a250499db16cfa075932fd153c2baf2dfdda46a14082b7ddf3eab1118d5";

            string deviceCfg = $"{{{Q}solutionId{Q}:{Q}{solutionId}{Q},{Q}csConfHash{Q}:{Q}{CsConfHash}{Q}}}";
            string anonDeviceCfg = $"{{{Q}solutionId{Q}:{Q}{solutionId}{Q},{Q}csConfHash{Q}:{Q}{AnonCsConfHash}{Q}}}";

            string userInfo = $"{{{Q}version{Q}:{Q}1{Q},{Q}users_list{Q}:[{{{Q}user_name{Q}:{Q}ASG!{Q},{Q}password{Q}:{Q}{AsgPwHash}{Q},{Q}state{Q}:{Q}Active{Q},{Q}AccountStartDate{Q}:{Q}{Q},{Q}assigned_role{Q}:[{Q}ASG!{Q}]}}]}}";
            string roleInfo = $"{{{Q}version{Q}:{Q}1{Q},{Q}roles_list{Q}:[{{{Q}name{Q}:{Q}ASG!{Q},{Q}permission_name{Q}:[{Q}Security Management{Q},{Q}File Transfer{Q},{Q}IP Configuration{Q},{Q}Firmware Management{Q},{Q}LaunchCanvas{Q},{Q}OpenFacePlate{Q},{Q}EditSymbol{Q},{Q}Level_15{Q}]}}]}}";
            string anonUserInfo = $"{{{Q}users_list{Q}:[{{{Q}user_name{Q}:{Q}Anonymous{Q},{Q}password{Q}:{Q}{AnonPwHash}{Q},{Q}state{Q}:{Q}Active{Q},{Q}AccountStartDate{Q}:null,{Q}assigned_role{Q}:[{Q}Anonymous{Q}]}}],{Q}version{Q}:{Q}1{Q}}}";
            string anonRoleInfo = $"{{{Q}roles_list{Q}:[{{{Q}name{Q}:{Q}Anonymous{Q},{Q}permission_name{Q}:[{Q}Security Management{Q},{Q}File Transfer{Q},{Q}IP Configuration{Q},{Q}Firmware Management{Q},{Q}LaunchCanvas{Q},{Q}OpenFacePlate{Q},{Q}EditSymbol{Q},{Q}Level_15{Q}]}}],{Q}version{Q}:{Q}1{Q}}}";

            return TemplateDocument.Load(cfg, @"Topology\Solution.solutionData",
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["SolutionId"] = solutionId,
                    ["CsConfHash"] = CsConfHash,
                    ["AnonCsConfHash"] = AnonCsConfHash,
                    ["CertThumbprintChain"] = CertThumbprintChain,
                    ["DeviceCfg"] = deviceCfg,
                    ["AnonDeviceCfg"] = anonDeviceCfg,
                    ["UserInfo"] = userInfo,
                    ["RoleInfo"] = roleInfo,
                    ["AnonUserInfo"] = anonUserInfo,
                    ["AnonRoleInfo"] = anonRoleInfo,
                });

        }

    }

    public class TopologyEmitResult
    {
        public List<string> FilesWritten { get; } = new();
        public List<string> Warnings { get; } = new();
        public int TopologyProjEntriesAdded { get; set; }
    }
}
