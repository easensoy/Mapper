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
    public static class Station2DeviceEmitter
    {
        const string LibElNs = "https://www.se.com/LibraryElements";

        const string M580SysdevId    = "00000000-0000-0000-0000-000000000003";
        const string BX1SysdevId     = "00000000-0000-0000-0000-000000000004";
        // Sysres IDs are 16-hex chars (EAE convention).
        const string M580ResourceId  = "3E5C2B7F1A4D6C8E";
        const string BX1ResourceId   = "C9F2A4B7E1D3F5A8";
        // M580 name "RES0" = EAE default + what M580IO.hcf symlinks use ('RES0.M580IO.<sym>'); a custom
        // name makes EAE track its default RES0 *plus* it = "2 instances of EMB_RES_ECO".
        const string M580ResourceName = "RES0";
        const string BX1ResourceName  = "BX1_RES";

        const string M580EquipmentUuid   = "11111111-2222-3333-4444-000000000040";
        const string M580RuntimeUuid     = "11111111-2222-3333-4444-000000000041";
        const string M580RackUuid        = "11111111-2222-3333-4444-000000000042";
        const string M580CpsUuid         = "11111111-2222-3333-4444-000000000043";
        const string M580CpuUuid         = "11111111-2222-3333-4444-000000000044";
        const string BX1EquipmentUuid    = "49363b74-1a84-46c1-b4cd-93f02374daec"; // HMIB1X_1
        const string BX1ContainerUuid    = "37f5487c-396f-477a-a9ae-9c0476a4f772"; // Softdpac_1
        const string BX1RuntimeUuid      = "52c5633b-f50b-4bc4-8fbd-e035bc5dfffa"; // RuntimeDEO
        const string BX1EtherNetIpUuid   = "49d2ea8e-3a4f-4ead-add4-ec4ba00d5239";

        const string Bx1SoftdpacDomainUuid = "db72f221-ece1-4b82-8132-731ce655044e";
        // Must match associatedScannerId on the EtherNetIPDevice AND the <ID> in the BX1 .hcf.
        const string Bx1ScannerId = "270AFDB7F209BFE8";
        // BX1 remote-I/O coupler (TM3BC_EtherNetIP) address — covers' physical I/O island.
        const string Bx1IoDeviceIp = "192.168.1.210";

        const string M580RuntimeTypeId = "7fd313c7-1da3-4618-9a5d-9ff3596aff7f";
        const string SoftDpacTypeId    = "29797a55-a6b8-47c4-9c06-e8a42b1a38b5";

        // NOCONF sentinel — no broadcast domain binding.
        const string NoConfDomainUuid = "00000000-0000-0000-0000-000000000000";

        // DomainTag MUST equal the live SolutionId, else EAE rejects the topology import.
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

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("Cannot derive EAE project root — Station 2 emit skipped.");
                return result;
            }

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

            // SolutionId must be a real GUID matching General/ProjectInfo.xml (== DomainTag).
            string solutionId = M262TopologyEmitter.ReadProjectGuid(eaeRoot)
                ?? FallbackSolutionUuid;
            if (solutionId == FallbackSolutionUuid)
                result.Warnings.Add(
                    "ProjectInfo.xml Guid not readable — Station 2 Topology emitted " +
                    "with zero SolutionId; EAE may reject the import. Restore General/ProjectInfo.xml.");

            // Two Equipment JSONs declaring the SAME uuid make EAE reject the whole topology.
            CleanupStaleTopologyJson(eaeRoot, "Equipment_Soft_dPAC_BX1.json", result);
            // The equipment identifier must differ from the sysdev Name (a "BX1"=="BX1" collision
            // caused a topology-import 500) — BX1's identifier is HMIB1X_1, not BX1.
            CleanupStaleTopologyJson(eaeRoot, "Equipment_Workstation_BX1.json", result);
            CleanupStaleTopologyJson(eaeRoot, "Equipment_BX1.json",            result);
            for (int n = 2; n <= 9; n++)
                CleanupStaleTopologyJson(eaeRoot, $"Equipment_M580dPAC_{n}.json", result);

            // The emitted sysres must adopt the .hcf's resource scoping or EAE can't bind it: M580 (X80
            // export) is NAME-scoped ('RES0.M580IO.<sym>'), BX1 (EtherNet/IP) is GUID-scoped
            // (DeviceHwConfigurationItem/@ResourceId).
            var bx1HcfPath = ResolveBx1HcfPath(cfg);
            var bx1Ident = ReadHcfResourceIdentity(bx1HcfPath);

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
                // Insecure-app override lets a plain mqtt:// MQTT_CONNECTION avoid the RC101 fault.
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
                // Insecure-app override (== EAE GUI "Security -> Insecure Application -> Enable") lets a
                // plain mqtt:// MQTT_CONNECTION avoid the RC101 fault.
                deployPluginPropertiesXml: BuildSoftDpacDeployPluginPropertiesXml(
                    cfg.MqttPublishEnabled && !cfg.MqttSecureTls),
                simulationBindingDeployPort: 51501,
                simulationBindingArchivePort: 51498);

            if (cfg.EmitBx1EtherNetIpDevice)
            {
                EmitBx1EtherNetIpDevice(cfg, eaeRoot, result, solutionId);
                // The BX1 .hcf EtherNet/IP scanner instantiates coupler FB type
                // Main.TM3BC_Ethe_yYhtt9jWKUOJs; without its saved .fbt BX1 fails ERR_NO_SUCH_TYPE.
                DeployBx1EtherNetIpType(cfg, eaeRoot, result);
                // The scanner model is deployed later by DeployBx1ScannerModelFinalPass (AFTER the
                // HwConfig copiers rebuild HwConfiguration/); doing it here no-ops after a Clean.
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

            // A stale <None …sysres> entry (resource id realigned) is a Missing Project File that
            // aborts the topology import — strip entries whose .sysres has no file on disk.
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
            // 1. sysdev
            var sysdevPath = Path.Combine(systemGuidDir, $"{sysdevId}.sysdev");
            File.WriteAllText(sysdevPath, BuildSysdevXml(sysdevId, deviceName, deviceType, resourceId, resourceName));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysdevPath));

            // 2. sysres — drop any sysres under a different resource ID so EAE never sees two per folder
            // (the "2 instances of EMB_RES_ECO" orphan-sweep).
            var sysdevFolder = Path.Combine(systemGuidDir, sysdevId);
            Directory.CreateDirectory(sysdevFolder);
            var sysresPath = Path.Combine(sysdevFolder, $"{resourceId}.sysres");
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
            // Sweep any .sysres sister folder whose stem has no matching .sysres.
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
                AlignSysresResourceName(sysresPath, resourceName, deviceName, result);
            }

            // 3. HCF — copy verbatim, then re-root the XML to the legacy <DeviceHwConfigurationItems>
            //    form EAE's PNConfiguratorBuildTask expects.
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

            // 3b. DeployPlugin Properties XML — EAE needs it to register the .hcf with the device card.
            var deployPluginPath = Path.Combine(sysdevFolder,
                "F513CAE3-7194-4086-936C-02912EA0B352.Properties.xml");
            File.WriteAllText(deployPluginPath, deployPluginPropertiesXml);
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, deployPluginPath));

            // 3c. SystemDeviceProperties (E0601B81) — empty default so the project compiles cold.
            var sysDevPropsPath = Path.Combine(sysdevFolder,
                "E0601B81-4A3A-4A96-B6C2-007BDC680D59.Properties.xml");
            if (!File.Exists(sysDevPropsPath))
            {
                File.WriteAllText(sysDevPropsPath, BuildEmptySystemDeviceProps());
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysDevPropsPath));
            }

            // 3d. Simulation.Binding.xml — LogicalDevice deployment + archive service ports.
            var simBindPath = Path.Combine(sysdevFolder, $"{sysdevId}.Simulation.Binding.xml");
            File.WriteAllText(simBindPath, BuildSimulationBindingXml(sysdevId,
                simulationBindingDeployPort, simulationBindingArchivePort));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, simBindPath));

            // 4. Topology Equipment JSON — force-clean write (delete first) so a hybrid two-RuntimeDEO
            //    merge (EAE picks the first, a 0.0.0.0 IP) can't persist.
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

            // 6. Register sysdev in dfbproj.
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

        // DeployPlugin Properties XML — EAE reads it (plugin GUID F513CAE3-…) to register the device's
        // .hcf with the Hardware Configuration tree.
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
            // SecurityApp/InsecureApplication override lets a plain mqtt:// MQTT_CONNECTION avoid RC101.
            (enableInsecureApp
                ? "    <GroupProperty Name=\"SecurityApp\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                  "      <GroupProperty Name=\"InsecureApplication\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                  "        <Property Name=\"Enable\" Value=\"True\" IsPassword=\"false\" />\r\n" +
                  "      </GroupProperty>\r\n" +
                  "    </GroupProperty>\r\n"
                : string.Empty) +
            "  </GroupProperty>\r\n" +
            "</SystemDeviceProperties>";

        // Soft_dPAC variant — adds SetActiveProjectAsABootProject; enableInsecureApp emits the
        // SecurityApp/InsecureApplication override (see above) to avoid the RC101 fault.
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

        // LogicalDevice service-port binding XML — Deployment (F7C90C9D-…) + Archive Service (32B24F96-…).
        internal static string BuildSimulationBindingXml(string logicalDeviceId, int deployPort, int archivePort) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\r\n" +
            "<Bindings>\r\n" +
            $"  <LogicalDeviceBinding LogicalDeviceId=\"{logicalDeviceId}\">\r\n" +
            $"    <LogicalDeviceService ServiceId=\"F7C90C9D-BD8B-4D0B-B8DE-C659AF6EABCC\" LogicalPort=\"{deployPort}\" />\r\n" +
            $"    <LogicalDeviceService ServiceId=\"32B24F96-50F3-429E-9586-58A14DEB5DD5\" LogicalPort=\"{archivePort}\" />\r\n" +
            "  </LogicalDeviceBinding>\r\n" +
            "</Bindings>";

        // The .sysdev MUST carry an inline <Resources><Resource> mirroring the sibling .sysres ID+Name,
        // else EAE's catalog auto-adds a default EMB_RES_ECO -> "2 instances of EMB_RES_ECO".
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

        // Set an existing .sysres root Resource Name (idempotent), preserving its FBNetwork.
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

        internal static string BuildEmptySystemDeviceProps() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<SystemDeviceProperties xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"http://www.nxtControl.com/DeviceProperties\" />";

        // M580 dPAC equipment JSON — X80 8-slot rack + PSU + CPU. Catalog refs must match EAE 24.1's
        // catalog names or they render as unknown placeholder boxes.
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

        // BX1 equipment JSON in the HMIB1X form (host .209 hosting a nested SoftdpacContainer running
        // the BX1 softdpac at .151, where EAE deploys). MUST be HMIB1X, not Workstation — the
        // Workstation form resolves the runtime to 127.0.0.1 and the deploy fails.
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

        // EtherNet/IP remote-I/O coupler (TM3BC_EtherNetIP, Cover PnP I/O island) at deviceIp .210,
        // scanned by scannerId. Topology-only — a field device has no logical runtime.
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

        // Resolves the BX1 EtherNet/IP .hcf (the real export is BX1IO.ethernetip.hcf), falling back
        // through the IO folder so it is always passed.
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

        // Emits the BX1 EtherNet/IP coupler (DtmDeviceDEO): Equipment JSON + its TWO MANDATORY DTM
        // Content artifacts (Content\<uuid>_FdtProject.prj + _IOProfile.xml, loaded by device uuid) +
        // topologyproj registrations. Without the Content the whole topology import aborts.
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

            // 2. DTM Content artifacts (copied verbatim from IO-folder templates).
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

        // The saved coupler FB type the BX1 EtherNet/IP scanner needs; its name is referenced by the BX1 .hcf.
        const string Bx1EtherNetIpDeviceType = "TM3BC_Ethe_yYhtt9jWKUOJs";

        // Deploys the saved coupler FB type from {TemplateLibrary}\EtherNetIP\ + registers its dfbproj
        // entries. Idempotent. Gate types (AND_*, NOT_*, DS_SELECTX_*) are compiler-generated by EAE.
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

        // Sweeps the saved coupler FB type (IEC61499 + HMI folders + dfbproj entries). Idempotent.
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

        // EAE compiles EIPSCANNER2.xml from the HwConfiguration device model, not the .hcf/.sysres.
        const string Bx1HwConfigScannerId = "270AFDB7F209BFE8";
        static readonly string[] Bx1Tm3bcModelFolders =
            { "TM3BC_Ethe_R1C9LFqq0OfJh", "TM3BC_Ethe_yYhtt9jWKUOJs" };

        // Deploys the BX1 EtherNet/IP scanner HwConfiguration device model (TM3BC_Ethe_* +
        // EIPSolutionsV2\<scannerId>\scanner.xml) + registers it in HwConfiguration.hwconfigproj. EAE
        // compiles EIPSCANNER2.xml FROM this model; without it the scanner is EMPTY (no .210). BX1-only.
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
                // Create in case a Clean wiped HwConfiguration/.
                Directory.CreateDirectory(dstHc);

                var subs = new List<string> { Path.Combine("EIPSolutionsV2", Bx1HwConfigScannerId) };
                subs.AddRange(Bx1Tm3bcModelFolders);
                foreach (var sub in subs)
                {
                    var s = Path.Combine(srcHc, sub);
                    if (Directory.Exists(s)) CopyDirectory(s, Path.Combine(dstHc, sub));
                }

                var hwproj = Path.Combine(dstHc, "HwConfiguration.hwconfigproj");
                // A Clean can remove the hwconfigproj shell; RegisterBx1HwConfigScannerModel only adds
                // to an existing project, so recreate the shell or EAE compiles an empty scanner.
                if (!File.Exists(hwproj))
                {
                    foreach (var shell in new[] { "HwConfiguration.hwconfigproj", "AssemblyInfo.cs",
                                                  Path.Combine("ImageStorage", "ImageStorage.xml") })
                    {
                        var s = Path.Combine(srcHc, shell);
                        var d = Path.Combine(dstHc, shell);
                        if (File.Exists(s) && !File.Exists(d))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                            File.Copy(s, d);
                        }
                    }
                    if (!File.Exists(hwproj))
                        result.Warnings.Add("[BX1] HwConfiguration.hwconfigproj was wiped and no shell " +
                            "template exists in 'EtherNetIP/HwConfiguration' — the scanner cannot be " +
                            "registered and EAE will compile an EMPTY scanner. Stage the project shell.");
                }
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

        // Deploys the scanner HwConfiguration model as the FINAL pass (from BX1HwConfigCopier.Copy,
        // AFTER the HwConfig copiers rebuild HwConfiguration/). BX1-only, gated.
        public static void DeployBx1ScannerModelFinalPass(MapperConfig cfg)
        {
            if (cfg == null || !cfg.EmitBx1EtherNetIpDevice) return;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            var result = new EmitResult();
            DeployBx1HwConfigScannerModel(cfg, eaeRoot, result);
        }

        // Aborts the Generate if the scanner model is not deployed (scanner.xml missing / no .210
        // buscoupler / no hwconfigproj registration) — else EAE compiles an empty scanner.
        public static void ValidateBx1ScannerModelOrThrow(MapperConfig cfg)
        {
            if (cfg == null || !cfg.EmitBx1EtherNetIpDevice) return;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            var scannerXml = Path.Combine(eaeRoot, "HwConfiguration", "EIPSolutionsV2", Bx1HwConfigScannerId, "scanner.xml");
            var hwproj = Path.Combine(eaeRoot, "HwConfiguration", "HwConfiguration.hwconfigproj");
            var problems = new List<string>();
            if (!File.Exists(scannerXml)) problems.Add($"scanner.xml MISSING ({scannerXml})");
            else if (!File.ReadAllText(scannerXml).Contains("192.168.1.210"))
                problems.Add("scanner.xml has NO 192.168.1.210 buscoupler");
            if (!File.Exists(hwproj)) problems.Add($"HwConfiguration.hwconfigproj MISSING ({hwproj})");
            else
            {
                var p = File.ReadAllText(hwproj);
                if (!p.Contains("EIPSolutionsV2") && !p.Contains("scanner.xml"))
                    problems.Add("HwConfiguration.hwconfigproj has NO scanner-model registration");
            }
            if (problems.Count > 0)
                throw new InvalidOperationException(
                    "[BX1][SCANNER-GUARD] EtherNet/IP scanner model NOT deployed -> EAE would compile an EMPTY " +
                    "EIPSCANNER2.xml (333 bytes, no .210) and the covers would NOT move. Generate ABORTED to block " +
                    "shipping the empty-scanner regression. Problems: " + string.Join("; ", problems) +
                    ". Fix: close EAE, confirm the Template Library 'EtherNetIP/HwConfiguration' model exists, then re-run Test Runtime.");
        }

        // Removes the BX1 scanner HwConfiguration model (folders + hwconfigproj entries). Idempotent.
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

        // Idempotently registers the scanner model files in HwConfiguration.hwconfigproj (TM3BC
        // .prop.cs/.script.cs <Compile>, scanner*.xml + .prop.xml <None>, folders <Folder>).
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

        // Removes the entries added by RegisterBx1HwConfigScannerModel. Idempotent.
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

        // Resolves the EtherNet/IP DTM Content templates (FDT project + IO profile) from the IO folder.
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

        // Deletes a stale Topology Equipment JSON + its topologyproj <None Include> registration. Idempotent.
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

        // Reads the resource identity an authored .hcf expects so the sysres can adopt it: GUID-scoped
        // (DeviceHwConfigurationItem/@ResourceId) for BX1, or Name-scoped ('RES0.M580IO.<sym>') for
        // M580. null where absent; never throws.
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

                // Name form: first symlink 'NAME.GROUP.symbol' whose head is a symbolic resource name.
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
