using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Authoritative, pure-verbatim <c>.hcf</c> deployer for the secondary PLCs
    /// (M580 X80, BX1 soft-dPAC). Locates the deployed sysdev of a given device
    /// <c>Type</c>/<c>Namespace</c>, copies the user-authored IO-folder
    /// <c>.hcf</c> into it verbatim (<c>{sysdevGuid}.hcf</c>), then re-roots it to
    /// the <c>DeviceHwConfigurationItems</c> form EAE's build expects, stamping
    /// the deployed resource's ID. Channel/symbol bindings are carried
    /// byte-for-byte (only the outer root wrapper changes).
    /// </summary>
    public static class HwConfigVerbatimCopier
    {
        private const string DefaultIoFolder = @"C:\VueOneMapper\IO";

        /// <summary>
        /// Resolves the authored <c>.hcf</c> path: prefer the configured path,
        /// then <c>ioFolderPath</c> + conventional file name, then the well-known
        /// IO folder. Returns <c>null</c> if none exist on disk.
        /// </summary>
        public static string? ResolveTemplatePath(
            string? configuredPath, string? ioFolderPath, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;
            if (!string.IsNullOrWhiteSpace(ioFolderPath))
            {
                var p = Path.Combine(ioFolderPath, fileName);
                if (File.Exists(p)) return p;
            }
            var def = Path.Combine(DefaultIoFolder, fileName);
            return File.Exists(def) ? def : null;
        }

        /// <summary>
        /// Copies <paramref name="templatePath"/> into the deployed sysdev of the
        /// given device type, re-rooting it with the deployed resource's ID.
        /// Best-effort — every outcome is recorded on the returned result.
        /// </summary>
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

            string? sysdevFile = null;
            foreach (var sd in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var root = XDocument.Load(sd).Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    if ((string?)root.Attribute("Type") == deviceType &&
                        (string?)root.Attribute("Namespace") == deviceNamespace)
                    {
                        sysdevFile = sd;
                        break;
                    }
                }
                catch { /* skip malformed */ }
            }
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
                    try { File.Delete(stale); } catch { /* best-effort */ }
                }
            }

            try
            {
                File.Copy(templatePath, hcfDest, overwrite: true);
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
            try { bytes = new FileInfo(hcfDest).Length; } catch { /* informational */ }
            result.Warnings.Add(rewrite.Rewrote
                ? $"{deviceType}: .hcf deployed ({bytes} bytes), re-rooted to DeviceHwConfigurationItems (ResourceId={resourceId})."
                : $"{deviceType}: .hcf deployed verbatim ({bytes} bytes; {rewrite.Skipped}).");

            return result;
        }
    }
}
