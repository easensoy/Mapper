using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Devices.M262;

using CodeGen.Mapping;
using System.Globalization;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Services;
using CodeGen.Devices.Core;
namespace CodeGen.Devices.Core
{
    public static class Station2DeviceEmitter
    {
        internal const string LibElNs = "https://www.se.com/LibraryElements";

        // The name EAE shows the device under, declared beside its addresses. Refused rather than
        // defaulted: an unnamed device is one an engineer cannot find in the tree.
        static string DeviceNameOf(CodeGen.Translation.PlcAssignment plc) =>
            TargetRegistry.Of(plc).DeviceName
            ?? throw new InvalidOperationException(
                $"device.yml declares no deviceName for target '{plc}', so its system device has no name.");

        static Configuration.DeviceIdentity M580Id =>
            Configuration.DeviceConfig.Identity(CodeGen.Translation.PlcAssignment.Named("M580"));
        static Configuration.DeviceIdentity Bx1Id =>
            Configuration.DeviceConfig.Identity(CodeGen.Translation.PlcAssignment.Named("BX1"));
        static Configuration.InstallationIdentity Install(Configuration.CompilerConfiguration cfg) =>
            cfg.Devices.Installation;

        internal static string M580SysdevId => M580Id.Sysdev;
        internal static string BX1SysdevId => Bx1Id.Sysdev;
        // Sysres IDs are 16-hex chars (EAE convention).
        static string M580ResourceId => M580Id.Resource;
        static string BX1ResourceId => Bx1Id.Resource;
        // M580 name "RES0" is the EAE default and what M580IO.hcf symlinks use; a custom name makes EAE track its default RES0 as well.
        static readonly string M580ResourceName = TargetRegistry.Of(Translation.PlcAssignment.Named("M580")).ResourceName;
        static readonly string BX1ResourceName  = TargetRegistry.Of(Translation.PlcAssignment.Named("BX1")).ResourceName;

        static string M580EquipmentUuid => M580Id.Equipment;
        static string M580RuntimeUuid => M580Id.Runtime;
        static string M580RackUuid => M580Id.Rack;
        static string M580CpsUuid => M580Id.Cps;
        internal static string M580CpuUuid => M580Id.Cpu;
        internal static string BX1EquipmentUuid => Bx1Id.Equipment; // HMIB1X_1
        static string BX1ContainerUuid => Bx1Id.Container; // Softdpac_1
        static string BX1RuntimeUuid => Bx1Id.Runtime; // RuntimeDEO
        internal static string BX1EtherNetIpUuid => Bx1Id.EtherNetIp;

        internal static string Bx1SoftdpacDomainUuid => Bx1Id.ContainerDomain;
        // Must match associatedScannerId on the EtherNetIPDevice AND the <ID> in the BX1 .hcf.
        internal static string Bx1ScannerId => Bx1Id.Scanner;

        // EAE reads each device's Properties file by plugin GUID: DeployPlugin registers the .hcf, SystemDeviceProperties holds per-device settings.
        internal static string DeployPluginPropertiesFile(Configuration.CompilerConfiguration cfg) =>
            Install(cfg).DeployPluginProperties;
        internal static string SystemDevicePropertiesFile(Configuration.CompilerConfiguration cfg) =>
            Install(cfg).SystemDeviceProperties;

        static string M580RuntimeTypeId => M580Id.RuntimeType;
        internal static string SoftDpacTypeId => Bx1Id.RuntimeType;

        // NOCONF sentinel — no broadcast domain binding.
        const string NoConfDomainUuid = "00000000-0000-0000-0000-000000000000";

        // DomainTag MUST equal the live SolutionId, else EAE rejects the topology import.
        const string FallbackSolutionUuid = "00000000-0000-0000-0000-000000000000";

        public sealed class EmitResult
        {
            public List<string> FilesWritten { get; } = new();
            public List<string> Warnings { get; } = new();
            public int TopologyProjEntriesAdded { get; set; }
            }

