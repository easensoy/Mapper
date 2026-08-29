using System;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // Registers the emitted devices' sysdev GUIDs in General\Folders.xml. A sysdev NOT listed here is
    // silently dropped from EAE's Deploy & Diagnostic enumeration even if it exists on disk + in the dfbproj.
    // Idempotent; save is skipped when nothing changed (no spurious "Reload Solution" prompt).
    public static class FoldersXmlEmitter
    {
        // Only the devices this emitter could register are ever removed again: anything else in the file
        // was put there by EAE or by hand and is not this generator's to sweep. DECLARED, not listed:
        // a fixed four-name list silently stopped sweeping the moment a fifth target was declared.
        private static bool Owned(Configuration.CompilerConfiguration cfg, string sysdevId) =>
            cfg.Devices.Targets.Any(t =>
                sysdevId.Equals(t.Identity.Sysdev, StringComparison.OrdinalIgnoreCase));

        public sealed class EmitResult
        {
            public int ItemsAdded { get; set; }
            public int ItemsRemoved { get; set; }
            public System.Collections.Generic.List<string> Warnings { get; } = new();
            }

        // anythingRelocated says whether this run moved a component onto a relocation target, which is
        // The separately-owned HMI module registers its own device and hands the paths it was given;
        // a generation hands its whole configuration. One implementation, two ways of being asked.
        // A sysdev missing from Folders.xml is silently dropped from EAE's Solution Explorer AND
        // from Deploy - the project opens, looks right, and simply does not carry that device.
        // Every path that would have returned without registering now aborts.
        const string NotRegistered =
            "The devices this run emits would not be registered, so EAE would open a project that "
            + "silently omits them. Generation ABORTED.";

        // A COMPATIBILITY SIGNATURE. The separately-owned HMI module calls this exact shape and cannot be
        // handed a run's snapshot, so this overload composes one. Every in-process caller inside the
        // compiler uses the cfg-taking overload below.
        public static EmitResult Register(MapperConfig paths, bool partialRevPi = false,
            params string[] additionalSysdevIds) =>
            Register(Configuration.CompilerConfiguration.Load(paths), partialRevPi, additionalSysdevIds);

        public static EmitResult Register(Configuration.CompilerConfiguration cfg, bool partialRevPi = false,
            params string[] additionalSysdevIds)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new EmitResult();
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                throw new InvalidOperationException(
                    "[Folders] the EAE project root is not derivable, so no sysdev can be registered. "
                    + NotRegistered);
            }
            var foldersPath = Path.Combine(eaeRoot, "General", "Folders.xml");
            if (!File.Exists(foldersPath))
            {
                throw new InvalidOperationException(
                    $"[Folders] General\\Folders.xml is missing at '{foldersPath}'. " + NotRegistered);
            }

            XDocument doc;
            try { doc = XDocument.Load(foldersPath, LoadOptions.PreserveWhitespace); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[Folders] Folders.xml could not be parsed: {ex.Message} " + NotRegistered, ex);
            }

            var ns = doc.Root?.GetDefaultNamespace();
            if (ns == null)
            {
                throw new InvalidOperationException(
                    "[Folders] Folders.xml has no root element. " + NotRegistered);
            }

            // <Folder Type="SystemDevice" Name="Root"> is the bucket the SystemDevice tree node binds to.
            var sysdevFolder = doc.Descendants(ns + "Folder")
                .FirstOrDefault(f =>
                    string.Equals((string?)f.Attribute("Type"), "SystemDevice", StringComparison.Ordinal) &&
                    string.Equals((string?)f.Attribute("Name"), "Root",         StringComparison.Ordinal));
            if (sysdevFolder == null)
            {
                throw new InvalidOperationException(
                    "[Folders] Folders.xml has no <Folder Type=\"SystemDevice\" Name=\"Root\"> element. "
                    + NotRegistered);
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

            // THE DEVICES THIS RUN ACTUALLY EMITS, in the order it drives them. A target that only
            // exists when something is relocated onto it is registered only when something was, which
            // is the same rule the emission itself follows - so the file can never enumerate a device
            // that is not on disk, whatever the declared target set happens to be.
            var emitted = EmittedSysdevIds(cfg, partialRevPi)
                .Concat(additionalSysdevIds ?? Array.Empty<string>()).ToList();
            foreach (var sysdevId in emitted)
            {
                if (existing.Contains(sysdevId)) continue;
                items.Add(new XElement(ns + "item", sysdevId));
                result.ItemsAdded++;
            }

            // A device this run does NOT emit must lose its registration, or EAE keeps enumerating one
            // that is no longer on disk: the previous run's selection would survive into this one.
            var registered = new System.Collections.Generic.HashSet<string>(
                emitted, StringComparer.OrdinalIgnoreCase);
            var stale = items.Elements(ns + "item")
                .Where(e => Owned(cfg, (e.Value ?? string.Empty).Trim()) &&
                            !registered.Contains((e.Value ?? string.Empty).Trim()))
                .ToList();
            foreach (var e in stale) { e.Remove(); result.ItemsRemoved++; }

            if (result.ItemsAdded > 0 || result.ItemsRemoved > 0)
                doc.Save(foldersPath);
            return result;
        }

        // The sysdev of every target this run emits, in the DECLARED DRIVE ORDER - which is the order
        // the backends run in, so the registration matches the emission it describes. A target that
        // receives relocated components is emitted only when this run relocated something onto it.
        static System.Collections.Generic.IEnumerable<string> EmittedSysdevIds(
            Configuration.CompilerConfiguration cfg, bool anythingRelocated)
        {
            var byPlc = cfg.Devices.Targets.ToDictionary(t => t.Plc);
            foreach (var plc in cfg.Devices.BackendEmitOrder)
            {
                if (!byPlc.TryGetValue(plc, out var t)) continue;
                if (!string.IsNullOrWhiteSpace(t.StandsInFor) && !anythingRelocated) continue;
                yield return t.Identity.Sysdev;
            }
        }
    }
}
