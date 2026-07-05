using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    // Verbatim .hcf deployer for the secondary PLCs (M580 X80, BX1 soft-dPAC): copies the
    // user-authored IO-folder .hcf into the deployed sysdev, then re-roots it to the
    // DeviceHwConfigurationItems form EAE expects + stamps the resource ID. Channel/symbol
    // bindings are carried byte-for-byte; only the outer root wrapper changes.
    public static class HwConfigVerbatimCopier
    {
        private const string DefaultIoFolder = @"C:\VueOneMapper\IO";

        // Prefer the configured path, then ioFolderPath + fileName, then the default IO folder; null if none exist.
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
                catch { }
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
                    try { File.Delete(stale); } catch { }
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
            try { bytes = new FileInfo(hcfDest).Length; } catch { }
            result.Warnings.Add(rewrite.Rewrote
                ? $"{deviceType}: .hcf deployed ({bytes} bytes), re-rooted to DeviceHwConfigurationItems (ResourceId={resourceId})."
                : $"{deviceType}: .hcf deployed verbatim ({bytes} bytes; {rewrite.Skipped}).");

            return result;
        }
    }
}