        // The M580 dPAC. Its sysres is NAME-scoped, so the resource keeps its declared name.
        public static EmitResult EmitM580(Configuration.CompilerConfiguration cfg, DeviceScope scope)
        {
            var result = new EmitResult();
            // Two Equipment JSONs declaring the SAME uuid make EAE reject the whole topology.
            for (int n = 2; n <= 9; n++)
                CleanupStaleTopologyJson(scope.EaeRoot, $"Equipment_M580dPAC_{n}.json", result);

            EmitOnePlc(cfg, scope.EaeRoot, scope.SystemGuidDir, result,
                sysdevId: M580SysdevId,
                deviceName: DeviceNameOf(CodeGen.Translation.PlcAssignment.Named("M580")),
                deviceType: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M580")).DeviceType,
                resourceId: M580ResourceId,
                resourceName: M580ResourceName,
                hcfTemplatePath: cfg.Paths.M580HcfTemplatePath,
                equipmentJsonName: "Equipment_M580dPAC_1.json",
                equipmentBuilder: () => BuildM580EquipmentJson(cfg, M580SysdevId, scope.SolutionId,
                                          cfg.Paths.M580TargetIp, cfg.Paths.M580BroadcastDomainUuid),
                deployPluginPropertiesXml: BuildDeployPluginPropertiesXml(cfg, bootProject: false,
                    cfg.Paths.MqttPublishEnabled && !cfg.Paths.MqttSecureTls),
                simulationBindingDeployPort: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M580")).SimulationDeployPort,
                simulationBindingArchivePort: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M580")).SimulationArchivePort);
            return result;
        }

        // The BX1 Soft dPAC and, when declared, the EtherNet/IP coupler its scanner drives. Its sysres is
        // GUID-scoped, so the resource id is adopted from the authored .hcf.
        public static EmitResult EmitBx1(Configuration.CompilerConfiguration cfg, DeviceScope scope)
        {
            var result = new EmitResult();
            CleanupStaleTopologyJson(scope.EaeRoot, "Equipment_Soft_dPAC_BX1.json", result);
            // The equipment identifier must differ from the sysdev Name, so BX1's identifier is HMIB1X_1.
            CleanupStaleTopologyJson(scope.EaeRoot, "Equipment_Workstation_BX1.json", result);
            CleanupStaleTopologyJson(scope.EaeRoot, "Equipment_BX1.json", result);

            var bx1HcfPath = ResolveBx1HcfPath(cfg);
            var bx1ResourceId = ReadHcfResourceIdentity(bx1HcfPath).GuidId ?? BX1ResourceId;
            if (!string.Equals(bx1ResourceId, BX1ResourceId, StringComparison.Ordinal))
                result.Warnings.Add(
                    $"[BX1] sysres ID aligned to '{bx1ResourceId}' from the BX1 .hcf ResourceId " +
                    $"(default was '{BX1ResourceId}').");

            EmitOnePlc(cfg, scope.EaeRoot, scope.SystemGuidDir, result,
                sysdevId: BX1SysdevId,
                deviceName: DeviceNameOf(CodeGen.Translation.PlcAssignment.Named("BX1")),
                deviceType: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("BX1")).DeviceType,
                resourceId: bx1ResourceId,
                resourceName: BX1ResourceName,
                hcfTemplatePath: bx1HcfPath,
                equipmentJsonName: "Equipment_HMIB1X_1.json",
                equipmentBuilder: () => BuildBX1HmiB1XEquipmentJson(cfg, BX1SysdevId, scope.SolutionId,
                                          cfg.Paths.BX1TargetIp, cfg.Paths.BX1HostIp),
                // The insecure-app override lets a plain mqtt:// connection avoid RC101.
                deployPluginPropertiesXml: BuildSoftDpacDeployPluginPropertiesXml(cfg,
                    cfg.Paths.MqttPublishEnabled && !cfg.Paths.MqttSecureTls),
                simulationBindingDeployPort: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("BX1")).SimulationDeployPort,
                simulationBindingArchivePort: TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("BX1")).SimulationArchivePort);

