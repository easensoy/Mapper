using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // Registers M262/M580/BX1 sysdev GUIDs in General\Folders.xml. A sysdev NOT listed here is
    // silently dropped from EAE's Deploy & Diagnostic enumeration even if it exists on disk + in the dfbproj.
    // Idempotent; save is skipped when nothing changed (no spurious "Reload Solution" prompt).
    public static class FoldersXmlEmitter
    {
        // Must match the IDs the sysdev/dfbproj/Equipment JSON path uses.
        const string M262SysdevId  = "00000000-0000-0000-0000-000000000002";
        const string M580SysdevId  = "00000000-0000-0000-0000-000000000003";
        const string BX1SysdevId   = "00000000-0000-0000-0000-000000000004";
        const string RevPiSysdevId = "00000000-0000-0000-0000-000000000005"; // RevPiDeviceEmitter

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

            // <Folder Type="SystemDevice" Name="Root"> is the bucket the SystemDevice tree node binds to.
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

            var existing = new System.Collections.Generic.HashSet<string>(
                items.Elements(ns + "item")
                     .Select(e => (e.Value ?? string.Empty).Trim())
                     .Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            // The Feed station runs on M262 (default) or is FULLY swapped to RevPi (M262 deleted). In
            // PARTIAL mode (Feeder/Checker on RevPi, M262 keeps the rest) BOTH coexist -> register both.
            var feedSysdevIds = MapperConfig.FeedStationController == FeedController.RevPi
                ? new[] { RevPiSysdevId }
                : MapperConfig.PartialRevPi
                    ? new[] { M262SysdevId, RevPiSysdevId }
                    : new[] { M262SysdevId };
            foreach (var sysdevId in feedSysdevIds.Concat(new[] { M580SysdevId, BX1SysdevId }))
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
