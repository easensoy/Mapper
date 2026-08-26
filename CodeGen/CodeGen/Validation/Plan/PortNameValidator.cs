using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using CodeGen.Mapping;

namespace CodeGen.Translation
{
    public class PortNameMismatch
    {
        public string FbType { get; set; } = string.Empty;
        public string ExpectedPort { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public static class PortNameValidator
    {
        // Proves every port templates.yml declares exists in the archive that ships the type. Run BEFORE
        // the project is cleaned, so a declaration that has drifted from its archive stops the run with
        // a diagnostic rather than emitting a wire to a port that is not there - which EAE rejects the
        // whole resource for, after the previous project has already been wiped.
        public static void AssertContractMatchesArchives(string templateLibraryPath, Mapping.TemplateIndex manifest)
        {
            var mismatches = Validate(templateLibraryPath, manifest)
                .Where(m => m.ExpectedPort.Length > 0).ToList();
            if (mismatches.Count == 0) return;
            throw new InvalidOperationException(
                $"[Template] {mismatches.Count} port(s) templates.yml declares are not in the archive " +
                "that ships the type: " +
                string.Join("; ", mismatches.Select(m => $"{m.FbType}.{m.ExpectedPort}")) +
                ". The declaration and the shipped FBT have drifted apart.");
        }

        public static List<PortNameMismatch> Validate(string templateLibraryPath, Mapping.TemplateIndex manifest)
        {
            var mismatches = new List<PortNameMismatch>();
            if (string.IsNullOrEmpty(templateLibraryPath) || !Directory.Exists(templateLibraryPath))
            {
                mismatches.Add(new PortNameMismatch
                {
                    FbType = "<library>",
                    Reason = $"Template Library not found at {templateLibraryPath}"
                });
                return mismatches;
            }

            foreach (var kvp in manifest.PortContract)
            {
                var fbType = kvp.Key;
                var expected = kvp.Value;
                var actual = TryReadFbtPorts(templateLibraryPath, fbType);
                if (actual == null)
                {
                    mismatches.Add(new PortNameMismatch
                    {
                        FbType = fbType,
                        Reason = "FBT file not found in Template Library"
                    });
                    continue;
                }

                foreach (var port in expected)
                {
                    if (!actual.Contains(port, StringComparer.Ordinal))
                    {
                        mismatches.Add(new PortNameMismatch
                        {
                            FbType = fbType,
                            ExpectedPort = port,
                            Reason = $"Port '{port}' not found in {fbType}.fbt; actual ports: [{string.Join(", ", actual)}]"
                        });
                    }
                }
            }

            return mismatches;
        }

        private static HashSet<string>? TryReadFbtPorts(string libRoot, string fbType)
        {
            var found = FindFbt(libRoot, fbType);
            if (found == null) return null;

            try
            {
                using var stream = found.IsZip
                    ? OpenFromZip(found.ZipPath, found.EntryName)
                    : File.OpenRead(found.ZipPath);

                var doc = XDocument.Load(stream);
                var root = doc.Root;
                if (root == null) return null;
                var ports = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ad in root.Descendants("AdapterDeclaration"))
                {
                    var name = ad.Attribute("Name")?.Value;
                    if (!string.IsNullOrEmpty(name)) ports.Add(name);
                }
                return ports;
            }
            catch
            {
                return null;
            }
        }

        private record FbtLocation(string ZipPath, string EntryName, bool IsZip);

        private static FbtLocation? FindFbt(string libRoot, string fbType)
        {
            var directMatch = Directory.GetFiles(libRoot, fbType + ".fbt", SearchOption.AllDirectories);
            if (directMatch.Length > 0)
                return new FbtLocation(directMatch[0], string.Empty, false);

            foreach (var zip in Directory.GetFiles(libRoot, "*.zip", SearchOption.AllDirectories))
            {
                try
                {
                    using var z = ZipFile.OpenRead(zip);
                    foreach (var entry in z.Entries)
                    {
                        if (!entry.FullName.EndsWith(".fbt", StringComparison.OrdinalIgnoreCase)) continue;
                        var leaf = Path.GetFileNameWithoutExtension(entry.Name);
                        if (string.Equals(leaf, fbType, StringComparison.Ordinal))
                            return new FbtLocation(zip, entry.FullName, true);
                    }
                }
                catch { }
            }
            return null;
        }

        private static Stream OpenFromZip(string zipPath, string entryName)
        {
            var z = ZipFile.OpenRead(zipPath);
            var entry = z.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
            var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            ms.Position = 0;
            z.Dispose();
            return ms;
        }
    }
}
