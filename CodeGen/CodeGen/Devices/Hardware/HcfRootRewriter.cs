using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    // EAE 24.1's PNConfiguratorBuildTask expects the legacy DeviceHwConfigurationItems root; a newer
    // exporter's HwConfigExportedConfiguration root fails XmlSerializer at compile. Re-root to the legacy
    // form. Idempotent; channel ParameterValue (DI00..DO15 symlink) contents are untouched.
    public static class HcfRootRewriter
    {
        public static RewriteResult RewriteIfNeeded(string hcfPath, string resourceId)
        {
            var result = new RewriteResult { HcfPath = hcfPath };
            if (string.IsNullOrWhiteSpace(hcfPath) || !File.Exists(hcfPath))
            {
                result.Skipped = "file missing";
                return result;
            }

            XDocument doc;
            try { doc = XDocument.Load(hcfPath); }
            catch (Exception ex)
            {
                result.Skipped = $"XML parse failed: {ex.Message}";
                return result;
            }

            var root = doc.Root;
            if (root == null) { result.Skipped = "no root"; return result; }

            if (string.Equals(root.Name.LocalName, "DeviceHwConfigurationItems",
                StringComparison.Ordinal))
            {
                result.Skipped = "already DeviceHwConfigurationItems";
                return result;
            }

            if (!string.Equals(root.Name.LocalName, "HwConfigExportedConfiguration",
                StringComparison.Ordinal))
            {
                result.Skipped = $"unknown root '{root.Name.LocalName}'";
                return result;
            }

            var keep = root.Elements()
                .Where(e => !string.Equals(e.Name.LocalName,
                    "ContainsResourceName", StringComparison.Ordinal))
                .ToList();

            var newRoot = new XElement("DeviceHwConfigurationItems",
                new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

            // ResourceId (hex) must match the .sysres file stem so EAE's runtime resolves the resource.
            var wrapper = new XElement("DeviceHwConfigurationItem",
                new XAttribute("ResourceId", resourceId ?? string.Empty));
            foreach (var child in keep)
                wrapper.Add(new XElement(child));   // clone so original tree is unaffected
            newRoot.Add(wrapper);

            // UTF-8 + BOM to match Schneider's own exporter.
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            };
            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(hcfPath,
                        FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    var outDoc = new XDocument(
                        new XDeclaration("1.0", "utf-8", null),
                        newRoot);
                    outDoc.Save(w);
                    result.Rewrote = true;
                    result.ChildrenWrapped = keep.Count;
                    if (attempt > 1)
                        result.Warnings.Add($"write succeeded on attempt {attempt}");
                    return result;
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
            return result;
        }

        // Derives the resource ID from the .sysres file stem (EAE names the sysres after the resource ID).
        public static RewriteResult RewriteIfNeededDeriveId(string hcfPath, string sysdevFolder)
        {
            var sysresFile = Directory.Exists(sysdevFolder)
                ? Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault()
                : null;
            var resourceId = sysresFile != null
                ? Path.GetFileNameWithoutExtension(sysresFile)
                : string.Empty;
            return RewriteIfNeeded(hcfPath, resourceId);
        }

        public sealed class RewriteResult
        {
            public string HcfPath { get; set; } = string.Empty;
            public bool Rewrote { get; set; }
            public int ChildrenWrapped { get; set; }
            public string Skipped { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new();
        }
    }
}
