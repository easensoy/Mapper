using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    public static class EaeProjectLayout
    {
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

        public static string? FindSysresFor(string sysdevPath)
        {
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));
            if (!Directory.Exists(sysdevFolder)) return null;
            var sysresFiles = Directory
                .EnumerateFiles(sysdevFolder, "*.sysres", SearchOption.TopDirectoryOnly)
                .ToList();
            if (sysresFiles.Count == 0) return null;
            if (sysresFiles.Count == 1) return sysresFiles[0];

            // MORE THAN ONE .sysres in the folder => an orphan from a prior deploy
            // sits alongside the active resource. We MUST return the ACTIVE one --
            // the .sysres whose stem matches the resource ID the parent sysdev's
            // <Resource ID="..."/> actually references -- NOT FirstOrDefault, which
            // routinely returned the orphan (its GUID often sorts before the active
            // resource's). Picking the orphan made the FB mirror + opcua-stamp write
            // to the orphan: it populated the orphan .sysres and created an orphan
            // "{orphanId}/" sister folder (with opcua.xml), while the ACTIVE resource
            // stayed empty. EAE then loads that ghost sister folder and raises the
            // "Solution Integrity / Repair Instances" dialog on the duplicated CAT
            // instances. Matching the sysdev's active ID makes the mirror always
            // target the live resource; the orphan is left for the stale-sysres /
            // sister-folder sweep to delete.
            var activeIds = ReadActiveResourceIds(sysdevPath);
            var active = sysresFiles.FirstOrDefault(f =>
                activeIds.Contains(Path.GetFileNameWithoutExtension(f)));
            return active ?? sysresFiles[0];
        }

        /// <summary>
        /// The resource IDs a sysdev actually references via
        /// <c>&lt;Resources&gt;&lt;Resource ID="..."/&gt;</c>. Used to tell the
        /// live resource apart from an orphan .sysres left in the same folder.
        /// </summary>
        static HashSet<string> ReadActiveResourceIds(string sysdevPath)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = XDocument.Load(sysdevPath).Root;
                if (root == null) return ids;
                XNamespace ns = root.GetDefaultNamespace();
                foreach (var r in root.Element(ns + "Resources")?.Elements(ns + "Resource")
                                  ?? Enumerable.Empty<XElement>())
                {
                    var id = (string?)r.Attribute("ID");
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
            catch { /* malformed sysdev -> empty set, caller falls back to first file */ }
            return ids;
        }

        /// <summary>
        /// Locates the deployed sysdev whose root &lt;Device&gt; has the given
        /// <paramref name="deviceType"/> (e.g. "M580_dPAC", "Soft_dPAC") in the
        /// SE.DPAC namespace. Returns null if none match.
        /// </summary>
        public static string? FindSysdevByDeviceType(string eaeRoot, string deviceType)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sd in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var root = XDocument.Load(sd).Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    if (string.Equals((string?)root.Attribute("Type"), deviceType, StringComparison.Ordinal) &&
                        string.Equals((string?)root.Attribute("Namespace"), "SE.DPAC", StringComparison.Ordinal))
                        return sd;
                }
                catch { /* skip malformed */ }
            }
            return null;
        }
    }
}
