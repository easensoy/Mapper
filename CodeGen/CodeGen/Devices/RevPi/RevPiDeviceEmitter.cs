using System;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Translation;

namespace CodeGen.Devices.RevPi
{
    // Emits the Revolution Pi Feed-station controller: a Soft_dPAC device (identical device family to
    // BX1) that HOSTS the current Feed-station FB network (Process1_Generic Feed_Station + Feeder/Checker/
    // Transfer/Ejector/sensors + the M262 station scaffold) instead of the Modicon M262. The FB network,
    // the recipe and the interlocks are UNCHANGED — only the owning device/topology/deployment differ.
    //
    // Reuse over duplication: the Soft_dPAC shell (sysdev/sysres/Properties/Simulation.Binding/topology/
    // dfbproj) comes from Station2DeviceEmitter.EmitOnePlc (the BX1 path), the FB mirror from
    // SysresFbMirror, and the Feed ring from M262SysresWireEmitter.EmitFeedRing. Only the RevPi identity
    // (ids/name/IP) and the Revolution Pi equipment JSON are RevPi-specific and live here.
    //
    // Active only when MapperConfig.FeedStationController == RevPi; ComponentRegistry has by then
    // relocated the Feed components onto PlcAssignment.RevPi, so SysresFbMirror.BucketFor routes them here.
    //
    // Physical Feed IO: the reference's Modbus word broker (PLC_RW_REVPI + a Modbus master .hcf). The
    // reference wires that broker to Jyotsna's direct-wire Process1_CAT actuator PINS (which this Mapper
    // must NOT adopt — INVARIANTS I-1 / REVERTED_FIXES R-4). Our Five_State CAT binds IO via symlinks, so
    // RevPiIoBrokerInjector bridges the broker to the Feed actuators' symlinks (the proven BX1 pattern).
    public static class RevPiDeviceEmitter
    {
        // Sysdev GUID follows the M262/M580/BX1 (…002/003/004) series; resource id is the reference
        // Revolution_Pi resource id (a valid 16-hex, distinct from the other three resources).
        const string RevPiSysdevId    = "00000000-0000-0000-0000-000000000005";
        const string RevPiResourceId  = "D090B4163A62A815";
        const string RevPiResourceName = "RevPi_RES";
        const string DeviceName        = "Revolution_Pi";

        // Fresh RevPi topology UUIDs (…005x series) — must not collide with M262/M580/BX1 equipment.
        const string RevPiEquipmentUuid = "11111111-2222-3333-4444-000000000050";
        const string RevPiNicUuid       = "11111111-2222-3333-4444-000000000051";
        const string RevPiContainerUuid = "11111111-2222-3333-4444-000000000052";
        const string RevPiRuntimeUuid   = "11111111-2222-3333-4444-000000000053";

        // Shared with BX1/reference; a soft-dPAC vlan domain the BroadcastDomainEmitter already declares.
        const string SoftDpacTypeId    = "29797a55-a6b8-47c4-9c06-e8a42b1a38b5";
        const string SoftdpacDomainUuid = "db72f221-ece1-4b82-8132-731ce655044e";
        const string NoConfDomainUuid   = "00000000-0000-0000-0000-000000000000";

        const string EquipmentJsonName = "Equipment_Revolution_Pi.json";

        // RevPi's OWN boot FB IDs — MUST differ from M262's (the SysresFbMirror defaults 593A…/3DB1…), else
        // in the PARTIAL swap the coexisting M262 + RevPi resources share an FB ID. EAE indexes FBs by ID in
        // its global system model, so a shared boot-FB ID across resources throws "An item with the same key
        // has already been added" on load. M580/BX1 already pass their own (Station2SysresMirror); do the same.
        const string RevPiDpacFullInitFbId = "9C4E7A1F5B3D8062";
        const string RevPiPlcStartFbId     = "A5D8F2B60E4C1937";

        // Partial swap: RevPi has no Feed_Station process, so it wires like BX1 (no Area/Station/Process/
        // Terminator anchors, tag "RevPi") -> the feed segment inits off FB1 and the report ring is left
        // OPEN at the seam so EAE bridges it to the M262 Feed ring via the shared syslay.
        static readonly ResourceWireEmitter.ResourceAnchors RevPiPartialAnchors = new(
            Label: "RevPi", AreaFb: null, StationFb: null, ProcessFb: null,
            TerminatorFb: null, HmiAdapterWires: Array.Empty<ResourceWireEmitter.Wire>());

        // Device stage (mirrors M262SysdevEmitter.Emit + M262TopologyEmitter.Emit): emit the Soft_dPAC
        // shell + topology + dfbproj, then mirror the Feed-station FBs onto the RevPi sysres.
        public static SystemInjector.BindingApplicationReport EmitDevice(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                report.Missing.Add("[RevPi] skipped, EAE project root not derivable");
                return report;
            }

