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
    /// This wiper takes the project to a brand-new-EAE-project state with NO
    /// devices — the Mapper recreates every logical + physical device on the next
    /// Test Runtime:
    ///   * IEC61499/        — only the dfbproj shell + System/ folder
    ///   * System/          — the .system project root + the application (.sysapp +
    ///                       its folder) kept; the dPAC LOGICAL DEVICES (every
    ///                       .sysdev + its device folder) DELETED — recreated by
    ///                       M262SysdevEmitter (bootstrap) + Station2DeviceEmitter
    ///   * .syslay          — empty SubAppNetwork
    ///   * dfbproj          — stripped of CAT/Basic/Adapter entries AND the deleted
    ///                       sysdev/sysres entries (System refs kept only if the
    ///                       file still exists); Mapper re-registers devices
    ///   * HwConfiguration/ — DELETED (TM3 module + M262 hardware-config snapshots
    ///                       that M262HwConfigCopier replays every Button 2; without
    ///                       wiping it, baseline entries stack on top of the
    ///                       previously-deployed copy and EAE's Deploy &amp;
    ///                       Diagnostic tree shows duplicate M262_RES nodes)
    ///   * Topology/        — the PHYSICAL DEVICES (Equipment/Wire/BroadcastDomain
    ///                       JSON + their topologyproj registrations) DELETED —
    ///                       recreated by M262TopologyEmitter / Station2DeviceEmitter
    ///                       / TopologyNetworkEmitter / BroadcastDomainEmitter. The
    ///                       .solutionData (trust/identity) + topologyproj shell stay.
    ///   * General/         — UNTOUCHED (project metadata)
    ///   * HMI/, AvevaOMI/  — UNTOUCHED (other project sections)
    ///
    /// Sysdev <Resource> dedup is intentionally NOT done here — it lives in
    /// SystemInjector.PrepareDemonstratorForGeneration (logged as
    /// [CleanDevice] ...) so the same logic runs whether the user clicks
    /// Clean Demonstrator (wires Prepare after this Wipe) or Button 1/2.
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
            /// <summary>Files removed from the project's HwConfiguration/ folder
            /// (TM3 module snapshots + M262 hardware-config exports).</summary>
            public int HwConfigFilesDeleted { get; set; }
        }

        // Folder names directly under IEC61499/ that get nuked entirely (everything
        // under them is a Mapper-deployed FB type or build cache).
        static readonly string[] FoldersToDelete = new[]
        {
            "Area_CAT", "Station_CAT", "Five_State_Actuator_CAT",
            "Sensor_Bool_CAT", "Process1_Generic", "Seven_State_Actuator_CAT",
            "Seven_State_Actuator_Centre_Home_CAT",
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

            // 1. Empty all canvases (.syslay, .sysres, .hcf, .sysapp) back
            //    to their EAE-skeleton state — empty <SubAppNetwork/> and
            //    <FBNetwork/> shells. This wipes Mapper-emitted FB
            //    instances + connections (Area, Area_HMI, Station1,
            //    PartInHopper, M262IO, FB1/FB2, all wires) while leaving
            //    the canvas FILE itself intact for EAE to keep referencing.
            //    The .sysdev/.system/dfbproj/Topology/General/HMI ARE the
            //    skeleton and stay untouched.
            EmptyAllCanvases(iec, report);

            // 1b. Delete the LOGICAL DEVICES (each dPAC's .sysdev file + its device
            //     folder) so the Mapper recreates them from scratch. Runs BEFORE
            //     StripDfbproj so the now-missing sysdev/sysres/.hcf/Properties
            //     entries are pruned from the dfbproj automatically (it keeps a
            //     System/* entry only if File.Exists). The .system project root + the
            //     application (.sysapp + its folder) stay.
            DeleteLogicalDevices(iec, report);

            // 1c. Delete the APPLICATION (the .sysapp + its content folder) so EAE shows
            //     nothing under "Applications" — exactly like the now-empty Devices tree.
            //     Runs BEFORE StripDfbproj so the now-missing .sysapp/.syslay/aspmap/opcua
            //     entries are pruned. The Mapper
            //     recreates the shell on the next Generate via
            //     ApplicationShellEmitter.EnsureApplicationShell (wired into
            //     PrepareDemonstratorForGeneration), mirroring the device bootstrap.
            CodeGen.Devices.Core.ApplicationShellEmitter.DeleteApplicationShell(
                iec, line => report.Steps.Add(line));

            // 2. Delete Mapper-deployed FB type files (.fbt, .adp, .dt, etc.) at IEC61499/ root.
            DeleteFlatTypeFiles(iec, report);

            // 3. Delete CAT folders + build caches.
            DeleteFolders(iec, report);

            // 4. Strip dfbproj of <None>/<Compile> entries pointing to files that no longer exist.
            StripDfbproj(iec, report);

            // 5. Delete top-level export/scratch files in the repo root that EAE/Mapper leave behind.
            DeleteRepoRootScratch(demonstratorRepoRoot, report);

            // 6. Wipe HwConfiguration/ — the TM3 module + M262 hardware-config
            //    snapshots that M262HwConfigCopier.Copy replays from baseline
            //    every Button 2. Leaving this folder populated stacks fresh
            //    baseline entries on top of stale deployed ones, which EAE
            //    surfaces as duplicate M262_RES nodes under Devices > M262.
            //    M262HwConfigCopier recreates the folder on the next run when
            //    cfg.M262HardwareConfigBaselinePath points at a baseline; if
            //    it doesn't, EAE just shows an empty Hardware Configurator
            //    until the user wires one up — strictly better than carrying
            //    duplicated resource entries forward.
            DeleteHwConfiguration(demonstratorRepoRoot, report);

            // 7. Delete the PHYSICAL DEVICES — Topology Equipment (PLCs, the L2
            //    switch, the EtherNet/IP coupler), the Wires connecting them, and
            //    the BroadcastDomains — plus their TopologyManager.topologyproj
            //    registrations. Every one is Mapper-regenerated on the next Test
            //    Runtime (M262TopologyEmitter / Station2DeviceEmitter /
            //    TopologyNetworkEmitter / BroadcastDomainEmitter), so the wipe is
            //    safe. The project .solutionData (trust/identity) + the topologyproj
            //    shell stay.
            DeletePhysicalDevices(iec, report);

            // Sysdev <Resource> dedup lives in
            // SystemInjector.PrepareDemonstratorForGeneration — see the
            // [CleanDevice] block there. The Clean Demonstrator button calls
            // both this Wipe and Prepare so the dedup runs alongside.

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
            // Clean BLANKS the layer name — SMC_Rig is the Mapper's output (SyslayBuilder writes
            // Name="SMC_Rig" on Test Runtime), so Clean leaves no name behind (not SMC_Rig, not a
            // placeholder). The layer is identified by ID; Test Runtime restores the SMC_Rig name.
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                $"<Layer ID=\"{layerId}\" Name=\"\" Comment=\"\" IsDefault=\"true\" " +
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
            // Clean BLANKS the application name — WMG is the Mapper's output (M262SysdevEmitter.
            // AlignApplicationName sets Name="WMG" on Test Runtime), so Clean leaves no name behind
            // (not WMG, not a placeholder). The app is identified by ID; Test Runtime restores WMG.
            string name = "";
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
                // Include <Content> too: the device .hcf + the EAE compile
                // artifacts (opcua.xml / offline.xml / opcuaclient.xml / symlink.xml)
                // are registered as <Content System\…>. Without stripping these,
                // a Clean that deletes the device folders leaves the dfbproj
                // pointing at non-existent files → EAE's Solution Integrity lists
                // them as Missing Project Files. ShouldKeepDfbprojEntry keeps the
                // ones whose file still exists (e.g. the application 0001 opcua/
                // aspmap), so widening the match is safe.
                var m = Regex.Match(line, @"^\s*<(Compile|None|EmbeddedResource|Content)\b", RegexOptions.IgnoreCase);
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

        /// <summary>
        /// Recursively deletes the project's <c>HwConfiguration/</c> folder
        /// (typo-preserving "Demonstator" path takes precedence, with the
        /// canonical "Demonstrator" spelling as fallback). Best-effort —
        /// per-file failures are logged as warnings but never abort the wipe.
        /// On the next Button 2 run, <c>M262HwConfigCopier.Copy</c> recreates
        /// the folder from <c>cfg.M262HardwareConfigBaselinePath</c>.
        /// </summary>
        static void DeleteHwConfiguration(string repoRoot, WipeReport report)
        {
            string? found = null;
            foreach (var candidate in new[]
            {
                Path.Combine(repoRoot, "Demonstator", "HwConfiguration"),
                Path.Combine(repoRoot, "Demonstrator", "HwConfiguration"),
            })
            {
                if (Directory.Exists(candidate)) { found = candidate; break; }
            }
            if (found == null)
            {
                report.Steps.Add("HwConfiguration/ not present — nothing to clean.");
                return;
            }

            int fileCount = 0;
            try { fileCount = Directory.GetFiles(found, "*", SearchOption.AllDirectories).Length; }
            catch { /* count is best-effort */ }

            try
            {
                Directory.Delete(found, recursive: true);
                report.HwConfigFilesDeleted = fileCount;
                report.Steps.Add(
                    $"Deleted HwConfiguration/ ({fileCount} file(s)) — Button 2 will " +
                    "recopy fresh from the configured baseline.");
            }
            catch (Exception ex)
            {
                report.Warnings.Add(
                    $"Could not delete HwConfiguration/ at {found}: {ex.Message}. " +
                    "Stale entries will continue to surface as duplicate M262_RES nodes in EAE.");
            }
        }

        /// <summary>
        /// Deletes the LOGICAL DEVICES — every dPAC <c>.sysdev</c> file plus its
        /// same-stem device folder (which holds that device's <c>.sysres</c>,
        /// <c>.hcf</c>, <c>opcua.xml</c>, the two Properties XMLs and
        /// <c>Simulation.Binding.xml</c>). KEEPS the project root (<c>.system</c>),
        /// the application (<c>.sysapp</c> + its folder), and the
        /// <c>.cfg</c>/<c>.doc.xml</c>/<c>snapshot.xml</c> skeleton — those are not
        /// devices; they are what the Mapper recreates devices INTO. On the next
        /// Test Runtime <see cref="CodeGen.Devices.M262.M262SysdevEmitter"/>
        /// (bootstrap) + <see cref="CodeGen.Devices.Core.Station2DeviceEmitter"/>
        /// rebuild the M262/M580/BX1 logical devices from scratch.
        /// </summary>
        static void DeleteLogicalDevices(string iecDir, WipeReport report)
        {
            var systemDir = Path.Combine(iecDir, "System");
            if (!Directory.Exists(systemDir))
            {
                report.Warnings.Add("IEC61499/System not found — no logical devices to wipe.");
                return;
            }

            int devs = 0;
            foreach (var sysdev in Directory.EnumerateFiles(
                systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                var folder = Path.Combine(
                    Path.GetDirectoryName(sysdev)!, Path.GetFileNameWithoutExtension(sysdev));
                try { File.Delete(sysdev); devs++; }
                catch (Exception ex)
                {
                    report.Warnings.Add(
                        $"Could not delete sysdev {Path.GetFileName(sysdev)}: {ex.Message}");
                }
                if (Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, recursive: true); }
                    catch (Exception ex)
                    {
                        report.Warnings.Add(
                            $"Could not delete device folder {Path.GetFileName(folder)}: {ex.Message}");
                    }
                }
            }
            report.FilesDeleted += devs;
            report.Steps.Add(
                $"Deleted {devs} logical device(s) (.sysdev + device folder) under System/ — " +
                "Mapper recreates M262/M580/BX1 on the next Test Runtime.");
        }

        /// <summary>
        /// Deletes the PHYSICAL DEVICES — the Topology <c>Equipment_*.json</c>
        /// (PLCs, the L2 switch, the EtherNet/IP coupler), the <c>Wire_*.json</c>
        /// that connect them, and the <c>BroadcastDomain_*.json</c> networks — and
        /// prunes their <c>TopologyManager.topologyproj</c> registrations. All are
        /// Mapper-regenerated on the next Test Runtime, so the wipe yields a clean
        /// "no physical devices" slate. The project <c>.solutionData</c> (trust /
        /// identity) and the topologyproj shell are kept.
        /// </summary>
        static void DeletePhysicalDevices(string iecDir, WipeReport report)
        {
            // Topology/ is a sibling of IEC61499/ under the project folder.
            var projectDir = Path.GetDirectoryName(iecDir);
            if (projectDir == null) return;
            var topoDir = Path.Combine(projectDir, "Topology");
            if (!Directory.Exists(topoDir))
            {
                report.Steps.Add("Topology/ not present — no physical devices to wipe.");
                return;
            }

            int n = 0;
            foreach (var pattern in new[] { "Equipment_*.json", "Wire_*.json", "BroadcastDomain_*.json" })
                foreach (var f in Directory.EnumerateFiles(topoDir, pattern))
                {
                    try { File.Delete(f); n++; }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"Could not delete {Path.GetFileName(f)}: {ex.Message}");
                    }
                }

            StripTopologyProj(topoDir, report);
            report.FilesDeleted += n;
            report.Steps.Add(
                $"Deleted {n} physical device file(s) (Equipment/Wire/BroadcastDomain) under Topology/ — " +
                "Mapper recreates them on the next Test Runtime.");
        }

        /// <summary>
        /// Removes <c>&lt;None Include="…"&gt;</c> entries from
        /// <c>TopologyManager.topologyproj</c> that point at an Equipment/Wire/
        /// BroadcastDomain JSON just deleted (file no longer on disk). Keeps every
        /// other entry (solutionData, Content/, folders, files still present). The
        /// Mapper re-registers the regenerated devices on the next Test Runtime.
        /// </summary>
        static void StripTopologyProj(string topoDir, WipeReport report)
        {
            var proj = Path.Combine(topoDir, "TopologyManager.topologyproj");
            if (!File.Exists(proj)) return;
            string text;
            try { text = File.ReadAllText(proj); }
            catch (Exception ex) { report.Warnings.Add($"Read topologyproj failed: {ex.Message}"); return; }

            var lines = text.Split('\n');
            var kept = new List<string>(lines.Length);
            int removed = 0;
            foreach (var line in lines)
            {
                var inc = Regex.Match(line, @"Include\s*=\s*""([^""]+)""");
                if (inc.Success)
                {
                    var raw = inc.Groups[1].Value;
                    var fileName = Path.GetFileName(raw.Replace('\\', '/'));
                    var rel = raw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    bool isDeviceJson =
                        fileName.StartsWith("Equipment_", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("Wire_", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("BroadcastDomain_", StringComparison.OrdinalIgnoreCase);
                    if (isDeviceJson && !File.Exists(Path.Combine(topoDir, rel)))
                    {
                        removed++;
                        continue;  // drop the registration for the deleted file
                    }
                }
                kept.Add(line);
            }

            if (removed > 0)
            {
                try
                {
                    File.WriteAllText(proj, string.Join("\n", kept));
                    report.Steps.Add(
                        $"Stripped {removed} topologyproj entry/entries for deleted physical devices.");
                }
                catch (Exception ex) { report.Warnings.Add($"Write topologyproj failed: {ex.Message}"); }
            }
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
