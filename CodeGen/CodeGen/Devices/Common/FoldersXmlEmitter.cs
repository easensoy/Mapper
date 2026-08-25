using System;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // Registers M262/M580/BX1 sysdev GUIDs in General\Folders.xml. A sysdev NOT listed here is
    // silently dropped from EAE's Deploy & Diagnostic enumeration even if it exists on disk + in the dfbproj.
    // Idempotent; save is skipped when nothing changed (no spurious "Reload Solution" prompt).
    public static class FoldersXmlEmitter
    {
        // Only the devices this emitter registers are ever removed again: anything else in the file was
        // put there by EAE or by hand and is not this generator's to sweep.
        private static bool Owned(string sysdevId) =>
            sysdevId.Equals(M262SysdevId, StringComparison.OrdinalIgnoreCase) ||
            sysdevId.Equals(M580SysdevId, StringComparison.OrdinalIgnoreCase) ||
            sysdevId.Equals(BX1SysdevId, StringComparison.OrdinalIgnoreCase) ||
            sysdevId.Equals(RevPiSysdevId, StringComparison.OrdinalIgnoreCase);

        // Each device emitter owns its own sysdev id; restating them here is how they drift.
        const string M262SysdevId  = CodeGen.Devices.M262.M262SysdevEmitter.M262SysdevId;
        const string M580SysdevId  = Station2DeviceEmitter.M580SysdevId;
        const string BX1SysdevId   = Station2DeviceEmitter.BX1SysdevId;
        const string RevPiSysdevId = CodeGen.Devices.RevPi.RevPiDeviceEmitter.SysdevId;

        public sealed class EmitResult
        {
            public int ItemsAdded { get; set; }
            public int ItemsRemoved { get; set; }
            public System.Collections.Generic.List<string> Warnings { get; } = new();
            public string? FilePath { get; set; }
        }

        // partialRevPi adds the RevPi sysdev alongside the M262 that keeps the rest of the Feed station.
        public static EmitResult Register(MapperConfig cfg, bool partialRevPi = false,
            params string[] additionalSysdevIds)
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

            // The M262 always hosts the Feed station; the partial swap adds a RevPi alongside it.
            var feedSysdevIds = partialRevPi
                ? new[] { M262SysdevId, RevPiSysdevId }
                : new[] { M262SysdevId };
            foreach (var sysdevId in feedSysdevIds
                         .Concat(new[] { M580SysdevId, BX1SysdevId })
                         .Concat(additionalSysdevIds ?? Array.Empty<string>()))
            {
                if (existing.Contains(sysdevId)) continue;
                items.Add(new XElement(ns + "item", sysdevId));
                result.ItemsAdded++;
            }

            // A device this run does NOT emit must lose its registration, or EAE keeps enumerating one
            // that is no longer on disk: the previous run's selection would survive into this one.
            var registered = new System.Collections.Generic.HashSet<string>(
                feedSysdevIds.Concat(new[] { M580SysdevId, BX1SysdevId })
                             .Concat(additionalSysdevIds ?? Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase);
            var stale = items.Elements(ns + "item")
                .Where(e => Owned((e.Value ?? string.Empty).Trim()) &&
                            !registered.Contains((e.Value ?? string.Empty).Trim()))
                .ToList();
            foreach (var e in stale) { e.Remove(); result.ItemsRemoved++; }

            if (result.ItemsAdded > 0 || result.ItemsRemoved > 0)
                doc.Save(foldersPath);
            return result;
        }
    }
}
