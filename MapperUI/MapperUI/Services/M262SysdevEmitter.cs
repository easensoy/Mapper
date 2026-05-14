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
        const string DeviceName = "M262";
        // Default kept for back-compat with the unused ReplaceMappingsBlock helper;
        // the live Emit path now reads cfg.ResourceName and passes it through.
        const string DefaultResourceName = "RES0";

        /// <summary>
        /// When true, the Mapper treats the M262 sysdev as user-managed: the
        /// device file, its resource declaration, the Topology Equipment
        /// JSON, and the SystemDeviceProperties XML are all left as-is
        /// whenever an M262 sysdev already exists under
        /// <c>IEC61499/System/</c>. Only application-layer content
        /// (.sysres FBNetwork, .syslay, .hcf, dfbproj registrations) is
        /// written. The flag preserves the trust binding EAE establishes
        /// on first connect to the controller.
        /// </summary>
        public const bool PreserveExistingM262Device = true;

        /// <summary>
        /// Walks <c>{eaeRoot}/IEC61499/System/</c> and returns true if any
        /// .sysdev declares an M262 dPAC device
        /// (<c>Type="M262_dPAC" Namespace="SE.DPAC"</c> on the root). The
        /// trust-preservation guard branches on this — when true, the
        /// device-layer writes (sysdev rewrite, sysres root rename, Topology
        /// Equipment JSON, SystemDeviceProperties XML, network profile
        /// writes) are skipped.
        /// </summary>
        public static bool M262SysdevAlreadyExists(MapperConfig cfg)
        {
            if (cfg == null) return false;
            var eaeRoot = DeriveEaeProjectRoot(cfg);
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

            var eaeRoot = DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            var sysdevPath = FindSysdev(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .sysdev found under {eaeRoot}\\IEC61499\\System\\");
            var resourceName = string.IsNullOrWhiteSpace(cfg.ResourceName)
                ? DefaultResourceName
                : cfg.ResourceName;

            // Trust-preservation guard. If the sysdev on disk is already an
            // M262, every device-layer write below is skipped to keep EAE's
            // trust binding with the controller intact. Only the .sysres
            // FBNetwork mirror and dfbproj registration (both application
            // content, per spec) still run further down.
            bool preserveDevice =
                PreserveExistingM262Device && IsM262SysdevFile(sysdevPath);

            string propsPath = string.Empty;
            if (!preserveDevice)
            {
                RewriteSysdev(sysdevPath, DeviceName, "M262_dPAC",
                    cfg.M262TargetIp ?? string.Empty, resourceName);
                // While we have the EAE root, keep the .sysres root's Name
                // attribute in sync so EAE's Deploy & Diagnostic tree doesn't
                // show RES0 + RES0 at the same time. (Skipped when the
                // device is preserved — the resource declaration is part of
                // the trust-bound device record.)
                var sysresPathForRename = FindSysresFor(sysdevPath);
                if (sysresPathForRename != null)
                    RenameSysresName(sysresPathForRename, resourceName);
                // Force every Ethernet interface on the M262 Topology
                // equipment record to NOCONF (IP 0.0.0.0, zero domain). EAE's
                // device card shows "Logical network: NOCONF" and
                // "IPV4 Address: 0.0.0.0" on both ETH1 and ETH2. User wires
                // the network manually after deploy — Mapper never bakes a
                // default IP into Topology. Skipped under preserveDevice
                // because re-writing IPs invalidates the controller-side
                // trust certificate.
                SetTopologyEquipmentToNoConf(eaeRoot);
                propsPath = WriteM262DevicePropertiesXml(sysdevPath);
            }

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
                DevicePreserved = preserveDevice,
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

            // Strip any IPV4Address parameter. Emitting one makes EAE flag the
            // device as DefaultNetwork; we want NoCONF (no preset network) so
            // the user can wire it manually after deploy. Leaves a baseline
            // IPV4Address in place if present in the source — but our
            // post-Wiper sysdev never has one, so result is NoCONF.
            foreach (var ipParam in root.Elements(ns + "Parameter")
                .Where(e => string.Equals((string?)e.Attribute("Name"),
                    "IPV4Address", StringComparison.Ordinal)).ToList())
            {
                ipParam.Remove();
            }
            // targetIp arg kept for signature compatibility but no longer used.
            _ = targetIp;

            var resources = root.Element(ns + "Resources");
            if (resources == null)
            {
                resources = new XElement(ns + "Resources");
                root.Add(resources);
            }
            // Find the existing Resource entry by Name OR by being the only Resource child;
            // tolerates either RES0 (legacy) or the new resourceName already in place.
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

        /// <summary>
        /// Renames the .sysres root's <c>Name</c> attribute to <paramref name="resourceName"/>
        /// (e.g. "RES0"). EAE Deploy &amp; Diagnostic shows this name in the runtime
        /// tree, so it must match what the .sysdev's &lt;Resource&gt; entry says or EAE
        /// flags the project as inconsistent. Idempotent — safe to re-run.
        /// </summary>
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

            // Mirror every CAT/composite/HMI type that EAE expects to see
            // mapped to M262.M262_RES. Each mirrored FB carries a Mapping
            // attribute pointing back at the syslay FB, which is how EAE
            // shows it under Devices > M262 > M262_RES > Local.
            var keepTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "Five_State_Actuator_CAT",
                "Five_State_Actuator_No_Sensors_CAT",
                "Seven_State_Actuator_CAT",
                "Sensor_Bool_CAT",
                "PLC_RW_M262",
                "Area",
                "Area_CAT",
                "Station",
                "Station_CAT",
                "Process1_Generic",
                "CaSAdptrTerminator",
                "Robot_Task_CAT",
            };

            int added = 0;
            foreach (var fb in syslayFbs)
            {
                if (string.IsNullOrEmpty(fb.Id)) continue;
                if (existingMappings.Contains(fb.Id)) continue;
                if (existingNames.Contains(fb.Name)) continue;
                if (!keepTypes.Contains(fb.Type)) continue;
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

        /// <summary>
        /// Walk <c>{eaeRoot}/Topology/</c> for any <c>Equipment_*.json</c>
        /// describing the M262 dPAC and force every Ethernet endpoint to
        /// <c>"ipAddress": "0.0.0.0"</c> + <c>"domain":
        /// "00000000-0000-0000-0000-000000000000"</c>. EAE then renders the
        /// Logical network field as NOCONF on every interface card.
        /// Best-effort: silently skips files that can't be parsed; never
        /// fails the run.
        /// </summary>
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
                        // Lightweight regex rewrite — JSON layout from EAE is
                        // stable enough to skip a full deserialise/round-trip.
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
        /// <summary>True when the trust-preservation guard skipped every
        /// device-layer write (sysdev rewrite, sysres root rename, Topology
        /// Equipment JSON, SystemDeviceProperties XML). Application content
        /// — .sysres FBNetwork mirror, dfbproj registrations — still
        /// ran.</summary>
        public bool DevicePreserved { get; set; }
    }
}
