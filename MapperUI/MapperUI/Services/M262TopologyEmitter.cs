using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    public static class M262TopologyEmitter
    {
        const string FallbackSolutionUuid      = "00000000-0000-0000-0000-000000000000";
        const string DefaultM262Uuid           = "11111111-2222-3333-4444-000000000010";
        const string DefaultDomainUuid         = "11111111-2222-3333-4444-000000000020";
        const string DefaultRuntimeUuid        = "11111111-2222-3333-4444-000000000030";
        const string RuntimeDeoTypeId          = "b0723d05-50bb-4c15-94a4-d8b5981bcb56";

        public static TopologyEmitResult Emit(MapperConfig cfg, string sysdevId)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new TopologyEmitResult();

            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null)
            {
                result.Warnings.Add("Cannot derive EAE project root — topology not emitted.");
                return result;
            }

            string solutionId = ReadProjectGuid(eaeRoot)
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
            var domainFile        = Path.Combine(topologyDir,
                $"BroadcastDomain_{cfg.M262LogicalNetworkName}.json");
            var solutionDataFile  = Path.Combine(topologyDir, $"{solutionId}.solutionData");

            File.WriteAllText(equipmentFile,    BuildEquipmentJson(cfg, sysdevId, solutionId));
            File.WriteAllText(domainFile,       BuildBroadcastDomainJson(cfg));
            File.WriteAllText(solutionDataFile, BuildSolutionDataJson(solutionId));
            result.FilesWritten.Add(Path.GetFileName(equipmentFile));
            result.FilesWritten.Add(Path.GetFileName(domainFile));
            result.FilesWritten.Add(Path.GetFileName(solutionDataFile));

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded = RegisterInTopologyProj(topologyProj, new[]
                {
                    Path.GetFileName(equipmentFile),
                    Path.GetFileName(domainFile),
                    Path.GetFileName(solutionDataFile),
                });
            }
            else
            {
                result.Warnings.Add(
                    "Topology\\TopologyManager.topologyproj missing — Equipment JSONs " +
                    "written but not registered with TopologyManager build target.");
            }

            return result;
        }

        static string? ReadProjectGuid(string eaeRoot)
        {
            var path = Path.Combine(eaeRoot, "General", "ProjectInfo.xml");
            if (!File.Exists(path)) return null;
            try
            {
                var doc = XDocument.Load(path);
                var raw = (string?)doc.Root?.Attribute("Guid");
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return raw.Trim().Trim('{', '}').ToLowerInvariant();
            }
            catch { return null; }
        }

        static string BuildEquipmentJson(MapperConfig cfg, string sysdevId, string solutionId) => $$"""
{
  "catalogReference": "M060_V01.00_01.00",
  "uuid": "{{DefaultM262Uuid}}",
  "identifier": "M262dPAC_1",
  "path": "Topology",
  "partNumber": "TM262L01MDESE8T",
  "properties": [
    {
      "propertyName": "IsUnderConstruction",
      "propertyValue": "False"
    },
    {
      "propertyName": "DomainTag",
      "propertyValue": "{{solutionId}}"
    }
  ],
  "references": [
    {
      "diagramPath": "Physical Views",
      "x": -195,
      "y": -193
    }
  ],
  "components": [
    {
      "interfaces": [
        {
          "identifier": "MDESE_ETH1",
          "disabled": false,
          "physicalAddress": "",
          "endpoints": [
            {
              "identifier": "IP Address",
              "isReadOnly": false,
              "domainReadOnly": false,
              "ipAddress": "{{cfg.M262TargetIp}}",
              "domain": "{{DefaultDomainUuid}}"
            }
          ]
        },
        {
          "identifier": "MDESE_ETH2",
          "disabled": false,
          "physicalAddress": "",
          "endpoints": [
            {
              "identifier": "IP Address",
              "isReadOnly": false,
              "domainReadOnly": false,
              "ipAddress": "0.0.0.0",
              "domain": "00000000-0000-0000-0000-000000000000"
            }
          ]
        }
      ],
      "ports": [
        { "identifier": "Ethernet1",   "side": "Default" },
        { "identifier": "Ethernet2_1", "side": "Default" },
        { "identifier": "Ethernet2_2", "side": "Default" }
      ],
      "componentType": "EthernetDEO"
    },
    {
      "bridgePriority": 0,
      "serviceEnabled": false,
      "componentType": "RstpDEO"
    },
    {
      "endpoint": "MDESE_ETH1\\IP Address",
      "connectionTypes": "None",
      "componentType": "EthernetMasterDEO"
    },
    {
      "enabled": false,
      "securityMode": 0,
      "componentType": "SysLogClientDEO"
    },
    {
      "mode": 0,
      "componentType": "CyberSecurityDEO"
    },
    {
      "uuid": "{{DefaultRuntimeUuid}}",
      "typeId": "{{RuntimeDeoTypeId}}",
      "logicalDeviceId": "{{sysdevId}}",
      "runtimeServices": [
        {
          "identifier": "Deployment"
        },
        {
          "identifier": "Archive Service",
          "logicalPortSecured": "0"
        }
      ],
      "componentType": "RuntimeDEO"
    }
  ]
}
""";

        static string BuildBroadcastDomainJson(MapperConfig cfg) => $$"""
{
  "uuid": "{{DefaultDomainUuid}}",
  "identifier": "{{cfg.M262LogicalNetworkName}}",
  "ipV4Address": "{{cfg.M262SubnetAddress}}",
  "ipV4Mask": "{{cfg.M262SubnetMask}}",
  "ipV4Gateway": "{{cfg.M262Gateway}}"
}
""";

        static string BuildSolutionDataJson(string solutionId)
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

            return $$"""
{
  "SolutionId": "{{solutionId}}",
  "CsConfHash": "{{CsConfHash}}",
  "AnonymousCsConfHash": "{{AnonCsConfHash}}",
  "CertThumbprint": "{{CertThumbprintChain}}",
  "deviceConfiguration": "{{deviceCfg}}",
  "userInformation": "{{userInfo}}",
  "roleInformation": "{{roleInfo}}",
  "anonymousDeviceConfiguration": "{{anonDeviceCfg}}",
  "anonymousUserInformation": "{{anonUserInfo}}",
  "anonymousRoleInformation": "{{anonRoleInfo}}",
  "solutionName": "Demonstrator"
}
""";
        }

        public static int RegisterInTopologyProj(string topologyProjPath, IEnumerable<string> jsonFileNames)
        {
            var doc = XDocument.Load(topologyProjPath);
            var ns = doc.Root!.GetDefaultNamespace();
            var noneGroup = doc.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any())
                ?? new XElement(ns + "ItemGroup");
            if (noneGroup.Parent == null) doc.Root!.Add(noneGroup);

            int added = 0;
            foreach (var name in jsonFileNames)
            {
                bool exists = noneGroup.Elements(ns + "None").Any(e =>
                    string.Equals((string?)e.Attribute("Include"), name, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;
                noneGroup.Add(new XElement(ns + "None", new XAttribute("Include", name)));
                added++;
            }
            if (added > 0) doc.Save(topologyProjPath);
            return added;
        }

        public static int RemoveEmittedTopology(MapperConfig cfg)
        {
            int removed = 0;
            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null) return 0;
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir)) return 0;

            var fileList = new List<string>
            {
                Path.Combine(topologyDir, "Equipment_M262dPAC_1.json"),
                Path.Combine(topologyDir, $"BroadcastDomain_{cfg.M262LogicalNetworkName}.json"),
            };
            fileList.AddRange(Directory.EnumerateFiles(topologyDir, "*.solutionData"));
            var files = fileList.ToArray();
            foreach (var f in files)
            {
                if (File.Exists(f)) { try { File.Delete(f); removed++; } catch { } }
            }

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                try
                {
                    var doc = XDocument.Load(topologyProj);
                    var ns = doc.Root!.GetDefaultNamespace();
                    var stale = new HashSet<string>(files.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
                    var nodesToRemove = doc.Descendants(ns + "None")
                        .Where(e => stale.Contains((string?)e.Attribute("Include") ?? ""))
                        .ToList();
                    foreach (var node in nodesToRemove) { node.Remove(); removed++; }
                    if (nodesToRemove.Count > 0) doc.Save(topologyProj);
                }
                catch { }
            }
            return removed;
        }
    }

    public class TopologyEmitResult
    {
        public List<string> FilesWritten { get; } = new();
        public List<string> Warnings { get; } = new();
        public int TopologyProjEntriesAdded { get; set; }
    }
}
