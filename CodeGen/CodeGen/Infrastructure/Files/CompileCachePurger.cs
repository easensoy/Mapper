using System;
using System.IO;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Wipes EAE 24.1's per-device compile cache that survives across Mapper
    /// Generate runs. Specifically:
    /// <list type="bullet">
    ///   <item><c>IEC61499\bin\CompilerResults\&lt;sysdev-guid&gt;\</c> —
    ///         per-device compile output (FB binaries, hashes, build IDs).</item>
    ///   <item><c>IEC61499\obj\&lt;sysdev-guid&gt;\</c> —
    ///         intermediate object files + .multires resource-count cache.</item>
    ///   <item><c>IEC61499\System\snapshot.xml</c> —
    ///         per-network-profile compile baseline; reset to empty so EAE
    ///         re-walks the system tree on next Compile.</item>
    /// </list>
    ///
    /// <para>Why this exists: EAE caches compile state structurally — when a
    /// sysres gets renamed (e.g. RES0 -> M262_RES) or a stale sysres in a
    /// sysdev folder is removed, EAE's cache still records the OLD layout and
    /// surfaces stale errors like "Device M580 contains 2 instances of
    /// Runtime.Management.EMB_RES_ECO" even after the disk is clean. EAE's own
    /// "Clean" button performs only a minor cache reset and does not flush
    /// these per-device caches. DemonstratorWiper.Wipe() already deletes bin/
    /// + obj/ as part of its deep clean, but the regular Generate flow does
    /// not — so a user who clicks Generate (without first clicking Clean
    /// Demonstrator) inherits the stale state.</para>
    ///
    /// <para>Idempotent and best-effort: missing folders are skipped; file
    /// locks are tolerated (EAE itself holding the .so / .multires open
    /// briefly is normal during deploy). Run BEFORE the rest of Generate so
    /// Compile sees a fresh tree.</para>
    /// </summary>
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

            // Wipe the .obsolete folder. EAE stashes the previous compile's
            // System.sys + per-device history there. Compile rewrites it,
            // but EAE's internal validator also walks .obsolete looking for
            // "removed but still referenced" elements — stale device records
            // there can surface in modern compile errors.
            var obsolete = Path.Combine(iec, "System", ".obsolete");
            if (Directory.Exists(obsolete))
            {
                try { Directory.Delete(obsolete, recursive: true); result.FoldersRemoved++; }
                catch (Exception ex) { result.Warnings.Add($"Could not delete System\\.obsolete: {ex.Message}"); }
            }

            // Detect duplicate-Layer-ID .syslay files. EAE walks every .syslay
            // under System/ and reads its <Layer ID="…"> root attribute. If two
            // .syslay files declare the SAME Layer ID, EAE double-counts every
            // Resource the Layer references — surfaces at compile time as
            // "Device <name> contains 2 instances of Runtime.Management.EMB_RES_ECO".
            // Observed 2026-05-27: sysapp folder carried both the canonical
            // 00000000-0000-0000-0000-000000000000.syslay AND a 238-byte
            // empty stub 2240693B1370B496.syslay — same Layer ID 2240693B1370B496
            // in both. The stub appears after a partial compile / EAE crash and
            // re-emerges on subsequent compiles. Keep the canonical (zero-GUID
            // filename); drop any other .syslay whose Layer ID matches its sibling.
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
                        // Prefer the canonical zero-GUID filename
                        // (00000000-0000-0000-0000-000000000000.syslay) as the keeper.
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
                                // Also delete the sister folder that matches the
                                // stale stem (EAE may have laid one down to hold
                                // the .syslay's opcua / metadata files).
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

            // Reset snapshot.xml to the empty baseline. EAE rewrites it on
            // next Compile with the current device set, so wiping it forces a
            // full re-snapshot instead of an incremental one that could carry
            // stale device records.
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

        /// <summary>
        /// Reads the &lt;Layer ID="…"&gt; root attribute of a .syslay file
        /// without loading the whole document. Returns null on parse failure.
        /// </summary>
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
