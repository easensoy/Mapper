using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    /// <summary>
    /// Rewrites the EAE project's .sysdev so EcoRT_0 is declared as Type="M262_dPAC"
    /// Namespace="SE.DPAC" with one Resource RES0 of Type="EMB_RES_ECO"
    /// Namespace="Runtime.Management". Then patches the matching .system file's root
    /// to ensure two &lt;Mapping&gt; entries exist binding APP1.Feeder and
    /// APP1.Feed_Station to EcoRT_0.RES0.
    ///
    /// The sysdev is the only artefact in the project that carries the "this device is
    /// an embedded M262, not a Soft dPAC" declaration — without it, EAE's deploy step
    /// targets the workstation runtime, the .hcf is ignored, and the controller never
    /// receives the application. The two &lt;Mapping&gt; elements at the System root
    /// are what tells EAE which application FBs to compile into the resource on that
    /// device.
    /// </summary>
    public static class M262SysdevEmitter
    {
        const string LibElNs = "https://www.se.com/LibraryElements";

        public static SysdevEmitResult Emit(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var eaeRoot = DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            var sysdevPath = FindSysdev(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .sysdev found under {eaeRoot}\\IEC61499\\System\\");

            RewriteSysdev(sysdevPath);

            var systemFile = FindSystemFile(eaeRoot)
                ?? throw new FileNotFoundException(
                    $"No .system found under {eaeRoot}\\IEC61499\\System\\");

            int added = EnsureMappings(systemFile);

            return new SysdevEmitResult
            {
                SysdevPath = sysdevPath,
                SystemFilePath = systemFile,
                MappingsAdded = added,
            };
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

        // --- sysdev rewrite ---

        static void RewriteSysdev(string sysdevPath)
        {
            var doc = XDocument.Load(sysdevPath);
            var root = doc.Root
                ?? throw new InvalidDataException($"Empty sysdev: {sysdevPath}");
            XNamespace ns = root.GetDefaultNamespace().NamespaceName.Length > 0
                ? root.GetDefaultNamespace()
                : LibElNs;

            // Force device declaration. If the baseline shipped Type="Soft_dPAC" we
            // overwrite it; if the attribute is missing we add it.
            SetAttr(root, "Name", "EcoRT_0");
            SetAttr(root, "Type", "M262_dPAC");
            SetAttr(root, "Namespace", "SE.DPAC");

            // Ensure exactly one Resource RES0 of Type="EMB_RES_ECO".
            var resources = root.Element(ns + "Resources");
            if (resources == null)
            {
                resources = new XElement(ns + "Resources");
                root.Add(resources);
            }
            var res0 = resources.Elements(ns + "Resource")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), "RES0",
                    StringComparison.OrdinalIgnoreCase));
            if (res0 == null)
            {
                res0 = new XElement(ns + "Resource",
                    new XAttribute("ID", Guid.Empty.ToString()),
                    new XAttribute("Name", "RES0"));
                resources.Add(res0);
            }
            SetAttr(res0, "Name", "RES0");
            SetAttr(res0, "Type", "EMB_RES_ECO");
            SetAttr(res0, "Namespace", "Runtime.Management");

            doc.Save(sysdevPath);
        }

        // --- .system Mappings patch ---

        static int EnsureMappings(string systemFilePath)
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

            var required = new (string From, string To)[]
            {
                ("APP1.Feeder",       "EcoRT_0.RES0"),
                ("APP1.Feed_Station", "EcoRT_0.RES0"),
            };

            int added = 0;
            foreach (var (from, to) in required)
            {
                bool exists = mappings.Elements(ns + "Mapping").Any(e =>
                    string.Equals((string?)e.Attribute("From"), from, StringComparison.Ordinal) &&
                    string.Equals((string?)e.Attribute("To"),   to,   StringComparison.Ordinal));
                if (exists) continue;
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
    }
}
