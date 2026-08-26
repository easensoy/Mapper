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

        // The .solutionData security payload. The JSON SHAPE below is EAE's own document grammar and
        // stays here; every VALUE - the trust chain, the account names, the password hashes, the roles
        // and their permissions - is declared in Config/security.yml. Nothing here is logged.
        // internal so the document can be pinned by test: it is written ONLY into a project that has
        // none, so no gate run reaches it, and an untested path that emits credentials is exactly the
        // one worth pinning.
        internal static string BuildSolutionDataJson(Configuration.CompilerConfiguration cfg, string solutionId)
        {
            // A quote INSIDE the JSON string this document embeds, so it is the six characters
            // \u0022 rather than a quote character.
            const string Q = "\\u0022";
            var sec = cfg.Security;

            string Json(params string[] parts) => "{" + string.Join(",", parts) + "}";
            string Field(string name, string value) => $"{Q}{name}{Q}:{Q}{value}{Q}";
            string Raw(string name, string value) => $"{Q}{name}{Q}:{value}";
            string List(IEnumerable<string> items) =>
                "[" + string.Join(",", items.Select(i => $"{Q}{i}{Q}")) + "]";

            string deviceCfg = Json(Field("solutionId", solutionId), Field("csConfHash", sec.CsConfHash));
            string anonDeviceCfg = Json(Field("solutionId", solutionId), Field("csConfHash", sec.AnonCsConfHash));

            // The two account documents differ in field order and in how an absent start date is
            // written - EAE emits them that way, so they are spelled separately rather than shared.
            string userInfo = Json(Field("version", "1"),
                Raw("users_list", "[" + Json(
                    Field("user_name", sec.Principal.UserName),
                    Field("password", sec.Principal.PasswordHash),
                    Field("state", sec.Principal.State),
                    Field("AccountStartDate", string.Empty),
                    Raw("assigned_role", List(new[] { sec.Principal.RoleName }))) + "]"));

            string roleInfo = Json(Field("version", "1"),
                Raw("roles_list", "[" + Json(
                    Field("name", sec.Principal.RoleName),
                    Raw("permission_name", List(sec.Principal.Permissions))) + "]"));

            string anonUserInfo = Json(
                Raw("users_list", "[" + Json(
                    Field("user_name", sec.Anonymous.UserName),
                    Field("password", sec.Anonymous.PasswordHash),
                    Field("state", sec.Anonymous.State),
                    Raw("AccountStartDate", "null"),
                    Raw("assigned_role", List(new[] { sec.Anonymous.RoleName }))) + "]"),
                Field("version", "1"));

            string anonRoleInfo = Json(
                Raw("roles_list", "[" + Json(
                    Field("name", sec.Anonymous.RoleName),
                    Raw("permission_name", List(sec.Anonymous.Permissions))) + "]"),
                Field("version", "1"));

            return TemplateDocument.Load(cfg, @"Topology\Solution.solutionData",
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["SolutionId"] = solutionId,
                    ["CsConfHash"] = sec.CsConfHash,
                    ["AnonCsConfHash"] = sec.AnonCsConfHash,
                    ["CertThumbprintChain"] = sec.ChainLiteral,
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
