using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Translation;
using CodeGen.Mapping;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.RevPi
{
    // The Revolution Pi as an EAE deployment target. It is NOT a new kind of controller: its .sysdev is
    // Type="Soft_dPAC" like the BX1's, so the whole device shell comes from Station2DeviceEmitter.EmitOnePlc.
    // Only the RevPi DELTA lives here: the equipment document (a Workstation host with a child NIC, and a
    // Soft dPAC container on a Docker macvlan parented to it), the Modbus hardware config, and moving
    // relocated components off whichever resource used to host them. See Docs/REVPI_PROVISIONING.md.
    public static class RevPiDeviceEmitter
    {
        // Continues the M262/M580/BX1 (…002/003/004) series. Also named in Devices/Common/FoldersXmlEmitter.cs.
        internal const string SysdevId = "00000000-0000-0000-0000-000000000005";
        static readonly string DeviceName = TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceName!;
        const string EquipmentJsonName = "Equipment_Revolution_Pi.json";
        // Topology uuids. NicUuid is also named in TopologyNetworkEmitter, which wires NIC_2[Port1] to the switch.
        const string EquipmentUuid = "11111111-2222-3333-4444-000000000050";
        internal const string NicUuid = "11111111-2222-3333-4444-000000000051";
        const string ContainerUuid = "11111111-2222-3333-4444-000000000052";
        const string RuntimeUuid = "11111111-2222-3333-4444-000000000053";
        // EAE schema constants: the Soft dPAC runtime type, and the broadcast domain both endpoints join.
        const string SoftDpacTypeId = CodeGen.Devices.Core.Station2DeviceEmitter.SoftDpacTypeId;
        const string DeviceNetworkUuid = CodeGen.Devices.Core.Station2DeviceEmitter.Bx1SoftdpacDomainUuid;
        const string NoDomainUuid = "00000000-0000-0000-0000-000000000000";

        // One supported profile: the Pi's primary NIC and the ARM Soft dPAC image (an x86 image exec-format-fails).
        const string HostInterface = "eth0";
        const string SoftDpacImage = "softdpac";
        const string SoftDpacImageVersion = "v24.1.25090.08";

        // Simulation-binding ports: every coexisting resource needs its own pair.
        const int SimulationDeployPort = 51502, SimulationArchivePort = 51499;

        public static SystemInjector.BindingApplicationReport EmitDevice(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Config;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var systemGuidDir = string.IsNullOrEmpty(eaeRoot) ? null : EaeProjectLayout.FindSystemGuidDir(eaeRoot);
            if (systemGuidDir == null)
            {
                report.Missing.Add("[RevPi] skipped, no EAE project root / System GUID folder " +
                                   "(run a generation once first)");
                return report;
            }

            // Throws with a precise reason BEFORE anything is written if coupler, workbook and hcf disagree.
            var coupler = RevPiIoBrokerInjector.Resolve(cfg.TemplateLibraryPath);
            var hosted = HostedComponents(ctx, coupler);

            // A component instance may exist on exactly ONE resource; the same instance on two is EAE's
            // "Repair Instances" / duplicate-key load failure. Nothing here assumes which resource it leaves.
            SweepFromOtherResources(systemGuidDir, hosted, report);

            var solutionId = EaeProjectLayout.ReadProjectGuid(eaeRoot!) ?? NoDomainUuid;
            var shell = new Station2DeviceEmitter.EmitResult();
            Station2DeviceEmitter.EmitOnePlc(cfg, eaeRoot!, systemGuidDir, shell,
                sysdevId: SysdevId,
                deviceName: DeviceName,
                deviceType: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceType,
                resourceId: coupler.ResourceId,
                resourceName: ResourceName,
                hcfTemplatePath: HcfTemplatePath(cfg),
                equipmentJsonName: EquipmentJsonName,
                equipmentBuilder: () => EquipmentJson(solutionId, cfg.RevPiHostIp, cfg.RevPiTargetIp),
                deployPluginPropertiesXml: Station2DeviceEmitter.BuildSoftDpacDeployPluginPropertiesXml(cfg,
                    cfg.MqttPublishEnabled && !cfg.MqttSecureTls),
                simulationBindingDeployPort: SimulationDeployPort,
                simulationBindingArchivePort: SimulationArchivePort);
            foreach (var w in shell.Warnings) report.Missing.Add($"[RevPi] {w}");

            // A missing hardware config is an EAE "Missing Project Files" report; EnsureHcf re-copies it.
            var sysres = SysresPath(systemGuidDir, coupler.ResourceId);
            EnsureHcf(cfg, systemGuidDir, coupler.ResourceId, report);

            // Each resource needs its OWN boot pair: EAE indexes FBs by id in one global model, so a shared
            // boot id is a duplicate-key load failure. Seeding on the resource name keeps them unique.
            var syslay = cfg.ActiveSyslayPath;
            if (File.Exists(sysres) && !string.IsNullOrWhiteSpace(syslay) && File.Exists(syslay))
            {
                var fbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslay)
                    .Where(f => SysresFbMirror.BucketFor(f.Name, ctx.Allocation) == PlcAssignment.RevPi)
                    .ToList();
                int mirrored = SysresFbMirror.MirrorFbsIntoSysres(sysres, fbs,
                    TargetBootstrap.For(PlcAssignment.RevPi, ctx.Layout));
                report.Missing.Add($"[RevPi] device emitted; resource mirrored {mirrored} component(s)");
                // EAE fails to LOAD a resource whose {resId}/opcua.xml companion folder is absent, and SysresFbMirror does not create it.
                CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(sysres);
            }
            else report.Missing.Add("[RevPi] sysres or application layer missing — component mirror skipped");

            return report;
        }

        // Order matters: the shared wiring pass rebuilds the resource's connections, so the broker is placed after it or its edges are lost.
        public static void WireResource(GenerationContext ctx,
            SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Config;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var systemGuidDir = string.IsNullOrEmpty(eaeRoot) ? null : EaeProjectLayout.FindSystemGuidDir(eaeRoot);
            var coupler = RevPiIoBrokerInjector.Resolve(cfg.TemplateLibraryPath);
            var sysres = systemGuidDir == null ? null : SysresPath(systemGuidDir, coupler.ResourceId);
            if (sysres == null || !File.Exists(sysres))
            {
                report.Missing.Add("[Wire] skipped, the Revolution Pi resource is not on disk");
                return;
            }

            ResourceWireEmitter.EmitForResource(ctx, sysres, ctx.ResourceFor(PlcAssignment.RevPi), report);

            var hosted = HostedComponents(ctx, coupler);
            var bootFb = ctx.Layout.BootFbs.Count > 0
                ? ctx.Layout.BootFbs[0].Name : Mapping.TargetBootstrap.InitRole;
            int written = 0;
            foreach (var (label, path, isResource) in new[]
                     {
                         ("resource", sysres, true),
                         ("application", cfg.ActiveSyslayPath ?? string.Empty, false),
                     })
            {
                try
                {
                    if (RevPiIoBrokerInjector.PlaceBroker(coupler, path, isResource, hosted, ctx.Layout, bootFb))
                        written++;
                }
                catch (IOException)
                {
                    report.Missing.Add($"[RevPi][IO] FAILED to write the broker to the {label} — file " +
                        "LOCKED. Close the resource view in EAE (or close EAE) before generating.");
                }
                catch (Exception ex) { report.Missing.Add($"[RevPi][IO] {label} error: {ex.Message}"); }
            }
            if (written > 0)
                report.Missing.Add($"[RevPi][IO] {RevPiIoBrokerInjector.BrokerName} " +
                    $"({RevPiIoBrokerInjector.BrokerType}) placed on {written} document(s) carrying " +
                    $"{coupler.Signals.Count} signal(s) for [{string.Join(", ", hosted)}].");
        }

        static string ResourceName => Mapping.ControllerMap.ResourceForPlc(PlcAssignment.RevPi);

        static string SysresPath(string systemGuidDir, string resourceId) =>
            Path.Combine(systemGuidDir, SysdevId, $"{resourceId}.sysres");

        // Its ResourceId and MB_Read/Write LinkNames are what the resource and the broker instance must satisfy.
        static string HcfTemplatePath(MapperConfig cfg) =>
            Path.Combine(cfg.TemplateLibraryPath ?? string.Empty, "RevPi", "RevPiIO.modbus.hcf");

        // In the coupler's own signal order, so the result is stable whatever order the operator picked.
        static IReadOnlyList<string> HostedComponents(GenerationContext ctx, RevPiIoBrokerInjector.Coupler c) =>
            c.Signals.Select(s => s.Component)
                .Where(n => ctx.Profile.RevPiComponents.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static void EnsureHcf(MapperConfig cfg, string systemGuidDir, string resourceId,
            SystemInjector.BindingApplicationReport report)
        {
            var dest = Path.Combine(systemGuidDir, SysdevId, $"{SysdevId}.hcf");
            if (File.Exists(dest)) return;
            try
            {
                File.Copy(HcfTemplatePath(cfg), dest, overwrite: true);
                HcfRootRewriter.RewriteIfNeeded(dest, resourceId);
                report.Missing.Add("[RevPi] hardware config was missing — re-copied.");
            }
            catch (Exception ex) { report.Missing.Add($"[RevPi] hardware config copy error: {ex.Message}"); }
        }

        static void SweepFromOtherResources(string systemGuidDir, IReadOnlyList<string> hosted,
            SystemInjector.BindingApplicationReport report)
        {
            if (hosted.Count == 0) return;
            var names = new HashSet<string>(hosted, StringComparer.Ordinal);
            var mine = Path.GetFullPath(Path.Combine(systemGuidDir, SysdevId));

            foreach (var sysres in Directory.EnumerateFiles(systemGuidDir, "*.sysres", SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(Path.GetDirectoryName(sysres)!).StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var doc = CodeGen.Services.FbtXmlEditor.LoadXmlWithRetry(sysres, LoadOptions.PreserveWhitespace);
                    var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                    var net = doc.Root?.Element(ns + "FBNetwork");
                    if (net == null) continue;
                    var stale = net.Elements(ns + "FB")
                        .Where(f => names.Contains((string?)f.Attribute("Name") ?? "")).ToList();
                    if (stale.Count == 0) continue;
                    foreach (var fb in stale) fb.Remove();
                    foreach (var group in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
                        net.Element(ns + group)?.Elements(ns + "Connection")
                            .Where(c => names.Any(n =>
                                ((string?)c.Attribute("Source") ?? "").StartsWith(n + ".", StringComparison.Ordinal) ||
                                ((string?)c.Attribute("Destination") ?? "").StartsWith(n + ".", StringComparison.Ordinal)))
                            .ToList().ForEach(c => c.Remove());
                    // EAE locks an open resource and a bare save fails silently, leaving the duplicate in place.
                    CodeGen.Services.FbtXmlEditor.SaveXmlWithRetry(doc, sysres);
                    report.Missing.Add($"[RevPi] swept {stale.Count} relocated component(s) off " +
                        $"'{Path.GetFileName(sysres)}' — prevents a duplicate instance.");
                }
                catch (Exception ex)
                {
                    // Early cleanup only: the wiring pass writes each resource last and drops them again.
                    report.Missing.Add($"[RevPi] early sweep of '{Path.GetFileName(sysres)}' deferred " +
                        $"({ex.GetType().Name}); the wiring pass drops them from the final file.");
                }
            }
        }

        // Structurally identical to the reference solution's own "Equipment_Revolution Pi.json"; only the
        // uuids, identifier and diagram position are generated rather than copied.
        // dockerVlans is NOT decorative: EAE validates that a Soft dPAC interface's logical network is
        // associated with a Docker network, and type 0 == VLanType.MacVLan, whose own MAC is what lets the
        // switch and EAE Deploy/Login see the container as its own endpoint. The Manager creates it, not us.
        // Host vs container is a ROLE split: the host NIC carries the Manager (8080) with an editable address,
        // the container's address is dictated by the vlan. They must never be equal (TopologyAddressValidator).
        static string EquipmentJson(string solutionId, string hostIp, string containerIp) =>
$$"""
{
  "catalogReference": "Workstation_V01.00_01.00",
  "uuid": "{{EquipmentUuid}}",
  "identifier": "{{DeviceName}}",
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
      "uuid": "{{NicUuid}}",
      "identifier": "NIC_2",
      "path": "{{DeviceName}}\\NIC_2",
      "components": [
        {
          "interfaces": [
            { "identifier": "{{HostInterface}}", "disabled": false, "physicalAddress": "",
              "endpoints": [ { "identifier": "IP Address", "isReadOnly": false, "domainReadOnly": false,
                               "ipAddress": "{{hostIp}}", "domain": "{{DeviceNetworkUuid}}" } ] }
          ],
          "ports": [ { "identifier": "Port1", "side": "Default" } ],
          "componentType": "EthernetDEO"
        }
      ]
    },
    {
      "catalogReference": "SoftdpacContainer_V01.00_01.00",
      "uuid": "{{ContainerUuid}}",
      "identifier": "Softdpac_3",
      "path": "{{DeviceName}}\\Softdpac_3",
      "components": [
        {
          "interfaces": [
            { "identifier": "Eth0", "disabled": false, "physicalAddress": "",
              "endpoints": [ { "identifier": "IP Address", "isReadOnly": false, "domainReadOnly": true,
                               "ipAddress": "{{containerIp}}", "domain": "{{DeviceNetworkUuid}}" } ] }
          ],
          "ports": [ { "identifier": "Port0", "side": "Default" } ],
          "componentType": "EthernetDEO"
        },
        { "endpoint": "Eth0\\IP Address", "connectionTypes": "None", "componentType": "EthernetMasterDEO" },
        { "enabled": false, "securityMode": 0, "componentType": "SysLogClientDEO" },
        {
          "imageName": "{{SoftDpacImage}}", "imageVersion": "{{SoftDpacImageVersion}}",
          "identifier": "DockerContainer", "allocatedRam": 524288, "cpuCores": [ 0, 1, 2, 3 ],
          "componentType": "DockerContainerDEO"
        },
        {
          "uuid": "{{RuntimeUuid}}", "typeId": "{{SoftDpacTypeId}}", "logicalDeviceId": "{{SysdevId}}",
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
      "dockerImages": [ { "identifier": "{{SoftDpacImage}}", "version": "" } ],
      "dockerVlans": [
        { "identifier": "softdpacDeviceNet", "type": 0, "domain": "{{DeviceNetworkUuid}}",
          "interface": "NIC_2\\{{HostInterface}}", "domainReadOnly": false }
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
