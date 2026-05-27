using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Registers M262 + M580 + BX1 sysdev GUIDs in
    /// <c>General\Folders.xml</c>. EAE seeds the Solution Explorer's
    /// SystemDevice node — and transitively the Deploy &amp; Diagnostic
    /// device list — from this file. A sysdev that exists on disk and is
    /// registered in IEC61499.dfbproj but is NOT listed in Folders.xml
    /// gets silently dropped from D&amp;D enumeration.
    ///
    /// <para>Observed 2026-05-27: Demonstrator's Folders.xml listed only
    /// the M262 GUID (<c>00000000-...000002</c>). Both M580
    /// (<c>...000003</c>) and BX1 (<c>...000004</c>) were missing, so EAE's
    /// Deploy &amp; Diagnostic tab showed only M262 despite every other
    /// project file being structurally identical. Reference
    /// SMC_Rig_Expo_withClamp/General/Folders.xml lists every PLC sysdev
    /// GUID under the SystemDevice Root folder; mirror that.</para>
    ///
    /// <para>Idempotent — adds only the items that are not already in the
    /// SystemDevice Root folder. Save is skipped when nothing changed so an
    /// idempotent re-run does not bump the file's mtime and trigger a
    /// spurious EAE "Reload Solution" prompt.</para>
    /// </summary>
    public static class FoldersXmlEmitter
    {
        // Sysdev GUIDs picked by Mapper convention. Must match the IDs the
        // sysdev/dfbproj/Equipment JSON path uses.
        const string M262SysdevId = "00000000-0000-0000-0000-000000000002";
        const string M580SysdevId = "00000000-0000-0000-0000-000000000003";
        const string BX1SysdevId  = "00000000-0000-0000-0000-000000000004";

        public sealed class EmitResult
        {
            public int ItemsAdded { get; set; }
            public System.Collections.Generic.List<string> Warnings { get; } = new();
            public string? FilePath { get; set; }
        }

        public static EmitResult Register(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("EAE project root not derivable — Folders.xml not updated.");
                return result;
            }
            var foldersPath = Path.Combine(eaeRoot, "General", "Folders.xml");
            result.FilePath = foldersPath;
            if (!File.Exists(foldersPath))
            {
                result.Warnings.Add($"General\\Folders.xml not found at {foldersPath}.");
                return result;
            }

            XDocument doc;
            try { doc = XDocument.Load(foldersPath, LoadOptions.PreserveWhitespace); }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not parse Folders.xml: {ex.Message}");
                return result;
            }

            var ns = doc.Root?.GetDefaultNamespace();
            if (ns == null)
            {
                result.Warnings.Add("Folders.xml has no root element.");
                return result;
            }

            // Find <Folder Type="SystemDevice" Name="Root"> — that's the
            // bucket the SystemDevice tree node binds to. EAE will tolerate
            // additional Folder nodes (other types — Application, CAT, …)
            // but the SystemDevice Root one is the canonical home for
            // sysdev GUID items.
            var sysdevFolder = doc.Descendants(ns + "Folder")
                .FirstOrDefault(f =>
                    string.Equals((string?)f.Attribute("Type"), "SystemDevice", StringComparison.Ordinal) &&
                    string.Equals((string?)f.Attribute("Name"), "Root",         StringComparison.Ordinal));
            if (sysdevFolder == null)
            {
                result.Warnings.Add("Folders.xml has no <Folder Type=\"SystemDevice\" Name=\"Root\"> element.");
                return result;
            }
            var items = sysdevFolder.Element(ns + "Items");
            if (items == null)
            {
                items = new XElement(ns + "Items");
                sysdevFolder.Add(items);
            }

            // Existing GUID set — case-insensitive.
            var existing = new System.Collections.Generic.HashSet<string>(
                items.Elements(ns + "item")
                     .Select(e => (e.Value ?? string.Empty).Trim())
                     .Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            foreach (var sysdevId in new[] { M262SysdevId, M580SysdevId, BX1SysdevId })
            {
                if (existing.Contains(sysdevId)) continue;
                items.Add(new XElement(ns + "item", sysdevId));
                result.ItemsAdded++;
            }

            if (result.ItemsAdded > 0)
                doc.Save(foldersPath);
            return result;
        }
    }
}
