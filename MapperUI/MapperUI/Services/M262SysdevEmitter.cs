using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
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
            RewriteSysdev(sysdevPath, DeviceName, "M262_dPAC", cfg.M262TargetIp ?? string.Empty);
            var propsPath = WriteM262DevicePropertiesXml(sysdevPath);

            var systemFile = FindSystemFile(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .system found under {eaeRoot}\\IEC61499\\System\\");

            var syslayPath = cfg.ActiveSyslayPath;
            var fbInstances = string.IsNullOrWhiteSpace(syslayPath) || !File.Exists(syslayPath)
                ? new List<SyslayFb>()
                : ReadSyslayTopLevelFbs(syslayPath);

            var sysresPath = FindSysresFor(sysdevPath);
            int sysresMirrorCount = 0;
            if (sysresPath != null && fbInstances.Count > 0)
                sysresMirrorCount = MirrorFbsIntoSysres(sysresPath, fbInstances);

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
            };
        }

        const string M262DevicePropertiesPluginGuid = "F513CAE3-7194-4086-936C-02912EA0B352";

        public static string WriteM262DevicePropertiesXml(string sysdevPath)
        {
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

        static void RewriteSysdev(string sysdevPath, string deviceName, string deviceType, string targetIp)
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

            var ipParam = root.Elements(ns + "Parameter").FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("Name"), "IPV4Address", StringComparison.Ordinal));
            if (ipParam == null)
            {
                ipParam = new XElement(ns + "Parameter",
                    new XAttribute("Name", "IPV4Address"),
                    new XAttribute("Value", targetIp));
                var firstChild = root.Elements().FirstOrDefault();
                if (firstChild != null) firstChild.AddBeforeSelf(ipParam);
                else root.Add(ipParam);
            }
            else
            {
                SetAttr(ipParam, "Value", targetIp);
            }

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
            var to = $"{DeviceName}.{ResourceName}";
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

        public static List<string> ReadSyslayTopLevelFbNames(string syslayPath)
        {
            return ReadSyslayTopLevelFbs(syslayPath).Select(fb => fb.Name).ToList();
        }

        public record SyslayFbParameter(string Name, string Value);
        public record SyslayFb(string Id, string Name, string Type, string Namespace,
            string X, string Y, List<SyslayFbParameter> Parameters);

        public static List<SyslayFb> ReadSyslayTopLevelFbs(string syslayPath)
        {
            var doc = XDocument.Load(syslayPath);
            var root = doc.Root;
            if (root == null) return new List<SyslayFb>();
            XNamespace ns = root.GetDefaultNamespace();
            var net = root.Element(ns + "SubAppNetwork") ?? root.Element(ns + "FBNetwork");
            if (net == null) return new List<SyslayFb>();
            return net.Elements(ns + "FB")
                .Select(e => new SyslayFb(
                    Id:        (string?)e.Attribute("ID")        ?? string.Empty,
                    Name:      (string?)e.Attribute("Name")      ?? string.Empty,
                    Type:      (string?)e.Attribute("Type")      ?? string.Empty,
                    Namespace: (string?)e.Attribute("Namespace") ?? "Main",
                    X:         (string?)e.Attribute("x")         ?? "0",
                    Y:         (string?)e.Attribute("y")         ?? "0",
                    Parameters: e.Elements(ns + "Parameter")
                        .Select(p => new SyslayFbParameter(
                            (string?)p.Attribute("Name")  ?? string.Empty,
                            (string?)p.Attribute("Value") ?? string.Empty))
                        .Where(p => !string.IsNullOrEmpty(p.Name))
                        .ToList()))
                .Where(fb => !string.IsNullOrWhiteSpace(fb.Name))
                .ToList();
        }

        public const string M262IoFbId        = "E786D6371CF444F9";
        public const string DpacFullInitFbId  = "593A8F4FDEA0A668";
        public const string PlcStartFbId      = "3DB1FB0F578E5F1E";

        public static int MirrorFbsIntoSysres(string sysresPath, List<SyslayFb> syslayFbs)
        {
            if (!File.Exists(sysresPath)) return 0;
            var doc = XDocument.Load(sysresPath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty sysres: {sysresPath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            var network = root.Element(ns + "FBNetwork");
            if (network == null)
            {
                network = new XElement(ns + "FBNetwork");
                root.Add(network);
            }

            EnsureSystemFb(network, ns,
                id: M262IoFbId, name: "M262IO", type: "PLC_RW_M262", nsAttr: "Main",
                mapping: ComputeMirrorId(M262IoFbId), x: 3760, y: 1020,
                loaded: false);
            EnsureSystemFb(network, ns,
                id: DpacFullInitFbId, name: "FB1", type: "DPAC_FULLINIT", nsAttr: "SE.DPAC",
                mapping: null, x: 1900, y: 140,
                loaded: true);
            EnsureSystemFb(network, ns,
                id: PlcStartFbId, name: "FB2", type: "plcStart", nsAttr: "SE.AppBase",
                mapping: null, x: 820, y: 660,
                loaded: true,
                parameters: new[] { ("Prio", "10"), ("Delay", "T#1000ms") });

            var existingMappings = new HashSet<string>(
                network.Elements(ns + "FB")
                    .Select(e => (string?)e.Attribute("Mapping") ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);
            var existingNames = new HashSet<string>(
                network.Elements(ns + "FB")
                    .Select(e => (string?)e.Attribute("Name") ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);

            int added = 0;
            foreach (var fb in syslayFbs)
            {
                if (string.IsNullOrEmpty(fb.Id)) continue;
                if (existingMappings.Contains(fb.Id)) continue;
                if (existingNames.Contains(fb.Name)) continue;
                var mirrorId = ComputeMirrorId(fb.Id);
                var fbElement = new XElement(ns + "FB",
                    new XAttribute("ID",        mirrorId),
                    new XAttribute("Name",      fb.Name),
                    new XAttribute("Type",      fb.Type),
                    new XAttribute("Namespace", fb.Namespace),
                    new XAttribute("Mapping",   fb.Id),
                    new XAttribute("x",         fb.X),
                    new XAttribute("y",         fb.Y));

                foreach (var p in fb.Parameters)
                {
                    fbElement.Add(new XElement(ns + "Parameter",
                        new XAttribute("Name",  p.Name),
                        new XAttribute("Value", p.Value)));
                }

                network.Add(fbElement);
                added++;
            }

            if (added > 0) doc.Save(sysresPath);
            return added;
        }

        static void EnsureSystemFb(XElement network, XNamespace ns,
            string id, string name, string type, string nsAttr,
            string? mapping, int x, int y, bool loaded,
            (string Name, string Value)[]? parameters = null)
        {
            foreach (var stale in network.Elements(ns + "FB")
                .Where(e => string.Equals((string?)e.Attribute("ID"), id, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                stale.Remove();
            }

            var fb = new XElement(ns + "FB",
                new XAttribute("ID",        id),
                new XAttribute("Name",      name),
                new XAttribute("Type",      type),
                new XAttribute("Namespace", nsAttr));
            if (!string.IsNullOrEmpty(mapping)) fb.SetAttributeValue("Mapping", mapping);
            fb.SetAttributeValue("x", x);
            fb.SetAttributeValue("y", y);
            if (loaded) fb.SetAttributeValue("Loaded", "true");

            if (parameters != null)
            {
                foreach (var (pn, pv) in parameters)
                {
                    fb.Add(new XElement(ns + "Parameter",
                        new XAttribute("Name",  pn),
                        new XAttribute("Value", pv)));
                }
            }

            network.Add(fb);
        }

        public static string? FindSysresFor(string sysdevPath)
        {
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));
            if (!Directory.Exists(sysdevFolder)) return null;
            return Directory.EnumerateFiles(sysdevFolder, "*.sysres", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }

        static string ComputeMirrorId(string syslayId)
        {
            if (syslayId.Length >= 16)
            {
                var first = syslayId[0];
                int v = Convert.ToInt32(first.ToString(), 16);
                var flipped = (v ^ 0x8).ToString("X");
                return flipped + syslayId.Substring(1, 15);
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mirror:" + syslayId));
            return Convert.ToHexString(bytes).Substring(0, 16);
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
    }
}
