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
        // An .hcf that was not deployed is a REQUIRED output that is missing: the device still
        // deploys, and nothing on the rig reads or writes a channel. Every path that would have
        // returned without writing it now aborts instead.
        const string Aborted =
            " The device would deploy with no hardware configuration, which looks like success until "
            + "nothing on the rig reads or writes a channel. Generation ABORTED.";

        public static HwConfigCopyResult CopyFor(
            Configuration.CompilerConfiguration cfg, CodeGen.Translation.PlcAssignment plc, string? configuredPath)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var target = Mapping.TargetRegistry.Of(plc);
            return Deploy(
                EaeProjectLayout.DeriveEaeProjectRoot(cfg),
                target.DeviceType,
                Mapping.TargetDescriptor.DeviceNamespace,
                ResolveTemplatePath(configuredPath, cfg.Paths.RequireIoFolderPath(), target.HcfTemplate),
                cfg.Generation.FileWriteRetries);
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
            string? eaeRoot, string deviceType, string deviceNamespace, string? templatePath, int retries)
        {
            var result = new HwConfigCopyResult();

            if (string.IsNullOrEmpty(eaeRoot) || !Directory.Exists(eaeRoot))
            {
                throw new InvalidOperationException(
                    $"[Hcf] {deviceType}: the EAE project root was not found, so its .hcf cannot be "
                    + "deployed." + Aborted);
            }
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            {
                throw new InvalidOperationException(
                    $"[Hcf] {deviceType}: the authored .hcf is missing (looked for "
                    + $"'{templatePath ?? "<unresolved>"}')." + Aborted);
            }

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir))
            {
                throw new InvalidOperationException(
                    $"[Hcf] {deviceType}: IEC61499/System is missing from the project." + Aborted);
            }

            var sysdevFile = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
            if (sysdevFile == null)
            {
                throw new InvalidOperationException(
                    $"[Hcf] {deviceType}: no deployed sysdev of Type='{deviceType}' "
                    + $"Namespace='{deviceNamespace}', so there is nothing to bind the .hcf to." + Aborted);
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
                throw new InvalidOperationException(
                    $"[Hcf] {deviceType}: copying the .hcf failed: {ex.Message}" + Aborted, ex);
            }

            var rewrite = HcfRootRewriter.RewriteIfNeeded(hcfDest, resourceId, retries);
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
