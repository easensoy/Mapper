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
        // M580 resource name — per-PLC, consistent with M262_RES / BX1_RES (no RES0 in the tree).
        //
        // History: an EAE compile error "Device M580 contains 2 instances of
        // Runtime.Management.EMB_RES_ECO" once forced a temporary "RES0" override. The root cause was a
        // stale RES0-named orphan .sysres left behind when a resource ID flipped (the same orphan that bit
        // M262), NOT a structural duplicate. Here the M580 ResourceId is a stable const, so the rename
        // touches only the Name attribute (no filename flip → no orphan), and SweepOrphanSysres clears any
        // pre-existing orphan on a Clean. The .hcf binds via Form-1 GUID triples (<resId>.<fbId>.<port>),
        // so it carries no name dependency. EAE compile is the rig-only confirmation; revert = "RES0".
        const string M580ResourceName = "M580_RES";
        const string BX1ResourceName  = "BX1_RES";

        // Topology Equipment UUIDs — stable so the JSON Equipment entries
        // don't churn between Test Runtime clicks (which would invalidate
        // any user-drawn wires on the Physical Views canvas).
        const string M580EquipmentUuid   = "11111111-2222-3333-4444-000000000040";
        const string M580RuntimeUuid     = "11111111-2222-3333-4444-000000000041";
        const string M580RackUuid        = "11111111-2222-3333-4444-000000000042";
        const string M580CpsUuid         = "11111111-2222-3333-4444-000000000043";
        const string M580CpuUuid         = "11111111-2222-3333-4444-000000000044";
        // BX1 HMIB1X equipment uuids — REUSE the reference SMC_Rig_Expo_withClamp's
        // exact uuids so the emitted Equipment_HMIB1X_1.json is byte-identical to the
        // reference (which imports cleanly). Only the solution-specific DomainTag and
        // the RuntimeDEO.logicalDeviceId (→ this project's BX1 sysdev) are substituted.
        const string BX1EquipmentUuid    = "49363b74-1a84-46c1-b4cd-93f02374daec"; // HMIB1X_1
        const string BX1ContainerUuid    = "37f5487c-396f-477a-a9ae-9c0476a4f772"; // Softdpac_1
        const string BX1RuntimeUuid      = "52c5633b-f50b-4bc4-8fbd-e035bc5dfffa"; // RuntimeDEO
        // EtherNetIPDevice equipment uuid — REUSE the reference's exact uuid so the
        // DTM Content artifacts (Topology\Content\<uuid>_FdtProject.prj /
        // _IOProfile.xml) can be copied verbatim from the reference (their content is
        // uuid-independent; only the filename carries the uuid). EAE's FDT importer
        // loads the Content by this uuid; without those two files the DtmDeviceDEO
        // aborts the whole topology import ("verify file format / Internal Server Error").
        const string BX1EtherNetIpUuid   = "49d2ea8e-3a4f-4ead-add4-ec4ba00d5239";

        // softdpacDeviceNet docker-vlan domain shared by the HMIB1X host's
        // dockerVlans declaration and the nested Softdpac container's eth0
        // endpoint (same value the reference SMC_Rig_Expo_withClamp uses). It is
        // self-declared in the HMIB1X Equipment JSON, so reusing the reference
        // uuid is safe — EAE creates the docker network from the dockerVlans block.
        const string Bx1SoftdpacDomainUuid = "db72f221-ece1-4b82-8132-731ce655044e";
        // EtherNet/IP scanner id of the BX1 softdpac's EthernetMasterDEO. Must
        // match the associatedScannerId on the EtherNetIPDevice AND the <ID> in
        // the BX1 EtherNet/IP .hcf (270AFDB7F209BFE8) so EAE binds the scanned
        // device to the scanner.
        const string Bx1ScannerId = "270AFDB7F209BFE8";
        // BX1 remote-I/O coupler (TM3BC_EtherNetIP) address — the covers' physical
        // I/O island, scanned by the softdpac over EtherNet/IP (reference = .210).
        const string Bx1IoDeviceIp = "192.168.1.210";

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
            // 2026-06-08: BX1 is emitted in the REFERENCE HMIB1X form as
            // Equipment_HMIB1X_1.json — BYTE-IDENTICAL to the reference's BX1 device
            // (host panel .209 + nested Softdpac container .151), reference uuids,
            // identifier "HMIB1X_1". Sweep BOTH the old Workstation form AND the
            // interim "Equipment_BX1.json" name. The interim "BX1" equipment
            // identifier COLLIDED with the BX1 sysdev Name "BX1" (every working
            // device keeps equipment-identifier != sysdev-name: M580dPAC_1 != M580,
            // reference HMIB1X_1 != BX1) — the cause of the topology-import 500.
            // Equipment_HMIB1X_1.json is now the ACTIVE file (no longer cleaned here;
            // EmitOnePlc force-cleans it just before re-writing).
            CleanupStaleTopologyJson(eaeRoot, "Equipment_Workstation_BX1.json", result);
            CleanupStaleTopologyJson(eaeRoot, "Equipment_BX1.json",            result);
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
            // Resolve the BX1 EtherNet/IP .hcf robustly. The configured path
            // historically defaulted to "BX1IO.hcf" — a file that never existed;
            // the real EAE export is "BX1IO.ethernetip.hcf" — so the copy silently
            // no-op'd and BX1 shipped with NO hardware config ("pass the BX1 hcf").
            // Fall back to <IoFolder>\BX1IO.ethernetip.hcf so it is always passed.
            var bx1HcfPath = ResolveBx1HcfPath(cfg);
            var bx1Ident = ReadHcfResourceIdentity(bx1HcfPath);

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
                // M580 now carries MqttConn_M580 — write the SecurityApp/InsecureApplication override so the
                // device can accept a plain mqtt:// URL (else MQTT_CONNECTION faults RC101). EAE caches the
                // device model, so the user still enables 'Security -> Insecure Application' on the M580 device
                // once (the same one-time step BX1 needed); this keeps the file consistent. Insecure MQTT mode.
                deployPluginPropertiesXml: BuildStandardDeployPluginPropertiesXml(
                    cfg.MqttPublishEnabled && !cfg.MqttSecureTls),
                simulationBindingDeployPort: 51500,
                simulationBindingArchivePort: 51497);

            EmitOnePlc(cfg, eaeRoot, systemGuidDir, result,
                sysdevId: BX1SysdevId,
                deviceName: "BX1",
                deviceType: "Soft_dPAC",
                resourceId: bx1ResourceId,
                resourceName: BX1ResourceName,
                hcfTemplatePath: bx1HcfPath,
                equipmentJsonName: "Equipment_HMIB1X_1.json",
                equipmentBuilder: () => BuildBX1HmiB1XEquipmentJson(
                    BX1SysdevId, solutionId, cfg.BX1TargetIp, cfg.BX1HostIp),
                // BX1 is the only PLC that runs MQTT; with a plain mqtt:// broker the device must
                // ALLOW insecure application config or MQTT_CONNECTION faults RC101. Enable it here
                // (== the EAE GUI "Security -> Insecure Application -> Enable") in insecure MQTT mode.
                deployPluginPropertiesXml: BuildSoftDpacDeployPluginPropertiesXml(
                    cfg.MqttPublishEnabled && !cfg.MqttSecureTls),
                simulationBindingDeployPort: 51501,
                simulationBindingArchivePort: 51498);

            // BX1 remote-I/O coupler (TM3BC_EtherNetIP @ Bx1IoDeviceIp) scanned by
            // the BX1 softdpac's EtherNet/IP master (Bx1ScannerId). Matches the
            // reference Equipment_EtherNetIPDevice_1.json + its DTM Content artifacts.
            // ISOLATION (cfg.EmitBx1EtherNetIpDevice): a DtmDeviceDEO makes EAE's FDT
            // framework load an FdtProject.prj on topology import — a project copied
            // from another solution can throw an immediate server 500 ("Unable to
            // import topology / Internal Server Error"). Held OUT by default until the
            // HMIB1X import + login is confirmed; when OFF, SWEEP any previously-
            // deployed copy (equipment + Content + topologyproj registrations) so the
            // topology imports clean.
            if (cfg.EmitBx1EtherNetIpDevice)
            {
                EmitBx1EtherNetIpDevice(cfg, eaeRoot, result, solutionId);
                // The EtherNet/IP scanner (EIPSCANNER2) in the BX1 .hcf instantiates
                // the generated coupler FB type Main.TM3BC_Ethe_yYhtt9jWKUOJs. Without
                // its saved .fbt the BX1 dPAC fails to compile with
                // "Type 'Main.TM3BC_Ethe_yYhtt9jWKUOJs' is undefined (ERR_NO_SUCH_TYPE)".
                // Deploy the saved device type (the gate types it pulls in — AND_*,
                // NOT_*, DS_SELECTX_* — are compiler-generated by EAE, not shipped).
                DeployBx1EtherNetIpType(cfg, eaeRoot, result);
                // The compiled EtherNet/IP scanner (EIPSCANNER2.xml) is built by EAE from
                // the HwConfiguration project's DEVICE MODEL — NOT from the .hcf/.sysres or
                // the deployed scanner XML (that is build output EAE re-emits each compile).
                // Without the TM3BC_Ethe_* device-model folders + the EIPSolutionsV2 scanner
                // config registered in HwConfiguration.hwconfigproj, EAE's HwConfiguration
                // model has no TM3BC device, so the Deploy export is an EMPTY scanner (333
                // bytes, no .210 buscoupler) and the cover I/O never reaches the coupler —
                // even though the app model shows the scanner FB and EIP_Output_Word packs
                // correctly (split-brain root-caused 2026-06-09, verified fixed: compiled
                // EIPSCANNER2.xml became 1200 bytes incl. 192.168.1.210). BX1-only.
                DeployBx1HwConfigScannerModel(cfg, eaeRoot, result);
            }
            else
            {
                CleanupStaleTopologyJson(eaeRoot, "Equipment_EtherNetIPDevice_1.json", result);
                CleanupStaleTopologyJson(eaeRoot,
                    Path.Combine("Content", $"{BX1EtherNetIpUuid}_FdtProject.prj"), result);
                CleanupStaleTopologyJson(eaeRoot,
                    Path.Combine("Content", $"{BX1EtherNetIpUuid}_IOProfile.xml"), result);
                SweepBx1EtherNetIpType(eaeRoot, result);
                SweepBx1HwConfigScannerModel(eaeRoot, result);
                result.Warnings.Add(
                    "[BX1] EtherNet/IP field device HELD OUT (cfg.EmitBx1EtherNetIpDevice=false) — " +
                    "isolating the topology-import failure; equipment + FDT Content + device type swept.");
            }

            // Sweep stale sysres references out of the .dfbproj. A prior deploy
            // that used the OLD BX1 resource id (C9F2A4B7E1D3F5A8, before the
            // .hcf-driven realignment to 78E9CD3D27851B64) left a dangling
            // <None Include="…\C9F2A4B7E1D3F5A8.sysres"> entry. EAE's Solution
            // Integrity lists it as a Missing Project File and aborts the
            // topology import ("Unable to import topology / Internal Server
            // Error"). StripStaleSysresStemEntries removes every dfbproj entry
            // whose .sysres stem (or sister-folder stem) has no matching file on
            // disk — so only the live resources remain.
            var dfbprojForStrip = FindDfbproj(eaeRoot);
            if (dfbprojForStrip != null)
            {
                int stripped = DfbprojRegistrar.StripStaleSysresStemEntries(dfbprojForStrip, eaeRoot);
                if (stripped > 0)
                    result.Warnings.Add(
                        $"Removed {stripped} stale sysres reference(s) from the .dfbproj " +
                        "(resource id realigned to the .hcf ResourceId).");
            }

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
            else
            {
                // Re-align an EXISTING resource's Name (e.g. a prior-deploy "RES0" -> "M580_RES")
                // without disturbing its FBNetwork — the file is preserved to keep mirrored FB content.
                AlignSysresResourceName(sysresPath, resourceName, deviceName, result);
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
                File.WriteAllText(sysDevPropsPath, BuildEmptySystemDeviceProps());
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
        static string BuildStandardDeployPluginPropertiesXml(bool enableInsecureApp = false) =>
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
            // Same SecurityApp/InsecureApplication override BX1 uses (BuildSoftDpacDeployPluginPropertiesXml):
            // M580 also carries a MqttConn_M580 MQTT_CONNECTION, so with a plain mqtt:// broker the device
            // must allow insecure app config or it faults RC101 (secure-by-default). Gated on insecure MQTT mode.
            (enableInsecureApp
                ? "    <GroupProperty Name=\"SecurityApp\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                  "      <GroupProperty Name=\"InsecureApplication\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                  "        <Property Name=\"Enable\" Value=\"True\" IsPassword=\"false\" />\r\n" +
                  "      </GroupProperty>\r\n" +
                  "    </GroupProperty>\r\n"
                : string.Empty) +
            "  </GroupProperty>\r\n" +
            "</SystemDeviceProperties>";

        /// <summary>
        /// Soft_dPAC variant of the DeployPlugin Properties — adds the
        /// SetActiveProjectAsABootProject property the reference's BX1 uses.
        /// Required for the BX1 softdpac container to flip the deployed
        /// project into "boot" mode on next container start.
        ///
        /// When <paramref name="enableInsecureApp"/> is true it also emits the
        /// <c>Configuration → SecurityApp → InsecureApplication → Enable=True</c> override —
        /// the SAME device-property the EAE GUI writes under "Security → Insecure Application →
        /// Enable" (verified byte-for-byte against the reference TrainingIIoT Properties.xml).
        /// WITHOUT it, EAE's MQTT runtime is secure-by-default and a plain <c>mqtt://</c>
        /// MQTT_CONNECTION FAULTS with ReturnCode 101 ("Secure URL required for secure
        /// application"); WITH it, EAE generates <c>Configuration.SecurityApp.InsecureApplication.
        /// Enable = "true"</c> in the deployed runtime.config and the plain connection reaches
        /// ReturnCode 0. Gated on insecure MQTT mode (publish on + not <c>MqttSecureTls</c>);
        /// secure mode (mqtts:// + real TLS) needs NO insecure override, so it is omitted there.
        /// </summary>
        static string BuildSoftDpacDeployPluginPropertiesXml(bool enableInsecureApp) =>
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
            (enableInsecureApp
                ? "    <GroupProperty Name=\"SecurityApp\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                  "      <GroupProperty Name=\"InsecureApplication\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                  "        <Property Name=\"Enable\" Value=\"True\" IsPassword=\"false\" />\r\n" +
                  "      </GroupProperty>\r\n" +
                  "    </GroupProperty>\r\n"
                : string.Empty) +
            "  </GroupProperty>\r\n" +
            "</SystemDeviceProperties>";

        /// <summary>
        /// LogicalDevice service-port binding XML. Two services every dPAC needs:
        /// Deployment (F7C90C9D-…) and Archive Service (32B24F96-…). EAE's
        /// Deploy &amp; Diagnostic panel + Hardware Configuration tab both look
        /// up the .hcf via these LogicalDevice service registrations.
        /// </summary>
        internal static string BuildSimulationBindingXml(string logicalDeviceId, int deployPort, int archivePort) =>
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
        // internal so M262SysdevEmitter's bootstrap-from-empty path reuses the
        // EXACT same proven sysdev/sysres XML the M580/BX1 path emits.
        internal static string BuildSysdevXml(string sysdevId, string name, string type,
                                     string resourceId, string resourceName) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            $"<Device xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ID=\"{sysdevId}\" Name=\"{name}\" Type=\"{type}\" Namespace=\"SE.DPAC\" Locked=\"false\" xmlns=\"{LibElNs}\">\r\n" +
            "  <Resources>\r\n" +
            $"    <Resource ID=\"{resourceId}\" Name=\"{resourceName}\" Type=\"EMB_RES_ECO\" Namespace=\"Runtime.Management\" />\r\n" +
            "  </Resources>\r\n" +
            "</Device>\r\n";

        internal static string BuildSysresXml(string resourceId, string name) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            $"<Resource xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ID=\"{resourceId}\" Name=\"{name}\" Type=\"EMB_RES_ECO\" Namespace=\"Runtime.Management\" xmlns=\"{LibElNs}\">\r\n" +
            "  <FBNetwork>\r\n" +
            "  </FBNetwork>\r\n" +
            "</Resource>\r\n";

        // Set an existing .sysres root Resource Name (idempotent — no-op when already correct),
        // preserving its FBNetwork. Used to migrate a prior-deploy "RES0" M580 sysres to "M580_RES".
        static void AlignSysresResourceName(string sysresPath, string resourceName, string deviceName, EmitResult result)
        {
            try
            {
                var doc = XDocument.Load(sysresPath, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                var current = (string?)root.Attribute("Name");
                if (string.Equals(current, resourceName, StringComparison.Ordinal)) return;
                root.SetAttributeValue("Name", resourceName);
                doc.Save(sysresPath);
                result.Warnings.Add($"{deviceName}: sysres resource Name '{current}' -> '{resourceName}'.");
            }
            catch { /* best-effort — emit pipeline continues even if the sysres rewrite fails */ }
        }

        /// <summary>Empty SystemDeviceProperties (E0601B81 plugin) — shipped pre-empty
        /// so the project compiles cold. Shared with M262SysdevEmitter's bootstrap.</summary>
        internal static string BuildEmptySystemDeviceProps() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<SystemDeviceProperties xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"http://www.nxtControl.com/DeviceProperties\" />";

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
        /// BX1 equipment JSON in the REFERENCE <c>HMIB1X</c> form — an exact
        /// structural match of
        /// <c>SMC_Rig_Expo_withClamp/Topology/Equipment_HMIB1X_1.json</c>.
        ///
        /// <para>BX1 is a Harmony HMIB1X industrial panel (host at
        /// <paramref name="hostIp"/> = 192.168.1.209) that HOSTS a nested
        /// <c>HMIB1X_SoftdpacContainer</c> running the BX1 softdpac runtime at
        /// <paramref name="softpacIp"/> = 192.168.1.151. The nested container's
        /// RuntimeDEO carries <c>logicalDeviceId = </c><paramref name="sysdevId"/>,
        /// binding it to the BX1 .sysdev. EAE deploys/logs in to the softdpac IP
        /// (.151).</para>
        ///
        /// <para>The earlier <c>Workstation_V01.00_01.00</c> form was WRONG: the
        /// Workstation catalog is the local engineering PC, so EAE resolved the
        /// runtime to 127.0.0.1 and the deploy failed with "cannot connect to
        /// device 'BX1' / IP 127.0.0.1 port 51443". The HMIB1X form is what the
        /// reference rig uses and what the physical panels (.209 host, .151
        /// softdpac) on the network actually are.</para>
        ///
        /// <para>The HMIB1X host declares the <c>softdpacDeviceNet</c> docker vlan
        /// (SoftdpacManagerDEO) on <see cref="Bx1SoftdpacDomainUuid"/>; the nested
        /// container's eth0 endpoint references the same domain (domainReadOnly =
        /// true — the docker network assigns the .151). The container's
        /// EthernetMasterDEO scanner id (<see cref="Bx1ScannerId"/>) ties the
        /// EtherNet/IP scanned cover-I/O coupler to this softdpac.</para>
        /// </summary>
        static string BuildBX1HmiB1XEquipmentJson(string sysdevId, string solutionId,
            string softpacIp, string hostIp)
        {
            return $$"""
            {
              "catalogReference": "HMIB1X_V01.00_01.00",
              "uuid": "{{BX1EquipmentUuid}}",
              "identifier": "HMIB1X_1",
              "path": "Topology",
              "properties": [
                { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
                { "propertyName": "DomainTag",            "propertyValue": "{{solutionId}}" }
              ],
              "references": [
                { "diagramPath": "Physical Views", "x": 112.30403451708418, "y": -352.30162018013522 }
              ],
              "equipments": [
                {
                  "catalogReference": "HMIB1X_SoftdpacContainer_V01.00_01.00",
                  "uuid": "{{BX1ContainerUuid}}",
                  "identifier": "Softdpac_1",
                  "path": "HMIB1X_1\\Softdpac_1",
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
                              "domainReadOnly": true,
                              "ipAddress": "{{softpacIp}}",
                              "domain": "{{Bx1SoftdpacDomainUuid}}"
                            }
                          ]
                        }
                      ],
                      "ports": [
                        { "identifier": "Port0", "side": "Default" }
                      ],
                      "componentType": "EthernetDEO"
                    },
                    {
                      "scannerId": "{{Bx1ScannerId}}",
                      "endpoint": "eth0\\IP Address",
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
                      "uuid": "{{BX1RuntimeUuid}}",
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
                          "domain": "{{NoConfDomainUuid}}"
                        }
                      ]
                    },
                    {
                      "identifier": "eth1",
                      "disabled": false,
                      "physicalAddress": "",
                      "endpoints": [
                        {
                          "identifier": "IP Address",
                          "isReadOnly": false,
                          "domainReadOnly": false,
                          "ipAddress": "0.0.0.0",
                          "domain": "{{NoConfDomainUuid}}"
                        }
                      ]
                    }
                  ],
                  "ports": [
                    { "identifier": "LAN1", "side": "Default" },
                    { "identifier": "LAN2", "side": "Default" }
                  ],
                  "componentType": "EthernetDEO"
                },
                {
                  "preferredPrimary": false,
                  "dockerImages": [
                    { "identifier": "softdpac", "version": "" }
                  ],
                  "dockerVlans": [
                    {
                      "identifier": "softdpacDeviceNet",
                      "type": 0,
                      "domain": "{{Bx1SoftdpacDomainUuid}}",
                      "interface": "eth0",
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
                },
                {
                  "mode": 0,
                  "componentType": "CyberSecurityDEO"
                }
              ]
            }
            """;
        }

        /// <summary>
        /// EtherNet/IP remote-I/O coupler equipment JSON — an exact structural
        /// match of <c>SMC_Rig_Expo_withClamp/Topology/Equipment_EtherNetIPDevice_1.json</c>.
        /// A <c>TM3BC_EtherNetIP</c> bus coupler (the Cover PnP physical I/O island)
        /// at <paramref name="deviceIp"/> = 192.168.1.210, scanned by the BX1
        /// softdpac's EtherNet/IP master (<paramref name="scannerId"/>). Topology-
        /// only: a field device has no logical runtime, so there is no sysdev/sysres.
        /// </summary>
        static string BuildEtherNetIpDeviceEquipmentJson(string solutionId, string deviceIp, string scannerId)
        {
            return $$"""
            {
              "catalogReference": "GenericEthernetIPFieldDevice_V01.00_01.00",
              "uuid": "{{BX1EtherNetIpUuid}}",
              "identifier": "EtherNetIPDevice_1",
              "path": "Topology",
              "properties": [
                { "propertyName": "IsUnderConstruction", "propertyValue": "False" },
                { "propertyName": "DomainTag",            "propertyValue": "{{solutionId}}" }
              ],
              "references": [
                { "diagramPath": "Physical Views", "x": 109, "y": -104 }
              ],
              "components": [
                {
                  "fdtProjectIdentifiers": [ "FdtProject" ],
                  "catalogDtmIsInitialized": true,
                  "catalogDeviceName": "TM3BC_EtherNetIP Revision 2.3 (from EDS)",
                  "catalogDeviceVersion": "2.3",
                  "catalogDeviceVendor": "Schneider Electric",
                  "catalogDtmName": "Generic EDS Device DTM",
                  "catalogDtmVendor": "Schneider Electric",
                  "catalogDtmVersion": "1.15.1.0",
                  "componentType": "DtmDeviceDEO"
                },
                {
                  "interfaces": [
                    {
                      "identifier": "Embedded Interface",
                      "disabled": false,
                      "physicalAddress": "",
                      "endpoints": [
                        {
                          "identifier": "IP Address",
                          "isReadOnly": false,
                          "domainReadOnly": false,
                          "ipAddress": "{{deviceIp}}",
                          "domain": "{{NoConfDomainUuid}}"
                        }
                      ]
                    }
                  ],
                  "ports": [
                    { "identifier": "Port1", "side": "Default" },
                    { "identifier": "Port2", "side": "Default" }
                  ],
                  "componentType": "EthernetDEO"
                },
                {
                  "associatedScannerId": "{{scannerId}}",
                  "associatedHwCatType": "Generic",
                  "ioProfileIdentifiers": [ "IOProfile" ],
                  "componentType": "EthernetScannedDeviceDEO"
                }
              ]
            }
            """;
        }

        /// <summary>
        /// Resolves the BX1 EtherNet/IP .hcf. The configured path historically
        /// defaulted to <c>BX1IO.hcf</c> — a file that never existed (the real EAE
        /// export is <c>BX1IO.ethernetip.hcf</c>), so the copy silently no-op'd and
        /// BX1 shipped with no hardware config. Falls back to
        /// <c>&lt;IoFolder&gt;\BX1IO.ethernetip.hcf</c> then the absolute IO path so
        /// the BX1 hcf is always passed regardless of mapper_config.json.
        /// </summary>
        static string ResolveBx1HcfPath(MapperConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.BX1HcfTemplatePath) &&
                File.Exists(cfg.BX1HcfTemplatePath))
                return cfg.BX1HcfTemplatePath;

            var ioFolder = !string.IsNullOrWhiteSpace(cfg.IoFolderPath)
                ? cfg.IoFolderPath : @"C:\VueOneMapper\IO";
            var candidate = Path.Combine(ioFolder, "BX1IO.ethernetip.hcf");
            if (File.Exists(candidate)) return candidate;

            return @"C:\VueOneMapper\IO\BX1IO.ethernetip.hcf";
        }

        /// <summary>
        /// Emits the BX1 EtherNet/IP remote-I/O coupler (a DtmDeviceDEO field
        /// device): the Equipment JSON, its TWO required DTM Content artifacts
        /// (<c>Topology\Content\&lt;uuid&gt;_FdtProject.prj</c> +
        /// <c>&lt;uuid&gt;_IOProfile.xml</c> — copied verbatim from the reference,
        /// their content is uuid-independent), and the TopologyManager.topologyproj
        /// registrations for all three. The Content artifacts are MANDATORY: EAE's
        /// FDT importer loads them by the device uuid, and without them the whole
        /// topology import aborts with "Unable to import topology / verify file
        /// format / Internal Server Error". A field device has no logical runtime,
        /// so there is no sysdev/sysres.
        /// </summary>
        static void EmitBx1EtherNetIpDevice(MapperConfig cfg, string eaeRoot,
            EmitResult result, string solutionId)
        {
            const string EquipmentJsonName = "Equipment_EtherNetIPDevice_1.json";
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            Directory.CreateDirectory(topologyDir);

            // 1. Equipment JSON (force-clean).
            var equipmentPath = Path.Combine(topologyDir, EquipmentJsonName);
            if (File.Exists(equipmentPath))
            {
                try { File.Delete(equipmentPath); }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"{EquipmentJsonName}: could not delete stale copy before re-emit: {ex.Message}");
                }
            }
            File.WriteAllText(equipmentPath,
                BuildEtherNetIpDeviceEquipmentJson(solutionId, Bx1IoDeviceIp, Bx1ScannerId));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, equipmentPath));

            // 2. DTM Content artifacts (copied verbatim from the IO-folder templates).
            var contentDir = Path.Combine(topologyDir, "Content");
            Directory.CreateDirectory(contentDir);
            var (prjTemplate, xmlTemplate) = ResolveEtherNetIpContentTemplates(cfg);
            var registerNames = new List<string> { EquipmentJsonName };

            void CopyContent(string template, string suffix)
            {
                var destName = $"{BX1EtherNetIpUuid}_{suffix}";
                if (string.IsNullOrEmpty(template) || !File.Exists(template))
                {
                    result.Warnings.Add(
                        $"EtherNetIPDevice: DTM content template for '{suffix}' not found at " +
                        $"'{template ?? "<unset>"}' — the device will FAIL to import. Expected " +
                        "BX1_EtherNetIP_FdtProject.prj / BX1_EtherNetIP_IOProfile.xml in the IO folder.");
                    return;
                }
                var dest = Path.Combine(contentDir, destName);
                File.Copy(template, dest, overwrite: true);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, dest));
                registerNames.Add(Path.Combine("Content", destName));
            }
            CopyContent(prjTemplate, "FdtProject.prj");
            CopyContent(xmlTemplate, "IOProfile.xml");

            // 3. Register equipment + content in topologyproj.
            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
                result.TopologyProjEntriesAdded +=
                    M262TopologyEmitter.RegisterInTopologyProj(topologyProj, registerNames);
            else
                result.Warnings.Add(
                    "EtherNetIPDevice: TopologyManager.topologyproj missing — equipment + content " +
                    "written but not registered with TopologyManager build target.");
        }

        /// <summary>
        /// The single SAVED FB type the BX1 EtherNet/IP scanner needs. Generated once
        /// by EAE's FDT/DTM for the TM3BC coupler and committed to the Template Library
        /// (IEC61499 + HMI subfolders). Its name is referenced verbatim by the BX1
        /// <c>.hcf</c> (which we copy from the reference), so the saved-type name and the
        /// scanner's instance type stay in lock-step.
        /// </summary>
        const string Bx1EtherNetIpDeviceType = "TM3BC_Ethe_yYhtt9jWKUOJs";

        /// <summary>
        /// Deploys the saved EtherNet/IP coupler FB type (<see cref="Bx1EtherNetIpDeviceType"/>)
        /// from <c>{TemplateLibrary}\EtherNetIP\</c> into the EAE project: copies the
        /// <c>IEC61499\&lt;type&gt;\</c> folder (.fbt/.cfg/_HMI.fbt) and the
        /// <c>HMI\&lt;type&gt;\</c> folder (faceplate sources referenced by the .cfg), then
        /// registers the four dfbproj entries. Idempotent (overwrites files, idempotent
        /// dfbproj add). The compiler-generated gate types (AND_*, NOT_*, DS_SELECTX_*)
        /// are produced by EAE at compile — not copied here.
        /// </summary>
        static void DeployBx1EtherNetIpType(MapperConfig cfg, string eaeRoot, EmitResult result)
        {
            try
            {
                var libRoot = !string.IsNullOrWhiteSpace(cfg.TemplateLibraryPath)
                    ? cfg.TemplateLibraryPath : @"C:\VueOneMapper\Template Library";
                var srcIec = Path.Combine(libRoot, "EtherNetIP", "IEC61499", Bx1EtherNetIpDeviceType);
                var srcHmi = Path.Combine(libRoot, "EtherNetIP", "HMI", Bx1EtherNetIpDeviceType);
                if (!Directory.Exists(srcIec))
                {
                    result.Warnings.Add(
                        $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType}' NOT found in the " +
                        $"Template Library ('{srcIec}'). BX1 will fail to compile (ERR_NO_SUCH_TYPE). " +
                        "Stage it from the reference project's IEC61499 + HMI folders.");
                    return;
                }

                var dstIec = Path.Combine(eaeRoot, "IEC61499", Bx1EtherNetIpDeviceType);
                var dstHmi = Path.Combine(eaeRoot, "HMI", Bx1EtherNetIpDeviceType);
                CopyDirectory(srcIec, dstIec);
                if (Directory.Exists(srcHmi)) CopyDirectory(srcHmi, dstHmi);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, dstIec));

                var dfbproj = FindDfbproj(eaeRoot);
                if (dfbproj != null)
                {
                    int added = DfbprojRegistrar.RegisterHardwareDeviceCat(dfbproj, Bx1EtherNetIpDeviceType);
                    result.Warnings.Add(added > 0
                        ? $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType}' deployed + registered " +
                          $"({added} dfbproj entr{(added == 1 ? "y" : "ies")}); gate types compile-generated by EAE."
                        : $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType}' deployed (dfbproj already current).");
                }
                else
                {
                    result.Warnings.Add(
                        $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType}' copied but no .dfbproj " +
                        "found to register it — BX1 may not compile.");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP device type deploy failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sweeps the saved EtherNet/IP coupler FB type — deletes its IEC61499 + HMI
        /// folders and removes its dfbproj registrations — when the EtherNet/IP device is
        /// held out (cfg.EmitBx1EtherNetIpDevice false). Idempotent.
        /// </summary>
        static void SweepBx1EtherNetIpType(string eaeRoot, EmitResult result)
        {
            try
            {
                var dstIec = Path.Combine(eaeRoot, "IEC61499", Bx1EtherNetIpDeviceType);
                var dstHmi = Path.Combine(eaeRoot, "HMI", Bx1EtherNetIpDeviceType);
                if (Directory.Exists(dstIec)) Directory.Delete(dstIec, recursive: true);
                if (Directory.Exists(dstHmi)) Directory.Delete(dstHmi, recursive: true);
                var dfbproj = FindDfbproj(eaeRoot);
                if (dfbproj != null)
                    DfbprojRegistrar.UnregisterHardwareDeviceCat(dfbproj, Bx1EtherNetIpDeviceType);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP device type sweep failed: {ex.Message}");
            }
        }

        // ── BX1 EtherNet/IP scanner HwConfiguration device model ───────────────────
        // EAE compiles EIPSCANNER2.xml from THESE source files (the HwConfiguration
        // device model), NOT from the .hcf/.sysres or the deployed scanner XML.
        const string Bx1HwConfigScannerId = "270AFDB7F209BFE8";
        static readonly string[] Bx1Tm3bcModelFolders =
            { "TM3BC_Ethe_R1C9LFqq0OfJh", "TM3BC_Ethe_yYhtt9jWKUOJs" };

        /// <summary>
        /// Deploys the BX1 EtherNet/IP scanner's HwConfiguration DEVICE MODEL — the
        /// TM3BC_Ethe_* device-model folders (.prop.cs/.prop.xml/.script.cs) + the
        /// EIPSolutionsV2\&lt;scannerId&gt;\scanner.xml + scanner_items.xml — and registers
        /// them in HwConfiguration.hwconfigproj. EAE compiles EIPSCANNER2.xml from this
        /// model; without it the scanner export is EMPTY (no .210 buscoupler) and the
        /// cover I/O never reaches the coupler. BX1-only (M262/M580 have no EtherNet/IP
        /// scanner). Acceptance: compiled EIPSCANNER2.xml ~1200 bytes incl. 192.168.1.210.
        /// </summary>
        static void DeployBx1HwConfigScannerModel(MapperConfig cfg, string eaeRoot, EmitResult result)
        {
            try
            {
                var libRoot = !string.IsNullOrWhiteSpace(cfg.TemplateLibraryPath)
                    ? cfg.TemplateLibraryPath : @"C:\VueOneMapper\Template Library";
                var srcHc = Path.Combine(libRoot, "EtherNetIP", "HwConfiguration");
                var dstHc = Path.Combine(eaeRoot, "HwConfiguration");
                if (!Directory.Exists(srcHc))
                {
                    result.Warnings.Add(
                        $"[BX1] EtherNet/IP HwConfiguration device model NOT in the Template Library ('{srcHc}') — " +
                        "EAE will compile an EMPTY EIPSCANNER2.xml (no .210 buscoupler) and the cover I/O will not " +
                        "reach the coupler. Stage TM3BC_Ethe_* + EIPSolutionsV2 from the reference HwConfiguration.");
                    return;
                }
                if (!Directory.Exists(dstHc))
                {
                    result.Warnings.Add($"[BX1] no HwConfiguration project at '{dstHc}' — cannot deploy the EtherNet/IP scanner model.");
                    return;
                }

                var subs = new List<string> { Path.Combine("EIPSolutionsV2", Bx1HwConfigScannerId) };
                subs.AddRange(Bx1Tm3bcModelFolders);
                foreach (var sub in subs)
                {
                    var s = Path.Combine(srcHc, sub);
                    if (Directory.Exists(s)) CopyDirectory(s, Path.Combine(dstHc, sub));
                }

                var hwproj = Path.Combine(dstHc, "HwConfiguration.hwconfigproj");
                int reg = RegisterBx1HwConfigScannerModel(hwproj);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot,
                    Path.Combine(dstHc, "EIPSolutionsV2", Bx1HwConfigScannerId, "scanner.xml")));
                result.Warnings.Add(
                    $"[BX1] EtherNet/IP HwConfiguration device model deployed (TM3BC_Ethe_* + EIPSolutionsV2 scanner; " +
                    $"{reg} hwconfigproj entr{(reg == 1 ? "y" : "ies")}). EAE compiles a POPULATED EIPSCANNER2.xml " +
                    "(acceptance: ~1200 bytes incl. 192.168.1.210).");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP HwConfiguration model deploy failed: {ex.Message}");
            }
        }

        /// <summary>Removes the BX1 EtherNet/IP scanner HwConfiguration model (folders +
        /// hwconfigproj entries) when the EtherNet/IP device is held out. Idempotent.</summary>
        static void SweepBx1HwConfigScannerModel(string eaeRoot, EmitResult result)
        {
            try
            {
                var dstHc = Path.Combine(eaeRoot, "HwConfiguration");
                if (!Directory.Exists(dstHc)) return;
                var subs = new List<string> { Path.Combine("EIPSolutionsV2", Bx1HwConfigScannerId) };
                subs.AddRange(Bx1Tm3bcModelFolders);
                foreach (var sub in subs)
                {
                    var d = Path.Combine(dstHc, sub);
                    if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                }
                var eip = Path.Combine(dstHc, "EIPSolutionsV2");
                if (Directory.Exists(eip) && !Directory.EnumerateFileSystemEntries(eip).Any())
                    Directory.Delete(eip);
                UnregisterBx1HwConfigScannerModel(Path.Combine(dstHc, "HwConfiguration.hwconfigproj"));
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP HwConfiguration model sweep failed: {ex.Message}");
            }
        }

        /// <summary>Idempotently registers the BX1 scanner model source files in
        /// HwConfiguration.hwconfigproj exactly as the reference does: TM3BC .prop.cs +
        /// .script.cs under &lt;Compile&gt;, scanner.xml/scanner_items.xml + TM3BC .prop.xml
        /// under &lt;None&gt;, the four folders under &lt;Folder&gt;. Returns the count added.</summary>
        static int RegisterBx1HwConfigScannerModel(string hwproj)
        {
            if (!File.Exists(hwproj)) return 0;
            var xml = XDocument.Load(hwproj);
            var ns = xml.Root!.GetDefaultNamespace();
            int added = 0;

            var cg = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            var ng = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "None").Any());
            var fg = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Folder").Any());

            void AddItem(ref XElement? group, string tag, string include, XElement? child)
            {
                if (group == null) { group = new XElement(ns + "ItemGroup"); xml.Root!.Add(group); }
                if (group.Elements(ns + tag).Any(e =>
                    string.Equals((string?)e.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase)))
                    return;
                var el = new XElement(ns + tag, new XAttribute("Include", include));
                if (child != null) el.Add(child);
                group.Add(el); added++;
            }

            foreach (var t in Bx1Tm3bcModelFolders)
            {
                AddItem(ref cg, "Compile", $@"{t}\{t}.prop.cs", null);
                AddItem(ref cg, "Compile", $@"{t}\{t}.script.cs", null);
                AddItem(ref ng, "None", $@"{t}\{t}.prop.xml", new XElement(ns + "DependentUpon", $"{t}.fbt"));
            }
            AddItem(ref ng, "None", $@"EIPSolutionsV2\{Bx1HwConfigScannerId}\scanner.xml", null);
            AddItem(ref ng, "None", $@"EIPSolutionsV2\{Bx1HwConfigScannerId}\scanner_items.xml", null);

            AddItem(ref fg, "Folder", "EIPSolutionsV2", null);
            AddItem(ref fg, "Folder", $@"EIPSolutionsV2\{Bx1HwConfigScannerId}", null);
            foreach (var t in Bx1Tm3bcModelFolders) AddItem(ref fg, "Folder", t, null);

            if (added > 0) xml.Save(hwproj);
            return added;
        }

        /// <summary>Removes the entries added by RegisterBx1HwConfigScannerModel. Idempotent.</summary>
        static void UnregisterBx1HwConfigScannerModel(string hwproj)
        {
            if (!File.Exists(hwproj)) return;
            var xml = XDocument.Load(hwproj, LoadOptions.PreserveWhitespace);
            var ns = xml.Root!.GetDefaultNamespace();
            bool changed = false;
            foreach (var name in new[] { "Compile", "None", "Folder" })
            {
                foreach (var el in xml.Descendants(ns + name).ToList())
                {
                    var inc = (string?)el.Attribute("Include");
                    if (string.IsNullOrEmpty(inc)) continue;
                    bool match = inc.StartsWith("EIPSolutionsV2", StringComparison.OrdinalIgnoreCase)
                              || Bx1Tm3bcModelFolders.Any(t => inc.Equals(t, StringComparison.OrdinalIgnoreCase)
                                     || inc.StartsWith(t + @"\", StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;
                    var nextWs = el.NextNode as XText;
                    el.Remove();
                    if (nextWs != null) nextWs.Remove();
                    changed = true;
                }
            }
            if (changed) xml.Save(hwproj);
        }

        static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }

        /// <summary>
        /// Resolves the EtherNet/IP DTM Content templates (FDT project + IO profile)
        /// from the IO folder, falling back to the absolute IO path. These are the
        /// reference SMC_Rig_Expo_withClamp artifacts staged into the IO folder.
        /// </summary>
        static (string Prj, string Xml) ResolveEtherNetIpContentTemplates(MapperConfig cfg)
        {
            var ioFolder = !string.IsNullOrWhiteSpace(cfg.IoFolderPath)
                ? cfg.IoFolderPath : @"C:\VueOneMapper\IO";
            string Pick(string name)
            {
                var p = Path.Combine(ioFolder, name);
                if (File.Exists(p)) return p;
                var fallback = Path.Combine(@"C:\VueOneMapper\IO", name);
                return File.Exists(fallback) ? fallback : p;
            }
            return (Pick("BX1_EtherNetIP_FdtProject.prj"), Pick("BX1_EtherNetIP_IOProfile.xml"));
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
