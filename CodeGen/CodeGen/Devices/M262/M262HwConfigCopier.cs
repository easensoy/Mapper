using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;
using CodeGen.Devices.Core;

namespace CodeGen.Devices.M262
{
    /// <summary>
    /// Deploys the M262 .hcf file. Pure verbatim copy from
    /// <c>cfg.M262HcfTemplatePath</c> (the user-authored
    /// <c>C:\VueOneMapper\IO\M262IO.hcf</c>) to the M262 sysdev folder
    /// inside the Demonstrator. No XML parsing, no symbol rewriting, no
    /// per-pin filtering — the file in the IO folder is the canonical
    /// truth and is carried over byte-for-byte.
    ///
    /// <para>Why pure verbatim?</para>
    /// <list type="bullet">
    ///   <item>The IO folder is the authoring surface — the user exports each
    ///         PLC's .hcf from EAE into this folder, then Mapper just relays
    ///         the file to the Demonstrator. Any transform Mapper applies risks
    ///         silently dropping channel bindings the user authored on purpose
    ///         (the regression that produced the 169-byte empty M262 .hcf on
    ///         2026-05-21 was exactly this — a filter pass clearing pins whose
    ///         owning component name didn't match a hard-coded map).</item>
    ///   <item>Symmetric with M580 + BX1: both of those are verbatim-copied by
    ///         <see cref="Station2DeviceEmitter"/>. M262 deserves the same
    ///         simple behaviour.</item>
    ///   <item>Resource-name resolution (the <c>RES0.M262IO.…</c> prefix inside
    ///         the .hcf vs the deployed sysres <c>Name</c>) is intentionally
    ///         left untouched. If you need that prefix rewritten to match a
    ///         non-default sysres name, do it in the IO folder authoring step
    ///         OR rename the sysres to match the .hcf — Mapper no longer
    ///         touches the contents.</item>
    /// </list>
    /// </summary>
    public static class M262HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg) => Copy(cfg, null);

        public static HwConfigCopyResult Copy(MapperConfig cfg, IoBindings? bindingsOverride)
        {
            _ = bindingsOverride; // Verbatim copy ignores bindings — kept for back-compat.
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new HwConfigCopyResult();

            var srcHcf = cfg.M262HcfTemplatePath;
            if (string.IsNullOrWhiteSpace(srcHcf) || !File.Exists(srcHcf))
            {
                result.Warnings.Add(
                    "MapperConfig.M262HcfTemplatePath empty or file missing — M262 .hcf not deployed. " +
                    "Set it to C:\\VueOneMapper\\IO\\M262IO.hcf in mapper_config.json.");
                return result;
            }

            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add(
                    "Cannot derive EAE project root from MapperConfig — M262 .hcf not deployed.");
                return result;
            }

            var dstHcf = ResolveTargetHcfPath(eaeRoot);
            if (string.IsNullOrEmpty(dstHcf))
            {
                result.Warnings.Add(
                    $"Cannot resolve target M262 .hcf path under {eaeRoot}\\IEC61499\\System\\ " +
                    "— no .sysdev present yet. Run Test Runtime once so M262SysdevEmitter creates it.");
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dstHcf)!);
            CopyFileWithRetry(srcHcf, dstHcf, result);
            result.HcfPath = dstHcf;
            result.ParametersOverwritten.Add(
                $"VERBATIM {Path.GetFileName(srcHcf)} -> {Path.GetRelativePath(eaeRoot, dstHcf)}");

            // PNConfiguratorBuildTask compatibility — IO-folder exports use the
            // newer <HwConfigExportedConfiguration> root, but EAE 24.1's build
            // task XmlSerializer expects the legacy <DeviceHwConfigurationItems>
            // form. Re-root the file in place (idempotent — no-op if already
            // legacy form). Channel bindings inside are untouched.
            var sysdevFolder = Path.GetDirectoryName(dstHcf)!;
            var rewrite = HcfRootRewriter.RewriteIfNeededDeriveId(dstHcf, sysdevFolder);
            if (rewrite.Rewrote)
                result.ParametersOverwritten.Add(
                    $"REROOTED HwConfigExportedConfiguration -> DeviceHwConfigurationItems " +
                    $"({rewrite.ChildrenWrapped} child(ren) wrapped)");
            else if (!string.IsNullOrEmpty(rewrite.Skipped))
                result.Warnings.Add($"HCF re-root skipped: {rewrite.Skipped}");
            foreach (var w in rewrite.Warnings) result.Warnings.Add(w);

            return result;
        }

        /// <summary>
        /// Byte-for-byte file copy with exponential-backoff retry. EAE
        /// occasionally holds the .hcf open for milliseconds at a time
        /// (live deployment / online change); the retry loop survives that
        /// without bouncing the user back to a red MessageBox.
        /// </summary>
        static void CopyFileWithRetry(string src, string dst, HwConfigCopyResult result)
        {
            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    File.Copy(src, dst, overwrite: true);
                    if (attempt > 1)
                        result.Warnings.Add(
                            $"M262 .hcf copy succeeded on attempt {attempt} (EAE briefly held a lock).");
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2;
                }
            }
            File.Copy(src, dst, overwrite: true);   // final try — let exceptions bubble
        }

        // ============================================================
        // Public helpers kept for back-compat with DemonstratorWiper /
        // HcfPatchService / M262SysdevEmitter / disabled MapperTests.
        // None of these touch .hcf content; they only resolve paths and
        // patch the deprecated GUID-based ResourceId attribute when the
        // legacy DeviceHwConfigurationItems format is in play.
        // ============================================================

        public static string? FindBaselineHcf(string baselineRoot)
        {
            var systemDir = Path.Combine(baselineRoot, "IEC61499", "System");
            if (Directory.Exists(systemDir))
            {
                var hit = Directory.EnumerateFiles(systemDir, "*.hcf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            return Directory.EnumerateFiles(baselineRoot, "*.hcf", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        /// <summary>
        /// Resolves the M262 sysdev's <c>.hcf</c> path. M262 SysdevId is
        /// <c>00000000-0000-0000-0000-000000000002</c> by Mapper convention.
        /// </summary>
        public static string? ResolveTargetHcfPath(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            var sysdev = Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                .Where(p => IsM262Sysdev(p))
                .FirstOrDefault()
                // Fallback for old projects where the M262 sysdev wasn't
                // tagged as M262_dPAC yet — pick the first sysdev we find.
                ?? Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                    .FirstOrDefault();
            if (sysdev == null) return null;
            var sysdevDir = Path.GetDirectoryName(sysdev)!;
            var stem = Path.GetFileNameWithoutExtension(sysdev);
            return Path.Combine(sysdevDir, stem, stem + ".hcf");
        }

        static bool IsM262Sysdev(string sysdevPath)
        {
            try
            {
                var doc = XDocument.Load(sysdevPath);
                var type = (string?)doc.Root?.Attribute("Type") ?? string.Empty;
                return string.Equals(type, "M262_dPAC", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        public static string ReadTargetSysresId(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return string.Empty;
            // Prefer the .sysres alongside an M262 sysdev so multi-PLC projects
            // don't accidentally pick up an M580/BX1 resource ID.
            foreach (var sysdev in Directory.EnumerateFiles(
                systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                if (!IsM262Sysdev(sysdev)) continue;
                var folder = Path.Combine(
                    Path.GetDirectoryName(sysdev)!,
                    Path.GetFileNameWithoutExtension(sysdev));
                var sysres = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*.sysres").FirstOrDefault()
                    : null;
                if (sysres == null) continue;
                try
                {
                    var doc = XDocument.Load(sysres);
                    return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
                }
                catch { /* try next */ }
            }
            return string.Empty;
        }

        public static int PatchHcfResourceId(string hcfPath, string newResourceId)
        {
            if (!File.Exists(hcfPath) || string.IsNullOrWhiteSpace(newResourceId)) return 0;
            try
            {
                var doc = XDocument.Load(hcfPath);
                var item = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
                if (item == null) return 0;   // New HwConfigExportedConfiguration format — no ResourceId attribute to patch.
                var attr = item.Attribute("ResourceId");
                if (attr != null && string.Equals(attr.Value, newResourceId, StringComparison.Ordinal)) return 0;
                item.SetAttributeValue("ResourceId", newResourceId);
                doc.Save(hcfPath);
                return 1;
            }
            catch { return 0; }
        }

        // ============================================================
        // Deprecated internal API — preserved as no-op so M262HcfDocument
        // still compiles. None of these methods are called from any
        // active runtime path; verbatim Copy() is the entry point.
        // ============================================================

        /// <summary>
        /// No-op kept for back-compat with <c>M262HcfDocument</c>. The
        /// per-pin rewrite path was retired in favour of pure verbatim
        /// <c>File.Copy</c> on 2026-05-21 after a filter pass produced a
        /// 169-byte empty-shell M262 .hcf (channel bindings silently
        /// dropped because their owning components weren't in a
        /// hard-coded signal-to-component map).
        /// </summary>
        internal static int OverwriteHcfParameterValuesInMemory(XDocument doc, IoBindings bindings,
            HashSet<string> syslayFbNames, HwConfigCopyResult result, string resourceId,
            string m262IoFbId)
        {
            _ = doc; _ = bindings; _ = syslayFbNames; _ = resourceId; _ = m262IoFbId;
            result.Warnings.Add(
                "M262HwConfigCopier.OverwriteHcfParameterValuesInMemory is deprecated — " +
                "Copy() now performs a pure verbatim copy of the IO-folder .hcf.");
            return 0;
        }
    }
}
