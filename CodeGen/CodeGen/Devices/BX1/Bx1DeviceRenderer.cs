using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Mapping;
using CodeGen.Services;

namespace CodeGen.Devices.BX1
{
    // THE BX1 SOFT dPAC AND THE ETHERNET/IP COUPLER ITS SCANNER DRIVES.
    //
    // Its sysres is GUID-scoped, so the resource id is adopted from the authored .hcf. Everything here
    // is about this device's own hardware: the coupler equipment, the saved coupler type and the
    // HwConfiguration scanner model EAE compiles EIPSCANNER2.xml from. The shell every device shares
    // is EaeDeviceWriter's.
    public static class Bx1DeviceRenderer
    {
        // The BX1 Soft dPAC and, when declared, the EtherNet/IP coupler its scanner drives. Its sysres is
        // GUID-scoped, so the resource id is adopted from the authored .hcf.
        public static EaeDeviceWriter.EmitResult EmitBx1(Configuration.CompilerConfiguration cfg,
            Translation.PlcAssignment target, DeviceScope scope)
        {
            var self = cfg.Targets.Of(target);
            var result = new EaeDeviceWriter.EmitResult();
            EaeDeviceWriter.CleanupStaleTopologyJson(scope.EaeRoot, "Equipment_Soft_dPAC_BX1.json", result);
            // The equipment identifier must differ from the sysdev Name, so BX1's identifier is HMIB1X_1.
            EaeDeviceWriter.CleanupStaleTopologyJson(scope.EaeRoot, "Equipment_Workstation_BX1.json", result);
            EaeDeviceWriter.CleanupStaleTopologyJson(scope.EaeRoot, "Equipment_BX1.json", result);

            var bx1HcfPath = ResolveBx1HcfPath(cfg);
            var declaredResourceId = self.Identity.Resource;
            var bx1ResourceId = EaeDeviceWriter.ReadHcfResourceIdentity(bx1HcfPath).GuidId ?? declaredResourceId;
            if (!string.Equals(bx1ResourceId, declaredResourceId, StringComparison.Ordinal))
                result.Warnings.Add(
                    $"[{target}] sysres ID aligned to '{bx1ResourceId}' from the .hcf ResourceId " +
                    $"(declared was '{declaredResourceId}').");

            EaeDeviceWriter.EmitOnePlc(cfg, self, scope.EaeRoot, scope.SystemGuidDir, result,
                sysdevId: self.Identity.Sysdev,
                deviceName: EaeDeviceWriter.DeviceNameOf(cfg.Targets, target),
                deviceType: self.DeviceType,
                resourceId: bx1ResourceId,
                resourceName: self.ResourceName,
                hcfTemplatePath: bx1HcfPath,
                equipmentJsonName: "Equipment_HMIB1X_1.json",
                equipmentBuilder: () => BuildBX1HmiB1XEquipmentJson(cfg, self, self.Identity.Sysdev,
                                          scope.SolutionId,
                                          cfg.Devices.NetworkOf(target.Name).TargetIp,
                                          cfg.Devices.NetworkOf(target.Name).HostIp),
                // The insecure-app override lets a plain mqtt:// connection avoid RC101.
                deployPluginPropertiesXml: EaeDeviceWriter.BuildSoftDpacDeployPluginPropertiesXml(cfg,
                    cfg.Telemetry.PublishEnabled && !cfg.Telemetry.SecureTls),
                simulationBindingDeployPort: self.SimulationDeployPort,
                simulationBindingArchivePort: self.SimulationArchivePort);

            EmitBx1EtherNetIpDevice(cfg, self, scope.EaeRoot, result, scope.SolutionId);
            // The scanner instantiates coupler type Main.TM3BC_Ethe_yYhtt9jWKUOJs; without its saved
            // .fbt the device fails ERR_NO_SUCH_TYPE.
            DeployBx1EtherNetIpType(cfg, scope.EaeRoot, result);
            return result;
        }

        // BX1 equipment JSON in the HMIB1X form: host .209 with a nested SoftdpacContainer at .151 where EAE deploys.
        // MUST be HMIB1X, not Workstation — the Workstation form resolves the runtime to 127.0.0.1 and the deploy fails.
        static string BuildBX1HmiB1XEquipmentJson(Configuration.CompilerConfiguration cfg,
            Mapping.TargetDescriptor self, string sysdevId, string solutionId,
            string softpacIp, string hostIp)
        {
            return TemplateDocument.Load(cfg, @"Topology\Equipment_HMIB1X.json",
                new Dictionary<string, string>
                {
                    ["BX1ContainerUuid"] = self.Identity.Container,
                    ["BX1EquipmentUuid"] = self.Identity.Equipment,
                    ["BX1RuntimeUuid"] = self.Identity.Runtime,
                    ["Bx1ScannerId"] = self.Identity.Scanner,
                    ["Bx1SoftdpacDomainUuid"] = self.Identity.ContainerDomain,
                    ["NoConfDomainUuid"] = EaeDeviceWriter.NoConfDomainUuid,
                    ["SoftDpacTypeId"] = self.Identity.RuntimeType,
                    ["hostIp"] = hostIp,
                    ["softpacIp"] = softpacIp,
                    ["solutionId"] = solutionId,
                    ["sysdevId"] = sysdevId,
                });
        }

