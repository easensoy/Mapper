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

            // 3. Walk every <ParameterValue Name="DIxx"|"DOxx"> on the TM3DI16_G /
            //    TM3DQ16T_G modules and rewrite Value with the symbol IoBindings
            //    resolves for that pin. Pin -> symbol mapping is driven entirely by the
            //    optional pin_di_athome / pin_di_atwork / pin_do_outputToWork columns
            //    in the IO bindings xlsx; if those columns are absent the .hcf is
            //    left at its baseline values.
            var bindings = bindingsOverride ?? LoadBindings(cfg);
            if (bindings == null)
            {
                result.Warnings.Add("IO bindings xlsx not found — hcf channel symlinks left as baseline.");
                return result;
            }

            int written = OverwriteHcfParameterValues(dstHcf, bindings, result);
            if (written == 0 && bindings.PinAssignments.Count == 0)
                result.Warnings.Add(
                    "IO bindings xlsx has no pin_di_athome / pin_di_atwork / " +
                    "pin_do_outputToWork columns — add them to drive .hcf channel " +
                    "symlinks. .hcf left at baseline values.");

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
        /// Walks the TM3DI16_G / TM3DQ16T_G modules in the copied .hcf, looks up each
        /// pin's Value via <see cref="IoBindings.ResolveSymbol"/>, and rewrites Value
        /// when the pin is bound. Element-name-agnostic on the channel container
        /// (matches both <c>&lt;ParameterValue&gt;</c> and other shapes) but scoped
        /// to channels under those two specific module names so unrelated pins on
        /// the BMTM3 / TM262L01MDESE8T don't get touched.
        /// </summary>
        static int OverwriteHcfParameterValues(string hcfPath, IoBindings bindings, HwConfigCopyResult result)
        {
            var doc = XDocument.Load(hcfPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            int written = 0;

            // The two IO modules whose pin channels carry RES0 symlinks.
            var ioModuleNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "TM3DI16_G", "TM3DQ16T_G"
            };

            foreach (var module in doc.Descendants().Where(e =>
                ioModuleNames.Contains((string?)e.Element(ns + "Name")?.Value ?? string.Empty)))
            {
                var pvContainer = module.Element(ns + "ParameterValues");
                if (pvContainer == null) continue;

                foreach (var pv in pvContainer.Elements(ns + "ParameterValue"))
                {
                    var pin = (string?)pv.Attribute("Name");
                    if (string.IsNullOrEmpty(pin)) continue;
                    var symbol = bindings.ResolveSymbol(pin);
                    if (symbol == null) continue; // unbound pin — leave baseline value

                    var valueAttr = pv.Attribute("Value");
                    if (valueAttr == null)
                        pv.SetAttributeValue("Value", symbol);
                    else
                        valueAttr.Value = symbol;

                    result.ParametersOverwrittenSet.Add(pin);
                    result.ParametersOverwritten.Add($"{pin}={symbol}");
                    written++;
                }
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
