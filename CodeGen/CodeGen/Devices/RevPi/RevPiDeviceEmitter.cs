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
using CodeGen.Services;

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
        // EVERY IDENTITY IS THE EMITTING TARGET'S OWN, read from its descriptor. Keyed on the name
        // "RevPi" they were the FIRST such target's, so a second one could not be emitted at all.
        static string DeviceName(Mapping.TargetDescriptor t) => t.DeviceName!;
        const string EquipmentJsonName = "Equipment_Revolution_Pi.json";
        const string NoDomainUuid = Artefacts.EaeAbi.NoBroadcastDomain;

        // One supported profile: the Pi's primary NIC and the ARM Soft dPAC image (an x86 image exec-format-fails).
        const string HostInterface = "eth0";
        const string SoftDpacImage = "softdpac";
        const string SoftDpacImageVersion = "v24.1.25090.08";

        // Simulation-binding ports: every coexisting resource needs its own pair.

        public static SystemInjector.BindingApplicationReport EmitDevice(GenerationContext ctx,
            PlcAssignment target, SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Cfg;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var systemGuidDir = string.IsNullOrEmpty(eaeRoot) ? null : EaeProjectLayout.FindSystemGuidDir(eaeRoot);
            if (systemGuidDir == null)
            {
                report.Missing.Add("[RevPi] skipped, no EAE project root / System GUID folder " +
                                   "(run a generation once first)");
                return report;
            }

            // Throws with a precise reason BEFORE anything is written if coupler, workbook and hcf disagree.
            var self = ctx.Targets.Of(target);
            var coupler = RevPiIoBrokerInjector.Resolve(cfg);
            var hosted = HostedComponents(ctx, coupler);

            // A component instance may exist on exactly ONE resource; the same instance on two is EAE's
            // "Repair Instances" / duplicate-key load failure. Nothing here assumes which resource it leaves.
            SweepFromOtherResources(cfg.Generation.FileWriteRetries, systemGuidDir, self.Identity.Sysdev, hosted, report);

            var solutionId = EaeProjectLayout.ReadProjectGuid(eaeRoot!) ?? NoDomainUuid;
            var shell = new Station2DeviceEmitter.EmitResult();
            Station2DeviceEmitter.EmitOnePlc(cfg, self, eaeRoot!, systemGuidDir, shell,
                sysdevId: self.Identity.Sysdev,
                deviceName: DeviceName(self),
                deviceType: self.DeviceType,
                resourceId: coupler.ResourceId,
                resourceName: self.ResourceName,
                hcfTemplatePath: HcfTemplatePath(cfg),
                equipmentJsonName: EquipmentJsonName,
                equipmentBuilder: () => EquipmentJson(cfg, self, solutionId,
                    cfg.Devices.NetworkOf(self.Plc.Name).HostIp,
                    cfg.Devices.NetworkOf(self.Plc.Name).TargetIp),
                deployPluginPropertiesXml: Station2DeviceEmitter.BuildSoftDpacDeployPluginPropertiesXml(cfg,
                    cfg.Telemetry.PublishEnabled && !cfg.Telemetry.SecureTls),
                simulationBindingDeployPort: self.SimulationDeployPort,
                simulationBindingArchivePort: self.SimulationArchivePort);
            foreach (var w in shell.Warnings) report.Missing.Add($"[RevPi] {w}");

            // A missing hardware config is an EAE "Missing Project Files" report; EnsureHcf re-copies it.
            var sysres = SysresPath(systemGuidDir, self.Identity.Sysdev, coupler.ResourceId);
            EnsureHcf(cfg, systemGuidDir, self.Identity.Sysdev, coupler.ResourceId, report);

            // Each resource needs its OWN boot pair: EAE indexes FBs by id in one global model, so a shared
            // boot id is a duplicate-key load failure. Seeding on the resource name keeps them unique.
            var syslay = cfg.Paths.ActiveSyslayPath;
            if (File.Exists(sysres) && !string.IsNullOrWhiteSpace(syslay) && File.Exists(syslay))
            {
                var fbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslay)
                    .Where(f => SysresFbMirror.BucketFor(f.Name, ctx.Allocation, ctx.Cfg) == target)
                    .ToList();
                int mirrored = SysresFbMirror.MirrorFbsIntoSysres(sysres, fbs,
                    ctx.Targets.BootFor(target, ctx.Layout), ctx.Manifest);
                report.Missing.Add($"[RevPi] device emitted; resource mirrored {mirrored} component(s)");
                // EAE fails to LOAD a resource whose {resId}/opcua.xml companion folder is absent, and SysresFbMirror does not create it.
                CodeGen.Artefacts.OpcuaCompanionEmitter.EmitForArtefact(ctx.Cfg, sysres);
            }
            else report.Missing.Add("[RevPi] sysres or application layer missing — component mirror skipped");

            return report;
        }

        // Order matters: the shared wiring pass rebuilds the resource's connections, so the broker is placed after it or its edges are lost.
        public static void WireResource(GenerationContext ctx, PlcAssignment target,
            SystemInjector.BindingApplicationReport report)
        {
            var cfg = ctx.Cfg;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var systemGuidDir = string.IsNullOrEmpty(eaeRoot) ? null : EaeProjectLayout.FindSystemGuidDir(eaeRoot);
            var self = ctx.Targets.Of(target);
            var coupler = RevPiIoBrokerInjector.Resolve(cfg);
            var sysres = systemGuidDir == null ? null : SysresPath(systemGuidDir, self.Identity.Sysdev, coupler.ResourceId);
            if (sysres == null || !File.Exists(sysres))
            {
                report.Missing.Add("[Wire] skipped, the Revolution Pi resource is not on disk");
                return;
            }

            ResourceWireEmitter.EmitForResource(ctx, sysres, ctx.ResourceFor(target), report);

            var hosted = HostedComponents(ctx, coupler);
            var bootFb = ctx.Layout.BootFbs.Count > 0
                ? ctx.Layout.BootFbs[0].Name : ctx.Targets.InitRole;
            int written = 0;
            foreach (var (label, path, isResource) in new[]
                     {
                         ("resource", sysres, true),
                         ("application", cfg.Paths.ActiveSyslayPath ?? string.Empty, false),
                     })
            {
                try
                {
                    if (RevPiIoBrokerInjector.PlaceBroker(coupler, target, path, isResource, hosted, ctx.Layout, bootFb))
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

        static string SysresPath(string systemGuidDir, string sysdevId, string resourceId) =>
            Path.Combine(systemGuidDir, sysdevId, $"{resourceId}.sysres");

        // Its ResourceId and MB_Read/Write LinkNames are what the resource and the broker instance must satisfy.
        static string HcfTemplatePath(Configuration.CompilerConfiguration cfg) =>
            Path.Combine(cfg.Paths.TemplateLibraryPath ?? string.Empty, "RevPi", "RevPiIO.modbus.hcf");

        // In the coupler's own signal order, so the result is stable whatever order the operator picked.
        static IReadOnlyList<string> HostedComponents(GenerationContext ctx, RevPiIoBrokerInjector.Coupler c) =>
            c.Signals.Select(s => s.Component)
                .Where(n => ctx.Profile.Assignments.Keys.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static void EnsureHcf(Configuration.CompilerConfiguration cfg, string systemGuidDir,
            string sysdevId, string resourceId, SystemInjector.BindingApplicationReport report)
        {
            var dest = Path.Combine(systemGuidDir, sysdevId, $"{sysdevId}.hcf");
            if (File.Exists(dest)) return;
            try
            {
                File.Copy(HcfTemplatePath(cfg), dest, overwrite: true);
                HcfRootRewriter.RewriteIfNeeded(dest, resourceId, cfg.Generation.FileWriteRetries);
                report.Missing.Add("[RevPi] hardware config was missing — re-copied.");
            }
            catch (Exception ex) { report.Missing.Add($"[RevPi] hardware config copy error: {ex.Message}"); }
        }

        static void SweepFromOtherResources(int retries, string systemGuidDir, string sysdevId,
            IReadOnlyList<string> hosted, SystemInjector.BindingApplicationReport report)
        {
            if (hosted.Count == 0) return;
            var names = new HashSet<string>(hosted, StringComparer.Ordinal);
            var mine = Path.GetFullPath(Path.Combine(systemGuidDir, sysdevId));

            foreach (var sysres in Directory.EnumerateFiles(systemGuidDir, "*.sysres", SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(Path.GetDirectoryName(sysres)!).StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var doc = CodeGen.Services.FbtXmlEditor.LoadXmlWithRetry(sysres, LoadOptions.PreserveWhitespace, retries);
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
                    CodeGen.Services.FbtXmlEditor.SaveXmlWithRetry(doc, sysres, retries);
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
        static string EquipmentJson(Configuration.CompilerConfiguration cfg, Mapping.TargetDescriptor self,
            string solutionId, string hostIp, string containerIp) =>
            TemplateDocument.Load(cfg, @"Topology\Equipment_RevolutionPi.json",
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["EquipmentUuid"] = self.Identity.Equipment,
                    ["NicUuid"] = self.Identity.Nic,
                    ["ContainerUuid"] = self.Identity.Container,
                    ["RuntimeUuid"] = self.Identity.Runtime,
                    ["RuntimeTypeId"] = self.Identity.RuntimeType,
                    ["SysdevId"] = self.Identity.Sysdev,
                    ["DeviceNetwork"] = self.Identity.ContainerDomain,
                    ["DeviceName"] = DeviceName(self),
                    ["HostInterface"] = HostInterface,
                    ["ContainerImage"] = SoftDpacImage,
                    ["ContainerImageVersion"] = SoftDpacImageVersion,
                    ["SolutionId"] = solutionId,
                    ["HostIp"] = hostIp,
                    ["ContainerIp"] = containerIp,
                });
    }
}
