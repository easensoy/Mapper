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

            // M262-only emission. The M580 sidecar was tried but EAE's TopologyManager
            // raises NRE on the Physical Devices canvas without a matching
            // Topology/Equipment_M580dPAC_1.json — and we don't deploy to the M580 for
            // tomorrow's Feeder test. Strip it: one sysdev = one device = no canvas crash.
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

            // Mirror each top-level syslay FB into the .sysres FBNetwork with
            // Mapping="<syslay FB ID>". This is the actual binding mechanism EAE
            // reads — the .system <Mapping From=.. To=..> elements that we used
            // to write are ignored by EAE for the canvas-resource binding (they
            // appear empty in SMC_Rig_Expo's working setup too).
            var sysresPath = FindSysresFor(sysdevPath);
            int sysresMirrorCount = 0;
            if (sysresPath != null && fbInstances.Count > 0)
                sysresMirrorCount = MirrorFbsIntoSysres(sysresPath, fbInstances);

            // Do NOT touch .system. EAE's binding mechanism is the per-FB
            // Mapping="<syslay-FB-ID>" attribute inside .sysres's FBNetwork (handled
            // above by MirrorFbsIntoSysres). Verified against Station1 + SMC_Rig_Expo
            // working M262 references — both have an empty .system (root + VersionInfo
            // only) and full FB mirrors in .sysres. Writing <Mapping> elements to
            // .system pollutes the file and is ignored by EAE's binding resolver.
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

        static void RewriteSysdev(string sysdevPath, string deviceName, string deviceType, string targetIp)
        {
            var doc = XDocument.Load(sysdevPath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty sysdev: {sysdevPath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            // Force device declaration. If the baseline shipped a different Type we
            // overwrite it; if attributes are missing we add them.
            SetAttr(root, "Name", deviceName);
            SetAttr(root, "Type", deviceType);
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
            return ReadSyslayTopLevelFbs(syslayPath).Select(fb => fb.Name).ToList();
        }

        public record SyslayFbParameter(string Name, string Value);
        public record SyslayFb(string Id, string Name, string Type, string Namespace,
            string X, string Y, List<SyslayFbParameter> Parameters);

        /// <summary>
        /// Returns each top-level FB in the syslay's SubAppNetwork as a record so the
        /// sysres mirror can stamp <c>Mapping="&lt;syslay FB ID&gt;"</c> on each entry
        /// AND copy its Parameter children verbatim. EAE's manual mapping action
        /// (verified against a manual map of Feeder in Demonstrator) writes both the
        /// Mapping attribute and copies all Parameters from the syslay FB into the
        /// sysres FB — without the Parameters the runtime executes against the
        /// resource-side defaults instead of the syslay's actual values.
        /// </summary>
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

        /// <summary>
        /// Mirrors every top-level syslay FB into the sysres's <c>&lt;FBNetwork&gt;</c>
        /// with a <c>Mapping="&lt;syslay FB ID&gt;"</c> attribute. EAE keys the
        /// FB-to-resource binding off this attribute (verified against
        /// SMC_Rig_Expo's working M262 binding — see e.g. Feeder there has
        /// .syslay ID=51FCB3CF8F9F350B and .sysres entry has Mapping=51FCB3CF8F9F350B).
        ///
        /// Idempotent: existing entries with the same Mapping target are left alone.
        /// Non-mirror FBs already in the sysres (DPAC_FULLINIT, plcStart, etc.) are
        /// preserved untouched.
        ///
        /// The sysres mirror's own ID is derived from the syslay ID via a simple hash
        /// so re-runs produce stable IDs (no version-control churn). Without the
        /// resource-side mirror EAE leaves FBs unmapped, $${PATH} resolves to empty,
        /// and the .hcf channel symlinks come out as bare port names like 'athome'.
        /// </summary>
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

            // Snapshot existing FB entries to avoid duplicate mirrors on re-runs.
            // Dedup by Mapping target (matches our generated mirror) AND by Name
            // (matches an EAE-written entry which may have a different Mapping value
            // assigned by EAE itself when the user clicked Mapping → EcoRT_0.RES0
            // in the right-click menu).
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
                // Mirror ID is the syslay ID with the high nibble flipped — stable but
                // distinct from the syslay's own ID so EAE doesn't see them as the
                // same instance. Falls back to a Guid-derived 16-hex if the syslay ID
                // is empty or shorter than 16 chars.
                var mirrorId = ComputeMirrorId(fb.Id);
                var fbElement = new XElement(ns + "FB",
                    new XAttribute("ID",        mirrorId),
                    new XAttribute("Name",      fb.Name),
                    new XAttribute("Type",      fb.Type),
                    new XAttribute("Namespace", fb.Namespace),
                    new XAttribute("Mapping",   fb.Id),
                    new XAttribute("x",         fb.X),
                    new XAttribute("y",         fb.Y));

                // Copy every Parameter from the syslay FB — EAE's manual mapping
                // action (Mapping → EcoRT_0.RES0 in the right-click menu) does this
                // verbatim. Without these the runtime falls back to type defaults
                // and the actuator's actual values (toWorkTime=1000ms etc.) get lost.
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

        // Locates the .sysres file paired with the sysdev (same per-device folder).
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
                // XOR the first character's hex value with 8 to flip the high bit —
                // stable, deterministic, distinct from the input.
                var first = syslayId[0];
                int v = Convert.ToInt32(first.ToString(), 16);
                var flipped = (v ^ 0x8).ToString("X");
                return flipped + syslayId.Substring(1, 15);
            }
            // Fallback: hash the syslay ID into 16 hex chars.
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mirror:" + syslayId));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }

        // --- .system Mappings patch (idempotent, generalised) ---

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
