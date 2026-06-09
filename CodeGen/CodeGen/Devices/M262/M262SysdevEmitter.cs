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
                var sysresPathForRename = EaeProjectLayout.FindSysresFor(sysdevPath);
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
                ? new List<SysresFbMirror.SyslayFb>()
                : SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath);

            var sysresPath = EaeProjectLayout.FindSysresFor(sysdevPath);

            // Sweep any extra .sysres files in the M262 sysdev folder. The
            // canonical sysres is the one EaeProjectLayout returns above
            // (1459BCD12760907D.sysres — Name="M262_RES"); anything else is a
            // leftover from an older deploy that used a different resource
            // ID/name (typically "RES0"). EAE compile rejects a sysdev that
            // contains 2 instances of Runtime.Management.EMB_RES_ECO, so the
            // stale file MUST go. Observed 2026-05-27: M262 folder carried
            // both 1459BCD12760907D.sysres (M262_RES, active) AND
            // 5ACDAFFD2183E4AD.sysres (RES0, stale), and the EAE message log
            // surfaced "Device M580 contains 2 instances of EMB_RES_ECO" —
            // note the misleading device name in EAE's message; the actual
            // duplicate was on M262 but EAE reports it against the next
            // device in the system tree. Same sweep pattern Station2DeviceEmitter
            // already uses for M580 + BX1.
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
                // Mirror only the FBs that belong on the M262 (Feed Station).
                // The Station-2 FBs (Shaft/Clamp/Cover/Bearing/sensors and the
                // Station2/Assembly_Station/Stn2_Term structural FBs) now live
                // on the M580/BX1 resources, emitted by EmitStation2Sysres. If
                // they were left in this bucket too they would be mapped onto
                // BOTH the M262 and a Station-2 PLC, which EAE flags as a
                // duplicate instance mapping in Solution Integrity.
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
