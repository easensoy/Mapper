using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Services
{
    public static class RuntimeArtifactVerifier
    {
        static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        static readonly string[] SimulatorFbPrefixes =
        {
            "SimHopperForce",
            "SimSwivelForce_",
            "SimPosition",
        };

        static readonly string[] SimulatorTypes =
        {
            "SimCentreHomeSensor_7SCH",
        };

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

        public static int RemoveSimulatorInstancesForRuntime(
            string syslayPath, MapperConfig cfg, Action<string>? log = null)
        {
            var eaeRoot = FindIec61499Root(syslayPath, cfg);
            if (string.IsNullOrEmpty(eaeRoot))
                return 0;

            int removed = 0;
            foreach (var file in EnumerateNetworkFiles(eaeRoot, syslayPath, cfg))
            {
                removed += ScrubNetworkFile(file);
            }

            if (removed > 0)
            {
                log?.Invoke(
                    $"[Test Runtime][IO] Removed {removed} stale simulator FB/connection artefact(s) " +
                    "from runtime syslay/sysres files.");
            }

            return removed;
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

        public static void AssertNoSimulatorInstancesForRuntime(
            string syslayPath, MapperConfig cfg)
        {
            var eaeRoot = FindIec61499Root(syslayPath, cfg);
            if (string.IsNullOrEmpty(eaeRoot))
                throw new InvalidOperationException(
                    "Cannot verify Test Runtime artifacts: IEC61499 project root was not found.");

            var violations = new List<string>();

            foreach (var file in EnumerateNetworkFiles(eaeRoot, syslayPath, cfg))
                CollectNetworkViolations(file, violations);

            if (violations.Count > 0)
            {
                throw new InvalidOperationException(
                    "Test Runtime artifact integrity check could not confirm the artefacts are " +
                    "free of simulator-only wiring (a locked/unreadable file is usually a stale " +
                    "EAE handle from an online session, not sim wiring — see details):\n" +
                    string.Join("\n", violations.Select(v => " - " + v)));
            }
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

        static IEnumerable<string> EnumerateNetworkFiles(
            string eaeRoot, string syslayPath, MapperConfig cfg)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIfExists(syslayPath);
            AddIfExists(cfg.SyslayPath2);
            AddIfExists(cfg.SysresPath2);
            AddIfExists(cfg.ActiveSyslayPath);
            AddIfExists(cfg.ActiveSysresPath);

            var systemDir = Path.Combine(eaeRoot, "System");
            if (Directory.Exists(systemDir))
            {
                foreach (var f in Directory.EnumerateFiles(systemDir, "*.syslay", SearchOption.AllDirectories))
                    files.Add(f);
                foreach (var f in Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories))
                    files.Add(f);
            }

            return files.Where(File.Exists).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            void AddIfExists(string? file)
            {
                if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                    files.Add(file);
            }
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

        static int ScrubNetworkFile(string file)
        {
            XDocument doc;
            try
            {
                doc = LoadShared(file);
            }
            catch
            {
                return 0;
            }

            int removed = 0;
            bool changed = false;

            foreach (var net in doc.Descendants(Ns + "FBNetwork"))
            {
                var simNames = net.Elements(Ns + "FB")
                    .Where(IsSimulatorFb)
                    .Select(f => (string?)f.Attribute("Name"))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToHashSet(StringComparer.Ordinal);

                if (simNames.Count == 0)
                    continue;

                foreach (var fb in net.Elements(Ns + "FB")
                             .Where(f => simNames.Contains((string?)f.Attribute("Name") ?? string.Empty))
                             .ToList())
                {
                    fb.Remove();
                    removed++;
                    changed = true;
                }

                foreach (var sectionName in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
                {
                    var section = net.Element(Ns + sectionName);
                    if (section == null)
                        continue;

                    foreach (var connection in section.Elements(Ns + "Connection")
                                 .Where(c => ReferencesAnySimulatorFb(c, simNames))
                                 .ToList())
                    {
                        connection.Remove();
                        removed++;
                        changed = true;
                    }
                }

                if (simNames.Contains("SimHopperForce"))
                    changed |= RestoreAreaInitBridge(net);
            }

            if (changed)
                doc.Save(file);

            return removed;
        }

        static bool RestoreAreaInitBridge(XElement net)
        {
            bool hasFb1 = net.Elements(Ns + "FB")
                .Any(f => string.Equals((string?)f.Attribute("Name"), "FB1", StringComparison.Ordinal));
            bool hasArea = net.Elements(Ns + "FB")
                .Any(f => string.Equals((string?)f.Attribute("Name"), "Area", StringComparison.Ordinal));
            if (!hasFb1 || !hasArea)
                return false;

            var eventConnections = net.Element(Ns + "EventConnections");
            if (eventConnections == null)
            {
                eventConnections = new XElement(Ns + "EventConnections");
                net.Add(eventConnections);
            }

            bool exists = eventConnections.Elements(Ns + "Connection").Any(c =>
                string.Equals((string?)c.Attribute("Source"), "FB1.INITO", StringComparison.Ordinal) &&
                string.Equals((string?)c.Attribute("Destination"), "Area.INIT", StringComparison.Ordinal));
            if (exists)
                return false;

            eventConnections.Add(new XElement(Ns + "Connection",
                new XAttribute("Source", "FB1.INITO"),
                new XAttribute("Destination", "Area.INIT")));
            return true;
        }

        static void CollectNetworkViolations(string file, List<string> violations)
        {
            XDocument doc;
            try
            {
                doc = LoadShared(file);
            }
            catch (IOException ex)
            {
                // A lock is NOT simulator wiring. Report it honestly so the operator
                // closes / logs out of the EAE solution (EAE holds the deployed .sysres
                // while online) and retries, instead of hunting for sim FBs that don't exist.
                violations.Add($"{Path.GetFileName(file)} could not be read — it is locked by " +
                    "another process (close or log out of the EAE solution, then retry Test " +
                    "Runtime). This is NOT simulator wiring; the scan simply could not open the " +
                    $"file. ({ex.Message})");
                return;
            }
            catch (Exception ex)
            {
                violations.Add($"{Path.GetFileName(file)} could not be parsed: {ex.Message}");
                return;
            }

            foreach (var net in doc.Descendants(Ns + "FBNetwork"))
            {
                foreach (var fb in net.Elements(Ns + "FB").Where(IsSimulatorFb))
                {
                    violations.Add(
                        $"{Short(file)} contains simulator FB instance " +
                        $"{(string?)fb.Attribute("Name")}:{(string?)fb.Attribute("Type")}");
                }

                foreach (var sectionName in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
                {
                    var section = net.Element(Ns + sectionName);
                    if (section == null)
                        continue;

                    foreach (var connection in section.Elements(Ns + "Connection")
                                 .Where(c => ReferencesSimulatorName(c)))
                    {
                        violations.Add(
                            $"{Short(file)} contains simulator connection " +
                            $"{(string?)connection.Attribute("Source")} -> " +
                            $"{(string?)connection.Attribute("Destination")}");
                    }
                }
            }
        }

        static bool IsSimulatorFb(XElement fb)
        {
            var name = (string?)fb.Attribute("Name") ?? string.Empty;
            var type = (string?)fb.Attribute("Type") ?? string.Empty;
            return SimulatorFbPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) ||
                   SimulatorTypes.Any(t => string.Equals(type, t, StringComparison.Ordinal));
        }

        static bool ReferencesAnySimulatorFb(XElement connection, HashSet<string> simNames)
        {
            var source = (string?)connection.Attribute("Source") ?? string.Empty;
            var destination = (string?)connection.Attribute("Destination") ?? string.Empty;
            return simNames.Any(n =>
                source.StartsWith(n + ".", StringComparison.Ordinal) ||
                destination.StartsWith(n + ".", StringComparison.Ordinal));
        }

        static bool ReferencesSimulatorName(XElement connection)
        {
            var source = (string?)connection.Attribute("Source") ?? string.Empty;
            var destination = (string?)connection.Attribute("Destination") ?? string.Empty;
            return SimulatorFbPrefixes.Any(p =>
                source.StartsWith(p, StringComparison.Ordinal) ||
                destination.StartsWith(p, StringComparison.Ordinal));
        }

        static string Short(string file)
        {
            var name = Path.GetFileName(file);
            var parent = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
            return string.IsNullOrEmpty(parent) ? name : Path.Combine(parent, name);
        }
    }
}
