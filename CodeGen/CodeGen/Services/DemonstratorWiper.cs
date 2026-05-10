using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGen.Services
{
    /// <summary>
    /// Deep-wipe of the Demonstrator EAE project. "Clean" used to mean only
    /// `git reset --hard` + `git clean -fd -e *.lock_sln` which still left the
    /// FB instances on the canvases (they're tracked at HEAD) and the FB type
    /// definitions deployed by Mapper (those are tracked too).
    ///
    /// This wiper takes the project to a brand-new-EAE-project state:
    ///   * IEC61499/  — only the dfbproj shell + System/ folder
    ///   * System/    — sysdev/sysapp/sysres shells with empty FBNetwork
    ///   * .syslay    — empty SubAppNetwork
    ///   * .hcf       — empty DeviceHwConfigurationItems
    ///   * dfbproj    — stripped of CAT/Basic/Adapter entries (System refs kept)
    ///   * Topology/  — UNTOUCHED (per standing instruction; topology is hand-curated)
    ///   * General/   — UNTOUCHED (project metadata)
    ///   * HMI/, HwConfiguration/, AvevaOMI/ — UNTOUCHED (other project sections)
    ///
    /// Every step is best-effort (tries to swallow individual file errors) so the
    /// caller gets a complete summary of what succeeded vs failed.
    /// </summary>
    public static class DemonstratorWiper
    {
        public sealed class WipeReport
        {
            public List<string> Steps    { get; } = new();
            public List<string> Warnings { get; } = new();
            public int FilesEmptied { get; set; }
            public int FilesDeleted { get; set; }
            public int FoldersDeleted { get; set; }
            public int DfbprojEntriesRemoved { get; set; }
        }

        // Folder names directly under IEC61499/ that get nuked entirely (everything
        // under them is a Mapper-deployed FB type or build cache).
        static readonly string[] FoldersToDelete = new[]
        {
            "Area_CAT", "Station_CAT", "Five_State_Actuator_CAT",
            "Sensor_Bool_CAT", "Process1_Generic", "Seven_State_Actuator_CAT",
            "Robot_Task_CAT", "Actuator_Fault_CAT", "PLC_RW_M262",
            "DataType",
            "Configuration", "Languages", "License", "Log", "SnapshotCompiles",
            "obj", "bin",
        };

        // Top-level files in IEC61499/ that get nuked. The .system/.syslay/.sysres/.hcf
        // files under System/ are NOT touched here — they're handled by the canvas-empty pass.
        static readonly string[] FileExtensionsToDelete = new[]
        {
            ".fbt", ".adp", ".dt",
            ".composite.offline.xml", ".composite.export",
            ".doc.xml", ".meta.xml", ".cfg",
            ".cat.export", ".cat.colors",
            ".Adapter.export", ".Basic.export",
        };

        public static WipeReport Wipe(string demonstratorRepoRoot)
        {
            var report = new WipeReport();
            if (string.IsNullOrEmpty(demonstratorRepoRoot) || !Directory.Exists(demonstratorRepoRoot))
            {
                report.Warnings.Add($"Demonstrator root not found: {demonstratorRepoRoot}");
                return report;
            }

            var iec = ResolveIec61499Dir(demonstratorRepoRoot);
            if (iec == null)
            {
                report.Warnings.Add($"IEC61499/ folder not found under {demonstratorRepoRoot}");
                return report;
            }
            report.Steps.Add($"Target: {iec}");

            // 1. Empty all canvases (.syslay, .sysres, .hcf, .sysapp). We rewrite each file
            //    to a minimal valid shell rather than deleting it — EAE expects them to exist.
            EmptyAllCanvases(iec, report);

            // 2. Delete Mapper-deployed FB type files (.fbt, .adp, .dt, etc.) at IEC61499/ root.
            DeleteFlatTypeFiles(iec, report);

            // 3. Delete CAT folders + build caches.
            DeleteFolders(iec, report);

            // 4. Strip dfbproj of <None>/<Compile> entries pointing to files that no longer exist.
            StripDfbproj(iec, report);

            // 5. Delete top-level export/scratch files in the repo root that EAE/Mapper leave behind.
            DeleteRepoRootScratch(demonstratorRepoRoot, report);

            return report;
        }

        static string? ResolveIec61499Dir(string repoRoot)
        {
            // Prefer /Demonstator/IEC61499 (project's actual layout — note the typo in path).
            var preferred = Path.Combine(repoRoot, "Demonstator", "IEC61499");
            if (Directory.Exists(preferred)) return preferred;
            var alt = Path.Combine(repoRoot, "Demonstrator", "IEC61499");
            if (Directory.Exists(alt)) return alt;
            // Fall back to a search.
            return Directory.EnumerateDirectories(repoRoot, "IEC61499", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        static void EmptyAllCanvases(string iecDir, WipeReport report)
        {
            var systemDir = Path.Combine(iecDir, "System");
            if (!Directory.Exists(systemDir))
            {
                report.Warnings.Add("IEC61499/System not found — canvases not emptied.");
                return;
            }

            int emptied = 0;
            foreach (var path in Directory.EnumerateFiles(systemDir, "*.syslay", SearchOption.AllDirectories))
                emptied += TryEmpty(path, BuildEmptySyslay, report) ? 1 : 0;
            foreach (var path in Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories))
                emptied += TryEmpty(path, BuildEmptySysres, report) ? 1 : 0;
            foreach (var path in Directory.EnumerateFiles(systemDir, "*.hcf", SearchOption.AllDirectories))
                emptied += TryEmpty(path, _ => EmptyHcf, report) ? 1 : 0;
            foreach (var path in Directory.EnumerateFiles(systemDir, "*.sysapp", SearchOption.AllDirectories))
                emptied += TryEmpty(path, BuildEmptySysapp, report) ? 1 : 0;

            report.FilesEmptied = emptied;
            report.Steps.Add($"Emptied {emptied} canvas file(s) under System/");
        }

        static bool TryEmpty(string path, Func<string, string> contentFactory, WipeReport report)
        {
            try
            {
                var content = contentFactory(path);
                File.WriteAllText(path, content);
                return true;
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Could not empty {path}: {ex.Message}");
                return false;
            }
        }

        static string BuildEmptySyslay(string path)
        {
            // Preserve the layer ID if we can read it; otherwise generate a placeholder.
            string layerId = TryReadAttr(path, "Layer", "ID") ?? "00000000-0000-0000-0000-000000000000";
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                $"<Layer ID=\"{layerId}\" Name=\"Default\" Comment=\"\" IsDefault=\"true\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"https://www.se.com/LibraryElements\">\n" +
                "  <SubAppNetwork />\n" +
                "</Layer>\n";
        }

        static string BuildEmptySysres(string path)
        {
            string id   = TryReadAttr(path, "Resource", "ID")   ?? "00000000-0000-0000-0000-000000000000";
            string name = TryReadAttr(path, "Resource", "Name") ?? "RES0";
            string type = TryReadAttr(path, "Resource", "Type") ?? "EMB_RES_ECO";
            string ns   = TryReadAttr(path, "Resource", "Namespace") ?? "Runtime.Management";
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                $"<Resource xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                $"xmlns=\"https://www.se.com/LibraryElements\" ID=\"{id}\" Name=\"{name}\" " +
                $"Type=\"{type}\" x=\"800\" y=\"800\" Namespace=\"{ns}\">\n" +
                "  <FBNetwork />\n" +
                "</Resource>\n";
        }

        static string BuildEmptySysapp(string path)
        {
            string id   = TryReadAttr(path, "Application", "ID")   ?? "00000000-0000-0000-0000-000000000001";
            string name = TryReadAttr(path, "Application", "Name") ?? "APP1";
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Application xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                $"xmlns=\"https://www.se.com/LibraryElements\" Name=\"{name}\" ID=\"{id}\" />\n";
        }

        const string EmptyHcf =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<DeviceHwConfigurationItems xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />\n";

        static string? TryReadAttr(string path, string elementLocalName, string attrName)
        {
            try
            {
                var doc = XDocument.Load(path);
                var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementLocalName);
                return (string?)el?.Attribute(attrName);
            }
            catch { return null; }
        }

        static void DeleteFlatTypeFiles(string iecDir, WipeReport report)
        {
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(iecDir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("IEC61499.dfbproj", StringComparison.OrdinalIgnoreCase)) continue;
                if (FileExtensionsToDelete.Any(ext =>
                        name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(f); n++; }
                    catch (Exception ex) { report.Warnings.Add($"Could not delete {name}: {ex.Message}"); }
                }
            }
            report.FilesDeleted += n;
            report.Steps.Add($"Deleted {n} flat FB-type file(s) at IEC61499/ root");
        }

        static void DeleteFolders(string iecDir, WipeReport report)
        {
            int n = 0;
            foreach (var folder in FoldersToDelete)
            {
                var p = Path.Combine(iecDir, folder);
                if (!Directory.Exists(p)) continue;
                try { Directory.Delete(p, recursive: true); n++; }
                catch (Exception ex) { report.Warnings.Add($"Could not delete folder {folder}: {ex.Message}"); }
            }
            report.FoldersDeleted = n;
            report.Steps.Add($"Deleted {n} type/cache folder(s) under IEC61499/");
        }

        static void StripDfbproj(string iecDir, WipeReport report)
        {
            var dfb = Path.Combine(iecDir, "IEC61499.dfbproj");
            if (!File.Exists(dfb))
            {
                report.Warnings.Add("IEC61499.dfbproj not found — skipped strip step.");
                return;
            }

            string text;
            try { text = File.ReadAllText(dfb); }
            catch (Exception ex) { report.Warnings.Add($"Read dfbproj failed: {ex.Message}"); return; }

            var lines = text.Split('\n');
            var kept = new List<string>(lines.Length);
            int removed = 0;
            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                var m = Regex.Match(line, @"^\s*<(Compile|None|EmbeddedResource)\b", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string tag = m.Groups[1].Value;
                    var block = new List<string> { line };
                    bool selfClosing = Regex.IsMatch(line, @"/>\s*\r?$");
                    int blockEnd = i;
                    if (!selfClosing)
                    {
                        int j = i + 1;
                        var closeRx = new Regex($@"</{tag}>\s*\r?$", RegexOptions.IgnoreCase);
                        while (j < lines.Length)
                        {
                            block.Add(lines[j]);
                            if (closeRx.IsMatch(lines[j])) { blockEnd = j; break; }
                            j++;
                        }
                        if (blockEnd == i) blockEnd = j;  // unterminated — bail
                    }

                    var includeMatch = Regex.Match(line, @"Include\s*=\s*""([^""]+)""");
                    string include = includeMatch.Success ? includeMatch.Groups[1].Value : string.Empty;
                    bool keep = ShouldKeepDfbprojEntry(include, iecDir);
                    if (keep) kept.AddRange(block);
                    else removed++;
                    i = blockEnd + 1;
                    continue;
                }
                kept.Add(line);
                i++;
            }

            try
            {
                File.WriteAllText(dfb, string.Join("\n", kept));
                report.DfbprojEntriesRemoved = removed;
                report.Steps.Add($"Stripped {removed} dfbproj entry/entries pointing to deleted files");
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Write dfbproj failed: {ex.Message}");
            }
        }

        static bool ShouldKeepDfbprojEntry(string include, string iecDir)
        {
            if (string.IsNullOrEmpty(include)) return true;
            if (include.StartsWith("..", StringComparison.Ordinal)) return true;  // ..\General\* etc
            // System/* references for dfbproj structural files always exist after wipe.
            if (include.StartsWith("System", StringComparison.OrdinalIgnoreCase))
            {
                var abs = Path.Combine(iecDir, include.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(abs);
            }
            // CAT-folder paths or top-level FB type files (Area_CAT\…, *.fbt, *.adp, *.cfg) —
            // we just deleted them all, so drop the entries.
            return false;
        }

        static void DeleteRepoRootScratch(string repoRoot, WipeReport report)
        {
            // EAE / Mapper occasionally drop *.export and *.colors scratch files at the
            // repo root or at Demonstator/. They're regenerable; keep a clean tree.
            var scratchPatterns = new[] { "*.export", "*.colors" };
            int n = 0;
            foreach (var dir in new[] { repoRoot, Path.Combine(repoRoot, "Demonstator"), Path.Combine(repoRoot, "Demonstrator") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var pat in scratchPatterns)
                {
                    foreach (var f in Directory.EnumerateFiles(dir, pat, SearchOption.TopDirectoryOnly))
                    {
                        try { File.Delete(f); n++; }
                        catch (Exception ex) { report.Warnings.Add($"Could not delete {f}: {ex.Message}"); }
                    }
                }
            }
            if (n > 0) report.Steps.Add($"Deleted {n} scratch (*.export / *.colors) file(s) at repo root");
        }
    }
}
