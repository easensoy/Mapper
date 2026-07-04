using System;
using System.IO;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // Wipes EAE's per-device compile cache (bin/ obj/ System/.obsolete/ + resets snapshot.xml) and drops
    // duplicate-Layer-ID .syslay stubs. EAE caches compile state structurally, so a stale cache surfaces
    // as "Device X contains 2 instances of EMB_RES_ECO" even on a clean disk. Run BEFORE Generate.
    public static class CompileCachePurger
    {
        public sealed class PurgeResult
        {
            public int FoldersRemoved { get; set; }
            public bool SnapshotReset { get; set; }
            public System.Collections.Generic.List<string> Warnings { get; } = new();
        }

        public static PurgeResult Purge(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new PurgeResult();
            var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add("EAE project root not derivable — compile cache not purged.");
                return result;
            }
            var iec = Path.Combine(eaeRoot, "IEC61499");
            if (!Directory.Exists(iec))
            {
                result.Warnings.Add($"IEC61499/ folder missing at {iec}.");
                return result;
            }

            foreach (var folder in new[] { "bin", "obj" })
            {
                var path = Path.Combine(iec, folder);
                if (!Directory.Exists(path)) continue;
                try
                {
                    Directory.Delete(path, recursive: true);
                    result.FoldersRemoved++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"Could not delete IEC61499\\{folder}: {ex.Message}. " +
                        "EAE may have a file lock — retry after closing EAE.");
                }
            }

            // EAE's validator walks System/.obsolete for "removed but still referenced" records; stale
            // ones there surface as compile errors.
            var obsolete = Path.Combine(iec, "System", ".obsolete");
            if (Directory.Exists(obsolete))
            {
                try { Directory.Delete(obsolete, recursive: true); result.FoldersRemoved++; }
                catch (Exception ex) { result.Warnings.Add($"Could not delete System\\.obsolete: {ex.Message}"); }
            }

            // Two .syslay files sharing a <Layer ID> make EAE double-count the Layer's Resources
            // ("2 instances of EMB_RES_ECO"). Keep the canonical zero-GUID-named one; drop siblings.
            var systemDir = Path.Combine(iec, "System");
            if (Directory.Exists(systemDir))
            {
                foreach (var sysappDir in Directory.EnumerateDirectories(systemDir, "*", SearchOption.AllDirectories))
                {
                    var syslays = Directory.EnumerateFiles(sysappDir, "*.syslay",
                        SearchOption.TopDirectoryOnly).ToList();
                    if (syslays.Count < 2) continue;

                    // Group by Layer ID; for any group with >1 file, keep the
                    // canonical zero-GUID-named one and delete the rest.
                    var byLayerId = new System.Collections.Generic.Dictionary<string,
                        System.Collections.Generic.List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sl in syslays)
                    {
                        string? layerId = TryReadLayerId(sl);
                        if (string.IsNullOrEmpty(layerId)) continue;
                        if (!byLayerId.TryGetValue(layerId, out var list))
                            byLayerId[layerId] = list = new System.Collections.Generic.List<string>();
                        list.Add(sl);
                    }
                    foreach (var grp in byLayerId.Values.Where(v => v.Count > 1))
                    {
                        var keeper = grp.FirstOrDefault(p =>
                            Path.GetFileNameWithoutExtension(p)
                                .StartsWith("00000000-0000-0000-0000-", StringComparison.Ordinal))
                            ?? grp.OrderByDescending(p => new FileInfo(p).Length).First();
                        foreach (var dup in grp.Where(p =>
                            !string.Equals(p, keeper, StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                File.Delete(dup);
                                // EAE lays a stem-named sister folder for the .syslay's opcua/metadata.
                                var sisterDir = Path.Combine(
                                    Path.GetDirectoryName(dup)!,
                                    Path.GetFileNameWithoutExtension(dup));
                                if (Directory.Exists(sisterDir))
                                    Directory.Delete(sisterDir, recursive: true);
                                result.Warnings.Add(
                                    $"Removed duplicate-Layer-ID syslay '{Path.GetFileName(dup)}' " +
                                    $"(canonical '{Path.GetFileName(keeper)}' kept).");
                            }
                            catch (Exception ex)
                            {
                                result.Warnings.Add(
                                    $"Could not delete duplicate syslay {Path.GetFileName(dup)}: {ex.Message}");
                            }
                        }
                    }
                }
            }

            // Reset snapshot.xml so EAE does a full re-snapshot instead of a stale incremental one.
            var snapshot = Path.Combine(iec, "System", "snapshot.xml");
            if (File.Exists(snapshot))
            {
                try
                {
                    File.WriteAllText(snapshot,
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                        "<baseline xmlns:xsd=\"http://www.nxtControl.com/Snapshot\" " +
                        "xmlns=\"http://tempuri.org/XMLSchema1.xsd\" />\r\n");
                    result.SnapshotReset = true;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"Could not reset snapshot.xml: {ex.Message}");
                }
            }

            return result;
        }

        static string? TryReadLayerId(string syslayPath)
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(syslayPath);
                return (string?)doc.Root?.Attribute("ID");
            }
            catch { return null; }
        }
    }
}
