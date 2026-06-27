using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    public static class EaeProjectLayout
    {
        public static string? DeriveEaeProjectRoot(MapperConfig cfg)
        {
            var path = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dir = Path.GetDirectoryName(path);
            while (dir != null)
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.dfbproj").Any())
                    return Path.GetDirectoryName(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        public static string? FindSysresFor(string sysdevPath)
        {
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));
            if (!Directory.Exists(sysdevFolder)) return null;
            var sysresFiles = Directory
                .EnumerateFiles(sysdevFolder, "*.sysres", SearchOption.TopDirectoryOnly)
                .ToList();
            if (sysresFiles.Count == 0) return null;
            if (sysresFiles.Count == 1) return sysresFiles[0];

            // MORE THAN ONE .sysres in the folder => an orphan from a prior deploy
            // sits alongside the active resource. We MUST return the ACTIVE one --
            // the .sysres whose stem matches the resource ID the parent sysdev's
            // <Resource ID="..."/> actually references -- NOT FirstOrDefault, which
            // routinely returned the orphan (its GUID often sorts before the active
            // resource's). Picking the orphan made the FB mirror + opcua-stamp write
            // to the orphan: it populated the orphan .sysres and created an orphan
            // "{orphanId}/" sister folder (with opcua.xml), while the ACTIVE resource
            // stayed empty. EAE then loads that ghost sister folder and raises the
            // "Solution Integrity / Repair Instances" dialog on the duplicated CAT
            // instances. Matching the sysdev's active ID makes the mirror always
            // target the live resource; the orphan is left for the stale-sysres /
            // sister-folder sweep to delete.
            var activeIds = ReadActiveResourceIds(sysdevPath);
            var active = sysresFiles.FirstOrDefault(f =>
                activeIds.Contains(Path.GetFileNameWithoutExtension(f)));
            return active ?? sysresFiles[0];
        }

        /// <summary>
        /// The resource IDs a sysdev actually references via
        /// <c>&lt;Resources&gt;&lt;Resource ID="..."/&gt;</c>. Used to tell the
        /// live resource apart from an orphan .sysres left in the same folder.
        /// </summary>
        static HashSet<string> ReadActiveResourceIds(string sysdevPath)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = XDocument.Load(sysdevPath).Root;
                if (root == null) return ids;
                XNamespace ns = root.GetDefaultNamespace();
                foreach (var r in root.Element(ns + "Resources")?.Elements(ns + "Resource")
                                  ?? Enumerable.Empty<XElement>())
                {
                    var id = (string?)r.Attribute("ID");
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
            catch { /* malformed sysdev -> empty set, caller falls back to first file */ }
            return ids;
        }

        /// <summary>
        /// Enforces the "exactly one .sysres per device folder" invariant: for every sysdev under
        /// <paramref name="eaeRoot"/>/IEC61499/System, deletes any <c>.sysres</c> in the sysdev's
        /// device folder (and its stem-named sister folder) whose stem is NOT one of the sysdev's
        /// active <c>&lt;Resource ID&gt;</c> values.
        ///
        /// Why this exists: when a device is (re)created its sysres is first written under a default
        /// id, then the <c>.hcf</c> id-realignment switches the sysdev's active Resource ID to the
        /// <c>.hcf</c> ResourceId (e.g. BX1 default → <c>78E9…</c>) and writes the live resource
        /// under the new id. The early per-device sweep in <c>Station2DeviceEmitter.EmitOnePlc</c>
        /// runs BEFORE that realignment, so the old default-named shell (an empty-FBNetwork
        /// <c>RES0</c> sysres) can linger as an orphan — which EAE then carries in its build cache
        /// (<c>obj/System.hash</c>). This runs LATE (after every device/hcf/mirror/wire step) so the
        /// folder ends with exactly the live resource. Idempotent and conservative: a sysdev that
        /// declares no active resource id (malformed) is skipped, so nothing is ever deleted blind.
        /// EAE refreshes <c>obj/System.hash</c> + <c>.obsolete</c> from the actual files on its next
        /// Build, so this sweep only needs to remove the stray <c>.sysres</c>. Returns the count removed.
        /// </summary>
        public static int SweepOrphanSysres(string? eaeRoot, Action<string>? log = null)
        {
            if (string.IsNullOrEmpty(eaeRoot)) return 0;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return 0;

            int removed = 0;
            foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                var activeIds = ReadActiveResourceIds(sysdev);
                if (activeIds.Count == 0) continue;   // can't distinguish active from orphan -> leave alone

                var folder = Path.Combine(
                    Path.GetDirectoryName(sysdev)!, Path.GetFileNameWithoutExtension(sysdev));
                if (!Directory.Exists(folder)) continue;

                foreach (var sysres in Directory
                             .EnumerateFiles(folder, "*.sysres", SearchOption.TopDirectoryOnly).ToList())
                {
                    var stem = Path.GetFileNameWithoutExtension(sysres);
                    if (activeIds.Contains(stem)) continue;   // the live resource — keep

                    try
                    {
                        File.Delete(sysres);
                        removed++;
                        var sister = Path.Combine(folder, stem);
                        if (Directory.Exists(sister)) Directory.Delete(sister, recursive: true);
                        log?.Invoke($"[Sysres][Sweep] removed orphan {Path.GetFileName(sysres)} from " +
                            $"{Path.GetFileName(folder)} (not the sysdev's active resource).");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[Sysres][Sweep][Warn] could not remove orphan " +
                            $"{Path.GetFileName(sysres)}: {ex.Message}");
                    }
                }
            }
            return removed;
        }

        /// <summary>
        /// Enforces EAE's "max 1 resource per device" limit at the FILE level: for every sysdev whose
        /// <c>&lt;Resources&gt;</c> lists more than one <c>&lt;Resource&gt;</c>, keeps the one whose ID has a
        /// matching <c>{ID}.sysres</c> on disk (else the first) and removes the rest, then re-saves.
        /// This is the file-level guard behind EAE's "Device X contains 2 instances of
        /// Runtime.Management.EMB_RES_ECO" error: heavy resource churn (a RES0→M580_RES rename, an id
        /// flip) with EAE open can leave a stray second <c>&lt;Resource&gt;</c> in a sysdev, which EAE then
        /// caches. Running this on every Generate means the project EAE re-reads always declares exactly
        /// one resource per device. Idempotent (no-op on an already-single sysdev). Returns the count removed.
        /// </summary>
        public static int DedupeSysdevResources(string? eaeRoot, Action<string>? log = null)
        {
            if (string.IsNullOrEmpty(eaeRoot)) return 0;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return 0;

            int removed = 0;
            foreach (var sysdev in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(sysdev, LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) continue;
                    XNamespace ns = root.GetDefaultNamespace();
                    var resources = root.Element(ns + "Resources");
                    if (resources == null) continue;
                    var resList = resources.Elements(ns + "Resource").ToList();
                    if (resList.Count <= 1) continue;   // already single -> nothing to do

                    var folder = Path.Combine(
                        Path.GetDirectoryName(sysdev)!, Path.GetFileNameWithoutExtension(sysdev));
                    var onDisk = Directory.Exists(folder)
                        ? new HashSet<string>(Directory
                            .EnumerateFiles(folder, "*.sysres", SearchOption.TopDirectoryOnly)
                            .Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);
                    // Keep the <Resource> whose ID has a live .sysres (else the first); drop the rest.
                    var keeper = resList.FirstOrDefault(r => onDisk.Contains((string?)r.Attribute("ID") ?? ""))
                                 ?? resList[0];
                    foreach (var r in resList.Where(r => r != keeper)) { r.Remove(); removed++; }
                    doc.Save(sysdev);
                    log?.Invoke($"[Sysdev][Dedupe] {Path.GetFileName(sysdev)}: removed {resList.Count - 1} " +
                        $"extra <Resource>, kept {(string?)keeper.Attribute("Name")} ({(string?)keeper.Attribute("ID")}).");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[Sysdev][Dedupe][Warn] {Path.GetFileName(sysdev)}: {ex.Message}");
                }
            }
            return removed;
        }

        /// <summary>
        /// Removes the dead <c>work1ToHomeTime</c> / <c>work2ToHomeTime</c> &lt;Parameter&gt; values
        /// from every <c>Seven_State_Actuator_Centre_Home_CAT</c> instance in every deployed sysres.
        /// The two work-to-home E_DELAY timers in that CAT are dead (their EO feeds only
        /// <c>ReturnToHomeHandler.Work1/Work2ToHomeTimerEvent</c>, which the No_Sensor_Handler_7SCH
        /// ECC has NO transitions on), so the values had no effect. The CAT InputVar default
        /// (<c>T#0s</c>) then applies; the dead timer is harmless. Returns the number of
        /// &lt;Parameter&gt; elements removed. Best-effort per file.
        /// </summary>
        public static int StripStaleHomeTimerParams(string? eaeRoot, Action<string>? log = null)
        {
            if (string.IsNullOrEmpty(eaeRoot)) return 0;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return 0;

            var timerParamNames = new[] { "work1ToHomeTime", "work2ToHomeTime" };
            int removed = 0;
            foreach (var sysres in Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories))
            {
                try
                {
                    var doc = XDocument.Load(sysres, LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) continue;
                    XNamespace ns = root.GetDefaultNamespace();
                    var net = root.Element(ns + "FBNetwork");
                    if (net == null) continue;

                    bool changed = false;
                    foreach (var fb in net.Elements(ns + "FB")
                                 .Where(f => (string?)f.Attribute("Type") == "Seven_State_Actuator_Centre_Home_CAT"))
                    {
                        foreach (var p in fb.Elements(ns + "Parameter")
                                     .Where(p => timerParamNames.Contains((string?)p.Attribute("Name")))
                                     .ToList())
                        {
                            p.Remove();
                            changed = true;
                            removed++;
                        }
                    }
                    if (changed)
                    {
                        // EAE holds a WRITE lock on the per-device sysres while the project is open
                        // (the .fbt is not locked the same way, which is why the poll strip lands but a
                        // plain Save here did not). Retry so the strip still writes if the lock frees in a
                        // window; if it never does, log loudly so it is not a silent no-op.
                        for (int attempt = 0; ; attempt++)
                        {
                            try { doc.Save(sysres); break; }
                            catch (IOException) when (attempt < 8)
                            {
                                System.Threading.Thread.Sleep(250);
                            }
                            catch (UnauthorizedAccessException) when (attempt < 8)
                            {
                                System.Threading.Thread.Sleep(250);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[Sysres][TimerStrip][Warn] could NOT write {Path.GetFileName(sysres)} " +
                        $"(EAE may hold it locked — close EAE before Test Runtime): {ex.Message}");
                }
            }
            return removed;
        }

        /// <summary>
        /// Locates the deployed sysdev whose root &lt;Device&gt; has the given
        /// <paramref name="deviceType"/> (e.g. "M580_dPAC", "Soft_dPAC") in the
        /// SE.DPAC namespace. Returns null if none match.
        /// </summary>
        public static string? FindSysdevByDeviceType(string eaeRoot, string deviceType)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sd in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var root = XDocument.Load(sd).Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    if (string.Equals((string?)root.Attribute("Type"), deviceType, StringComparison.Ordinal) &&
                        string.Equals((string?)root.Attribute("Namespace"), "SE.DPAC", StringComparison.Ordinal))
                        return sd;
                }
                catch { /* skip malformed */ }
            }
            return null;
        }
    }
}