        // EtherNet/IP remote-I/O coupler at deviceIp .210, scanned by scannerId. Topology-only: a field device has no logical runtime.
        static string BuildEtherNetIpDeviceEquipmentJson(Configuration.CompilerConfiguration cfg,
            Mapping.TargetDescriptor self, string solutionId, string deviceIp, string scannerId)
        {
            return TemplateDocument.Load(cfg, @"Topology\Equipment_EtherNetIPDevice.json",
                new Dictionary<string, string>
                {
                    ["BX1EtherNetIpUuid"] = self.Identity.EtherNetIp,
                    ["NoConfDomainUuid"] = EaeDeviceWriter.NoConfDomainUuid,
                    ["deviceIp"] = deviceIp,
                    ["scannerId"] = scannerId,
                    ["solutionId"] = solutionId,
                });
        }

        // Resolves the BX1 EtherNet/IP .hcf (the real export is BX1IO.ethernetip.hcf), falling back through the IO folder.
        // THE one answer to "where is the BX1 .hcf". The configured setting names a file that does not
        // ship; the authored one sits beside it under the declared IO folder. Both the device emit and
        // the hardware-config copy resolve through here, so they cannot disagree about it.
        internal static string ResolveBx1HcfPath(Configuration.CompilerConfiguration cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.Paths.BX1HcfTemplatePath) &&
                File.Exists(cfg.Paths.BX1HcfTemplatePath))
                return cfg.Paths.BX1HcfTemplatePath;

