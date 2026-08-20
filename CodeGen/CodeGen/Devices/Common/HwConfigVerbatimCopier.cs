using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // Verbatim .hcf deployer for the secondary PLCs (M580 X80, BX1 soft-dPAC): copies the
    // user-authored IO-folder .hcf into the deployed sysdev, then re-roots it to the
    // DeviceHwConfigurationItems form EAE expects + stamps the resource ID. Channel/symbol
    // bindings are carried byte-for-byte; only the outer root wrapper changes.
    public static class HwConfigVerbatimCopier
    {
        // Prefer the configured path, then ioFolderPath + fileName, then the default IO folder; null if none exist.
        // The authored .hcf for one target, carried byte-for-byte into its device folder. A transform
        // could silently drop an authored channel binding, so this never rewrites the file.
        public static HwConfigCopyResult CopyFor(
            MapperConfig cfg, CodeGen.Translation.PlcAssignment plc, string? configuredPath)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var target = Mapping.TargetRegistry.Of(plc);
            return Deploy(
                EaeProjectLayout.DeriveEaeProjectRoot(cfg),
                target.DeviceType,
                Mapping.TargetDescriptor.DeviceNamespace,
                ResolveTemplatePath(configuredPath, cfg.RequireIoFolderPath(), target.HcfTemplate));
        }

        public static string? ResolveTemplatePath(
            string? configuredPath, string? ioFolderPath, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;
            if (string.IsNullOrWhiteSpace(ioFolderPath)) return null;
            var p = Path.Combine(ioFolderPath, fileName);
            return File.Exists(p) ? p : null;
        }

        public static HwConfigCopyResult Deploy(
            string? eaeRoot, string deviceType, string deviceNamespace, string? templatePath)
        {
            var result = new HwConfigCopyResult();

            if (string.IsNullOrEmpty(eaeRoot) || !Directory.Exists(eaeRoot))
            {
                result.Warnings.Add($"{deviceType}: EAE project root not found — .hcf not deployed.");
                return result;
            }
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                result.Warnings.Add(
                    $"{deviceType}: authored .hcf not found (looked for '{templatePath ?? "<unresolved>"}') — .hcf not deployed.");
                return result;
            }

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir))
            {
                result.Warnings.Add($"{deviceType}: IEC61499/System not found — .hcf not deployed.");
                return result;
            }

            var sysdevFile = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            if (sysdevFile == null)
            {
                result.Warnings.Add(
                    $"{deviceType}: no deployed sysdev of Type='{deviceType}' Namespace='{deviceNamespace}' — run device emit first.");
                return result;
            }

            var sysdevStem = Path.GetFileNameWithoutExtension(sysdevFile);
            var sysdevFolder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, sysdevStem);
            Directory.CreateDirectory(sysdevFolder);

            var sysresFile = Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault();
            var resourceId = sysresFile != null
                ? Path.GetFileNameWithoutExtension(sysresFile)
                : string.Empty;

            var hcfDest = Path.Combine(sysdevFolder, sysdevStem + ".hcf");
            foreach (var stale in Directory.EnumerateFiles(sysdevFolder, "*.hcf"))
            {
                if (!string.Equals(stale, hcfDest, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(stale); } catch { }
                }
            }

            try
            {
                CopyWithRetry(templatePath, hcfDest);
                result.FilesCopied++;
                result.HcfPath = hcfDest;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{deviceType}: .hcf copy failed: {ex.Message}");
                return result;
            }

            var rewrite = HcfRootRewriter.RewriteIfNeeded(hcfDest, resourceId);
            long bytes = 0;
            try { bytes = new FileInfo(hcfDest).Length; } catch { }
            result.Warnings.Add(rewrite.Rewrote
                ? $"{deviceType}: .hcf deployed ({bytes} bytes), re-rooted to DeviceHwConfigurationItems (ResourceId={resourceId})."
                : $"{deviceType}: .hcf deployed verbatim ({bytes} bytes; {rewrite.Skipped}).");

            return result;
        }

        // EAE briefly holds a deployed .hcf open during a live deploy or online change, so a copy that
        // fails on a sharing violation is retried rather than reported as a missing hardware config.
        private static void CopyWithRetry(string src, string dst)
        {
            for (int attempt = 1, delayMs = 50; ; attempt++, delayMs *= 2)
            {
                try { File.Copy(src, dst, overwrite: true); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    System.Threading.Thread.Sleep(delayMs);
                }
            }
        }
    }
}
