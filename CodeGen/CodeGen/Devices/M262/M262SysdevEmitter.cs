using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Translation;

namespace CodeGen.Devices.M262
{
    public static class M262SysdevEmitter
    {
        const string LibElNs = "https://www.se.com/LibraryElements";
        const string ApplicationName = "WMG";
        const string DeviceName = "M262";
        const string DefaultResourceName = "RES0";

        // GUID + resource ID must match what EAE created (the .hcf Form-1 binding + FB mirror key off them).
        const string M262SysdevId   = "00000000-0000-0000-0000-000000000002";
        const string M262ResourceId = "1459BCD12760907D";

        // When true, an existing M262 sysdev is left as-is to preserve EAE's controller trust binding.
        public const bool PreserveExistingM262Device = true;

        public static bool M262SysdevAlreadyExists(MapperConfig cfg)
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
                return string.Equals(type, "M262_dPAC", StringComparison.Ordinal) &&
                       string.Equals(nspac, "SE.DPAC", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        public static SysdevEmitResult Emit(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            AlignApplicationName(eaeRoot);

            var resourceName = string.IsNullOrWhiteSpace(cfg.ResourceName)
                ? DefaultResourceName
                : cfg.ResourceName;

            // Bootstrap the M262 sysdev from scratch when absent (the empty-start path after Clean).
            bool justBootstrapped = false;
            var sysdevPath = FindSysdev(eaeRoot);
            if (sysdevPath == null)
            {
                sysdevPath = BootstrapM262Device(eaeRoot, resourceName);
                justBootstrapped = sysdevPath != null;
                if (sysdevPath == null)
                    throw new FileNotFoundException(
                        $"No .sysdev and no System GUID folder under {eaeRoot}\\IEC61499\\System\\ — " +
                        "cannot bootstrap M262 (the .system project root must exist).");
            }

            // Preserve an existing device (skip device-layer writes) to keep EAE's controller trust intact.
            bool preserveDevice =
                PreserveExistingM262Device && IsM262SysdevFile(sysdevPath) && !justBootstrapped;

            string propsPath = string.Empty;
            if (!preserveDevice)
            {
                RewriteSysdev(sysdevPath, DeviceName, "M262_dPAC",
                    cfg.M262TargetIp ?? string.Empty, resourceName);
                var sysresPathForRename = EaeProjectLayout.FindSysresFor(sysdevPath);
                if (sysresPathForRename != null)
                    RenameSysresName(sysresPathForRename, resourceName);
                // NOCONF the M262 Ethernet interfaces (IP 0.0.0.0) — the user wires the network after deploy.
                SetTopologyEquipmentToNoConf(eaeRoot);
            }

            // DeployPlugin Properties is deploy config (not the trust certificate), so written every run.
            propsPath = WriteM262DevicePropertiesXml(sysdevPath,
                cfg.MqttPublishEnabled && !cfg.MqttSecureTls);

            var systemFile = FindSystemFile(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .system found under {eaeRoot}\\IEC61499\\System\\");

            var syslayPath = cfg.ActiveSyslayPath;
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
                    fbInstances.Where(f => SysresFbMirror.BucketFor(f.Name) == PlcAssignment.M262).ToList());

            int systemMappingsAdded = 0;

            var dfbproj = FindDfbproj(eaeRoot);
            int registered = 0;
            if (dfbproj != null)
                registered = DfbprojRegistrar.RegisterSystemDevice(dfbproj, eaeRoot, sysdevPath);

            return new SysdevEmitResult
            {
                SysdevPath = sysdevPath,
                SystemFilePath = systemFile,
                MappingsAdded = systemMappingsAdded,
                FbInstancesMapped = fbInstances.Select(f => f.Name).ToList(),
                DfbprojEntriesRegistered = registered,
                PropertiesXmlPath = propsPath,
                SysresPath = sysresPath ?? string.Empty,
                SysresFbsMirrored = sysresMirrorCount,
                DevicePreserved = preserveDevice,
            };
        }

        const string M262DevicePropertiesPluginGuid = "F513CAE3-7194-4086-936C-02912EA0B352";

        public static string WriteM262DevicePropertiesXml(string sysdevPath, bool enableInsecureApp = false)
        {
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));
            Directory.CreateDirectory(sysdevFolder);

