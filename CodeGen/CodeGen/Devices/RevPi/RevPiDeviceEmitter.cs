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
    // NOTE (rig IO): the physical Feed IO on a Revolution Pi is a Modbus word broker (PLC_RW_REVPI) in the
    // reference. That IO bridge is coupled to the reference's direct-wire Process1_CAT model (which this
    // Mapper must NOT adopt — see INVARIANTS I-1 / REVERTED_FIXES R-4), so it is a separate follow-up. The
    // device is emitted WITHOUT a Modbus HCF here (EmitOnePlc handles a null template gracefully).
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

            // 0. No stale M262: RevPi replaces it. (A real Test Runtime deep-wipes all devices first, so
            //    this is usually a no-op; it makes a target switch without a Clean self-healing too.)
            SweepM262Device(eaeRoot, report);

            // 1. Soft_dPAC shell — sysdev + sysres skeleton + Properties + Simulation.Binding + topology
            //    equipment + topologyproj + dfbproj. No HCF (Feed IO is the Modbus follow-up).
            var shell = new Station2DeviceEmitter.EmitResult();
            Station2DeviceEmitter.EmitOnePlc(cfg, eaeRoot, systemGuidDir, shell,
                sysdevId: RevPiSysdevId,
                deviceName: DeviceName,
                deviceType: "Soft_dPAC",
                resourceId: RevPiResourceId,
                resourceName: RevPiResourceName,
                hcfTemplatePath: null,
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
                int mirrored = SysresFbMirror.MirrorFbsIntoSysres(sysresPath, feedFbs);
                report.Missing.Add($"[RevPi] sysdev emitted; .sysres mirrored {mirrored} Feed FB(s) to {sysresPath}");
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
            M262SysresWireEmitter.EmitFeedRing(cfg, sysresPath, report);
        }

        static string ResolveRevPiSysresPath(string systemGuidDir) =>
            Path.Combine(systemGuidDir, RevPiSysdevId, $"{RevPiResourceId}.sysres");

        // Delete the M262 device (sysdev + its same-stem folder with sysres/hcf/Properties) + the M262
        // topology equipment, so no M262 lingers when RevPi hosts the Feed station. Dangling dfbproj/sysres
        // refs are cleaned by the later StripStaleSysresStemEntries / SweepOrphanSysres pipeline steps.
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
                var m262Equipment = Path.Combine(eaeRoot, "Topology", "Equipment_M262dPAC_1.json");
                if (File.Exists(m262Equipment)) { try { File.Delete(m262Equipment); } catch { } }

                // Drop the M262 sysdev GUID from General/Folders.xml so EAE shows no phantom device node.
                var folders = Path.Combine(eaeRoot, "General", "Folders.xml");
                if (File.Exists(folders))
                {
                    var doc = System.Xml.Linq.XDocument.Load(folders, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var stale = doc.Descendants().Where(e => e.Name.LocalName == "item" &&
                        string.Equals((e.Value ?? "").Trim(), "00000000-0000-0000-0000-000000000002", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (stale.Count > 0) { foreach (var s in stale) s.Remove(); doc.Save(folders); }
                }
            }
            catch (Exception ex) { report.Missing.Add($"[RevPi] M262 sweep warning: {ex.Message}"); }
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
