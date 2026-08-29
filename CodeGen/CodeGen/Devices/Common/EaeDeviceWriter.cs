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
    public static class EaeDeviceWriter
    {
        internal const string LibElNs = "https://www.se.com/LibraryElements";

        // The name EAE shows the device under, declared beside its addresses. Refused rather than
        // defaulted: an unnamed device is one an engineer cannot find in the tree.
        internal static string DeviceNameOf(Mapping.TargetIndex targets, CodeGen.Translation.PlcAssignment plc) =>
            targets.Of(plc).DeviceName
            ?? throw new InvalidOperationException(
                $"device.yml declares no deviceName for target '{plc}', so its system device has no name.");

        static Configuration.InstallationIdentity Install(Configuration.CompilerConfiguration cfg) =>
            cfg.Devices.Installation;

        // EVERY IDENTITY COMES FROM THE DESCRIPTOR OF THE TARGET BEING EMITTED.
        //
        // These were two blocks of statics keyed on the names "M580" and "BX1", so a second controller of
        // either kind could only be emitted with the first one's sysdev, resource, equipment and scanner
        // ids - identities EAE requires to be unique per device.

        // EAE reads each device's Properties file by plugin GUID: DeployPlugin registers the .hcf, SystemDeviceProperties holds per-device settings.
        internal static string DeployPluginPropertiesFile(Configuration.CompilerConfiguration cfg) =>
            Install(cfg).DeployPluginProperties;
        internal static string SystemDevicePropertiesFile(Configuration.CompilerConfiguration cfg) =>
            Install(cfg).SystemDeviceProperties;

        // NOCONF sentinel — no broadcast domain binding.
        internal const string NoConfDomainUuid = Artefacts.EaeAbi.NoBroadcastDomain;

        // DomainTag MUST equal the live SolutionId, else EAE rejects the topology import.
        internal const string FallbackSolutionUuid = Artefacts.EaeAbi.UnknownSolution;

        public sealed class EmitResult
        {
            public List<string> FilesWritten { get; } = new();
            public List<string> Warnings { get; } = new();
            public int TopologyProjEntriesAdded { get; set; }
            }

        // The M580 dPAC. Its sysres is NAME-scoped, so the resource keeps its declared name.
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

        internal static void EmitOnePlc(Configuration.CompilerConfiguration cfg,
            Mapping.TargetDescriptor self, string eaeRoot, string systemGuidDir,
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
                    int added = DfbprojRegistrar.RegisterSystemDevice(dfbproj, eaeRoot, sysdevPath, self);
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
        internal static string BuildDeployPluginPropertiesXml(Configuration.CompilerConfiguration cfg, bool bootProject, bool enableInsecureApp) =>
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



        internal static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }


        internal static void CleanupStaleTopologyJson(string eaeRoot, string jsonName, EmitResult result)
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
        internal static (string? GuidId, string? Name) ReadHcfResourceIdentity(string? hcfPath)
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
