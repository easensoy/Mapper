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
        const string FeederFbName = "Feeder";

        public static HwConfigCopyResult Copy(MapperConfig cfg)
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

            // 2. Find the baseline .hcf and copy it to the matching sysdev folder
            //    in the target EAE project. The .hcf path under IEC61499/System mirrors
            //    {sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf.
            var srcHcf = FindBaselineHcf(baseline)
                ?? throw new FileNotFoundException(
                    $"No .hcf found under {baseline}\\IEC61499\\System\\");
            var dstHcf = ResolveTargetHcfPath(eaeRoot)
                ?? throw new InvalidOperationException(
                    "Cannot resolve target .hcf path — no .sysdev under target IEC61499/System tree.");

            Directory.CreateDirectory(Path.GetDirectoryName(dstHcf)!);
            File.Copy(srcHcf, dstHcf, overwrite: true);
            result.HcfPath = dstHcf;

            // 3. Load IoBindings and overwrite the three channel ParameterValue
            //    strings on the copied .hcf.
            var bindings = LoadBindings(cfg);
            if (bindings != null && bindings.Actuators.TryGetValue(FeederFbName, out var feeder))
            {
                var overwrites = new Dictionary<string, string?>
                {
                    ["DI00"] = feeder.AthomeTag        != null ? $"'RES0.{FeederFbName}.athome'"       : null,
                    ["DI01"] = feeder.AtworkTag        != null ? $"'RES0.{FeederFbName}.atwork'"       : null,
                    ["DO00"] = feeder.OutputToWorkTag  != null ? $"'RES0.{FeederFbName}.OutputToWork'" : null,
                };

                int written = OverwriteParameterValues(dstHcf, overwrites, result);
                result.ParametersOverwritten.AddRange(overwrites
                    .Where(kv => kv.Value != null && result.ParametersOverwrittenSet.Contains(kv.Key))
                    .Select(kv => $"{kv.Key}={kv.Value}"));
                if (written == 0)
                    result.Warnings.Add(
                        "No ParameterValue elements with Name=DI00/DI01/DO00 found in copied .hcf — " +
                        "channel symlinks not written. Verify the baseline schema.");
            }
            else
            {
                result.Warnings.Add(
                    $"IoBindings has no actuator named '{FeederFbName}' — hcf channel symlinks left as baseline.");
            }

            return result;
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
        /// Walks every element in the .hcf and, for any node that names a channel
        /// (Name="DI00" etc.) and carries a Value attribute, overwrites Value with
        /// the supplied string. Tolerates either &lt;ParameterValue Name="DI00"
        /// Value="..."/&gt; or other element shapes that follow the same Name/Value
        /// attribute pattern — we don't pin the element local-name so a future
        /// schema variant doesn't silently break the rewrite.
        /// </summary>
        static int OverwriteParameterValues(string hcfPath,
            Dictionary<string, string?> nameToValue, HwConfigCopyResult result)
        {
            var doc = XDocument.Load(hcfPath);
            int written = 0;

            foreach (var el in doc.Descendants())
            {
                var nameAttr = el.Attribute("Name");
                if (nameAttr == null) continue;
                if (!nameToValue.TryGetValue(nameAttr.Value, out var newValue)) continue;
                if (newValue == null) continue; // binding absent — leave baseline value
                var valueAttr = el.Attribute("Value");
                if (valueAttr == null) continue;
                valueAttr.Value = newValue;
                result.ParametersOverwrittenSet.Add(nameAttr.Value);
                written++;
            }

            if (written > 0) doc.Save(hcfPath);
            return written;
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
                File.Copy(f, target, overwrite: true);
                result.FilesCopied++;
            }
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
