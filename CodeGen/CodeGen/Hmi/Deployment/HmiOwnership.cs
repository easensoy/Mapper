using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeGen.Hmi
{
    // The ownership manifest: the list of files the previous HMI generation created.
    //
    // Without it, "clean up before regenerating" is a guess - either too broad (deleting a vendor or
    // device faceplate this generator never owned) or too narrow (leaving the screen of a component
    // that has since been removed from the twin). Recording exactly what was written makes removal
    // precise, so a component disappearing from Control.xml takes its screen with it and nothing else.
    internal static class HmiOwnership
    {
        internal static void Write(string hmiDir, HmiDefinition def, IEnumerable<string> relativePaths)
        {
            Directory.CreateDirectory(hmiDir);
            var body = new StringBuilder();
            body.Append("# ").Append(def.Deployment.GeneratedBanner).Append("\r\n");
            body.Append("# Files below are owned by the HMI generator and removed on the next run.\r\n");
            foreach (var p in relativePaths.Where(p => !string.IsNullOrWhiteSpace(p))
                                           .Select(Normalise)
                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                           .OrderBy(p => p, StringComparer.Ordinal))
                body.Append(p).Append("\r\n");

            File.WriteAllText(Path.Combine(hmiDir, def.Deployment.OwnershipManifest),
                              body.ToString(), new UTF8Encoding(false));
        }

        internal static IReadOnlyList<string> Read(string hmiDir, HmiDefinition def)
        {
            var path = Path.Combine(hmiDir, def.Deployment.OwnershipManifest);
            if (!File.Exists(path)) return Array.Empty<string>();
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .ToList();
        }

        // Removes only what the PREVIOUS generation claimed. Anything else in the HMI folder -
        // vendor resources, device faceplates emitted by other parts of the pipeline - is left alone.
        internal static int RemovePreviouslyGenerated(string hmiDir, HmiDefinition def)
        {
            if (!Directory.Exists(hmiDir)) return 0;

            var removed = 0;
            foreach (var rel in Read(hmiDir, def))
            {
                var full = Path.Combine(hmiDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!IsInside(hmiDir, full)) continue;      // never follow a manifest out of its own tree
                if (File.Exists(full)) { File.Delete(full); removed++; }
            }
            return removed;
        }

        private static string Normalise(string p) => p.Replace('\\', '/').TrimStart('/');

        private static bool IsInside(string root, string candidate)
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
    }
}
