using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Translation;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Devices.Core;

namespace CodeGen.Artefacts
{
    // Imperative shell: the EAE tree surgery a generation runs BEFORE anything is emitted - recreate
    // the application shell, sweep instances a previous generation left behind, and reconcile the
    // device resources. It touches files, so it deliberately lives in the backend rather than beside
    // the planner, which must stay side-effect free.
    public static class DemonstratorPreparer
    {
        public class CleanupReport
        {
            public List<string> RemovedFbs { get; } = new();
            public List<string> PreservedFbs { get; } = new();
            public int RemovedConnections { get; set; }
            public List<string> DeviceCleanupLog { get; } = new();
        }

        public static CleanupReport PrepareDemonstratorForGeneration(Configuration.CompilerConfiguration config)
        {
            var report = new CleanupReport();

            // Recreate the app shell (create-if-absent) BEFORE the SyslayPath2 check below.
            CodeGen.Devices.Core.ApplicationShellEmitter.EnsureApplicationShell(
                config, EaeProjectLayout.DeriveEaeProjectRoot(config),
                line => report.DeviceCleanupLog.Add(line));

            if (string.IsNullOrEmpty(config.Paths.SyslayPath2) || !File.Exists(config.Paths.SyslayPath2))
                throw new FileNotFoundException(
                    $"Demonstrator syslay not configured or missing: '{config.Paths.SyslayPath2}'");

            CleanFile(config.Paths.SyslayPath2, "SubAppNetwork", report, config.Manifest);

            // EAE renames the .sysres to the short-hex resource ID, so resolve the actual file by globbing the sysdev folder.
            foreach (var sysresPath in ResolveActualSysresPaths(config))
                CleanFile(sysresPath, "FBNetwork", report, config.Manifest);

            CleanM262SysdevResources(config, report);

            SweepBridgeFbsFromAllSysres(config, report);

            return report;
        }

        // Remove stale MQTT bridge FBs (MqttFmt_/MqttPub_ names only, never MqttConn) + their connections from every .sysres in place.
        private static void SweepBridgeFbsFromAllSysres(Configuration.CompilerConfiguration config, CleanupReport report)
        {
            var syslayDir = Path.GetDirectoryName(config.Paths.SyslayPath2);
            if (string.IsNullOrEmpty(syslayDir)) return;
            var sysGuidDir = Path.GetDirectoryName(syslayDir);
            if (string.IsNullOrEmpty(sysGuidDir) || !Directory.Exists(sysGuidDir)) return;

            try { if (!Directory.EnumerateFiles(sysGuidDir, "*.sysdev").Any()) return; }
            catch { return; }

            System.Xml.Linq.XNamespace ns = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;
            bool IsBridge(string? n) =>
                n != null && (n.StartsWith("MqttFmt_", StringComparison.Ordinal)
                           || n.StartsWith("MqttPub_", StringComparison.Ordinal));

            List<string> sysresFiles;
            try { sysresFiles = Directory.EnumerateFiles(sysGuidDir, "*.sysres", SearchOption.AllDirectories).ToList(); }
            catch { return; }

            foreach (var file in sysresFiles)
            {
                System.Xml.Linq.XDocument doc;
                try { doc = System.Xml.Linq.XDocument.Load(file, System.Xml.Linq.LoadOptions.PreserveWhitespace); }
                catch { continue; }
                var net = doc.Root?.Element(ns + "FBNetwork") ?? doc.Root?.Element(ns + "SubAppNetwork");
                if (net == null) continue;

                int removedFb = 0, removedConn = 0;
                foreach (var fb in net.Elements(ns + "FB")
                             .Where(f => IsBridge((string?)f.Attribute("Name"))).ToList())
                { fb.Remove(); removedFb++; }

                foreach (var section in new[] { "EventConnections", "DataConnections" })
                {
                    var sec = net.Element(ns + section);
                    if (sec == null) continue;
                    foreach (var c in sec.Elements(ns + "Connection").Where(c =>
                    {
                        var s = (string?)c.Attribute("Source") ?? "";
                        var d = (string?)c.Attribute("Destination") ?? "";
                        return IsBridge(s.Split('.')[0]) || IsBridge(d.Split('.')[0]);
                    }).ToList())
                    { c.Remove(); removedConn++; }
                }

                if (removedFb > 0 || removedConn > 0)
                {
                    try
                    {
                        doc.Save(file);
                        report.DeviceCleanupLog.Add(
                            $"[CleanDevice] swept {removedFb} stale bridge FB(s) + {removedConn} wire(s) " +
                            $"from {Path.GetFileName(file)}");
                    }
                    catch { /* best-effort */ }
                }
            }
        }