            EmitBx1EtherNetIpDevice(cfg, scope.EaeRoot, result, scope.SolutionId);
            // The scanner instantiates coupler type Main.TM3BC_Ethe_yYhtt9jWKUOJs; without its saved
            // .fbt the device fails ERR_NO_SUCH_TYPE.
            DeployBx1EtherNetIpType(cfg, scope.EaeRoot, result);
            return result;
        }

        // A stale <None ...sysres> entry is a Missing Project File that aborts the topology import, so it
        // is swept once after every device has taken its turn rather than by each of them.
        public static EmitResult StripStaleSysresEntries(string eaeRoot)
        {
            var result = new EmitResult();
            var dfbproj = EaeProjectLayout.FindDfbproj(eaeRoot);
            if (dfbproj == null) return result;
            int stripped = DfbprojRegistrar.StripStaleSysresStemEntries(dfbproj, eaeRoot);
            if (stripped > 0)
                result.Warnings.Add(
                    $"Removed {stripped} stale sysres reference(s) from the .dfbproj " +
                    "(resource id realigned to the .hcf ResourceId).");
            return result;
        }

        internal static void EmitOnePlc(Configuration.CompilerConfiguration cfg, string eaeRoot, string systemGuidDir,
            EmitResult result, string sysdevId, string deviceName, string deviceType,
            string resourceId, string resourceName, string? hcfTemplatePath,
            string equipmentJsonName, Func<string> equipmentBuilder,
            string deployPluginPropertiesXml,
            int simulationBindingDeployPort, int simulationBindingArchivePort)
        {
            var sysdevPath = Path.Combine(systemGuidDir, $"{sysdevId}.sysdev");
            File.WriteAllText(sysdevPath, BuildSysdevXml(cfg, sysdevId, deviceName, deviceType, resourceId, resourceName));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysdevPath));

            // 2. sysres — drop any sysres under a different resource ID so EAE never sees two per folder.
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
                File.WriteAllText(sysresPath, BuildSysresXml(cfg, resourceId, resourceName));
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysresPath));
            }
            else
            {
                AlignSysresResourceName(sysresPath, resourceName, deviceName, result);
            }

            // 3. HCF — copy verbatim, then re-root to the legacy <DeviceHwConfigurationItems> form PNConfiguratorBuildTask expects.
            if (!string.IsNullOrWhiteSpace(hcfTemplatePath) && File.Exists(hcfTemplatePath))
            {
                var hcfDest = Path.Combine(sysdevFolder, $"{sysdevId}.hcf");
                File.Copy(hcfTemplatePath, hcfDest, overwrite: true);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, hcfDest));

                var rewrite = HcfRootRewriter.RewriteIfNeeded(hcfDest, resourceId, cfg.Generation.FileWriteRetries);
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

            var deployPluginPath = Path.Combine(sysdevFolder,
                DeployPluginPropertiesFile(cfg));
            File.WriteAllText(deployPluginPath, deployPluginPropertiesXml);
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, deployPluginPath));

            // 3c. SystemDeviceProperties (E0601B81) — empty default so the project compiles cold.
            var sysDevPropsPath = Path.Combine(sysdevFolder,
                SystemDevicePropertiesFile(cfg));
            if (!File.Exists(sysDevPropsPath))
            {
                File.WriteAllText(sysDevPropsPath, BuildEmptySystemDeviceProps(cfg));
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, sysDevPropsPath));
            }

            var simBindPath = Path.Combine(sysdevFolder, $"{sysdevId}.Simulation.Binding.xml");
            File.WriteAllText(simBindPath, BuildSimulationBindingXml(cfg, sysdevId,
                simulationBindingDeployPort, simulationBindingArchivePort));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, simBindPath));

            // 4. Topology Equipment JSON — force-clean write (delete first) so a hybrid two-RuntimeDEO merge cannot persist.
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

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
            {
                result.TopologyProjEntriesAdded += EaeProjectLayout.RegisterInTopologyProj(
                    topologyProj, new[] { equipmentJsonName });
            }
            else
            {
                result.Warnings.Add(
                    $"{deviceName}: TopologyManager.topologyproj missing — Equipment JSON " +
                    "written but not registered with TopologyManager build target.");
            }

            var dfbproj = EaeProjectLayout.FindDfbproj(eaeRoot);
            if (dfbproj != null)
            {
                try
                {
                    int added = DfbprojRegistrar.RegisterSystemDevice(dfbproj, eaeRoot, sysdevPath);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"{deviceName}: dfbproj registration failed ({ex.Message}).");
                }
            }
        }

        // DeployPlugin Properties XML — EAE reads it (plugin GUID F513CAE3-…) to register the device's .hcf.
        // bootProject adds the Soft_dPAC-only SetActiveProjectAsABootProject; enableInsecureApp adds the RC101 override.
        static string BuildDeployPluginPropertiesXml(Configuration.CompilerConfiguration cfg, bool bootProject, bool enableInsecureApp) =>
            TemplateDocument.Load(cfg, @"Device\SystemDeviceProperties.xml", new Dictionary<string, string>
            {
                ["BootProjectProperty"] = bootProject
                    ? TemplateDocument.Load(cfg, @"Device\SystemDeviceProperties.BootProject.fragment.xml")
                    : string.Empty,
                ["SecurityAppGroup"] = enableInsecureApp
                    ? TemplateDocument.Load(cfg, @"Device\SystemDeviceProperties.SecurityApp.fragment.xml")
                    : string.Empty,
            });

        internal static string BuildSoftDpacDeployPluginPropertiesXml(Configuration.CompilerConfiguration cfg, bool enableInsecureApp) =>
            BuildDeployPluginPropertiesXml(cfg, bootProject: true, enableInsecureApp);

        internal static string BuildStandardDeployPluginPropertiesXml(Configuration.CompilerConfiguration cfg, bool enableInsecureApp) =>
            BuildDeployPluginPropertiesXml(cfg, bootProject: false, enableInsecureApp);

        // LogicalDevice service-port binding XML — Deployment (F7C90C9D-…) + Archive Service (32B24F96-…).
        internal static string BuildSimulationBindingXml(Configuration.CompilerConfiguration cfg,
            string logicalDeviceId, int deployPort, int archivePort) =>
            TemplateDocument.Load(cfg, @"Device\Simulation.Binding.xml", new Dictionary<string, string>
            {
                ["LogicalDeviceId"] = logicalDeviceId,
                ["DeployPort"] = deployPort.ToString(CultureInfo.InvariantCulture),
                ["ArchivePort"] = archivePort.ToString(CultureInfo.InvariantCulture),
            });

        // The .sysdev MUST carry an inline <Resources><Resource> mirroring the sibling .sysres ID+Name, else EAE auto-adds a default EMB_RES_ECO.
        internal static string BuildSysdevXml(Configuration.CompilerConfiguration cfg, string sysdevId, string name, string type,
                                              string resourceId, string resourceName) =>
            TemplateDocument.Load(cfg, @"Device\Device.sysdev", new Dictionary<string, string>
            {
                ["SysdevId"] = sysdevId,
                ["DeviceName"] = name,
                ["DeviceType"] = type,
                ["ResourceId"] = resourceId,
                ["ResourceName"] = resourceName,
            });

        internal static string BuildSysresXml(Configuration.CompilerConfiguration cfg, string resourceId, string name) =>
            TemplateDocument.Load(cfg, @"Device\Resource.sysres", new Dictionary<string, string>
            {
                ["ResourceId"] = resourceId,
                ["ResourceName"] = name,
            });

        internal static string BuildEmptySystemDeviceProps(Configuration.CompilerConfiguration cfg) =>
            TemplateDocument.Load(cfg, @"Device\SystemDeviceProperties.Empty.xml");






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


        // M580 dPAC equipment JSON (X80 rack + PSU + CPU); catalog refs must match EAE 24.1 names or they render as unknown boxes.
        static string BuildM580EquipmentJson(Configuration.CompilerConfiguration cfg, string sysdevId, string solutionId,
                                             string targetIp, string broadcastDomainUuid)
        {
            return TemplateDocument.Load(cfg, @"Topology\Equipment_M580dPAC.json",
                new Dictionary<string, string>
                {
                    ["M580CpsUuid"] = M580CpsUuid,
                    ["M580CpuUuid"] = M580CpuUuid,
                    ["M580EquipmentUuid"] = M580EquipmentUuid,
                    ["M580RackUuid"] = M580RackUuid,
                    ["M580RuntimeTypeId"] = M580RuntimeTypeId,
                    ["M580RuntimeUuid"] = M580RuntimeUuid,
                    ["broadcastDomainUuid"] = broadcastDomainUuid,
                    ["solutionId"] = solutionId,
                    ["sysdevId"] = sysdevId,
                    ["targetIp"] = targetIp,
                });
        }

        // BX1 equipment JSON in the HMIB1X form: host .209 with a nested SoftdpacContainer at .151 where EAE deploys.
        // MUST be HMIB1X, not Workstation — the Workstation form resolves the runtime to 127.0.0.1 and the deploy fails.
        static string BuildBX1HmiB1XEquipmentJson(Configuration.CompilerConfiguration cfg, string sysdevId, string solutionId,
            string softpacIp, string hostIp)
        {
            return TemplateDocument.Load(cfg, @"Topology\Equipment_HMIB1X.json",
                new Dictionary<string, string>
                {
                    ["BX1ContainerUuid"] = BX1ContainerUuid,
                    ["BX1EquipmentUuid"] = BX1EquipmentUuid,
                    ["BX1RuntimeUuid"] = BX1RuntimeUuid,
                    ["Bx1ScannerId"] = Bx1ScannerId,
                    ["Bx1SoftdpacDomainUuid"] = Bx1SoftdpacDomainUuid,
                    ["NoConfDomainUuid"] = NoConfDomainUuid,
                    ["SoftDpacTypeId"] = SoftDpacTypeId,
                    ["hostIp"] = hostIp,
                    ["softpacIp"] = softpacIp,
                    ["solutionId"] = solutionId,
                    ["sysdevId"] = sysdevId,
                });
        }

        // EtherNet/IP remote-I/O coupler at deviceIp .210, scanned by scannerId. Topology-only: a field device has no logical runtime.
        static string BuildEtherNetIpDeviceEquipmentJson(Configuration.CompilerConfiguration cfg, string solutionId, string deviceIp, string scannerId)
        {
            return TemplateDocument.Load(cfg, @"Topology\Equipment_EtherNetIPDevice.json",
                new Dictionary<string, string>
                {
                    ["BX1EtherNetIpUuid"] = BX1EtherNetIpUuid,
                    ["NoConfDomainUuid"] = NoConfDomainUuid,
                    ["deviceIp"] = deviceIp,
                    ["scannerId"] = scannerId,
                    ["solutionId"] = solutionId,
                });
        }

        // Resolves the BX1 EtherNet/IP .hcf (the real export is BX1IO.ethernetip.hcf), falling back through the IO folder.
        static string ResolveBx1HcfPath(Configuration.CompilerConfiguration cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.Paths.BX1HcfTemplatePath) &&
                File.Exists(cfg.Paths.BX1HcfTemplatePath))
                return cfg.Paths.BX1HcfTemplatePath;

            return Path.Combine(cfg.Paths.RequireIoFolderPath(), "BX1IO.ethernetip.hcf");
        }

        // Emits the BX1 EtherNet/IP coupler: Equipment JSON + its TWO MANDATORY DTM Content artifacts
        // (Content\<uuid>_FdtProject.prj + _IOProfile.xml). Without the Content the whole topology import aborts.
        static void EmitBx1EtherNetIpDevice(Configuration.CompilerConfiguration cfg, string eaeRoot,
            EmitResult result, string solutionId)
        {
            const string EquipmentJsonName = "Equipment_EtherNetIPDevice_1.json";
            var topologyDir = Path.Combine(eaeRoot, "Topology");
            Directory.CreateDirectory(topologyDir);

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
                BuildEtherNetIpDeviceEquipmentJson(cfg, solutionId, cfg.Devices.Bx1.CouplerIp, Bx1ScannerId));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, equipmentPath));

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

            var topologyProj = Path.Combine(topologyDir, "TopologyManager.topologyproj");
            if (File.Exists(topologyProj))
                result.TopologyProjEntriesAdded +=
                    EaeProjectLayout.RegisterInTopologyProj(topologyProj, registerNames);
            else
                result.Warnings.Add(
                    "EtherNetIPDevice: TopologyManager.topologyproj missing — equipment + content " +
                    "written but not registered with TopologyManager build target.");
        }

        // Declared on the BX1's own device.yml row: the coupler type its scanner instantiates, and
        // the HwConfiguration model folders that carry it.
        static string Bx1EtherNetIpDeviceType => EtherNetIpTarget.EtherNetIpDeviceType;
        static IReadOnlyList<string> Bx1Tm3bcModelFolders => EtherNetIpTarget.HwConfigModelFolders;
        // The target is the one that DECLARES an EtherNet/IP coupler, not the one with a known name:
        // moving the scanner to another device is then a device.yml edit. Two claimants is refused,
        // because the coupler type is deployed once and both would silently share it.
        static TargetDescriptor EtherNetIpTarget =>
            TargetRegistry.All.Where(t => !string.IsNullOrWhiteSpace(t.EtherNetIpDeviceType)).ToList() is { Count: 1 } one
                ? one[0]
                : throw new InvalidOperationException(
                    "[Topology] device.yml must declare etherNetIpDeviceType on exactly one target: " +
                    string.Join(", ", TargetRegistry.All
                        .Where(t => !string.IsNullOrWhiteSpace(t.EtherNetIpDeviceType))
                        .Select(t => t.Plc.Name)) + " claim it.");

        // Deploys the saved coupler FB type from {TemplateLibrary}\EtherNetIP\ + its dfbproj entries; gate types (AND_*, NOT_*) are EAE-generated.
        static void DeployBx1EtherNetIpType(Configuration.CompilerConfiguration cfg, string eaeRoot, EmitResult result)
        {
            try
            {
                var libRoot = cfg.Paths.RequireTemplateLibraryPath();
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

                var dfbproj = EaeProjectLayout.FindDfbproj(eaeRoot);
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

        static void SweepBx1EtherNetIpType(string eaeRoot, EmitResult result)
        {
            try
            {
                var dstIec = Path.Combine(eaeRoot, "IEC61499", Bx1EtherNetIpDeviceType);
                var dstHmi = Path.Combine(eaeRoot, "HMI", Bx1EtherNetIpDeviceType);
                if (Directory.Exists(dstIec)) Directory.Delete(dstIec, recursive: true);
                if (Directory.Exists(dstHmi)) Directory.Delete(dstHmi, recursive: true);
                var dfbproj = EaeProjectLayout.FindDfbproj(eaeRoot);
                if (dfbproj != null)
                    DfbprojRegistrar.UnregisterHardwareDeviceCat(dfbproj, Bx1EtherNetIpDeviceType);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP device type sweep failed: {ex.Message}");
            }
        }

        // EAE compiles EIPSCANNER2.xml from the HwConfiguration device model, not the .hcf/.sysres.
        static string Bx1HwConfigScannerId => Bx1ScannerId;

        // Deploys the BX1 scanner HwConfiguration device model (TM3BC_Ethe_* + EIPSolutionsV2\<scannerId>\scanner.xml)
        // and registers it in HwConfiguration.hwconfigproj; without it EAE compiles an EMPTY scanner. BX1-only.
        static void DeployBx1HwConfigScannerModel(Configuration.CompilerConfiguration cfg, string eaeRoot, EmitResult result)
        {
            try
            {
                var libRoot = cfg.Paths.RequireTemplateLibraryPath();
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
                Directory.CreateDirectory(dstHc);

                var subs = new List<string> { Path.Combine("EIPSolutionsV2", Bx1HwConfigScannerId) };
                subs.AddRange(Bx1Tm3bcModelFolders);
                foreach (var sub in subs)
                {
                    var s = Path.Combine(srcHc, sub);
                    if (Directory.Exists(s)) CopyDirectory(s, Path.Combine(dstHc, sub));
                }

                var hwproj = Path.Combine(dstHc, "HwConfiguration.hwconfigproj");
                // A Clean can remove the hwconfigproj shell, and registration only adds to an existing project, so recreate it.
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
                    $"(acceptance: ~1200 bytes incl. {cfg.Devices.Bx1.CouplerIp}).");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP HwConfiguration model deploy failed: {ex.Message}");
            }
        }

        // Deploys the scanner HwConfiguration model as the FINAL pass, AFTER the HwConfig copiers rebuild HwConfiguration/.
        public static void DeployBx1ScannerModelFinalPass(Configuration.CompilerConfiguration cfg)
        {
            if (cfg == null) return;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            var result = new EmitResult();
            DeployBx1HwConfigScannerModel(cfg, eaeRoot, result);
        }

        // Aborts the Generate if the scanner model is not deployed, else EAE compiles an empty scanner.
        public static void ValidateBx1ScannerModelOrThrow(Configuration.CompilerConfiguration cfg)
        {
            if (cfg == null) return;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            var scannerXml = Path.Combine(eaeRoot, "HwConfiguration", "EIPSolutionsV2", Bx1HwConfigScannerId, "scanner.xml");
            var hwproj = Path.Combine(eaeRoot, "HwConfiguration", "HwConfiguration.hwconfigproj");
            var problems = new List<string>();
            if (!File.Exists(scannerXml)) problems.Add($"scanner.xml MISSING ({scannerXml})");
            else if (!File.ReadAllText(scannerXml).Contains(cfg.Devices.Bx1.CouplerIp))
                problems.Add($"scanner.xml has NO {cfg.Devices.Bx1.CouplerIp} buscoupler");
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

        static (string Prj, string Xml) ResolveEtherNetIpContentTemplates(Configuration.CompilerConfiguration cfg)
        {
            string Pick(string name) => Path.Combine(cfg.Paths.RequireIoFolderPath(), name);
            return (Pick("BX1_EtherNetIP_FdtProject.prj"), Pick("BX1_EtherNetIP_IOProfile.xml"));
        }

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

        // Reads the resource identity an authored .hcf expects so the sysres can adopt it: GUID-scoped for BX1,
        // Name-scoped for M580. null where absent; never throws.
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
