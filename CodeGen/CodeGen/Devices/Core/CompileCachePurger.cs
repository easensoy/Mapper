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
    }
}
