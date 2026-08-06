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
    // Deploys the M262 .hcf: a pure verbatim copy from cfg.M262HcfTemplatePath (the authored
    // IO-folder file) to the M262 sysdev folder. No XML parsing / symbol rewriting / per-pin
    // filtering — the IO-folder file is canonical and carried byte-for-byte (a transform could
    // silently drop authored channel bindings). Symmetric with the verbatim M580/BX1 copy.
    public static class M262HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
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

            // PNConfiguratorBuildTask compatibility: re-root HwConfigExportedConfiguration ->
            // the legacy DeviceHwConfigurationItems EAE 24.1's build task expects. Idempotent;
            // channel bindings untouched.
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

        // Byte-for-byte copy with exponential-backoff retry (EAE may briefly hold the .hcf open
        // during live deploy / online change).
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

        public static string? ResolveTargetHcfPath(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            var sysdev = Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                .Where(p => IsM262Sysdev(p))
                .FirstOrDefault()
                // Fallback for old projects where the M262 sysdev wasn't tagged M262_dPAC yet.
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



    }
}
