using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Devices.Core;
using CodeGen.Translation;
using System.IO;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;

namespace CodeGen.Devices.M262
{
    public static class M262SysdevEmitter
    {
        const string LibElNs = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;
        const string ApplicationName = "WMG";
        static string DeviceName =>
            TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M262")).DeviceName
            ?? throw new InvalidOperationException(
                "device.yml declares no deviceName for target 'M262', so its system device has no name.");

        // Must match what EAE created: the .hcf Form-1 binding and the FB mirror key off these.
        static Configuration.DeviceIdentity M262Id =>
            Configuration.DeviceConfig.Identity(CodeGen.Translation.PlcAssignment.Named("M262"));

        internal static string M262SysdevId => M262Id.Sysdev;
        static string M262ResourceId => M262Id.Resource;

        public static bool M262SysdevAlreadyExists(Configuration.CompilerConfiguration cfg)
        {
            if (cfg == null) return false;
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot)) return false;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return false;
            foreach (var sysdev in Directory.EnumerateFiles(
                systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                if (IsM262SysdevFile(sysdev)) return true;
            }
            return false;
        }

        static bool IsM262SysdevFile(string sysdevPath)
        {
            try
            {
                var doc = XDocument.Load(sysdevPath);
                var root = doc.Root;
                if (root == null) return false;
                var type  = (string?)root.Attribute("Type")      ?? string.Empty;
                var nspac = (string?)root.Attribute("Namespace") ?? string.Empty;
                return string.Equals(type, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M262")).DeviceType, StringComparison.Ordinal) &&
                       string.Equals(nspac, "SE.DPAC", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        public static SysdevEmitResult Emit(GenerationContext ctx)
        {
            var cfg = ctx.Cfg;
            var allocation = ctx.Allocation;
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root: no .dfbproj above MapperConfig.SyslayPath2.");

            AlignApplicationName(eaeRoot);

            // device.yml owns the resource name; reading it here is what keeps the sysdev, the .hcf
            // symlinks and the sysres mirror agreeing about what this resource is called.
            var resourceName = TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M262")).ResourceName;

            bool justBootstrapped = false;
            var sysdevPath = FindSysdev(eaeRoot);
            if (sysdevPath == null)
            {
                sysdevPath = BootstrapM262Device(cfg, eaeRoot, resourceName);
                justBootstrapped = sysdevPath != null;
                if (sysdevPath == null)
                    throw new FileNotFoundException(
                        $"No .sysdev and no System GUID folder under {eaeRoot}\\IEC61499\\System\\ — " +
                        "cannot bootstrap M262 (the .system project root must exist).");
            }

            bool preserveDevice =
                IsM262SysdevFile(sysdevPath) && !justBootstrapped;

            string propsPath = string.Empty;
            if (!preserveDevice)
            {
                RewriteSysdev(sysdevPath, DeviceName, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M262")).DeviceType,
                    cfg.Devices.M262.TargetIp ?? string.Empty, resourceName);
                var sysresPathForRename = EaeProjectLayout.FindSysresFor(sysdevPath);
                if (sysresPathForRename != null)
                    RenameSysresName(sysresPathForRename, resourceName);
                SetTopologyEquipmentToNoConf(eaeRoot);
            }

            // DeployPlugin Properties is deploy config (not the trust certificate), so written every run.
            propsPath = WriteM262DevicePropertiesXml(cfg, sysdevPath,
                cfg.Telemetry.PublishEnabled && !cfg.Telemetry.SecureTls);

            var systemFile = FindSystemFile(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .system found under {eaeRoot}\\IEC61499\\System\\");

            var syslayPath = cfg.Paths.ActiveSyslayPath;
            var fbInstances = string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)
                ? new List<SysresFbMirror.SyslayFb>()
                : SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath);

            var sysresPath = EaeProjectLayout.FindSysresFor(sysdevPath);

            // Sweep stale extra .sysres files — EAE rejects a sysdev with 2 EMB_RES_ECO instances.
            if (sysresPath != null)
            {
                var sysdevFolderForSweep = Path.GetDirectoryName(sysresPath);
                if (!string.IsNullOrEmpty(sysdevFolderForSweep) &&
                    Directory.Exists(sysdevFolderForSweep))
                {
                    foreach (var staleSysres in Directory.EnumerateFiles(sysdevFolderForSweep, "*.sysres"))
                    {
                        if (string.Equals(staleSysres, sysresPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { File.Delete(staleSysres); }
                        catch { /* best-effort */ }
                    }
                }
            }

            int sysresMirrorCount = 0;
            if (sysresPath != null && fbInstances.Count > 0)
                // Mirror only the M262 (Feed Station) FBs — Station-2 FBs live on M580/BX1.
                sysresMirrorCount = SysresFbMirror.MirrorFbsIntoSysres(
                    sysresPath,
                    fbInstances.Where(f => SysresFbMirror.BucketFor(f.Name, allocation, ctx.Cfg) == PlcAssignment.Named("M262")).ToList(),
                    TargetBootstrap.For(PlcAssignment.Named("M262"), ctx.Layout));

            int systemMappingsAdded = 0;

            var dfbproj = EaeProjectLayout.FindDfbproj(eaeRoot);
            int registered = 0;
            if (dfbproj != null)
                registered = DfbprojRegistrar.RegisterSystemDevice(dfbproj, eaeRoot, sysdevPath);

            return new SysdevEmitResult
            {
                SysdevPath = sysdevPath,
                SystemFilePath = systemFile,
                MappingsAdded = systemMappingsAdded,
                SysresPath = sysresPath ?? string.Empty,
                SysresFbsMirrored = sysresMirrorCount,
                DevicePreserved = preserveDevice,
            };
        }


        public static string WriteM262DevicePropertiesXml(Configuration.CompilerConfiguration cfg, string sysdevPath,
                                                         bool enableInsecureApp = false)
        {
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));
            Directory.CreateDirectory(sysdevFolder);

            // The same EAE deploy plugin every target carries, so its file name has one owner.
            var propsPath = Path.Combine(sysdevFolder, Station2DeviceEmitter.DeployPluginPropertiesFile(cfg));

            // Byte-identical to the standard (non-Soft_dPAC) device properties every other PLC gets.
            var canonical = Station2DeviceEmitter.BuildStandardDeployPluginPropertiesXml(cfg, enableInsecureApp);

            if (!File.Exists(propsPath) || File.ReadAllText(propsPath) != canonical)
                File.WriteAllText(propsPath, canonical);

            return propsPath;
        }

        static string? FindSysdev(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            return Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                .FirstOrDefault(IsM262SysdevFile);
        }

        static string? FindSystemFile(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            return Directory.EnumerateFiles(systemDir, "*.system", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        // Creates the M262 logical device from scratch, the empty-start path after a Clean.
        static string? BootstrapM262Device(Configuration.CompilerConfiguration cfg, string eaeRoot, string resourceName)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            var sysGuidDir = Directory.EnumerateDirectories(systemDir)
                .FirstOrDefault(d =>
                {
                    var n = Path.GetFileName(d);
                    return Guid.TryParse(n, out _) && !n.StartsWith(".");
                });
            if (sysGuidDir == null) return null;

            var sysdevPath = Path.Combine(sysGuidDir, $"{M262SysdevId}.sysdev");
            File.WriteAllText(sysdevPath, Station2DeviceEmitter.BuildSysdevXml(cfg,
                M262SysdevId, DeviceName, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.Named("M262")).DeviceType, M262ResourceId, resourceName));

            var sysdevFolder = Path.Combine(sysGuidDir, M262SysdevId);
            Directory.CreateDirectory(sysdevFolder);
            var sysresPath = Path.Combine(sysdevFolder, $"{M262ResourceId}.sysres");
            if (!File.Exists(sysresPath))
                File.WriteAllText(sysresPath,
                    Station2DeviceEmitter.BuildSysresXml(cfg, M262ResourceId, resourceName));

            var e0601 = Path.Combine(sysdevFolder,
                CodeGen.Devices.Core.Station2DeviceEmitter.SystemDevicePropertiesFile(cfg));
            if (!File.Exists(e0601))
                File.WriteAllText(e0601, Station2DeviceEmitter.BuildEmptySystemDeviceProps(cfg));

            var simBind = Path.Combine(sysdevFolder, $"{M262SysdevId}.Simulation.Binding.xml");
            File.WriteAllText(simBind,
                Station2DeviceEmitter.BuildSimulationBindingXml(cfg, M262SysdevId,
                    TargetRegistry.Of(PlcAssignment.Named("M262")).SimulationDeployPort,
                    TargetRegistry.Of(PlcAssignment.Named("M262")).SimulationArchivePort));

            return sysdevPath;
        }

        static void RewriteSysdev(string sysdevPath, string deviceName, string deviceType, string targetIp,
            string resourceName)
        {
            var doc = XDocument.Load(sysdevPath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty sysdev: {sysdevPath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            SetAttr(root, "Name", deviceName);
            SetAttr(root, "Type", deviceType);
            SetAttr(root, "Namespace", "SE.DPAC");
            SetAttr(root, "Locked", "false");

            foreach (var ipParam in root.Elements(ns + "Parameter")
                .Where(e => string.Equals((string?)e.Attribute("Name"),
                    "IPV4Address", StringComparison.Ordinal)).ToList())
            {
                ipParam.Remove();
            }
            _ = targetIp;

            var resources = root.Element(ns + "Resources");
            if (resources == null)
            {
                resources = new XElement(ns + "Resources");
                root.Add(resources);
            }
            var res0 = resources.Elements(ns + "Resource")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), resourceName,
                    StringComparison.OrdinalIgnoreCase))
                ?? resources.Elements(ns + "Resource").FirstOrDefault();
            if (res0 == null)
            {
                res0 = new XElement(ns + "Resource",
                    new XAttribute("ID", Guid.Empty.ToString()),
                    new XAttribute("Name", resourceName));
                resources.Add(res0);
            }
            SetAttr(res0, "Name", resourceName);
            SetAttr(res0, "Type", "EMB_RES_ECO");
            SetAttr(res0, "Namespace", "Runtime.Management");

            doc.Save(sysdevPath);
        }

        // Align every .sysapp root Application Name to ApplicationName (idempotent; app is keyed by ID).
        static void AlignApplicationName(string eaeRoot)
        {
            try
            {
                var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
                if (!Directory.Exists(systemDir)) return;
                foreach (var sysapp in Directory.EnumerateFiles(systemDir, "*.sysapp", SearchOption.AllDirectories))
                {
                    try
                    {
                        var doc = XDocument.Load(sysapp, LoadOptions.PreserveWhitespace);
                        var root = doc.Root;
                        if (root == null) continue;
                        if (string.Equals((string?)root.Attribute("Name"), ApplicationName, StringComparison.Ordinal)) continue;
                        root.SetAttributeValue("Name", ApplicationName);
                        doc.Save(sysapp);
                    }
                    catch { /* best-effort per file */ }
                }
            }
            catch { /* best-effort */ }
        }

        static void RenameSysresName(string sysresPath, string resourceName)
        {
            try
            {
                var doc = XDocument.Load(sysresPath);
                var root = doc.Root;
                if (root == null) return;
                var current = (string?)root.Attribute("Name");
                if (string.Equals(current, resourceName, StringComparison.Ordinal)) return;
                SetAttr(root, "Name", resourceName);
                doc.Save(sysresPath);
            }
            catch { /* best-effort — emit pipeline continues even if sysres write fails */ }
        }

        // NOCONF every M262 Ethernet endpoint (0.0.0.0 + zero domain); the network is wired after deploy.
        static void SetTopologyEquipmentToNoConf(string eaeRoot)
        {
            try
            {
                var topoDir = Path.Combine(eaeRoot, "Topology");
                if (!Directory.Exists(topoDir)) return;
                const string ZeroDomain = "00000000-0000-0000-0000-000000000000";
                foreach (var path in Directory.EnumerateFiles(topoDir, "Equipment_*.json"))
                {
                    try
                    {
                        var text = File.ReadAllText(path);
                        var rewritten = System.Text.RegularExpressions.Regex.Replace(
                            text,
                            "\"ipAddress\"\\s*:\\s*\"[^\"]*\"",
                            "\"ipAddress\": \"0.0.0.0\"");
                        rewritten = System.Text.RegularExpressions.Regex.Replace(
                            rewritten,
                            "\"domain\"\\s*:\\s*\"[^\"]*\"",
                            $"\"domain\": \"{ZeroDomain}\"");
                        if (!string.Equals(rewritten, text, StringComparison.Ordinal))
                            File.WriteAllText(path, rewritten);
                    }
                    catch { /* skip malformed */ }
                }
            }
            catch { /* topology dir absent or locked — non-fatal */ }
        }

        static void SetAttr(XElement el, string name, string value)
        {
            var existing = el.Attribute(name);
            if (existing == null) el.SetAttributeValue(name, value);
            else existing.Value = value;
        }
    }

    public class SysdevEmitResult
    {
        public string SysdevPath { get; set; } = string.Empty;
        public string SystemFilePath { get; set; } = string.Empty;
        public int MappingsAdded { get; set; }
        public string SysresPath { get; set; } = string.Empty;
        public int SysresFbsMirrored { get; set; }
        public bool DevicePreserved { get; set; }
    }
}
