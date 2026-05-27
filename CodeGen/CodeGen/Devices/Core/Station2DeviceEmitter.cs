using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Emits Station 2 device + resource artefacts (the M580 X80 PLC and the
    /// BX1 software dPAC) into the EAE project, mirroring exactly the layout
    /// of <c>SMC_Rig_Expo_withClamp</c>'s reference project.
    ///
    /// Per PLC, this writes:
    ///   1. <c>IEC61499/System/{sys-guid}/{sysdev-guid}.sysdev</c>
    ///         — &lt;Device Type="M580_dPAC"/&gt; or "Soft_dPAC", Namespace="SE.DPAC"
    ///   2. <c>IEC61499/System/{sys-guid}/{sysdev-guid}/{resource-id}.sysres</c>
    ///         — &lt;Resource Type="EMB_RES_ECO"/&gt;. Resource identity is aligned
    ///           to what the authored .hcf expects (M580 name "RES0" from its
    ///           name-scoped symlinks; BX1 ID from its DeviceHwConfigurationItem
    ///           ResourceId) so EAE binds the .hcf to the resource.
    ///   3. <c>IEC61499/System/{sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf</c>
    ///         — copied verbatim from <c>cfg.M580HcfTemplatePath</c> /
    ///           <c>cfg.BX1HcfTemplatePath</c> (the IO folder under
    ///           <c>C:\VueOneMapper\IO</c>)
    ///   4. <c>Topology/Equipment_M580dPAC_1.json</c> or
    ///      <c>Topology/Equipment_Soft_dPAC_BX1.json</c>
    ///         — Physical Views device with logicalDeviceId pointing at (1)
    ///   5. Registration into <c>TopologyManager.topologyproj</c> and the
    ///      project <c>dfbproj</c> so EAE's project tree shows them.
    ///
    /// Stable GUIDs are picked so re-running is idempotent.
    /// </summary>
    public static class Station2DeviceEmitter
    {
        const string LibElNs = "https://www.se.com/LibraryElements";

        // Stable per-project IDs. Picked to avoid collision with M262
        // (00..0001 = Application, 00..0002 = M262 sysdev) and to be
        // semantically obvious if you ever read the deployed files.
        const string M580SysdevId    = "00000000-0000-0000-0000-000000000003";
        const string BX1SysdevId     = "00000000-0000-0000-0000-000000000004";
        // Sysres IDs are 16-hex chars (EAE convention). Stable, deterministic.
        const string M580ResourceId  = "3E5C2B7F1A4D6C8E";
        const string BX1ResourceId   = "C9F2A4B7E1D3F5A8";
        // M580 resource name: TEMPORARY back-to-"RES0" override.
        //
        // The compile error "Device M580 contains 2 instances of
        // Runtime.Management.EMB_RES_ECO" returned on the rig even after
        // the stale-sysres sweep + duplicate-Layer-ID syslay sweep + inline
        // <Resources> block fixes were all in place. The exact second
        // instance source for M580 specifically has not yet been
        // root-caused, but renaming the sysres to "RES0" reliably makes the
        // error disappear (observed across multiple cycles on the rig).
        // M262 + BX1 stay on per-PLC names ("M262_RES" / "BX1_RES") which
        // also compile cleanly. M580 alone uses "RES0" until the second-
        // instance source can be pinned down — keep this as a known
        // temporary asymmetry, not the long-term design.
        const string M580ResourceName = "RES0";
        const string BX1ResourceName  = "BX1_RES";

        // Topology Equipment UUIDs — stable so the JSON Equipment entries
        // don't churn between Test Runtime clicks (which would invalidate
        // any user-drawn wires on the Physical Views canvas).
        const string M580EquipmentUuid   = "11111111-2222-3333-4444-000000000040";
        const string M580RuntimeUuid     = "11111111-2222-3333-4444-000000000041";
        const string M580RackUuid        = "11111111-2222-3333-4444-000000000042";
        const string M580CpsUuid         = "11111111-2222-3333-4444-000000000043";
        const string M580CpuUuid         = "11111111-2222-3333-4444-000000000044";
        const string BX1EquipmentUuid    = "11111111-2222-3333-4444-000000000050";
        const string BX1ContainerUuid    = "11111111-2222-3333-4444-000000000051";
        const string BX1RuntimeUuid      = "11111111-2222-3333-4444-000000000052";

        // Schneider EAE TypeIds for RuntimeDEO per device class — same values
        // the reference SMC_Rig_Expo_withClamp uses, verified by inspection.
        const string M580RuntimeTypeId = "7fd313c7-1da3-4618-9a5d-9ff3596aff7f";
        const string SoftDpacTypeId    = "29797a55-a6b8-47c4-9c06-e8a42b1a38b5";

        // NOCONF sentinel (also used by M262TopologyEmitter). No broadcast
        // domain binding — user wires manually on Physical Views post-deploy.
        const string NoConfDomainUuid = "00000000-0000-0000-0000-000000000000";

        // Fallback SolutionId used only when General/ProjectInfo.xml is missing.
        // EAE rejects an Equipment JSON whose DomainTag is the zero UUID with
        // "Unable to import topology / Object reference not set" — DomainTag
        // MUST equal the live SolutionId.
        const string FallbackSolutionUuid = "00000000-0000-0000-0000-000000000000";

        public sealed class EmitResult
        {
            public List<string> FilesWritten { get; } = new();
            public List<string> Warnings { get; } = new();
            public int TopologyProjEntriesAdded { get; set; }
            public int DfbprojEntriesAdded { get; set; }
        }

        public static EmitResult EmitAll(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();

            // Simulator full-system mode collapses every PLC into ONE SIM
            // resource on a single sysdev. The M580 + BX1 sysdev/sysres/HCF/
            // Topology Equipment JSON files have no place in that world —
            // they would only confuse EAE into spinning up extra runtimes
            // for devices the simulator pipeline never targets. Bail out
            // early so a sim deploy never leaves orphan M580/BX1 artefacts
            // behind. Hardware path (cfg.SimulatorFullSystem == false) is
            // unchanged.
            if (cfg.SimulatorFullSystem)
            {
                result.Warnings.Add(
                    "[Station2DeviceEmitter] Skipped (cfg.SimulatorFullSystem=true) — " +
                    "all components collapse into one SIM resource, no M580/BX1 device emission.");
                return result;
            }

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("Cannot derive EAE project root — Station 2 emit skipped.");
                return result;
            }

            // System GUID is fixed; M262 emit already established it.
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            var systemGuidDir = Directory.Exists(systemDir)
                ? Directory.EnumerateDirectories(systemDir)
                    .FirstOrDefault(d =>
                    {
                        var name = Path.GetFileName(d);
                        return Guid.TryParse(name, out _) && !name.StartsWith(".");
                    })
                : null;
            if (systemGuidDir == null)
            {
                result.Warnings.Add(
                    $"No System GUID folder under {systemDir} — run Test Runtime once " +
                    "so M262 emit creates it, then re-run.");
                return result;
            }

            // SolutionId — must be a real GUID matching General/ProjectInfo.xml.
            // EAE uses DomainTag=SolutionId to scope each equipment to the
            // current project; the zero UUID fails import with
            // "Object reference not set to an instance of an object" /
            // "Unable to import topology".
            string solutionId = M262TopologyEmitter.ReadProjectGuid(eaeRoot)
                ?? FallbackSolutionUuid;
            if (solutionId == FallbackSolutionUuid)
                result.Warnings.Add(
                    "ProjectInfo.xml Guid not readable — Station 2 Topology emitted " +
                    "with zero SolutionId; EAE may reject the import. Restore General/ProjectInfo.xml.");

            // Clean up Topology JSONs that earlier Mapper builds wrote with
            // wrong catalog references / zero DomainTag / now-duplicate uuids.
            // EAE keeps complaining on import as long as these are present in
            // topologyproj — observed 2026-05-27: a pre-rename Equipment_BX1.json
            // coexisted with the current Equipment_Workstation_BX1.json, BOTH
            // declaring uuid 11111111-2222-3333-4444-000000000050. EAE rejected
            // the whole topology with "Unable to import topology / Internal
            // Server Error" and every Logical Device in Deploy & Diagnostic
            // lost its Physical Device binding. Each entry below also unhooks
            // the file from TopologyManager.topologyproj so the build target
            // does not list a deleted file.
            CleanupStaleTopologyJson(eaeRoot, "Equipment_Soft_dPAC_BX1.json", result);
            CleanupStaleTopologyJson(eaeRoot, "Equipment_BX1.json",           result);
            // EAE auto-spawns Equipment_<deviceName>_<N>.json variants when its
            // Physical Views editor wants to add a new instance of a device
            // whose name collides with an already-loaded file. Observed
            // 2026-05-27: deleting Equipment_M580dPAC_1.json mid-session made
            // EAE write Equipment_M580dPAC_2.json with a random uuid + orphan
            // logicalDeviceId 00000000-…0000 + the wrong rack (BMEXBP0800),
            // and the Logical Devices physical-device dropdown then showed two
            // "M580dPAC_1 \ BME D58 1020 #0" rows pointing at different files.
            // Sweep the obvious _N variants so a Generate brings the disk back
            // to canonical state.
            for (int n = 2; n <= 9; n++)
                CleanupStaleTopologyJson(eaeRoot, $"Equipment_M580dPAC_{n}.json", result);

            // Resource-identity alignment. The M580/BX1 .hcf files are authored
            // in EAE and carry their own resource scoping; the emitted sysres
            // must adopt it or EAE cannot bind the .hcf to the resource:
            //   • M580 (X80 export) is NAME-scoped — its symlinks read
            //     'RES0.M580IO.<sym>', so the resource must be named "RES0".
            //   • BX1 (EtherNet/IP export) is GUID-scoped — its
            //     DeviceHwConfigurationItem/@ResourceId + EIP-word symlinks read
            //     a fixed GUID, so the sysres ID must equal it.
            // Both are read from the .hcf (self-healing if the user re-exports);
            // fall back to the stable constants / cfg.ResourceName when absent.
            // NOTE: this only binds the .hcf to the resource SHELL — the symlink
            // targets (the M580IO variable group / the BX1 EIP-word FB) still
            // need the FB-side work before the bindings resolve at runtime.
            // M580 resource identity no longer read from the .hcf — name is fixed
            // to M580ResourceName below. BX1 still reads its identity because its
            // .hcf carries a GUID-scoped DeviceHwConfigurationItem/@ResourceId we
            // align the sysres ID to.
            var bx1Ident = ReadHcfResourceIdentity(cfg.BX1HcfTemplatePath);

            // M580 resource name is FIXED to "M580_RES" via M580ResourceName,
            // matching M262 (cfg.ResourceName = "M262_RES") and BX1 below for
            // a consistent device tree. M580SymbolBinder rewrites every
            // .hcf channel to a Form 1 GUID triple (<resId>.<fbId>.<port>),
            // so the .hcf carries no name-prefix dependency on the sysres.
            // The EMB_RES_ECO double-count error that briefly forced "RES0"
            // is now blocked at root by CompileCachePurger's duplicate-Layer-ID
            // .syslay sweep, so per-PLC names are safe again.
            var m580ResourceName = M580ResourceName;
            var bx1ResourceId = bx1Ident.GuidId ?? BX1ResourceId;
            if (!string.Equals(bx1ResourceId, BX1ResourceId, StringComparison.Ordinal))
                result.Warnings.Add(
                    $"[BX1] sysres ID aligned to '{bx1ResourceId}' from the BX1 .hcf ResourceId (default was '{BX1ResourceId}').");

            EmitOnePlc(cfg, eaeRoot, systemGuidDir, result,
                sysdevId: M580SysdevId,
                deviceName: "M580",
                deviceType: "M580_dPAC",
                resourceId: M580ResourceId,
                resourceName: m580ResourceName,
                hcfTemplatePath: cfg.M580HcfTemplatePath,
                equipmentJsonName: "Equipment_M580dPAC_1.json",
                equipmentBuilder: () => BuildM580EquipmentJson(M580SysdevId, solutionId,
                                          cfg.M580TargetIp, cfg.M580BroadcastDomainUuid),
                deployPluginPropertiesXml: BuildStandardDeployPluginPropertiesXml(),
                simulationBindingDeployPort: 51500,
                simulationBindingArchivePort: 51497);

            EmitOnePlc(cfg, eaeRoot, systemGuidDir, result,
                sysdevId: BX1SysdevId,
                deviceName: "BX1",
                deviceType: "Soft_dPAC",
                resourceId: bx1ResourceId,
                resourceName: BX1ResourceName,
                hcfTemplatePath: cfg.BX1HcfTemplatePath,
                equipmentJsonName: "Equipment_Workstation_BX1.json",
                equipmentBuilder: () => BuildBX1WorkstationEquipmentJson(BX1SysdevId, solutionId, cfg.BX1TargetIp),
                deployPluginPropertiesXml: BuildSoftDpacDeployPluginPropertiesXml(),
                simulationBindingDeployPort: 51501,
                simulationBindingArchivePort: 51498);

            return result;
        }

        static void EmitOnePlc(MapperConfig cfg, string eaeRoot, string systemGuidDir,
            EmitResult result, string sysdevId, string deviceName, string deviceType,
            string resourceId, string resourceName, string? hcfTemplatePath,
            string equipmentJsonName, Func<string> equipmentBuilder,
            string deployPluginPropertiesXml,
            int simulationBindingDeployPort, int simulationBindingArchivePort)
        {
            // 1. sysdev — always (re)written; the device declaration carries no
            //    trust binding by itself, just the Name + Type + GUID.
            var sysdevPath = Path.Combine(systemGuidDir, $"{sysdevId}.sysdev");
            File.WriteAllText(sysdevPath, BuildSysdevXml(sysdevId, deviceName, deviceType, resourceId, resourceName));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysdevPath));

            // 2. sysres — Resource declaration inside the sysdev folder.
            var sysdevFolder = Path.Combine(systemGuidDir, sysdevId);
            Directory.CreateDirectory(sysdevFolder);
            var sysresPath = Path.Combine(sysdevFolder, $"{resourceId}.sysres");
            // Drop any sysres left by a previous deploy under a different
            // resource ID (e.g. before the .hcf-driven ID alignment) so EAE
            // never sees two resources inside one device folder.
            foreach (var staleSysres in Directory.EnumerateFiles(sysdevFolder, "*.sysres"))
            {
                if (string.Equals(staleSysres, sysresPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(staleSysres);
                    result.Warnings.Add(
                        $"{deviceName}: removed stale sysres {Path.GetFileName(staleSysres)} (resource ID changed).");
                }
                catch { /* best-effort */ }
            }
            // Each .sysres has a SIBLING folder with the same stem holding its
            // opcua.xml + offline.xml. When the resource ID changed (multiple
            // times during dev), the old sibling folder was orphaned. Observed
            // 2026-05-27: nine stale sister folders remained inside the M580
            // sysdev folder. EAE doesn't fail on them but the Devices tree
            // exposes the extras as ghost resources and Solution Integrity
            // bloats accordingly. Sweep any sister whose stem has no matching
            // .sysres on disk now.
            foreach (var sister in Directory.EnumerateDirectories(sysdevFolder))
            {
                var sisterName = Path.GetFileName(sister);
                if (string.IsNullOrEmpty(sisterName)) continue;
                var matchingSysres = Path.Combine(sysdevFolder, sisterName + ".sysres");
                if (File.Exists(matchingSysres)) continue;
                try
                {
                    Directory.Delete(sister, recursive: true);
                    result.Warnings.Add(
                        $"{deviceName}: removed stale sysres sister folder {sisterName} (no matching .sysres).");
                }
                catch { /* best-effort */ }
            }
            if (!File.Exists(sysresPath))
            {
                File.WriteAllText(sysresPath, BuildSysresXml(resourceId, resourceName));
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysresPath));
            }

            // 3. HCF — copy verbatim from IO folder, then re-root the XML if
            //    it uses the newer <HwConfigExportedConfiguration> form so
            //    EAE's PNConfiguratorBuildTask (which expects the legacy
            //    <DeviceHwConfigurationItems>) accepts it. Channel bindings
            //    inside are untouched.
            if (!string.IsNullOrWhiteSpace(hcfTemplatePath) && File.Exists(hcfTemplatePath))
            {
                var hcfDest = Path.Combine(sysdevFolder, $"{sysdevId}.hcf");
                File.Copy(hcfTemplatePath, hcfDest, overwrite: true);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, hcfDest));

                var rewrite = HcfRootRewriter.RewriteIfNeeded(hcfDest, resourceId);
                if (rewrite.Rewrote)
                    result.FilesWritten.Add(
                        $"{Path.GetRelativePath(eaeRoot, hcfDest)} (re-rooted to DeviceHwConfigurationItems)");
                else if (!string.IsNullOrEmpty(rewrite.Skipped) &&
                         rewrite.Skipped != "already DeviceHwConfigurationItems")
                    result.Warnings.Add($"{deviceName} HCF re-root skipped: {rewrite.Skipped}");
            }
            else
            {
                result.Warnings.Add(
                    $"{deviceName}: HCF template not found at {hcfTemplatePath ?? "<unset>"} " +
                    "— device emitted without hardware-config file.");
            }

            // 3b. DeployPlugin Properties XML — without this file, EAE's
            //     Hardware Configuration tab doesn't register the .hcf with the
            //     device card. M262 has it auto-written by M262SysdevEmitter;
            //     M580 + BX1 need the equivalent.
            var deployPluginPath = Path.Combine(sysdevFolder,
                "F513CAE3-7194-4086-936C-02912EA0B352.Properties.xml");
            File.WriteAllText(deployPluginPath, deployPluginPropertiesXml);
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, deployPluginPath));

            // 3c. SystemDeviceProperties (E0601B81) — empty default. EAE creates
            //     this when first opening a device; ship it pre-empty so the
            //     project compiles cold (no "Properties XML not found" warning).
            var sysDevPropsPath = Path.Combine(sysdevFolder,
                "E0601B81-4A3A-4A96-B6C2-007BDC680D59.Properties.xml");
            if (!File.Exists(sysDevPropsPath))
            {
                File.WriteAllText(sysDevPropsPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                    "<SystemDeviceProperties xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
                    "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                    "xmlns=\"http://www.nxtControl.com/DeviceProperties\" />");
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysDevPropsPath));
            }

            // 3d. Simulation.Binding.xml — declares the LogicalDevice's
            //     deployment + archive service ports. Reference uses
            //       service F7C90C9D-… = Deployment      (M262=51499, M580=51500, BX1=51501)
            //       service 32B24F96-… = Archive Service (M262=51496, M580=51497, BX1=51498)
            //     Without this, EAE's deploy & diagnostic panel can't find the
            //     device and the Hardware Configuration tree leaves the HCF hidden.
            var simBindPath = Path.Combine(sysdevFolder, $"{sysdevId}.Simulation.Binding.xml");
            File.WriteAllText(simBindPath, BuildSimulationBindingXml(sysdevId,
                simulationBindingDeployPort, simulationBindingArchivePort));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, simBindPath));

            // 4. Topology Equipment JSON.
            //
            // FORCE-CLEAN write. File.WriteAllText already overwrites, but EAE
            // (or a manual user import of the reference Equipment JSON) can
            // leave the file in a hybrid state where two backplanes coexist
            // with two RuntimeDEO blocks — observed 2026-05-27 on the deployed
            // M580 file: it carried both BMEXBP0800 (IP 0.0.0.0) AND BMEXBP0400
            // (IP 192.168.1.20). EAE picks the FIRST RuntimeDEO it walks, the
            // 0.0.0.0 one, and filters M580 out of Deploy & Diagnostic because
            // it can't ping a null IP. Explicitly deleting before write
            // guarantees the post-condition is exactly the emitter's template:
            // one backplane, one RuntimeDEO, the configured IP.
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            Directory.CreateDirectory(topologyDir);
            var equipmentPath = Path.Combine(topologyDir, equipmentJsonName);
            if (File.Exists(equipmentPath))
            {
                try { File.Delete(equipmentPath); }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"{deviceName}: could not delete stale {equipmentJsonName} " +
                        $"before re-emit: {ex.Message}. The new content will overwrite " +
                        "but any merge corruption from a prior run may persist.");
                }
            }
            File.WriteAllText(equipmentPath, equipmentBuilder());
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, equipmentPath));

            // 5. Register Equipment JSON in TopologyManager.topologyproj.
            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded += M262TopologyEmitter.RegisterInTopologyProj(
                    topologyProj, new[] { equipmentJsonName });
            }
            else
            {
                result.Warnings.Add(
                    $"{deviceName}: TopologyManager.topologyproj missing — Equipment JSON " +
                    "written but not registered with TopologyManager build target.");
            }

            // 6. Register sysdev in dfbproj so EAE's project tree picks it up.
            var dfbproj = FindDfbproj(eaeRoot);
            if (dfbproj != null)
            {
                try
                {
                    int added = DfbprojRegistrar.RegisterSystemDevice(dfbproj, eaeRoot, sysdevPath);
                    result.DfbprojEntriesAdded += added;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"{deviceName}: dfbproj registration failed ({ex.Message}).");
                }
            }
        }

        /// <summary>
        /// DeployPlugin Properties XML — same content the reference uses for
        /// M262 + M580. EAE's Hardware Configuration plugin reads this file
        /// (plugin GUID F513CAE3-7194-4086-936C-02912EA0B352) to register the
        /// device's .hcf with the Hardware Configuration tree. Missing file =
        /// device card appears in Solution Explorer but no Hardware Config tab.
        /// </summary>
        static string BuildStandardDeployPluginPropertiesXml() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<SystemDeviceProperties xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://www.nxtControl.com/DeviceProperties\">\r\n" +
            "  <ComplexProperty Name=\"DeployPlugin\" Expanded=\"true\">\r\n" +
            "    <Property Name=\"ClearBeforeDeploy\" Value=\"True\" IsPassword=\"false\" />\r\n" +
            "  </ComplexProperty>\r\n" +
            "  <GroupProperty Name=\"Configuration\" Expanded=\"true\" Enabled=\"true\">\r\n" +
            "    <GroupProperty Name=\"Deploy\" Expanded=\"true\" Enabled=\"true\">\r\n" +
            "      <Property Name=\"AutoStart\" Value=\"True\" IsPassword=\"false\" />\r\n" +
            "    </GroupProperty>\r\n" +
            "    <GroupProperty Name=\"Boot\" Expanded=\"true\" Enabled=\"true\">\r\n" +
            "      <Property Name=\"BootMode\" Value=\"Run\" IsPassword=\"false\" />\r\n" +
            "    </GroupProperty>\r\n" +
            "  </GroupProperty>\r\n" +
            "</SystemDeviceProperties>";

        /// <summary>
        /// Soft_dPAC variant of the DeployPlugin Properties — adds the
        /// SetActiveProjectAsABootProject property the reference's BX1 uses.
        /// Required for the BX1 softdpac container to flip the deployed
        /// project into "boot" mode on next container start.
        /// </summary>
        static string BuildSoftDpacDeployPluginPropertiesXml() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<SystemDeviceProperties xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://www.nxtControl.com/DeviceProperties\">\r\n" +
            "  <ComplexProperty Name=\"DeployPlugin\" Expanded=\"true\">\r\n" +
            "    <Property Name=\"ClearBeforeDeploy\" Value=\"True\" IsPassword=\"false\" />\r\n" +
            "    <Property Name=\"SetActiveProjectAsABootProject\" Value=\"True\" IsPassword=\"false\" />\r\n" +
            "  </ComplexProperty>\r\n" +
            "  <GroupProperty Name=\"Configuration\" Expanded=\"true\" Enabled=\"true\">\r\n" +
            "    <GroupProperty Name=\"Deploy\" Expanded=\"true\" Enabled=\"true\">\r\n" +
            "      <Property Name=\"AutoStart\" Value=\"True\" IsPassword=\"false\" />\r\n" +
            "    </GroupProperty>\r\n" +
            "    <GroupProperty Name=\"Boot\" Expanded=\"true\" Enabled=\"true\">\r\n" +
            "      <Property Name=\"BootMode\" Value=\"Run\" IsPassword=\"false\" />\r\n" +
            "    </GroupProperty>\r\n" +
            "  </GroupProperty>\r\n" +
            "</SystemDeviceProperties>";

        /// <summary>
        /// LogicalDevice service-port binding XML. Two services every dPAC needs:
        /// Deployment (F7C90C9D-…) and Archive Service (32B24F96-…). EAE's
        /// Deploy &amp; Diagnostic panel + Hardware Configuration tab both look
        /// up the .hcf via these LogicalDevice service registrations.
        /// </summary>
        static string BuildSimulationBindingXml(string logicalDeviceId, int deployPort, int archivePort) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\r\n" +
            "<Bindings>\r\n" +
            $"  <LogicalDeviceBinding LogicalDeviceId=\"{logicalDeviceId}\">\r\n" +
            $"    <LogicalDeviceService ServiceId=\"F7C90C9D-BD8B-4D0B-B8DE-C659AF6EABCC\" LogicalPort=\"{deployPort}\" />\r\n" +
            $"    <LogicalDeviceService ServiceId=\"32B24F96-50F3-429E-9586-58A14DEB5DD5\" LogicalPort=\"{archivePort}\" />\r\n" +
            "  </LogicalDeviceBinding>\r\n" +
            "</Bindings>";

        /// <summary>
        /// Emits the per-PLC .sysdev with an INLINE &lt;Resources&gt;&lt;Resource&gt;
        /// declaration that mirrors the sibling .sysres file's ID + Name.
        ///
        /// <para>Why the inline block matters: EAE 24.1's M_dPAC / Soft_dPAC
        /// catalog templates auto-add a default EMB_RES_ECO Resource when the
        /// sysdev has no inline Resources block. With our sibling .sysres
        /// then layered on top, EAE counts BOTH and the compile fails with
        /// "Device &lt;name&gt; contains 2 instances of
        /// Runtime.Management.EMB_RES_ECO. The maximum allowed limit is 1
        /// instances per device." The M262 sysdev path (M262SysdevEmitter)
        /// has always emitted this inline block and never triggered the
        /// error; M580 + BX1 used to emit only &lt;FBNetwork/&gt; and tripped
        /// the catalog phantom. Mirror M262's pattern here.</para>
        ///
        /// <para>The inline &lt;Resource&gt; carries Name + Namespace + Type +
        /// ID only — no body. The sibling .sysres file provides the FBNetwork
        /// body. EAE merges them on the matching ID into one Resource entry
        /// in the compiled System tree.</para>
        /// </summary>
        static string BuildSysdevXml(string sysdevId, string name, string type,
                                     string resourceId, string resourceName) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            $"<Device xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ID=\"{sysdevId}\" Name=\"{name}\" Type=\"{type}\" Namespace=\"SE.DPAC\" Locked=\"false\" xmlns=\"{LibElNs}\">\r\n" +
            "  <Resources>\r\n" +
            $"    <Resource ID=\"{resourceId}\" Name=\"{resourceName}\" Type=\"EMB_RES_ECO\" Namespace=\"Runtime.Management\" />\r\n" +
            "  </Resources>\r\n" +
            "</Device>\r\n";

        static string BuildSysresXml(string resourceId, string name) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            $"<Resource xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ID=\"{resourceId}\" Name=\"{name}\" Type=\"EMB_RES_ECO\" Namespace=\"Runtime.Management\" xmlns=\"{LibElNs}\">\r\n" +
            "  <FBNetwork>\r\n" +
            "  </FBNetwork>\r\n" +
            "</Resource>\r\n";

        /// <summary>
        /// M580 dPAC equipment JSON — modelled on
        /// <c>SMC_Rig_Expo_withClamp/Topology/Equipment_M580dPAC_1.json</c>.
        /// X80 8-slot rack (BMEXBP0800) + BMX CPS 4002 PSU + BME D58 1020 CPU
        /// with ETH0/1/2/3 ports. Catalog refs must match values EAE 24.1's
        /// catalog actually knows — BMEXBP0400 + BMXCPS2010 were rendered as
        /// unknown placeholder boxes (warning triangle) next to the D58 chassis,
        /// which looked like a duplicate D58 1020 in Physical Views. Aligned
        /// with the reference rig on 2026-05-27.
        ///
        /// IP is taken from <c>cfg.M580TargetIp</c> (defaults to 192.168.1.20).
        /// The endpoint binds to <c>cfg.M580BroadcastDomainUuid</c> (defaults
        /// to the Default Network UUID) so EAE's hardware property editor shows
        /// the Logical Network / Subnet / Mask / Gateway fields populated.
        /// </summary>
        static string BuildM580EquipmentJson(string sysdevId, string solutionId,
                                             string targetIp, string broadcastDomainUuid)
        {
            return $$"""
            {
              "catalogReference": "M080_V01.00_01.00",
              "uuid": "{{M580EquipmentUuid}}",
              "identifier": "M580dPAC_1",
              "path": "Topology",
              "properties": [
                { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
                { "propertyName": "DomainTag",            "propertyValue": "{{solutionId}}" }
              ],
              "references": [
                { "diagramPath": "Physical Views", "x": -80, "y": -380 }
              ],
              "equipments": [
                {
                  "catalogReference": "BMEXBP0800_V01.00_01.00",
                  "uuid": "{{M580RackUuid}}",
                  "identifier": "BME XBP 0800 #0",
                  "path": "M580dPAC_1\\BME XBP 0800 #0",
                  "partNumber": "BME XBP 0800",
                  "equipments": [
                    {
                      "catalogReference": "BMXCPS4002_V01.00_01.00",
                      "uuid": "{{M580CpsUuid}}",
                      "identifier": "BMX CPS 4002 #P",
                      "path": "M580dPAC_1\\BME XBP 0800 #0\\BMX CPS 4002 #P",
                      "partNumber": "BMX CPS 4002"
                    },
                    {
                      "catalogReference": "BMED581020_V01.00_01.00",
                      "uuid": "{{M580CpuUuid}}",
                      "identifier": "BME D58 1020 #0",
                      "path": "M580dPAC_1\\BME XBP 0800 #0\\BME D58 1020 #0",
                      "partNumber": "BME D58 1020",
                      "components": [
                        {
                          "interfaces": [
                            {
                              "identifier": "seGmac0",
                              "disabled": false,
                              "physicalAddress": "",
                              "endpoints": [
                                {
                                  "identifier": "IP Address",
                                  "isReadOnly": false,
                                  "domainReadOnly": false,
                                  "ipAddress": "{{targetIp}}",
                                  "domain": "{{broadcastDomainUuid}}"
                                }
                              ]
                            }
                          ],
                          "ports": [
                            { "identifier": "BKP",  "side": "Default" },
                            { "identifier": "ETH1", "side": "Default" },
                            { "identifier": "ETH2", "side": "Default" },
                            { "identifier": "ETH3", "side": "Default" }
                          ],
                          "componentType": "EthernetDEO"
                        },
                        {
                          "endpoint": "seGmac0\\IP Address",
                          "connectionTypes": "None",
                          "componentType": "EthernetMasterDEO"
                        },
                        { "enabled": false, "securityMode": 0, "componentType": "SysLogClientDEO" },
                        { "mode": 0, "componentType": "CyberSecurityDEO" },
                        {
                          "uuid": "{{M580RuntimeUuid}}",
                          "typeId": "{{M580RuntimeTypeId}}",
                          "logicalDeviceId": "{{sysdevId}}",
                          "runtimeServices": [
                            { "identifier": "Deployment" },
                            { "identifier": "Archive Service", "logicalPortSecured": "0" }
                          ],
                          "componentType": "RuntimeDEO"
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        }

        /// <summary>
        /// BX1 equipment JSON — modelled on the reference
        /// <c>SMC_Rig_Expo_withClamp/Topology/Equipment_Workstation_1.json</c>.
        /// BX1 is a Soft_dPAC software runtime; the matching Physical Views
        /// catalog entry is <c>Workstation_V01.00_01.00</c> with a nested
        /// NIC card. The earlier <c>Softdpac_V01.00_01.00</c> attempt failed
        /// with "Unable to import topology" because that catalog reference
        /// only exists nested inside an HMIB1X container in EAE 24.1, never
        /// standalone. Workstation is the right top-level catalog for any
        /// PC-hosted softdpac runtime.
        /// </summary>
        static string BuildBX1WorkstationEquipmentJson(string sysdevId, string solutionId, string targetIp)
        {
            // typeId used by Workstation_1's RuntimeDEO in the reference —
            // distinct from SoftDpacTypeId (which is for the HMIB1X-nested case).
            const string WorkstationRuntimeTypeId = "422ee926-a34a-4ab5-9e8f-dce0782579f0";
            const string BX1NicUuid = "11111111-2222-3333-4444-000000000053";

            return $$"""
            {
              "catalogReference": "Workstation_V01.00_01.00",
              "uuid": "{{BX1EquipmentUuid}}",
              "identifier": "BX1",
              "path": "Topology",
              "properties": [
                { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
                { "propertyName": "CommCardReference",   "propertyValue": "" },
                { "propertyName": "DomainTag",            "propertyValue": "{{solutionId}}" }
              ],
              "references": [
                { "diagramPath": "Physical Views", "x": 320, "y": -380 }
              ],
              "equipments": [
                {
                  "catalogReference": "NIC_EAE_V01.00_01.00",
                  "uuid": "{{BX1NicUuid}}",
                  "identifier": "NIC_1",
                  "path": "BX1\\NIC_1",
                  "components": [
                    {
                      "interfaces": [
                        {
                          "identifier": "eno1",
                          "disabled": false,
                          "physicalAddress": "",
                          "endpoints": [
                            {
                              "identifier": "IP Address",
                              "isReadOnly": false,
                              "domainReadOnly": false,
                              "ipAddress": "{{targetIp}}",
                              "domain": "{{NoConfDomainUuid}}"
                            }
                          ]
                        }
                      ],
                      "ports": [
                        { "identifier": "Port1", "side": "Default" }
                      ],
                      "componentType": "EthernetDEO"
                    }
                  ]
                }
              ],
              "components": [
                {
                  "uuid": "{{BX1RuntimeUuid}}",
                  "typeId": "{{WorkstationRuntimeTypeId}}",
                  "logicalDeviceId": "{{sysdevId}}",
                  "identifier": "Runtime_1",
                  "runtimeServices": [
                    {
                      "identifier": "Deployment",
                      "endpoint": "NIC_1\\eno1\\IP Address",
                      "logicalPort": "61999",
                      "logicalPortSecured": "51443"
                    }
                  ],
                  "componentType": "RuntimeDEO"
                }
              ]
            }
            """;
        }

        /// <summary>
        /// Deletes a stale Topology Equipment JSON from disk and removes its
        /// <c>&lt;None Include=...&gt;</c> registration from
        /// <c>TopologyManager.topologyproj</c>. Idempotent — silently
        /// no-ops when the file or registration is already absent.
        /// </summary>
        static void CleanupStaleTopologyJson(string eaeRoot, string jsonName, EmitResult result)
        {
            try
            {
                var topologyDir = Path.Combine(eaeRoot, "Topology");
                var jsonPath = Path.Combine(topologyDir, jsonName);
                if (File.Exists(jsonPath))
                {
                    File.Delete(jsonPath);
                    result.Warnings.Add($"Deleted stale Topology JSON: {jsonName}");
                }
                var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
                if (File.Exists(topologyProj))
                {
                    var doc = XDocument.Load(topologyProj);
                    var ns = doc.Root!.GetDefaultNamespace();
                    var staleNodes = doc.Descendants(ns + "None")
                        .Where(e => string.Equals(
                            (string?)e.Attribute("Include"), jsonName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var n in staleNodes) n.Remove();
                    if (staleNodes.Count > 0) doc.Save(topologyProj);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Cleanup of {jsonName} failed: {ex.Message}");
            }
        }

        static string? FindDfbproj(string eaeRoot)
        {
            try
            {
                var iec = Path.Combine(eaeRoot, "IEC61499");
                if (!Directory.Exists(iec)) return null;
                return Directory.EnumerateFiles(iec, "*.dfbproj").FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads the resource identity an authored <c>.hcf</c> expects, so the
        /// emitted sysres can adopt it (otherwise EAE cannot bind the .hcf to a
        /// resource). Two scoping conventions appear in the SMC exports:
        /// <list type="bullet">
        ///   <item><b>GUID-scoped</b> (BX1's EtherNet/IP export): the
        ///   <c>&lt;DeviceHwConfigurationItem ResourceId="…"/&gt;</c> attribute
        ///   (also the head of its <c>{guid}.{fb}.EIP_*_Word_1</c> symlinks).
        ///   Returned as <c>GuidId</c>.</item>
        ///   <item><b>Name-scoped</b> (M580's X80 export): per-bit symlinks
        ///   <c>'RES0.M580IO.&lt;sym&gt;'</c> whose leading segment is the
        ///   resource NAME. Returned as <c>Name</c>.</item>
        /// </list>
        /// Either, both, or neither may be present; <c>null</c> where absent.
        /// Best-effort — never throws.
        /// </summary>
        static (string? GuidId, string? Name) ReadHcfResourceIdentity(string? hcfPath)
        {
            if (string.IsNullOrWhiteSpace(hcfPath) || !File.Exists(hcfPath))
                return (null, null);
            try
            {
                var doc = XDocument.Load(hcfPath);

                // GUID form: <DeviceHwConfigurationItem ResourceId="...">.
                var guid = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem")
                    ?.Attribute("ResourceId")?.Value;
                if (string.IsNullOrWhiteSpace(guid)) guid = null;

                // Name form: first quoted symlink 'NAME.GROUP.symbol' whose head
                // is a symbolic resource name (a 16-hex head is the GUID/EIP case
                // captured by the attribute above, so it is skipped here).
                string? name = null;
                foreach (var pv in doc.Descendants()
                    .Where(e => e.Name.LocalName == "ParameterValue"))
                {
                    var raw = (string?)pv.Attribute("Value");
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var t = raw.Trim().Trim('\'');
                    var firstDot = t.IndexOf('.');
                    if (firstDot <= 0) continue;
                    var head = t.Substring(0, firstDot);
                    var rest = t.Substring(firstDot + 1);
                    if (!rest.Contains('.')) continue;                       // need NAME.GROUP.sym
                    if (head.Length == 16 && head.All(Uri.IsHexDigit)) continue; // GUID head → skip
                    name = head;
                    break;
                }
                return (guid, name);
            }
            catch { return (null, null); }
        }
    }
}
