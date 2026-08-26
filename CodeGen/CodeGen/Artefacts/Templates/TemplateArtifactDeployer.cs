using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.IO;
using System.IO.Compression;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Services
{
    // Locate a template package, extract it copy-if-absent, write .cfg files and register it in the
    // .dfbproj. No FBT XML patching lives here.
    internal static class TemplateArtifactDeployer
    {
        internal static void SweepRetiredType(string eaeProjectDir, string typeName, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                int filesGone = 0;
                foreach (var p in new[]
                {
                    Path.Combine(iec, typeName + ".fbt"),
                    Path.Combine(iec, typeName + ".doc.xml"),
                    Path.Combine(iec, typeName + ".meta.xml"),
                    Path.Combine(eaeProjectDir, typeName + ".Basic.export"),
                })
                    if (File.Exists(p)) { File.Delete(p); filesGone++; }

                var dfbproj = Path.Combine(iec, "IEC61499.dfbproj");
                int entriesGone = 0;
                if (File.Exists(dfbproj))
                {
                    var doc = System.Xml.Linq.XDocument.Load(dfbproj, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    foreach (var el in doc.Descendants()
                        .Where(e => (e.Name.LocalName == "Compile" || e.Name.LocalName == "None")
                            && ((string?)e.Attribute("Include"))?.StartsWith(typeName + ".", StringComparison.Ordinal) == true)
                        .ToList())
                    { el.Remove(); entriesGone++; }
                    if (entriesGone > 0) doc.Save(dfbproj);
                }
                if (filesGone > 0 || entriesGone > 0)
                    result.PatchesApplied.Add($"retired {typeName}: {filesGone} file(s) + {entriesGone} dfbproj entry(ies) removed");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"retire {typeName} failed: {ex.Message}");
            }
        }

        internal static void DeployArtifact(string libPath, string subfolder, string name,
            string eaeProjectDir, DeployResult result, bool isBasic, bool isCat = false)
        {
            var folder = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(folder))
            {
                result.Warnings.Add($"Library subfolder missing: {subfolder}");
                return;
            }

            var zipPath = FindArtifactZip(folder, name);
            if (zipPath != null)
            {
                ExtractToEae(zipPath, eaeProjectDir, result);
            }
            else
            {
                var dirPath = FindArtifactDir(folder, name);
                if (dirPath != null)
                {
                    CopyDirToEae(dirPath, eaeProjectDir, result);
                }
                else
                {
                    result.Warnings.Add($"Artifact not found: {subfolder}/{name}");
                    return;
                }
            }

            if (isCat) result.CATsDeployed.Add(name);
            else if (string.Equals(subfolder, "Adapter", StringComparison.OrdinalIgnoreCase))
                result.AdaptersDeployed.Add(name);
            else if (string.Equals(subfolder, "Composite", StringComparison.OrdinalIgnoreCase))
                result.CompositesDeployed.Add(name);
            else if (isBasic) result.BasicFBsDeployed.Add(name);
        }

        // Skip ".subcats.zip" wrappers, which leave the CAT folder uncreated. Newest by filename wins.
        static string? FindArtifactZip(string folder, string name)
        {
            var zips = Directory.GetFiles(folder, "*.zip")
                .Where(f => !Path.GetFileName(f)
                    .Contains(".subcats.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var f in zips)
            {
                var fn = Path.GetFileName(f);
                if (fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fn, name + ".zip", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            foreach (var f in zips)
            {
                if (Path.GetFileName(f).Contains(name + ".", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            return null;
        }

        static string? FindArtifactDir(string folder, string name)
        {
            foreach (var d in Directory.GetDirectories(folder))
            {
                var dn = Path.GetFileName(d);
                if (dn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dn, name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        internal static void CopyDirToEae(string sourceDir, string eaeProjectDir, DeployResult result)
        {
            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var parts = rel.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    rel = string.Join("/", parts.Skip(1));

                var targetPath = Path.Combine(eaeProjectDir, rel);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
            }
        }

        // Copy-if-absent: existing files are not overwritten (I-7 deploy-revert trap).
        internal static void ExtractToEae(string zipPath, string eaeProjectDir, DeployResult result)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };
            string? prefixToStrip = null;

            var firstFile = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (firstFile != null)
            {
                var parts = firstFile.FullName.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    prefixToStrip = parts[0] + "/";
            }

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var relativePath = entry.FullName;
                if (prefixToStrip != null && relativePath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(prefixToStrip.Length);

                // HMI faceplates are regenerated by CodeGen.Hmi, so the CAT packages' copies are ignored.
                if (relativePath.StartsWith("HMI/", StringComparison.OrdinalIgnoreCase)) continue;

                var targetPath = Path.Combine(eaeProjectDir, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!File.Exists(targetPath))
                {
                    entry.ExtractToFile(targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
            }
        }

        internal static void RegisterInDfbproj(string eaeProjectDir,
            Configuration.CompilerConfiguration cfg, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec61499Dir)) return;

            var dfbproj = Directory.GetFiles(iec61499Dir, "*.dfbproj").FirstOrDefault();
            if (dfbproj == null) return;

            int changed = 0;
            foreach (var cat in result.CATsDeployed)
                changed += DfbprojRegistrar.RegisterCat(dfbproj, cat);

            foreach (var basic in result.BasicFBsDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt", "Basic");

            foreach (var adapter in result.AdaptersDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, adapter + ".adp", "Adapter");

            foreach (var composite in result.CompositesDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, composite + ".fbt", "Composite");

            foreach (var dt in result.DataTypesDeployed)
                changed += DfbprojRegistrar.RegisterDataType(dfbproj, $@"DataType\{dt}.dt");

            // Declared in device.yml, in declaration order, because the emitted order is the
            // artefact. A missing or malformed row is refused at load rather than producing a
            // .dfbproj whose topology import fails on one unresolved catalogReference.
            foreach (var lib in cfg.Devices.Libraries)
                changed += DfbprojRegistrar.RegisterReference(dfbproj, lib.Name, lib.Version);

            changed += DfbprojRegistrar.SweepIec61499Folder(dfbproj, iec61499Dir);

            // Bump mtime only on a real change, else an idempotent re-run triggers EAE "Reload Solution".
            if (changed > 0)
            {
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info($"[Deploy] dfbproj updated ({changed} entr(y/ies)): {Path.GetFileName(dfbproj)}");
            }
            else
            {
                MapperLogger.Info($"[Deploy] dfbproj already up to date; no write: {Path.GetFileName(dfbproj)}");
            }
        }

    }
}
