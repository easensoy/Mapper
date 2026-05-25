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
            return Directory.EnumerateFiles(sysdevFolder, "*.sysres", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
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
