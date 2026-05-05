using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    /// <summary>
    /// Rewrites the EAE project's .sysdev so EcoRT_0 is declared as <c>Type="M262_dPAC"
    /// Namespace="SE.DPAC"</c> with a single <c>RES0</c> resource of
    /// <c>Type="EMB_RES_ECO" Namespace="Runtime.Management"</c>, and adds a
    /// <c>&lt;Parameter Name="IPV4Address" Value="..."/&gt;</c> child driven by
    /// <see cref="MapperConfig.M262TargetIp"/>.
    ///
    /// Then patches the .system file's <c>&lt;Mappings&gt;</c> block so every top-level
    /// FB instance in the syslay maps to <c>EcoRT_0.RES0</c>. Generalisation walks the
    /// syslay's <c>SubAppNetwork</c> rather than hardcoding instance names, so a future
    /// fixture with more (or fewer) FBs needs no Mapper change.
    ///
    /// Without this step EAE's deploy targets the workstation runtime, the .hcf is
    /// ignored, and the controller never receives the application.
    /// </summary>
    public static class M262SysdevEmitter
    {
        const string LibElNs = "https://www.se.com/LibraryElements";
        const string ApplicationName = "APP1";
        const string DeviceName = "EcoRT_0";
        const string ResourceName = "RES0";

        public static SysdevEmitResult Emit(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var eaeRoot = DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            var sysdevPath = FindSysdev(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .sysdev found under {eaeRoot}\\IEC61499\\System\\");

            RewriteSysdev(sysdevPath, cfg.M262TargetIp ?? string.Empty);

            // Emit the F513CAE3-...Properties.xml file alongside the sysdev. EAE
            // raises a "Solution Integrity" warning at project open if this file is
            // missing — even though it's "just" the device-level deploy/boot defaults.
            // Schema reverse-engineered from
            // SMC_Rig_Expo .../6fe9f94f-.../F513CAE3-...Properties.xml (M262 dPAC).
            var propsPath = WriteM262DevicePropertiesXml(sysdevPath);

            var systemFile = FindSystemFile(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .system found under {eaeRoot}\\IEC61499\\System\\");

            var syslayPath = cfg.ActiveSyslayPath;
            var fbInstances = string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)
                ? new List<string>()
                : ReadSyslayTopLevelFbNames(syslayPath);

            int added = EnsureMappingsPerFb(systemFile, fbInstances);

            // Register the .sysdev (and matching .hcf + Properties.xml siblings) in
            // the project's .dfbproj as <None Include> entries with
            // IEC61499Type=SystemDevice. Idempotent — also de-duplicates broken
            // duplicate child elements.
            var dfbproj = FindDfbproj(eaeRoot);
            int registered = 0;
            if (dfbproj != null)
                registered = DfbprojRegistrar.RegisterSystemDevice(dfbproj, eaeRoot, sysdevPath);

            return new SysdevEmitResult
            {
                SysdevPath = sysdevPath,
                SystemFilePath = systemFile,
                MappingsAdded = added,
                FbInstancesMapped = fbInstances,
                DfbprojEntriesRegistered = registered,
                PropertiesXmlPath = propsPath,
            };
        }

        // --- M262 device-properties XML emission ---

        // Plugin GUID copied verbatim from canonical M262 dPAC Properties.xml file
        // names in SMC_Rig_Expo + LibCustomization. EAE keys the device's deploy/boot
        // defaults off this exact filename.
        const string M262DevicePropertiesPluginGuid = "F513CAE3-7194-4086-936C-02912EA0B352";

        /// <summary>
        /// Writes <c>{sysdev-folder}/F513CAE3-...Properties.xml</c> with the canonical
        /// M262 deploy/boot defaults: ClearBeforeDeploy=True, AutoStart=True, BootMode=Run.
        /// Idempotent — only writes when the file is absent or content differs from canonical.
        /// </summary>
        public static string WriteM262DevicePropertiesXml(string sysdevPath)
        {
            // Per-device folder convention: alongside the sysdev sit a same-stem folder
            // and the device-level Properties.xml + .hcf + Simulation.Binding.xml etc.
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));
            Directory.CreateDirectory(sysdevFolder);

            var propsPath = Path.Combine(sysdevFolder,
                $"{M262DevicePropertiesPluginGuid}.Properties.xml");

            const string canonical =
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
                "  </GroupProperty>\r\n" +
                "</SystemDeviceProperties>";

            if (!File.Exists(propsPath) || File.ReadAllText(propsPath) != canonical)
                File.WriteAllText(propsPath, canonical);

            return propsPath;
        }

        // --- file discovery ---

        public static string? DeriveEaeProjectRoot(MapperConfig cfg)
        {
            var path = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dir = Path.GetDirectoryName(path);
            while (dir != null)
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dfbproj").Any())
                    return Path.GetDirectoryName(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static string? FindSysdev(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            return Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        static string? FindSystemFile(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            return Directory.EnumerateFiles(systemDir, "*.system", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        static string? FindDfbproj(string eaeRoot)
        {
            var iec = Path.Combine(eaeRoot, "IEC61499");
            if (!Directory.Exists(iec)) return null;
            return Directory.EnumerateFiles(iec, "*.dfbproj").FirstOrDefault();
        }

        // --- sysdev rewrite ---

        static void RewriteSysdev(string sysdevPath, string targetIp)
        {
            var doc = XDocument.Load(sysdevPath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty sysdev: {sysdevPath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            // Force device declaration. If the baseline shipped Type="Soft_dPAC" we
            // overwrite it; if the attribute is missing we add it.
            SetAttr(root, "Name", DeviceName);
            SetAttr(root, "Type", "M262_dPAC");
            SetAttr(root, "Namespace", "SE.DPAC");

            // Idempotent IPV4Address Parameter.
            var ipParam = root.Elements(ns + "Parameter").FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("Name"), "IPV4Address", StringComparison.Ordinal));
            if (ipParam == null)
            {
                ipParam = new XElement(ns + "Parameter",
                    new XAttribute("Name", "IPV4Address"),
                    new XAttribute("Value", targetIp));
                // Insert as first Parameter child (before any <Resources>) so EAE
                // parses it before trying to bind the runtime.
                var firstNonAttr = root.Elements().FirstOrDefault();
                if (firstNonAttr != null) firstNonAttr.AddBeforeSelf(ipParam);
                else root.Add(ipParam);
            }
            else
            {
                SetAttr(ipParam, "Value", targetIp);
            }

            // Ensure exactly one Resource RES0 of Type="EMB_RES_ECO".
            var resources = root.Element(ns + "Resources");
            if (resources == null)
            {
                resources = new XElement(ns + "Resources");
                root.Add(resources);
            }
            var res0 = resources.Elements(ns + "Resource")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), ResourceName,
                    StringComparison.OrdinalIgnoreCase));
            if (res0 == null)
            {
                res0 = new XElement(ns + "Resource",
                    new XAttribute("ID", Guid.Empty.ToString()),
                    new XAttribute("Name", ResourceName));
                resources.Add(res0);
            }
            SetAttr(res0, "Name", ResourceName);
            SetAttr(res0, "Type", "EMB_RES_ECO");
            SetAttr(res0, "Namespace", "Runtime.Management");

            doc.Save(sysdevPath);
        }

        // --- syslay walk: every top-level FB Name attribute under SubAppNetwork ---

        public static List<string> ReadSyslayTopLevelFbNames(string syslayPath)
        {
            var doc = XDocument.Load(syslayPath);
            var root = doc.Root;
            if (root == null) return new List<string>();
            XNamespace ns = root.GetDefaultNamespace();
            var net = root.Element(ns + "SubAppNetwork") ?? root.Element(ns + "FBNetwork");
            if (net == null) return new List<string>();
            return net.Elements(ns + "FB")
                .Select(e => (string?)e.Attribute("Name") ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
        }

        // --- .system Mappings patch (idempotent, generalised) ---

        static int EnsureMappingsPerFb(string systemFilePath, List<string> fbInstances)
        {
            var doc = XDocument.Load(systemFilePath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty .system: {systemFilePath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            var mappings = root.Element(ns + "Mappings");
            if (mappings == null)
            {
                mappings = new XElement(ns + "Mappings");
                root.Add(mappings);
            }

            // Snapshot the existing edges to avoid duplicate-insertion churn on re-runs.
            var existing = new HashSet<(string From, string To)>();
            foreach (var m in mappings.Elements(ns + "Mapping"))
            {
                var f = (string?)m.Attribute("From") ?? string.Empty;
                var t = (string?)m.Attribute("To")   ?? string.Empty;
                existing.Add((f, t));
            }

            var to = $"{DeviceName}.{ResourceName}";
            int added = 0;
            foreach (var fbName in fbInstances)
            {
                var from = $"{ApplicationName}.{fbName}";
                if (existing.Contains((from, to))) continue;
                mappings.Add(new XElement(ns + "Mapping",
                    new XAttribute("From", from),
                    new XAttribute("To",   to)));
                added++;
            }

            if (added > 0) doc.Save(systemFilePath);
            return added;
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
    }
}