            return Path.Combine(cfg.Paths.RequireIoFolderPath(), "BX1IO.ethernetip.hcf");
        }

        // Emits the BX1 EtherNet/IP coupler: Equipment JSON + its TWO MANDATORY DTM Content artifacts
        // (Content\<uuid>_FdtProject.prj + _IOProfile.xml). Without the Content the whole topology import aborts.
        static void EmitBx1EtherNetIpDevice(Configuration.CompilerConfiguration cfg,
            Mapping.TargetDescriptor self, string eaeRoot,
            EaeDeviceWriter.EmitResult result, string solutionId)
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
                BuildEtherNetIpDeviceEquipmentJson(cfg, self, solutionId,
                    cfg.Devices.NetworkOf(self.Plc.Name).CouplerIp, self.Identity.Scanner));
            result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, equipmentPath));

            var contentDir = Path.Combine(topologyDir, "Content");
            Directory.CreateDirectory(contentDir);
            var (prjTemplate, xmlTemplate) = ResolveEtherNetIpContentTemplates(cfg);
            var registerNames = new List<string> { EquipmentJsonName };

            void CopyContent(string template, string suffix)
            {
                var destName = $"{self.Identity.EtherNetIp}_{suffix}";
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
        static string Bx1EtherNetIpDeviceType(Mapping.TargetIndex t) => EtherNetIpTarget(t).EtherNetIpDeviceType;
        static IReadOnlyList<string> Bx1Tm3bcModelFolders(Mapping.TargetIndex t) => EtherNetIpTarget(t).HwConfigModelFolders;
        // The target is the one that DECLARES an EtherNet/IP coupler, not the one with a known name:
        // moving the scanner to another device is then a device.yml edit. Two claimants is refused,
        // because the coupler type is deployed once and both would silently share it.
        static TargetDescriptor EtherNetIpTarget(Mapping.TargetIndex targets) =>
            targets.All.Where(t => !string.IsNullOrWhiteSpace(t.EtherNetIpDeviceType)).ToList() is { Count: 1 } one
                ? one[0]
                : throw new InvalidOperationException(
                    "[Topology] device.yml must declare etherNetIpDeviceType on exactly one target: " +
                    string.Join(", ", targets.All
                        .Where(t => !string.IsNullOrWhiteSpace(t.EtherNetIpDeviceType))
                        .Select(t => t.Plc.Name)) + " claim it.");

        // Deploys the saved coupler FB type from {TemplateLibrary}\EtherNetIP\ + its dfbproj entries; gate types (AND_*, NOT_*) are EAE-generated.
        static void DeployBx1EtherNetIpType(Configuration.CompilerConfiguration cfg, string eaeRoot, EaeDeviceWriter.EmitResult result)
        {
            try
            {
                var libRoot = cfg.Paths.RequireTemplateLibraryPath();
                var srcIec = Path.Combine(libRoot, "EtherNetIP", "IEC61499", Bx1EtherNetIpDeviceType(cfg.Targets));
                var srcHmi = Path.Combine(libRoot, "EtherNetIP", "HMI", Bx1EtherNetIpDeviceType(cfg.Targets));
                if (!Directory.Exists(srcIec))
                {
                    result.Warnings.Add(
                        $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType(cfg.Targets)}' NOT found in the " +
                        $"Template Library ('{srcIec}'). BX1 will fail to compile (ERR_NO_SUCH_TYPE). " +
                        "Stage it from the reference project's IEC61499 + HMI folders.");
                    return;
                }

                var dstIec = Path.Combine(eaeRoot, "IEC61499", Bx1EtherNetIpDeviceType(cfg.Targets));
                var dstHmi = Path.Combine(eaeRoot, "HMI", Bx1EtherNetIpDeviceType(cfg.Targets));
                EaeDeviceWriter.CopyDirectory(srcIec, dstIec);
                if (Directory.Exists(srcHmi)) EaeDeviceWriter.CopyDirectory(srcHmi, dstHmi);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot, dstIec));

                var dfbproj = EaeProjectLayout.FindDfbproj(eaeRoot);
                if (dfbproj != null)
                {
                    int added = DfbprojRegistrar.RegisterHardwareDeviceCat(dfbproj, Bx1EtherNetIpDeviceType(cfg.Targets));
                    result.Warnings.Add(added > 0
                        ? $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType(cfg.Targets)}' deployed + registered " +
                          $"({added} dfbproj entr{(added == 1 ? "y" : "ies")}); gate types compile-generated by EAE."
                        : $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType(cfg.Targets)}' deployed (dfbproj already current).");
                }
                else
                {
                    result.Warnings.Add(
                        $"[BX1] EtherNet/IP device type '{Bx1EtherNetIpDeviceType(cfg.Targets)}' copied but no .dfbproj " +
                        "found to register it — BX1 may not compile.");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"[BX1] EtherNet/IP device type deploy failed: {ex.Message}");
            }
        }

        // EAE compiles EIPSCANNER2.xml from the HwConfiguration device model, not the .hcf/.sysres.

        // Deploys the BX1 scanner HwConfiguration device model (TM3BC_Ethe_* + EIPSolutionsV2\<scannerId>\scanner.xml)
        // and registers it in HwConfiguration.hwconfigproj; without it EAE compiles an EMPTY scanner. BX1-only.
        static void DeployBx1HwConfigScannerModel(Configuration.CompilerConfiguration cfg,
            Mapping.TargetDescriptor self, string eaeRoot, EaeDeviceWriter.EmitResult result)
        {
            var scannerId = self.Identity.Scanner;
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

                var subs = new List<string> { Path.Combine("EIPSolutionsV2", scannerId) };
                subs.AddRange(Bx1Tm3bcModelFolders(cfg.Targets));
                foreach (var sub in subs)
                {
                    var s = Path.Combine(srcHc, sub);
                    if (Directory.Exists(s)) EaeDeviceWriter.CopyDirectory(s, Path.Combine(dstHc, sub));
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
                int reg = RegisterBx1HwConfigScannerModel(hwproj, cfg.Targets, scannerId);
                result.FilesWritten.Add(Path.GetRelativePath(eaeRoot,
                    Path.Combine(dstHc, "EIPSolutionsV2", scannerId, "scanner.xml")));
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
        public static void DeployBx1ScannerModelFinalPass(Configuration.CompilerConfiguration cfg,
            Translation.PlcAssignment target)
        {
            if (cfg == null) return;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            var result = new EaeDeviceWriter.EmitResult();
            DeployBx1HwConfigScannerModel(cfg, cfg.Targets.Of(target), eaeRoot, result);
        }

        // Aborts the Generate if the scanner model is not deployed, else EAE compiles an empty scanner.
        public static void ValidateBx1ScannerModelOrThrow(Configuration.CompilerConfiguration cfg,
            Translation.PlcAssignment target)
        {
            if (cfg == null) return;
            var scannerId = cfg.Targets.Of(target).Identity.Scanner;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)!;
            var scannerXml = Path.Combine(eaeRoot, "HwConfiguration", "EIPSolutionsV2", scannerId, "scanner.xml");
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

        static int RegisterBx1HwConfigScannerModel(string hwproj, Mapping.TargetIndex targets, string scannerId)
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

            foreach (var t in Bx1Tm3bcModelFolders(targets))
            {
                AddItem(ref cg, "Compile", $@"{t}\{t}.prop.cs", null);
                AddItem(ref cg, "Compile", $@"{t}\{t}.script.cs", null);
                AddItem(ref ng, "None", $@"{t}\{t}.prop.xml", new XElement(ns + "DependentUpon", $"{t}.fbt"));
            }
            AddItem(ref ng, "None", $@"EIPSolutionsV2\{scannerId}\scanner.xml", null);
            AddItem(ref ng, "None", $@"EIPSolutionsV2\{scannerId}\scanner_items.xml", null);

            AddItem(ref fg, "Folder", "EIPSolutionsV2", null);
            AddItem(ref fg, "Folder", $@"EIPSolutionsV2\{scannerId}", null);
            foreach (var t in Bx1Tm3bcModelFolders(targets)) AddItem(ref fg, "Folder", t, null);

            if (added > 0) xml.Save(hwproj);
            return added;
        }
        static (string Prj, string Xml) ResolveEtherNetIpContentTemplates(Configuration.CompilerConfiguration cfg)
        {
            string Pick(string name) => Path.Combine(cfg.Paths.RequireIoFolderPath(), name);
            return (Pick("BX1_EtherNetIP_FdtProject.prj"), Pick("BX1_EtherNetIP_IOProfile.xml"));
        }
    }
}