            var propsPath = Path.Combine(sysdevFolder,
                $"{M262DevicePropertiesPluginGuid}.Properties.xml");

            // A plain mqtt:// broker needs the SecurityApp -> InsecureApplication override or MQTT faults RC101.
            string canonical =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<SystemDeviceProperties xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
                    "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                    "xmlns=\"http://www.nxtControl.com/DeviceProperties\">\r\n" +
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
                (enableInsecureApp
                    ? "    <GroupProperty Name=\"SecurityApp\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                      "      <GroupProperty Name=\"InsecureApplication\" Expanded=\"true\" Enabled=\"true\">\r\n" +
                      "        <Property Name=\"Enable\" Value=\"True\" IsPassword=\"false\" />\r\n" +
                      "      </GroupProperty>\r\n" +
                      "    </GroupProperty>\r\n"
                    : string.Empty) +
                "  </GroupProperty>\r\n" +
                "</SystemDeviceProperties>";

            if (!File.Exists(propsPath) || File.ReadAllText(propsPath) != canonical)
                File.WriteAllText(propsPath, canonical);

            return propsPath;
        }

        static string? FindSysdev(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            // Match the M262 device specifically (Type="M262_dPAC") — never a sibling M580/BX1 sysdev.
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

        // Creates the M262 logical device from scratch when none exists (the empty-start path after Clean).
        static string? BootstrapM262Device(string eaeRoot, string resourceName)
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
            File.WriteAllText(sysdevPath, Station2DeviceEmitter.BuildSysdevXml(
                M262SysdevId, DeviceName, "M262_dPAC", M262ResourceId, resourceName));

            var sysdevFolder = Path.Combine(sysGuidDir, M262SysdevId);
            Directory.CreateDirectory(sysdevFolder);
            var sysresPath = Path.Combine(sysdevFolder, $"{M262ResourceId}.sysres");
            if (!File.Exists(sysresPath))
                File.WriteAllText(sysresPath,
                    Station2DeviceEmitter.BuildSysresXml(M262ResourceId, resourceName));

            var e0601 = Path.Combine(sysdevFolder,
                "E0601B81-4A3A-4A96-B6C2-007BDC680D59.Properties.xml");
            if (!File.Exists(e0601))
                File.WriteAllText(e0601, Station2DeviceEmitter.BuildEmptySystemDeviceProps());

            var simBind = Path.Combine(sysdevFolder, $"{M262SysdevId}.Simulation.Binding.xml");
            File.WriteAllText(simBind,
                Station2DeviceEmitter.BuildSimulationBindingXml(M262SysdevId, 51499, 51496));

            return sysdevPath;
        }

        static string? FindDfbproj(string eaeRoot)
        {
            var iec = Path.Combine(eaeRoot, "IEC61499");
            if (!Directory.Exists(iec)) return null;
            return Directory.EnumerateFiles(iec, "*.dfbproj").FirstOrDefault();
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

            // Strip any IPV4Address parameter so EAE renders the device as NoCONF (no preset network).
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

        static int ReplaceMappingsBlock(string systemFilePath, List<string> fbInstances)
        {
            var doc = XDocument.Load(systemFilePath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty .system: {systemFilePath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            foreach (var stale in root.Elements(ns + "Mappings").ToList())
                stale.Remove();

            var mappings = new XElement(ns + "Mappings");
            var to = $"{DeviceName}.{DefaultResourceName}";
            foreach (var fbName in fbInstances)
            {
                if (string.IsNullOrWhiteSpace(fbName)) continue;
                mappings.Add(new XElement(ns + "Mapping",
                    new XAttribute("From", $"{ApplicationName}.{fbName}"),
                    new XAttribute("To",   to)));
            }
            root.Add(mappings);
            doc.Save(systemFilePath);
            return mappings.Elements(ns + "Mapping").Count();
        }

        // Force every M262 Topology Equipment Ethernet endpoint to ipAddress 0.0.0.0 + zero domain (NOCONF).
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
        public List<string> FbInstancesMapped { get; set; } = new();
        public int DfbprojEntriesRegistered { get; set; }
        public string PropertiesXmlPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public int SysresFbsMirrored { get; set; }
        public bool DevicePreserved { get; set; }
    }
}