        // Every .sysres that actually exists in the M262 sysdev folder (SysresPath2's directory).
        private static IEnumerable<string> ResolveActualSysresPaths(Configuration.CompilerConfiguration config)
        {
            if (string.IsNullOrEmpty(config.Paths.SysresPath2)) yield break;
            var dir = Path.GetDirectoryName(config.Paths.SysresPath2);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
            foreach (var f in Directory.EnumerateFiles(dir, "*.sysres",
                         SearchOption.TopDirectoryOnly))
                yield return f;
        }

        // Dedup <Resource> entries in the M262 sysdev (first survives); each dropped Resource's sibling .sysres is deleted, the .hcf left alone.
        private static void CleanM262SysdevResources(Configuration.CompilerConfiguration config, CleanupReport report)
        {
            void Log(string line) => report.DeviceCleanupLog.Add($"[CleanDevice] {line}");

            string? eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(config);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                Log("could not derive EAE project root from MapperConfig.SyslayPath2; sysdev dedup skipped");
                return;
            }

            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir))
            {
                Log($"IEC61499/System not found under {eaeRoot}; sysdev dedup skipped");
                return;
            }

            string? sysdevPath = null;
            foreach (var candidate in Directory.EnumerateFiles(
                systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(candidate);
                    var root = doc.Root;
                    if (root == null) continue;
                    var type  = (string?)root.Attribute("Type")      ?? string.Empty;
                    var nspac = (string?)root.Attribute("Namespace") ?? string.Empty;
                    if (string.Equals(type, config.Targets.Of(config.Targets.FeedTarget).DeviceType,
                            StringComparison.Ordinal) &&
                        string.Equals(nspac, TargetDescriptor.DeviceNamespace, StringComparison.Ordinal))
                    {
                        sysdevPath = candidate;
                        break;
                    }
                }
                catch { /* skip malformed; keep scanning */ }
            }
            if (sysdevPath == null)
            {
                Log($"no M262 sysdev found under {systemDir}; nothing to dedupe");
                return;
            }

            Log($"reading sysdev at {sysdevPath}");

            XDocument sysdevDoc;
            try { sysdevDoc = XDocument.Load(sysdevPath); }
            catch (Exception ex)
            {
                Log($"failed to load sysdev {sysdevPath}: {ex.Message}");
                return;
            }
            var sysdevRoot = sysdevDoc.Root;
            if (sysdevRoot == null)
            {
                Log($"sysdev {sysdevPath} has no root element; nothing to dedupe");
                return;
            }

            XNamespace ns = sysdevRoot.GetDefaultNamespace();
            var resourcesEl = sysdevRoot.Element(ns + "Resources");
            var resources = resourcesEl?.Elements(ns + "Resource").ToList()
                ?? new List<XElement>();
            int count = resources.Count;

            Log($"found {count} resources");

            var sysdevStem = Path.GetFileNameWithoutExtension(sysdevPath);
            var sysdevDir  = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!, sysdevStem);
            int sysresCount = 0;
            if (Directory.Exists(sysdevDir))
                sysresCount = Directory.GetFiles(
                    sysdevDir, "*.sysres", SearchOption.TopDirectoryOnly).Length;

            if (count == 1 && sysresCount == 1)
            {
                Log("M262 sysdev clean, no duplicates");
                return;
            }

            if (count <= 1)
            {
                Log($"M262 sysdev has {count} resource(s), nothing to dedupe");
                return;
            }

            var keep = resources[0];
            var firstResourceId = (string?)keep.Attribute("ID")
                ?? (string?)keep.Attribute("Name")
                ?? "(unknown)";

            int removed = 0;
            for (int i = 1; i < resources.Count; i++)
            {
                var dup = resources[i];
                var dupId   = (string?)dup.Attribute("ID")   ?? string.Empty;
                var dupName = (string?)dup.Attribute("Name") ?? string.Empty;
                var dupIdent = !string.IsNullOrEmpty(dupId) ? dupId : dupName;

                string deletedSysresPath = string.Empty;
                if (!string.IsNullOrEmpty(dupId) && Directory.Exists(sysdevDir))
                {
                    var candidate = Path.Combine(sysdevDir, dupId + ".sysres");
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            File.Delete(candidate);
                            deletedSysresPath = candidate;
                        }
                        catch (Exception ex)
                        {
                            Log($"failed to delete sysres {candidate}: {ex.Message}");
                        }
                    }
                }

                dup.Remove();
                removed++;

                if (deletedSysresPath.Length > 0)
                    Log($"removed duplicate resource {dupIdent}, deleted sysres file {deletedSysresPath}");
                else
                    Log($"removed duplicate resource {dupIdent} (no matching .sysres file on disk)");
            }

            try
            {
                sysdevDoc.Save(sysdevPath);
            }
            catch (Exception ex)
            {
                Log($"failed to save sysdev {sysdevPath} after dedup: {ex.Message}");
                return;
            }

            Log($"removed {removed} duplicate Resource entries, kept {firstResourceId}");
            Log($"kept resource {firstResourceId}");
        }

        private static void CleanFile(string path, string netTag, CleanupReport report,
            Mapping.TemplateIndex manifest)
        {
            report.DeviceCleanupLog.Add($"[Clean] file={path} root=<{netTag}>");

            XNamespace ns = CodeGen.Devices.Core.Station2DeviceEmitter.LibElNs;
            var doc = XDocument.Load(path);
            var net = doc.Root?.Element(ns + netTag);
            if (net == null)
            {
                report.DeviceCleanupLog.Add($"[Clean] <{netTag}> not found in {Path.GetFileName(path)} — nothing to clean");
                return;
            }

            var fbsToRemove = new List<XElement>();
            var namesToRemove = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fb in net.Elements(ns + "FB").ToList())
            {
                var fbType = fb.Attribute("Type")?.Value ?? string.Empty;
                var fbName = fb.Attribute("Name")?.Value ?? string.Empty;
                var fbNs = fb.Attribute("Namespace")?.Value ?? string.Empty;

                // Swept because this run re-emits it; anything else on the canvas is left alone.
                bool isUniversal = manifest.EmittedTypes.Contains(fbType) ||
                    (fbType == "plcStart" && fbNs == "SE.AppBase");

                if (isUniversal)
                {
                    fbsToRemove.Add(fb);
                    namesToRemove.Add(fbName);
                    report.RemovedFbs.Add($"{fbName} ({fbType})");
                    report.DeviceCleanupLog.Add($"[Clean]   FB {fbName} type={fbType} -> REMOVE");
                }
                else
                {
                    report.PreservedFbs.Add($"{fbName} ({fbType})");
                    report.DeviceCleanupLog.Add($"[Clean]   FB {fbName} type={fbType} -> PRESERVE");
                }
            }

            foreach (var fb in fbsToRemove) fb.Remove();

            int connRemovedHere = 0;
            foreach (var section in new[] { "EventConnections", "DataConnections", "AdapterConnections" })
            {
                var s = net.Element(ns + section);
                if (s == null) continue;
                foreach (var conn in s.Elements(ns + "Connection").ToList())
                {
                    var src = conn.Attribute("Source")?.Value ?? string.Empty;
                    var dst = conn.Attribute("Destination")?.Value ?? string.Empty;
                    var srcFb = src.Split('.', 2)[0];
                    var dstFb = dst.Split('.', 2)[0];
                    if (namesToRemove.Contains(srcFb) || namesToRemove.Contains(dstFb))
                    {
                        conn.Remove();
                        report.RemovedConnections++;
                        connRemovedHere++;
                    }
                }
            }

            report.DeviceCleanupLog.Add(
                $"[Clean] {Path.GetFileName(path)}: removed {fbsToRemove.Count} FB(s), " +
                $"{connRemovedHere} connection(s)");

            doc.Save(path);
        }
    }
}
