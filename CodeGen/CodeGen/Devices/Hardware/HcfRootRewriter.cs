using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Transforms a newly-exported <c>.hcf</c> from Schneider's
    /// <c>HwConfigExportedConfiguration</c> root into the legacy
    /// <c>DeviceHwConfigurationItems</c> form that EAE 24.1's
    /// <c>PNConfiguratorBuildTask</c> expects.
    ///
    /// <para>Why this exists</para>
    /// <para>When the IO folder under <c>C:\VueOneMapper\IO\</c> holds .hcf
    /// files exported from a newer EAE/Hardware Configurator, the file's
    /// root is <c>&lt;HwConfigExportedConfiguration ... &gt;</c>. The
    /// PNConfiguratorBuildTask in EAE 24.1's build pipeline runs an
    /// <c>XmlSerializer</c> against the .hcf at project-compile time and
    /// throws:</para>
    /// <code>
    /// System.InvalidOperationException: There is an error in XML
    /// document (1, 40). ---&gt; &lt;HwConfigExportedConfiguration
    /// xmlns=''&gt; was not expected.
    /// </code>
    /// <para>because its XML-serializer schema declares the root as
    /// <c>DeviceHwConfigurationItems</c> (with each device wrapped in
    /// a <c>&lt;DeviceHwConfigurationItem ResourceId="…"&gt;</c>).</para>
    ///
    /// <para>The transform</para>
    /// <list type="number">
    ///   <item>Detect root by LocalName. No-op when already
    ///         <c>DeviceHwConfigurationItems</c> — file copied straight from
    ///         the legacy baseline or a previous Mapper run.</item>
    ///   <item>Strip the <c>&lt;ContainsResourceName&gt;</c> metadata element
    ///         (legacy format has no equivalent).</item>
    ///   <item>Wrap every remaining child of the root in
    ///         <c>&lt;DeviceHwConfigurationItem ResourceId="{id}"/&gt;</c>.</item>
    ///   <item>Rename the root to <c>DeviceHwConfigurationItems</c>.</item>
    /// </list>
    /// <para>Channel ParameterValue contents (the <c>DI00..DO15</c> symlink
    /// bindings authored by the user) are NOT touched — only the outer
    /// wrapper changes. Idempotent: rerunning on an already-transformed
    /// .hcf does nothing.</para>
    /// </summary>
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

            // Already in the legacy form → nothing to do.
            if (string.Equals(root.Name.LocalName, "DeviceHwConfigurationItems",
                StringComparison.Ordinal))
            {
                result.Skipped = "already DeviceHwConfigurationItems";
                return result;
            }

            // Only transform the new HwConfigExportedConfiguration form.
            if (!string.Equals(root.Name.LocalName, "HwConfigExportedConfiguration",
                StringComparison.Ordinal))
            {
                result.Skipped = $"unknown root '{root.Name.LocalName}'";
                return result;
            }

            // 1. Collect children, skipping the legacy-incompatible
            //    <ContainsResourceName>true</ContainsResourceName> metadata.
            var keep = root.Elements()
                .Where(e => !string.Equals(e.Name.LocalName,
                    "ContainsResourceName", StringComparison.Ordinal))
                .ToList();

            // 2. Build the new root carrying the two well-known xmlns
            //    prefixes EAE writes on every .hcf it owns.
            var newRoot = new XElement("DeviceHwConfigurationItems",
                new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

            // 3. Wrap children in <DeviceHwConfigurationItem ResourceId="…">.
            //    The hex resource ID matches the .sysres file stem so EAE's
            //    runtime resolver finds the correct M262/M580/BX1 resource.
            var wrapper = new XElement("DeviceHwConfigurationItem",
                new XAttribute("ResourceId", resourceId ?? string.Empty));
            foreach (var child in keep)
                wrapper.Add(new XElement(child));   // detach + clone so original tree is unaffected
            newRoot.Add(wrapper);

            // 4. Save with UTF-8 + BOM to match Schneider's own exporter.
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

        /// <summary>
        /// Convenience overload — derives the resource ID from the .sysres
        /// file's stem (EAE convention: the sysres file is named after the
        /// resource ID, e.g. <c>1459BCD12760907D.sysres</c>).
        /// </summary>
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
