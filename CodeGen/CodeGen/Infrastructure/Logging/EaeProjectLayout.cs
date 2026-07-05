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

        // The single System GUID folder (IEC61499/System/<guid>/) that holds every device's sysdev; the
        // application + all devices live under it. null if the project root has no such folder yet.
        public static string? FindSystemGuidDir(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            return Directory.EnumerateDirectories(systemDir).FirstOrDefault(d =>
            {
                var name = Path.GetFileName(d);
                return Guid.TryParse(name, out _) && !name.StartsWith(".");
            });
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

            // With >1 .sysres (an orphan alongside the live one), return the ACTIVE resource —
            // the stem the sysdev's <Resource ID="..."/> references — not FirstOrDefault.
            var activeIds = ReadActiveResourceIds(sysdevPath);
            var active = sysresFiles.FirstOrDefault(f =>
                activeIds.Contains(Path.GetFileNameWithoutExtension(f)));
            return active ?? sysresFiles[0];
        }

        // The resource IDs a sysdev references via <Resources><Resource ID="..."/>.
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

        // Enforces one .sysres per device folder: deletes any .sysres (+ sister folder) whose stem
        // is not one of the sysdev's active <Resource ID> values. Conservative — a malformed sysdev
        // (no active id) is skipped so nothing is deleted blind. Returns the count removed.
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

        // File-level guard for EAE's "max 1 resource per device": in a sysdev listing >1 <Resource>,
        // keep the one whose ID has a matching {ID}.sysres on disk (else the first) and drop the rest.
        // Idempotent. Returns the count removed.
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

        // Removes the dead work1ToHomeTime/work2ToHomeTime <Parameter> values from every
        // Seven_State_Actuator_Centre_Home_CAT instance in every deployed sysres. Returns count removed.
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
                        // EAE write-locks the per-device sysres while the project is open — retry to catch a free window.
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

        // Locates the deployed sysdev whose root <Device> has the given Type in the SE.DPAC namespace.
        public static string? FindSysdevByDeviceType(string eaeRoot, string deviceType) =>
            FindSysdev(eaeRoot, deviceType, deviceName: null);

        // Type + Name variant — disambiguates two devices of the same Type (BX1 vs Revolution_Pi, both
        // Soft_dPAC). deviceName == null matches on Type alone.
        public static string? FindSysdevByDeviceTypeAndName(string eaeRoot, string deviceType, string deviceName) =>
            FindSysdev(eaeRoot, deviceType, deviceName);

        static string? FindSysdev(string eaeRoot, string deviceType, string? deviceName)
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
                        string.Equals((string?)root.Attribute("Namespace"), "SE.DPAC", StringComparison.Ordinal) &&
                        (deviceName == null ||
                         string.Equals((string?)root.Attribute("Name"), deviceName, StringComparison.Ordinal)))
                        return sd;
                }
                catch { /* skip malformed */ }
            }
            return null;
        }
    }
}
