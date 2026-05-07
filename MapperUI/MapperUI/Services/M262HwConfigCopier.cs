using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace MapperUI.Services
{
    /// <summary>
    /// Materialises the M262 hardware-configuration .hcf inside the EAE project.
    ///
    /// The slot/module topology — BMTM3 → TM262L01MDESE8T → TM3DI16_G → TM3DQ16T_G —
    /// is fixed by the physical SMC rig wiring and cannot be synthesised from
    /// Control.xml; we copy it verbatim from <see cref="MapperConfig.M262HardwareConfigBaselinePath"/>
    /// so all ItemProperties, IDs, and channel filters survive untouched. After the
    /// copy we open the .hcf in-place and overwrite only the channel-symlink
    /// ParameterValue strings (DI00, DI01, DO00) with values derived from the
    /// IoBindings xlsx, so the controller wires its physical pins to the correct
    /// resource symlinks.
    ///
    /// Symlink format is <c>'RES0.{FB-instance-name}.{tag}'</c> with the FB instance
    /// name preserved from the syslay (case-sensitive — the .hcf strings reference
    /// the exact Name attribute on the syslay FB so EAE's symlink resolver can match).
    /// </summary>
    public static class M262HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg) => Copy(cfg, bindingsOverride: null);

        /// <summary>
        /// Test-injection overload: passes a pre-built <see cref="IoBindings"/> (typically
        /// with a hand-populated <see cref="IoBindings.PinAssignments"/> dict) so the
        /// copier can be exercised without an xlsx on disk. When
        /// <paramref name="bindingsOverride"/> is null this falls back to loading
        /// <see cref="MapperConfig.IoBindingsPath"/>.
        /// </summary>
        public static HwConfigCopyResult Copy(MapperConfig cfg, IoBindings? bindingsOverride)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            var result = new HwConfigCopyResult();
            var baseline = cfg.M262HardwareConfigBaselinePath;
            if (string.IsNullOrWhiteSpace(baseline))
            {
                result.Warnings.Add(
                    "MapperConfig.M262HardwareConfigBaselinePath is empty — skipping hcf copy.");
                return result;
            }
            if (!Directory.Exists(baseline))
                throw new DirectoryNotFoundException($"M262 baseline folder not found: {baseline}");

            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            // 1. Verbatim copy of the baseline HwConfiguration/ folder (carries the
            //    M262 backplane definition, pinned versions, project metadata).
            var srcHwDir = Path.Combine(baseline, "HwConfiguration");
            if (Directory.Exists(srcHwDir))
            {
                var dstHwDir = Path.Combine(eaeRoot, "HwConfiguration");
                CopyDirRecursive(srcHwDir, dstHwDir, result);
            }
            else
            {
                result.Warnings.Add($"Baseline HwConfiguration/ folder missing under {baseline}");
            }

            // 2. Build the target .hcf entirely in memory by:
            //      a) loading the baseline .hcf (BMTM3 backplane + TM3 modules),
            //      b) patching ResourceId to match the target .sysres,
            //      c) overwriting channel ParameterValues from IoBindings,
            //    then writing it atomically with retry-on-lock. Doing all three
            //    edits in memory and writing once is critical because EAE acquires
            //    a FileShare.Read lock on the .hcf as soon as the project is open.
            //    The previous "File.Copy → doc.Save → doc.Save" sequence raced EAE:
            //    File.Copy succeeded, the first Save sometimes succeeded, but the
            //    second Save lost the race and threw IOException — leaving the
            //    .hcf in whatever half-patched state EAE happened to lock at.
            //    A single atomic write closes the race window.
            var srcHcf = FindBaselineHcf(baseline)
                ?? throw new FileNotFoundException(
                    $"No .hcf found under {baseline}\\IEC61499\\System\\");
            var dstHcf = ResolveTargetHcfPath(eaeRoot)
                ?? throw new InvalidOperationException(
                    "Cannot resolve target .hcf path — no .sysdev under target IEC61499/System tree.");

            Directory.CreateDirectory(Path.GetDirectoryName(dstHcf)!);
            result.HcfPath = dstHcf;

            // 2a. Load the baseline .hcf into memory.
            XDocument hcfDoc;
            try
            {
                hcfDoc = XDocument.Load(srcHcf);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load baseline .hcf '{srcHcf}': {ex.Message}", ex);
            }

            // 2b. Patch the .hcf's ResourceId attribute to match the target's .sysres
            //     ID. The baseline .hcf carries its source project's ResourceId
            //     (54EB0B3D5D16444D for SMC_Rig_Expo) which won't match Demonstrator's
            //     .sysres ID (00000000-0000-0000-0000-000000000000). Without this
            //     patch EAE silently fails to bind .hcf to .sysres and the IO
            //     Mapping table stays empty even though both files exist on disk.
            var sysresId = ReadTargetSysresId(eaeRoot);
            if (!string.IsNullOrEmpty(sysresId))
            {
                if (PatchHcfResourceIdInMemory(hcfDoc, sysresId))
                    result.Warnings.Add($"Patched .hcf ResourceId to {sysresId} (was baseline's value)");
            }
            else
            {
                result.Warnings.Add(
                    "Could not read target .sysres ID — .hcf left with baseline ResourceId. " +
                    "EAE IO Mapping table will be empty until ResourceId matches the resource.");
            }

            // 2c. Walk every <ParameterValue Name="DIxx"|"DOxx"> on the TM3DI16_G /
            //     TM3DQ16T_G modules and rewrite Value with the symbol IoBindings
            //     resolves for that pin. Pin -> symbol mapping is driven entirely by the
            //     optional pin_di_athome / pin_di_atwork / pin_do_outputToWork columns
            //     in the IO bindings xlsx; if those columns are absent the .hcf is
            //     left at its baseline values.
            var bindings = bindingsOverride ?? LoadBindings(cfg);
            int paramsWritten = 0;
            if (bindings != null)
            {
                // Read the syslay's top-level FB instances so we know which symbols are
                // actually resolvable. Any pin whose IoBindings PinAssignment points at
                // a Component that isn't in the syslay gets blanked (Value="''") rather
                // than left at the baseline string — otherwise EAE shows bare 'athome'
                // / 'OutputToWork' on those pins (the $${PATH} prefix can't resolve to
                // a non-existent FB instance) and they collide across modules.
                var syslayFbNames = ReadSyslayFbNames(cfg);
                paramsWritten = OverwriteHcfParameterValuesInMemory(
                    hcfDoc, bindings, syslayFbNames, result);
                if (paramsWritten == 0 && bindings.PinAssignments.Count == 0)
                    result.Warnings.Add(
                        "IO bindings xlsx has no pin_di_athome / pin_di_atwork / " +
                        "pin_do_outputToWork columns — add them to drive .hcf channel " +
                        "symlinks. .hcf left at baseline values.");
            }
            else
            {
                result.Warnings.Add(
                    "IO bindings xlsx not found — hcf channel symlinks left as baseline.");
            }

            // 2d. Write the fully-patched .hcf atomically with retry. EAE may hold a
            //     FileShare.Read lock on the existing file; the retry-with-backoff
            //     gives EAE a chance to release the lock between attempts.
            WriteHcfWithRetry(hcfDoc, dstHcf, result);

            return result;
        }

        /// <summary>
        /// Writes the patched .hcf XDocument to <paramref name="dstHcf"/> with
        /// retry-on-IOException, since EAE often holds a read lock on the .hcf
        /// while the project is open. Up to 8 retries with exponential backoff
        /// (50ms → 6.4s, ~12s total). Surfaces the final failure as a warning so
        /// the caller can prompt the user to close/reopen the EAE project.
        /// </summary>
        static void WriteHcfWithRetry(XDocument doc, string dstHcf, HwConfigCopyResult result)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            };

            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    // FileShare.Read so EAE can keep its handle for read while we
                    // write — File.Open with default sharing was the original race.
                    using var fs = new FileStream(dstHcf,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    if (attempt > 1)
                        result.Warnings.Add(
                            $".hcf write succeeded on attempt {attempt} (EAE briefly held a lock).");
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2;
                }
            }

            // Final attempt — let exception propagate so the caller logs and the
            // user knows EAE has the file locked. Do NOT silently swallow.
            using (var fs = new FileStream(dstHcf,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            using (var w = System.Xml.XmlWriter.Create(fs, settings))
            {
                doc.Save(w);
            }
        }

        /// <summary>
        /// In-memory variant of <see cref="PatchHcfResourceId"/> — mutates
        /// <paramref name="doc"/> instead of saving to disk. Returns true if the
        /// ResourceId attribute was actually changed.
        /// </summary>
        static bool PatchHcfResourceIdInMemory(XDocument doc, string newResourceId)
        {
            if (string.IsNullOrWhiteSpace(newResourceId)) return false;
            var item = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
            if (item == null) return false;
            var attr = item.Attribute("ResourceId");
            if (attr != null && string.Equals(attr.Value, newResourceId, StringComparison.Ordinal)) return false;
            item.SetAttributeValue("ResourceId", newResourceId);
            return true;
        }

        /// <summary>
        /// In-memory variant of <see cref="OverwriteHcfParameterValues"/> — mutates
        /// <paramref name="doc"/> instead of saving to disk. Returns the number of
        /// ParameterValue elements modified.
        /// </summary>
        static int OverwriteHcfParameterValuesInMemory(XDocument doc, IoBindings bindings,
            HashSet<string> syslayFbNames, HwConfigCopyResult result)
        {
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            int written = 0;
            var ioModuleNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "TM3DI16_G", "TM3DQ16T_G"
            };
            const string EmptyPinValue = "''";

            foreach (var module in doc.Descendants().Where(e =>
                ioModuleNames.Contains((string?)e.Element(ns + "Name")?.Value ?? string.Empty)))
            {
                var pvContainer = module.Element(ns + "ParameterValues");
                if (pvContainer == null) continue;

                foreach (var pv in pvContainer.Elements(ns + "ParameterValue"))
                {
                    var pin = (string?)pv.Attribute("Name");
                    if (string.IsNullOrEmpty(pin)) continue;

                    string targetValue = EmptyPinValue;
                    var symbol = bindings.ResolveSymbol(pin);
                    if (symbol != null)
                    {
                        bindings.PinAssignments.TryGetValue(pin, out var pa);
                        var owner = pa?.ComponentName ?? string.Empty;
                        if (syslayFbNames.Contains(owner)) targetValue = symbol;
                    }

                    var valueAttr = pv.Attribute("Value");
                    var current = valueAttr?.Value ?? string.Empty;
                    if (string.Equals(current, targetValue, StringComparison.Ordinal)) continue;

                    if (valueAttr == null) pv.SetAttributeValue("Value", targetValue);
                    else valueAttr.Value = targetValue;

                    if (targetValue != EmptyPinValue)
                    {
                        result.ParametersOverwrittenSet.Add(pin);
                        result.ParametersOverwritten.Add($"{pin}={targetValue}");
                    }
                    written++;
                }
            }
            return written;
        }

        // --- baseline discovery ---

        public static string? FindBaselineHcf(string baselineRoot)
        {
            var systemDir = Path.Combine(baselineRoot, "IEC61499", "System");
            if (Directory.Exists(systemDir))
            {
                var hit = Directory.EnumerateFiles(systemDir, "*.hcf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            // Fallback: search the whole baseline root.
            return Directory.EnumerateFiles(baselineRoot, "*.hcf", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        public static string? ResolveTargetHcfPath(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            var sysdev = Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (sysdev == null) return null;
            var sysdevDir = Path.GetDirectoryName(sysdev)!;
            var stem = Path.GetFileNameWithoutExtension(sysdev);
            // Convention: {sys-guid}/{sysdev-guid}.sysdev paired with
            //             {sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf
            return Path.Combine(sysdevDir, stem, stem + ".hcf");
        }

        /// <summary>
        /// Reads the target sysdev's per-device folder for a .sysres file and returns
        /// its root Resource ID attribute. The .hcf's ResourceId attribute must equal
        /// this string for EAE to bind the hardware config to the resource. Returns
        /// empty if no .sysres found.
        /// </summary>
        public static string ReadTargetSysresId(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return string.Empty;
            var sysres = Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (sysres == null) return string.Empty;
            try
            {
                var doc = XDocument.Load(sysres);
                return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Rewrites the .hcf's <c>DeviceHwConfigurationItem ResourceId="..."</c>
        /// attribute to <paramref name="newResourceId"/>. Idempotent — returns 0 if
        /// the attribute already matches. Without this rewrite the .hcf points at
        /// the source baseline's ResourceId (e.g. SMC_Rig_Expo's
        /// <c>54EB0B3D5D16444D</c>) which doesn't exist in the target project, so
        /// EAE silently drops the binding and the IO Mapping table stays empty.
        /// </summary>
        public static int PatchHcfResourceId(string hcfPath, string newResourceId)
        {
            if (!File.Exists(hcfPath) || string.IsNullOrWhiteSpace(newResourceId)) return 0;
            var doc = XDocument.Load(hcfPath);
            var item = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
            if (item == null) return 0;
            var attr = item.Attribute("ResourceId");
            if (attr != null && string.Equals(attr.Value, newResourceId, StringComparison.Ordinal)) return 0;
            item.SetAttributeValue("ResourceId", newResourceId);
            doc.Save(hcfPath);
            return 1;
        }

        // --- IoBindings ---

        static IoBindings? LoadBindings(MapperConfig cfg)
        {
            var path = cfg.IoBindingsPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(path)) return null;
            return IoBindingsLoader.LoadBindings(path);
        }

        // --- .hcf rewrite ---

        /// <summary>
        /// Walks the TM3DI16_G / TM3DQ16T_G modules in the copied .hcf, looks up each
        /// pin's Value via <see cref="IoBindings.ResolveSymbol"/>, and rewrites Value
        /// when the pin is bound. Element-name-agnostic on the channel container
        /// (matches both <c>&lt;ParameterValue&gt;</c> and other shapes) but scoped
        /// to channels under those two specific module names so unrelated pins on
        /// the BMTM3 / TM262L01MDESE8T don't get touched.
        /// </summary>
        static int OverwriteHcfParameterValues(string hcfPath, IoBindings bindings,
            HashSet<string> syslayFbNames, HwConfigCopyResult result)
        {
            var doc = XDocument.Load(hcfPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            int written = 0;

            // The two IO modules whose pin channels carry RES0 symlinks. The
            // BMTM3 / TM262L01MDESE8T modules don't get touched.
            var ioModuleNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "TM3DI16_G", "TM3DQ16T_G"
            };

            // Empty value placeholder for pins not bound to any in-syslay FB. Two
            // single quotes (the EAE schema's "no symlink" marker) — different from
            // a missing Value attribute, which EAE treats as "leave at deploy time
            // default" and would not clear a baseline value.
            const string EmptyPinValue = "''";

            foreach (var module in doc.Descendants().Where(e =>
                ioModuleNames.Contains((string?)e.Element(ns + "Name")?.Value ?? string.Empty)))
            {
                var pvContainer = module.Element(ns + "ParameterValues");
                if (pvContainer == null) continue;

                foreach (var pv in pvContainer.Elements(ns + "ParameterValue"))
                {
                    var pin = (string?)pv.Attribute("Name");
                    if (string.IsNullOrEmpty(pin)) continue;

                    string targetValue = EmptyPinValue;
                    var symbol = bindings.ResolveSymbol(pin);
                    if (symbol != null)
                    {
                        // Confirm the pin's owning Component is actually in the syslay.
                        // If it isn't, the symbol's $${PATH} can't resolve so EAE shows
                        // the bare suffix and we get cross-actuator collisions.
                        bindings.PinAssignments.TryGetValue(pin, out var pa);
                        var owner = pa?.ComponentName ?? string.Empty;
                        if (syslayFbNames.Contains(owner)) targetValue = symbol;
                    }

                    var valueAttr = pv.Attribute("Value");
                    var current = valueAttr?.Value ?? string.Empty;
                    if (string.Equals(current, targetValue, StringComparison.Ordinal)) continue;

                    if (valueAttr == null)
                        pv.SetAttributeValue("Value", targetValue);
                    else
                        valueAttr.Value = targetValue;

                    if (targetValue != EmptyPinValue)
                    {
                        result.ParametersOverwrittenSet.Add(pin);
                        result.ParametersOverwritten.Add($"{pin}={targetValue}");
                    }
                    written++;
                }
            }

            if (written > 0) doc.Save(hcfPath);
            return written;
        }

        /// <summary>
        /// Reads the syslay's top-level FB Names so the .hcf rewrite can blank any
        /// pin whose owning Component isn't in the running syslay (avoids cross-
        /// actuator collisions like DI03='athome' from a Checker that doesn't exist).
        /// </summary>
        static HashSet<string> ReadSyslayFbNames(MapperConfig cfg)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var path = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return set;
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return set;
                XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "SubAppNetwork") ?? root.Element(ns + "FBNetwork");
                if (net == null) return set;
                foreach (var fb in net.Elements(ns + "FB"))
                {
                    var name = (string?)fb.Attribute("Name");
                    if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                }
            }
            catch { /* fall through with empty set — every pin gets blanked */ }
            return set;
        }

        // --- file copy helper ---

        static void CopyDirRecursive(string src, string dst, HwConfigCopyResult result)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f);
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Skip files EAE has open (e.g. .hwconfigproj.lock) rather than aborting
                // the whole copy. Anything we fail to copy gets surfaced as a warning.
                if (!TryCopyWithRetry(f, target))
                    result.Warnings.Add($"Could not copy '{rel}' (file locked) — skipped.");
                else
                    result.FilesCopied++;
            }
        }

        static bool TryCopyWithRetry(string src, string dst)
        {
            const int MaxAttempts = 5;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try { File.Copy(src, dst, overwrite: true); return true; }
                catch (IOException) when (attempt < MaxAttempts)
                { System.Threading.Thread.Sleep(delayMs); delayMs *= 2; }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                { System.Threading.Thread.Sleep(delayMs); delayMs *= 2; }
                catch { return false; }
            }
            return false;
        }
    }

    public class HwConfigCopyResult
    {
        public string? HcfPath { get; set; }
        public int FilesCopied { get; set; }
        public List<string> ParametersOverwritten { get; } = new();
        public HashSet<string> ParametersOverwrittenSet { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new();
    }
}
