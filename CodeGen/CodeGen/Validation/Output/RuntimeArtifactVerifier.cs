using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Services
{
    /// <summary>
    /// Belt-and-suspenders sysres parameter sync: after the FB mirror + wire emitters have run,
    /// re-reads every mapped sysres FB and refreshes its parameters from the generated syslay so a
    /// deployable resource can never carry a stale recipe/parameter set behind the syslay. The
    /// harder structural guarantee (every required FB/recipe is actually PRESENT on the sysres) is
    /// enforced by <see cref="SyslaySysresParityValidator"/>.
    /// </summary>
    public static class RuntimeArtifactVerifier
    {
        public static int SyncMappedSysresParametersFromSyslay(
            string syslayPath, MapperConfig cfg, Action<string>? log = null)
        {
            var eaeRoot = FindIec61499Root(syslayPath, cfg);
            if (string.IsNullOrEmpty(eaeRoot) || !File.Exists(syslayPath))
                return 0;

            var sourceFbs = new List<SysresFbMirror.SyslayFb>();
            try { sourceFbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath); }
            catch { return 0; }

            var syslayById = sourceFbs
                .Where(f => !string.IsNullOrWhiteSpace(f.Id))
                .ToDictionary(f => f.Id.Trim(), f => f, StringComparer.Ordinal);
            var syslayByName = sourceFbs
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .GroupBy(f => f.Name.Trim(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (syslayById.Count == 0)
                return 0;

            int synced = 0;
            var systemDir = Path.Combine(eaeRoot, "System");
            if (!Directory.Exists(systemDir))
                return 0;

            foreach (var sysresFile in Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories))
            {
                XDocument sysresDoc;
                try { sysresDoc = LoadShared(sysresFile); }
                catch { continue; }

                bool changed = false;
                foreach (var fb in sysresDoc.Descendants()
                             .Where(e => e.Name.LocalName == "FB"))
                {
                    var mapping = ((string?)fb.Attribute("Mapping") ?? string.Empty).Trim();
                    var name = ((string?)fb.Attribute("Name") ?? string.Empty).Trim();
                    if (!TryResolveSource(mapping, name, syslayById, syslayByName, out var source))
                        continue;

                    changed |= SyncAttribute(fb, "Type", source.Type);
                    changed |= SyncAttribute(fb, "Namespace", source.Namespace);

                    var oldParams = fb.Elements()
                        .Where(e => e.Name.LocalName == "Parameter")
                        .Select(p => $"{(string?)p.Attribute("Name")}={(string?)p.Attribute("Value")}")
                        .ToArray();
                    var newParams = source.Parameters
                        .Select(p => $"{p.Name}={p.Value}")
                        .ToArray();

                    if (!oldParams.SequenceEqual(newParams, StringComparer.Ordinal))
                    {
                        fb.Elements()
                            .Where(e => e.Name.LocalName == "Parameter")
                            .Remove();
                        var paramName = fb.Name.Namespace + "Parameter";
                        foreach (var p in source.Parameters)
                        {
                            fb.Add(new XElement(paramName,
                                new XAttribute("Name", p.Name),
                                new XAttribute("Value", p.Value)));
                        }
                        changed = true;
                        synced++;
                    }
                }

                if (changed && !TrySaveSysresWithRetry(sysresDoc, sysresFile, out var saveErr))
                    log?.Invoke(
                        "[Test Runtime][ERROR] Could NOT write the updated recipe/parameters to '" +
                        Path.GetFileName(sysresFile) + "' after retries — the file is LOCKED (EAE has the " +
                        "project open). CLOSE EAE and re-run Test Runtime; until then this resource keeps its " +
                        "STALE recipe and the PLC will run the OLD recipe. (" + saveErr + ")");
            }

            if (synced > 0)
                log?.Invoke($"[Test Runtime] synced {synced} mapped sysres FB parameter set(s) from generated syslay.");

            return synced;
        }

        // Saving the sysres can fail if EAE holds the project open (the file is
        // write-locked). Previously sysresDoc.Save() threw and the caller swallowed
        // it, silently leaving the OLD recipe on the resource while the syslay carried
        // the new one — the "Test Runtime ran but the recipe didn't change" trap.
        // Retry a few times for a transient lock, then surface a loud error so the
        // operator knows to close EAE rather than chase a phantom generator bug.
        static bool TrySaveSysresWithRetry(XDocument doc, string file, out string error)
        {
            error = string.Empty;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { doc.Save(file); return true; }
                catch (IOException ex) { error = ex.Message; }
                catch (UnauthorizedAccessException ex) { error = ex.Message; }
                if (attempt < 4) System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        static bool TryResolveSource(
            string mapping,
            string name,
            Dictionary<string, SysresFbMirror.SyslayFb> byId,
            Dictionary<string, SysresFbMirror.SyslayFb> byName,
            out SysresFbMirror.SyslayFb source)
        {
            if (mapping.Length > 0 && byId.TryGetValue(mapping, out source!))
                return true;

            if (mapping.Length > 1 && byId.TryGetValue(mapping[1..], out source!))
                return true;

            if (name.Length > 0 && byName.TryGetValue(name, out source!))
                return true;

            source = default!;
            return false;
        }

        static bool SyncAttribute(XElement target, string name, string? value)
        {
            value ??= string.Empty;
            var current = (string?)target.Attribute(name) ?? string.Empty;
            if (string.Equals(current, value, StringComparison.Ordinal))
                return false;
            target.SetAttributeValue(name, value);
            return true;
        }

        public static string? FindIec61499Root(string syslayPath, MapperConfig cfg)
        {
            foreach (var candidate in new[]
                     {
                         syslayPath,
                         cfg.SyslayPath2,
                         cfg.SysresPath2,
                         cfg.ActiveSyslayPath,
                         cfg.ActiveSysresPath,
                     })
            {
                var root = FindIec61499Root(candidate);
                if (!string.IsNullOrEmpty(root))
                    return root;
            }

            return null;
        }

        static string? FindIec61499Root(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                if (string.Equals(Path.GetFileName(dir), "IEC61499", StringComparison.OrdinalIgnoreCase))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        /// <summary>
        /// Loads an XML artefact tolerantly: shares the file (FileShare.ReadWrite) so an
        /// EAE solution holding the resource open does not block the read, and retries
        /// briefly on a sharing/lock violation to ride out the transient window while
        /// EAE syncs (or an AV/indexer touches) a freshly (re)written .sysres. A genuine
        /// missing file rethrows immediately; a persistent lock rethrows after the
        /// retries so the caller can report it honestly (a lock is NOT sim wiring).
        /// </summary>
        static XDocument LoadShared(string file)
        {
            const int attempts = 5;
            IOException? last = null;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    using var fs = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return XDocument.Load(fs, LoadOptions.PreserveWhitespace);
                }
                catch (FileNotFoundException) { throw; }
                catch (DirectoryNotFoundException) { throw; }
                catch (IOException ex)
                {
                    last = ex;   // sharing/lock violation — wait briefly and retry
                    System.Threading.Thread.Sleep(200);
                }
            }
            throw last!;
        }
    }
}