            var systemGuidDir = EaeProjectLayout.FindSystemGuidDir(eaeRoot);
            if (systemGuidDir == null)
            {
                report.Missing.Add("[RevPi] skipped, no System GUID folder (run a Test Runtime once first)");
                return report;
            }

            string solutionId = M262TopologyEmitter.ReadProjectGuid(eaeRoot) ?? NoConfDomainUuid;

            // 0. Full swap only: RevPi REPLACES M262, so remove it (a real Test Runtime deep-wipes all
            //    devices first, so this is usually a no-op; it self-heals a target switch without a Clean).
            //    PARTIAL mode (Feeder/Checker on RevPi, M262 keeps the rest) MUST keep M262 -> skip the sweep.
            if (!MapperConfig.PartialRevPi)
                SweepM262Device(eaeRoot, report);

            // 1. Soft_dPAC shell — sysdev + sysres skeleton + Properties + Simulation.Binding + topology
            //    equipment + topologyproj + dfbproj + the Modbus .hcf (the reference RevPi IO mechanism).
            var shell = new Station2DeviceEmitter.EmitResult();
            Station2DeviceEmitter.EmitOnePlc(cfg, eaeRoot, systemGuidDir, shell,
                sysdevId: RevPiSysdevId,
                deviceName: DeviceName,
                deviceType: "Soft_dPAC",
                resourceId: RevPiResourceId,
                resourceName: RevPiResourceName,
                hcfTemplatePath: ModbusHcfTemplatePath(cfg),
                equipmentJsonName: EquipmentJsonName,
                equipmentBuilder: () => BuildRevPiEquipmentJson(RevPiSysdevId, solutionId,
                                          cfg.RevPiHostIp, cfg.RevPiTargetIp),
                deployPluginPropertiesXml: Station2DeviceEmitter.BuildSoftDpacDeployPluginPropertiesXml(
                    cfg.MqttPublishEnabled && !cfg.MqttSecureTls),
                simulationBindingDeployPort: 51502,
                simulationBindingArchivePort: 51499);
            foreach (var w in shell.Warnings) report.Missing.Add($"[RevPi] {w}");

