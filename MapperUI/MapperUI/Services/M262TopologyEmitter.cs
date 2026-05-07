using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    /// <summary>
    /// Materialises the EAE Physical Devices canvas for an M262_dPAC controller by
    /// writing the Topology folder JSONs that EAE's TopologyManager renders into the
    /// canvas tile and IP banner. Without these the sysdev exists in the Solution
    /// Explorer Devices tree but the Physical Devices canvas crashes (NRE) when
    /// trying to render the controller — and the operator can't see / set the IP
    /// without dragging a Workstation onto the canvas manually.
    ///
    /// Files written, all relative to the EAE project root:
    ///   Topology\Equipment_M262dPAC_1.json    M262 catalog ref + NIC + IP +
    ///                                          RuntimeDEO bound to the EcoRT_0 sysdev
    ///   Topology\BroadcastDomain_{LogicalNetworkName}.json
    ///                                          Subnet/mask/gateway. Equipment endpoint
    ///                                          references this via the `domain` UUID
    ///   Topology\TopologyManager.topologyproj  &lt;None Include&gt; entry per JSON
    ///
    /// Schema reverse-engineered from
    ///   /c/Station1 - Sensor and FiveStateActuator with symbolic links_*.sln/Topology/
    /// (also cross-checked against /c/SMC_Rig_Expo_*.sln/Topology/Equipment_M262dPAC_1.json).
    /// Stable UUIDs derived from a fixed seed so re-runs are idempotent and dfbproj
    /// entries don't churn.
    /// </summary>
    public static class M262TopologyEmitter
    {
        // Stable UUIDs — fixed so re-running the deployer overwrites the same
        // equipment instead of creating duplicates each time.
        // Three SEPARATE concepts, all UUIDs:
        //   DomainTag         — security domain (matches a .solutionData SolutionId).
        //                       EAE filters runtime binding candidates by checking
        //                       this resolves to a known security domain on disk.
        //   BroadcastDomain   — network (Topology/BroadcastDomain_*.json UUID).
        //                       Equipment endpoints reference it via `domain` field.
        //   M262Uuid/RuntimeUuid — equipment + runtime instance IDs (just identity).
        //
        // DefaultSolutionUuid is reused from SMC_Rig_Expo (ec877ac8-…) because the
        // user's machine already has a trust_ec877ac8 cert installed in the Windows
        // cert store from opening SMC_Rig_Expo. Using a fresh UUID would force EAE
        // to surface a "doesn't belong to active domain" error against our generated
        // .solutionData (no matching trust cert chain) — exactly the failure that
        // got topology emission disabled in the first place. Sharing the security
        // domain across SMC_Rig_Expo and Demonstrator is intentional: Demonstrator
        // is a Mapper-output sibling of SMC_Rig_Expo and is meant to authenticate
        // with the same ASG!/Asg2025! credentials.
        const string DefaultSolutionUuid       = "ec877ac8-b1b4-4f0b-be4b-3e8e8887784b";
        const string DefaultM262Uuid           = "11111111-2222-3333-4444-000000000010";
        const string DefaultDomainUuid         = "11111111-2222-3333-4444-000000000020";
        const string DefaultRuntimeUuid        = "11111111-2222-3333-4444-000000000030";
        // typeId for the RuntimeDEO component — copied verbatim from the Station1
        // reference's Equipment_M262dPAC_1.json. EAE keys the runtime renderer off
        // this id; changing it produces a "no runtime" marker on the canvas tile.
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

            var topologyDir = Path.Combine(eaeRoot, "Topology");
            Directory.CreateDirectory(topologyDir);

            var equipmentFile     = Path.Combine(topologyDir, "Equipment_M262dPAC_1.json");
            var domainFile        = Path.Combine(topologyDir,
                $"BroadcastDomain_{cfg.M262LogicalNetworkName}.json");
            // .solutionData lives at <SolutionId>.solutionData. Without it EAE
            // filters the M262 RuntimeDEO out of the Logical→Physical binding
            // dropdown because it can't resolve the Equipment's DomainTag to a
            // known security domain.
            var solutionDataFile  = Path.Combine(topologyDir, $"{DefaultSolutionUuid}.solutionData");

            File.WriteAllText(equipmentFile,    BuildEquipmentJson(cfg, sysdevId));
            File.WriteAllText(domainFile,       BuildBroadcastDomainJson(cfg));
            File.WriteAllText(solutionDataFile, BuildSolutionDataJson());
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

        // --- Equipment JSON ---

        // Hand-rolled string instead of System.Text.Json because EAE's TopologyManager
        // is sensitive to property order and JSON formatting (indentation, spacing).
        // Letting JsonSerializer reorder keys would diff against EAE's own writer
        // and cause noise the next time the user saves the project.
        static string BuildEquipmentJson(MapperConfig cfg, string sysdevId) => $$"""
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
      "propertyValue": "{{DefaultSolutionUuid}}"
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

        // Schema mirrored byte-by-byte from SMC_Rig_Expo's working solutionData
        //   /c/SMC_Rig_Expo_*.sln/Topology/ec877ac8-b1b4-4f0b-be4b-3e8e8887784b.solutionData
        // — including:
        //   * PascalCase keys (SolutionId, CsConfHash, CertThumbprint) — EAE's
        //     deserialiser is case-sensitive against the SMC_Rig_Expo schema, and
        //     this is the schema the user's installed EAE is known to accept.
        //   * The full SMC_Rig_Expo CertThumbprint chain (10 thumbprints, ; -separated)
        //     — these reference certs already installed in the user's Windows cert
        //     store. An empty CertThumbprint causes EAE to reject the security
        //     domain and surface "doesn't belong to active domain" errors against
        //     any Topology Equipment carrying our DomainTag.
        //   * The exact ASG! user/role with bcrypt hash copied from SMC_Rig_Expo
        //     — same credentials the user already uses to authenticate against
        //     the M262 (ASG!/Asg2025!).
        //   * anonymous user/role copied verbatim — mandatory for unauthenticated
        //     access at project open time (otherwise EAE prompts for login).
        // EAE encodes embedded JSON strings using " (unicode-escaped quotes);
        // raw " inside a JSON string value would be invalid JSON.
        static string BuildSolutionDataJson()
        {
            const string Q = "\\u0022"; // escaped double quote inside a JSON string

            // Hashes + thumbprint chain copied verbatim from SMC_Rig_Expo's
            // ec877ac8-…solutionData. EAE recomputes CsConfHash on first save once
            // it owns the project, but the value must be a syntactically-valid
            // 64-char hex string at write time.
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
            // bcrypt-format ASG! password hash copied verbatim from SMC_Rig_Expo
            // (== ASG!/Asg2025! that the M262 currently authenticates against).
            const string AsgPwHash          = "$1$A1C337A6652A9ABCCE903AD7FD5F8F3559FC4544100BC4A17291866BB80258E9$DFD5A7DEA0BD092D78E99A4B2BDDB03A1D30F1192D6745A807AB8F4E4D5F0AD4";
            const string AnonPwHash         = "cb366a250499db16cfa075932fd153c2baf2dfdda46a14082b7ddf3eab1118d5";

            string deviceCfg = $"{{{Q}solutionId{Q}:{Q}{DefaultSolutionUuid}{Q},{Q}csConfHash{Q}:{Q}{CsConfHash}{Q}}}";
            string anonDeviceCfg = $"{{{Q}solutionId{Q}:{Q}{DefaultSolutionUuid}{Q},{Q}csConfHash{Q}:{Q}{AnonCsConfHash}{Q}}}";

            string userInfo = $"{{{Q}version{Q}:{Q}1{Q},{Q}users_list{Q}:[{{{Q}user_name{Q}:{Q}ASG!{Q},{Q}password{Q}:{Q}{AsgPwHash}{Q},{Q}state{Q}:{Q}Active{Q},{Q}AccountStartDate{Q}:{Q}{Q},{Q}assigned_role{Q}:[{Q}ASG!{Q}]}}]}}";
            string roleInfo = $"{{{Q}version{Q}:{Q}1{Q},{Q}roles_list{Q}:[{{{Q}name{Q}:{Q}ASG!{Q},{Q}permission_name{Q}:[{Q}Security Management{Q},{Q}File Transfer{Q},{Q}IP Configuration{Q},{Q}Firmware Management{Q},{Q}LaunchCanvas{Q},{Q}OpenFacePlate{Q},{Q}EditSymbol{Q},{Q}Level_15{Q}]}}]}}";
            string anonUserInfo = $"{{{Q}users_list{Q}:[{{{Q}user_name{Q}:{Q}Anonymous{Q},{Q}password{Q}:{Q}{AnonPwHash}{Q},{Q}state{Q}:{Q}Active{Q},{Q}AccountStartDate{Q}:null,{Q}assigned_role{Q}:[{Q}Anonymous{Q}]}}],{Q}version{Q}:{Q}1{Q}}}";
            string anonRoleInfo = $"{{{Q}roles_list{Q}:[{{{Q}name{Q}:{Q}Anonymous{Q},{Q}permission_name{Q}:[{Q}Security Management{Q},{Q}File Transfer{Q},{Q}IP Configuration{Q},{Q}Firmware Management{Q},{Q}LaunchCanvas{Q},{Q}OpenFacePlate{Q},{Q}EditSymbol{Q},{Q}Level_15{Q}]}}],{Q}version{Q}:{Q}1{Q}}}";

            return $$"""
{
  "SolutionId": "{{DefaultSolutionUuid}}",
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

        // --- TopologyManager.topologyproj registration ---

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

        /// <summary>
        /// Removes any topology files this emitter previously wrote (and de-registers
        /// them from TopologyManager.topologyproj). Used when M262 topology emission
        /// is disabled — EAE rejects the auto-emitted M262 with "doesn't belong to
        /// the active domain", so leftover files from prior runs need to be scrubbed.
        /// Returns the number of disk + topologyproj entries removed. Idempotent.
        /// </summary>
        public static int RemoveEmittedTopology(MapperConfig cfg)
        {
            int removed = 0;
            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
            if (eaeRoot == null) return 0;
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir)) return 0;

            var files = new[]
            {
                Path.Combine(topologyDir, "Equipment_M262dPAC_1.json"),
                Path.Combine(topologyDir, $"BroadcastDomain_{cfg.M262LogicalNetworkName}.json"),
                Path.Combine(topologyDir, $"{DefaultSolutionUuid}.solutionData"),
            };
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
