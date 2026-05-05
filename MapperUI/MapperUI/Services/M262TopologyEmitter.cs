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

            var equipmentFile = Path.Combine(topologyDir, "Equipment_M262dPAC_1.json");
            var domainFile    = Path.Combine(topologyDir,
                $"BroadcastDomain_{cfg.M262LogicalNetworkName}.json");

            File.WriteAllText(equipmentFile, BuildEquipmentJson(cfg, sysdevId));
            File.WriteAllText(domainFile,    BuildBroadcastDomainJson(cfg));
            result.FilesWritten.Add(Path.GetFileName(equipmentFile));
            result.FilesWritten.Add(Path.GetFileName(domainFile));

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded = RegisterInTopologyProj(topologyProj, new[]
                {
                    Path.GetFileName(equipmentFile),
                    Path.GetFileName(domainFile),
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
      "propertyValue": "{{DefaultDomainUuid}}"
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
    }

    public class TopologyEmitResult
    {
        public List<string> FilesWritten { get; } = new();
        public List<string> Warnings { get; } = new();
        public int TopologyProjEntriesAdded { get; set; }
    }
}
