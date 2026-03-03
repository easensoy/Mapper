using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using MapperUI.Services;

namespace CodeGen.Translation
{
    /// <summary>
    /// Production EAE delivery mechanism.
    /// NEVER modifies the live project. Copies baseline → staged folder → injects → user opens in EAE.
    /// </summary>
    public class StagingProjectBuilder
    {
        private readonly SystemLayoutInjector _injector = new();

        public StagingResult Build(
            string baselineFolder,
            string stagingRoot,
            List<VueOneComponent> components,
            string systemName)
        {
            var result = new StagingResult();
            try
            {
                // ── 1. Validate baseline ──────────────────────────────────────
                if (!Directory.Exists(baselineFolder))
                    throw new DirectoryNotFoundException($"Baseline not found: {baselineFolder}");

                if (!Directory.GetFiles(baselineFolder, "*.dfbproj", SearchOption.AllDirectories).Any())
                    throw new FileNotFoundException("No .dfbproj in baseline — is this a valid EAE project?");

                // ── 2. Create timestamped staging folder ──────────────────────
                var safe = Sanitise(systemName);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var staging = Path.Combine(stagingRoot, $"{safe}_Mapped_{stamp}");
                Directory.CreateDirectory(staging);
                result.StagingFolder = staging;

                // ── 3. Deep-copy baseline → staging ──────────────────────────
                CopyDirectory(baselineFolder, staging);
                result.Log.Add($"[COPY]  {baselineFolder}");
                result.Log.Add($"     → {staging}");

                // ── 4. Locate syslay + sysres in copy ────────────────────────
                var syslay = Find(staging, "*.syslay")
                    ?? throw new FileNotFoundException(".syslay not found in staged copy.");
                var sysres = Find(staging, "*.sysres")
                    ?? throw new FileNotFoundException(".sysres not found in staged copy.");

                result.Log.Add($"[FOUND] {Path.GetRelativePath(staging, syslay)}");
                result.Log.Add($"[FOUND] {Path.GetRelativePath(staging, sysres)}");

                // ── 5. Diff preview (logged, never re-read) ──────────────────
                var config = new MapperConfig { SyslayPath = syslay, SysresPath = sysres };
                var diff = _injector.PreviewDiff(config, components);

                result.Skipped = diff.AlreadyPresent;
                result.Injected = diff.ToBeInjected;
                result.Unsupported = diff.Unsupported;

                foreach (var s in diff.AlreadyPresent)
                    result.Log.Add($"[SKIP]        {s}  (already in baseline — preserved as-is)");
                foreach (var i in diff.ToBeInjected)
                    result.Log.Add($"[INJECT]      {i}");
                foreach (var u in diff.Unsupported)
                    result.Log.Add($"[UNSUPPORTED] {u}  (no CAT type — not injected)");

                // ── 6. Inject into staging copy ───────────────────────────────
                var inj = _injector.Inject(config, components);
                if (!inj.Success)
                    throw new Exception(inj.ErrorMessage);

                result.Log.Add($"[OK]    {inj.InjectedFBs.Count} FB(s) added to staged copy");

                // ── 7. Return .dfbproj path for user ─────────────────────────
                result.StagedProjectFile = Find(staging, "*.dfbproj")
                    ?? Path.Combine(staging, "IEC61499.dfbproj");

                result.Success = true;
                result.Log.Add(string.Empty);
                result.Log.Add($"OPEN IN EAE: {result.StagedProjectFile}");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Log.Add($"[ERROR] {ex.Message}");
                // Clean up failed staging — don't leave a broken project
                if (!string.IsNullOrEmpty(result.StagingFolder) && Directory.Exists(result.StagingFolder))
                    try { Directory.Delete(result.StagingFolder, true); } catch { }
            }

            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                if (SkipFile(Path.GetFileName(f))) continue;
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                var name = Path.GetFileName(d);
                if (SkipDir(name)) continue;
                CopyDirectory(d, Path.Combine(dst, name));
            }
        }

        // EAE regenerates these on open — copying causes version mismatch
        private static bool SkipDir(string n) => n is "RuntimeData" or "obj" or "Log" or ".git" or ".vs";
        private static bool SkipFile(string n) =>
            n.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(".user", StringComparison.OrdinalIgnoreCase);

        private static string? Find(string root, string pattern) =>
            Directory.GetFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();

        private static string Sanitise(string s) =>
            string.IsNullOrWhiteSpace(s) ? "Mapped"
            : new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }

    public class StagingResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string StagingFolder { get; set; } = string.Empty;
        public string StagedProjectFile { get; set; } = string.Empty;
        public List<string> Log { get; } = new();
        public List<string> Injected { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public List<string> Unsupported { get; set; } = new();
    }
}