            // 2. Mirror the RevPi (ex-Feed-station) FBs onto the RevPi sysres — same mechanism M262 uses,
            //    filtered to PlcAssignment.RevPi.
            var sysresPath = ResolveRevPiSysresPath(systemGuidDir);
            var syslayPath = cfg.ActiveSyslayPath;
            if (File.Exists(sysresPath) && !string.IsNullOrWhiteSpace(syslayPath) && File.Exists(syslayPath))
            {
                var feedFbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath)
                    .Where(f => SysresFbMirror.BucketFor(f.Name) == PlcAssignment.RevPi)
                    .ToList();
                int mirrored = SysresFbMirror.MirrorFbsIntoSysres(sysresPath, feedFbs,
                    RevPiDpacFullInitFbId, RevPiPlcStartFbId);
                report.Missing.Add($"[RevPi] sysdev emitted; .sysres mirrored {mirrored} Feed FB(s) to {sysresPath}");
                // EAE Solution Integrity FAILS TO LOAD a resource whose {resId}/opcua.xml companion folder
                // is absent ("Unable to load file: missing or corrupted"). SysresFbMirror — unlike
                // Station2SysresMirror (BX1/M580) — does not create it, so create it here beside the sysres.
                CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(sysresPath);
                report.Missing.Add($"[RevPi] opcua companion created beside {Path.GetFileName(sysresPath)}");
                // The Modbus IO broker is injected in WireResource — AFTER the Feed ring wiring, which
                // rebuilds the sysres FBNetwork connections and would otherwise wipe the broker's wires.
            }
            else
            {
                report.Missing.Add("[RevPi] sysres or syslay missing — Feed FB mirror skipped");
            }
            return report;
        }

        // Wire stage (mirrors M262SysresWireEmitter.Emit): the Feed ring on the RevPi sysres. Reuses the
        // exact Feed-station anchors so the wiring is identical regardless of the hosting device.
        public static void WireResource(MapperConfig cfg,
            SystemInjector.BindingApplicationReport report)
        {
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                report.Missing.Add("[Wire] skipped, EAE project root not derivable (RevPi)");
                return;
            }
            var systemGuidDir = EaeProjectLayout.FindSystemGuidDir(eaeRoot);
            var sysresPath = systemGuidDir == null ? null : ResolveRevPiSysresPath(systemGuidDir);
            if (sysresPath == null || !File.Exists(sysresPath))
            {
                report.Missing.Add("[Wire] skipped, RevPi sysres not found");
                return;
            }
            if (MapperConfig.PartialRevPi)
                // Partial swap: RevPi hosts only Feeder/Checker/PartInHopper (no Feed_Station). RevPi-local
                // anchors leave the report ring OPEN at the seam -> EAE bridges it to the M262 Feed ring.
                ResourceWireEmitter.EmitForResource(cfg, sysresPath, RevPiPartialAnchors, report);
            else
                M262SysresWireEmitter.EmitFeedRing(cfg, sysresPath, report);

            // Modbus IO broker + symlink bridge — AFTER the Feed ring so its connections survive.
            RevPiIoBrokerInjector.Inject(sysresPath, cfg.ActiveSyslayPath, RevPiResourceName, report);
        }

        static string ResolveRevPiSysresPath(string systemGuidDir) =>
            Path.Combine(systemGuidDir, RevPiSysdevId, $"{RevPiResourceId}.sysres");

        // The reference RevPi Modbus master .hcf (Standard.IoModbus), staged in the template library. Its
        // ResourceId (D090…) + MB_Read/Write LinkNames (…A6B61E2425DB1C30…) resolve to RevPI_IO on RevPi_RES.
        static string ModbusHcfTemplatePath(MapperConfig cfg) =>
            Path.Combine(cfg.TemplateLibraryPath ?? string.Empty, "RevPi", "RevPiIO.modbus.hcf");

        const string M262SysdevId       = "00000000-0000-0000-0000-000000000002";
        const string M262EquipmentJson  = "Equipment_M262dPAC_1.json";

        // Remove the M262 device COMPLETELY when the RevPi hosts the Feed station: the sysdev + its folder
        // (sysres/hcf/Properties), the topology equipment JSON, AND every project reference to it —
        // IEC61499.dfbproj entries, the TopologyManager.topologyproj equipment entry, and the Folders.xml
        // registration — so EAE Solution Integrity reports no missing M262 project files. Idempotent.
        static void SweepM262Device(string eaeRoot, SystemInjector.BindingApplicationReport report)
        {
            try
            {
                var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
                if (Directory.Exists(systemDir))
                {
                    foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
                    {
                        if (!IsDeviceType(sysdev, "M262_dPAC")) continue;
                        var folder = Path.Combine(Path.GetDirectoryName(sysdev)!, Path.GetFileNameWithoutExtension(sysdev));
                        try { File.Delete(sysdev); } catch { }
                        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
                        report.Missing.Add("[RevPi] removed the M262 device (RevPi now hosts the Feed station)");
                    }
                }
                var m262Equipment = Path.Combine(eaeRoot, "Topology", M262EquipmentJson);
                if (File.Exists(m262Equipment)) { try { File.Delete(m262Equipment); } catch { } }

                // IEC61499.dfbproj: drop every entry referencing the M262 sysdev (sysdev + siblings).
                var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
                int dfbGone = DfbprojRegistrar.UnregisterSystemDevice(dfbproj, M262SysdevId);
                if (dfbGone > 0) report.Missing.Add($"[RevPi] stripped {dfbGone} M262 entry(ies) from IEC61499.dfbproj");

                // TopologyManager.topologyproj: drop the M262 equipment registration.
                int topoGone = RemoveTopologyProjEntry(eaeRoot, M262EquipmentJson);
                if (topoGone > 0) report.Missing.Add($"[RevPi] stripped the M262 equipment entry from TopologyManager.topologyproj");

                // General/Folders.xml: drop the M262 sysdev GUID so EAE shows no phantom device node.
                var folders = Path.Combine(eaeRoot, "General", "Folders.xml");
                if (File.Exists(folders))
                {
                    var doc = System.Xml.Linq.XDocument.Load(folders, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var stale = doc.Descendants().Where(e => e.Name.LocalName == "item" &&
                        string.Equals((e.Value ?? "").Trim(), M262SysdevId, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (stale.Count > 0) { foreach (var s in stale) s.Remove(); doc.Save(folders); }
                }
            }
            catch (Exception ex) { report.Missing.Add($"[RevPi] M262 sweep warning: {ex.Message}"); }
        }

        // Remove a single <None Include="<jsonName>"> entry from TopologyManager.topologyproj (idempotent).
        static int RemoveTopologyProjEntry(string eaeRoot, string jsonName)
        {
            var topoProj = Path.Combine(eaeRoot, "Topology", "TopologyManager.topologyproj");
            if (!File.Exists(topoProj)) return 0;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(topoProj, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var stale = doc.Descendants().Where(e => e.Name.LocalName == "None" &&
                    string.Equals((string?)e.Attribute("Include"), jsonName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var s in stale) s.Remove();
                if (stale.Count > 0) doc.Save(topoProj);
                return stale.Count;
            }
            catch { return 0; }
        }

        static bool IsDeviceType(string sysdevPath, string type)
        {
            try
            {
                var head = File.ReadAllText(sysdevPath);
                return head.Contains($"Type=\"{type}\"", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        // Revolution Pi topology equipment — the reference form (Workstation host + NIC + SoftdpacContainer
        // + RuntimeDEO), parameterised with RevPi ids/IPs. Read verbatim from Jyotsna's RevPi reference.
        static string BuildRevPiEquipmentJson(string sysdevId, string solutionId,
                                              string hostIp, string softpacIp) =>
$$"""
{
  "catalogReference": "Workstation_V01.00_01.00",
  "uuid": "{{RevPiEquipmentUuid}}",
  "identifier": "Revolution_Pi",
  "path": "Topology",
  "properties": [
    { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
    { "propertyName": "CommCardReference",   "propertyValue": "" },
    { "propertyName": "DomainTag",           "propertyValue": "{{solutionId}}" }
  ],
  "references": [
    { "diagramPath": "Physical Views", "x": -564.27, "y": 61.87 }
  ],
  "equipments": [
    {
      "catalogReference": "NIC_EAE_V01.00_01.00",
      "uuid": "{{RevPiNicUuid}}",
      "identifier": "NIC_2",
      "path": "Revolution_Pi\\NIC_2",
      "components": [
        {
          "interfaces": [
            {
              "identifier": "eth0",
              "disabled": false,
              "physicalAddress": "",
              "endpoints": [
                {
                  "identifier": "IP Address",
                  "isReadOnly": false,
                  "domainReadOnly": false,
                  "ipAddress": "{{hostIp}}",
                  "domain": "{{SoftdpacDomainUuid}}"
                }
              ]
            }
          ],
          "ports": [ { "identifier": "Port1", "side": "Default" } ],
          "componentType": "EthernetDEO"
        }
      ]
    },
    {
      "catalogReference": "SoftdpacContainer_V01.00_01.00",
      "uuid": "{{RevPiContainerUuid}}",
      "identifier": "Softdpac_3",
      "path": "Revolution_Pi\\Softdpac_3",
      "components": [
        {
          "interfaces": [
            {
              "identifier": "Eth0",
              "disabled": false,
              "physicalAddress": "",
              "endpoints": [
                {
                  "identifier": "IP Address",
                  "isReadOnly": false,
                  "domainReadOnly": true,
                  "ipAddress": "{{softpacIp}}",
                  "domain": "{{SoftdpacDomainUuid}}"
                }
              ]
            }
          ],
          "ports": [ { "identifier": "Port0", "side": "Default" } ],
          "componentType": "EthernetDEO"
        },
        {
          "endpoint": "Eth0\\IP Address",
          "connectionTypes": "None",
          "componentType": "EthernetMasterDEO"
        },
        {
          "enabled": false,
          "securityMode": 0,
          "componentType": "SysLogClientDEO"
        },
        {
          "imageName": "softdpac",
          "imageVersion": "v24.1.25090.08",
          "identifier": "DockerContainer",
          "allocatedRam": 524288,
          "cpuCores": [ 0, 1, 2, 3 ],
          "componentType": "DockerContainerDEO"
        },
        {
          "uuid": "{{RevPiRuntimeUuid}}",
          "typeId": "{{SoftDpacTypeId}}",
          "logicalDeviceId": "{{sysdevId}}",
          "runtimeServices": [
            { "identifier": "Deployment" },
            { "identifier": "Archive Service", "logicalPortSecured": "0" }
          ],
          "componentType": "RuntimeDEO"
        }
      ]
    }
  ],
  "components": [
    { "mode": 0, "componentType": "CyberSecurityDEO" },
    {
      "preferredPrimary": false,
      "dockerImages": [ { "identifier": "softdpac", "version": "" } ],
      "dockerVlans": [
        {
          "identifier": "softdpacDeviceNet",
          "type": 0,
          "domain": "{{SoftdpacDomainUuid}}",
          "interface": "NIC_2\\eth0",
          "domainReadOnly": false
        }
      ],
      "softdpacManagerServices": [
        { "identifier": "Management services", "logicalPort": 8080, "endpoint": "" }
      ],
      "componentType": "SoftdpacManagerDEO"
    },
    {
      "mode": 1,
      "servers": [
        { "name": "Primary NTP Server_1", "address": "0.0.0.0", "type": 0, "minPoll": 1, "maxPoll": 1 },
        { "name": "Secondary NTP Server_1", "address": "0.0.0.0", "type": 1, "minPoll": 1, "maxPoll": 1 }
      ],
      "componentType": "TimeSettingsDEO"
    }
  ]
}
""";
    }
}
