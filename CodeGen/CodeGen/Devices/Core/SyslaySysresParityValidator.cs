using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Hard syslay↔sysres↔hcf parity guard. The top-level <c>.syslay</c> is the design canvas
    /// (one shared application across all PLCs); each device <c>.sysres</c> is the deployable that
    /// actually runs on that PLC and MUST be a faithful per-resource projection of the syslay. The
    /// generation pipeline projects the syslay onto each sysres via
    /// <see cref="SysresFbMirror.MirrorFbsIntoSysres"/> (FBs + parameters) and the wire emitters
    /// (connections). If that projection lags — a stale tree, a 2nd Test Runtime, a save lock, an
    /// emit-order bug — the deployed sysres silently DIVERGES from the syslay: the syslay shows
    /// Assembly waiting PartAtAssembly + a Disassembly ejector/robot tail + a Robot FB on M262,
    /// while the sysres still waits BearingSensor / ends at unclamp / has no Robot. EAE deploys the
    /// sysres, so the demo runs the OLD logic. The <see cref="HcfReferenceValidator"/> cannot catch
    /// this — it only checks that bindings which EXIST resolve, not that required FBs/recipes are
    /// PRESENT.
    ///
    /// This validator closes that gap. For every syslay FB the mirror WOULD project onto a PLC
    /// (<see cref="SysresFbMirror.MirroredCatTypes"/> ∩ <see cref="SysresFbMirror.BucketFor"/>), it
    /// asserts the FB is on that PLC's sysres; for every process it asserts the recipe matches; and
    /// when the cross-PLC discharge tail is active it asserts the M262 hcf binds the four discharge
    /// channels. The pipeline calls it after wiring and FAILS LOUDLY on any violation, so a sysres
    /// can never silently lag the syslay again.
    /// </summary>
    public static class SyslaySysresParityValidator
    {
        public sealed record Violation(string Scope, string Detail)
        {
            public override string ToString() => $"[{Scope}] {Detail}";
        }

        // Logical PLC ↔ EAE sysdev device-type ↔ short label, in deploy order.
        static readonly (string DeviceType, PlcAssignment Plc, string Label)[] Devices =
        {
            ("M262_dPAC", PlcAssignment.M262, "M262"),
            ("M580_dPAC", PlcAssignment.M580, "M580"),
            ("Soft_dPAC", PlcAssignment.BX1,  "BX1"),
        };

        // Recipe is carried either as a single STRUCT param (UseRecipeStruct) or as the six
        // parallel arrays. Compare whichever the process FB actually carries.
        static readonly string[] RecipeParamNames =
            { "Recipe", "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" };

        // M262 discharge channels the hcf MUST bind when the cross-PLC tail is active.
        static readonly (string Channel, string Meaning)[] DischargeChannels =
        {
            ("DI08", "PartAtAssembly.Input"),
            ("DO03", "Ejector.OutputToWork"),
            ("DO04", "Robot.RobotCommands_StartTask"),
            ("DI10", "Robot.RobotStatus_Task_Complete"),
        };

        /// <summary>
        /// Validate that each device sysres faithfully mirrors the syslay. <paramref name="eaeRoot"/>
        /// is the folder containing <c>IEC61499/System</c> (same root <see cref="HcfReferenceValidator"/>
        /// takes); <paramref name="syslayPath"/> is the generated top-level syslay.
        /// </summary>
        public static List<Violation> Validate(string? eaeRoot, string? syslayPath)
        {
            var violations = new List<Violation>();
            if (string.IsNullOrEmpty(eaeRoot) || string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath))
                return violations;

            List<SysresFbMirror.SyslayFb> syslayFbs;
            try { syslayFbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath); }
            catch (Exception ex)
            {
                violations.Add(new("syslay", $"could not read the generated syslay: {ex.Message}"));
                return violations;
            }

            foreach (var (deviceType, plc, label) in Devices)
            {
                // The syslay FBs the mirror WOULD project onto this PLC — the exact add/update
                // condition in MirrorFbsIntoSysres (MirroredCatTypes ∩ this bucket).
                var expected = syslayFbs
                    .Where(f => SysresFbMirror.MirroredCatTypes.Contains(f.Type) &&
                                SysresFbMirror.BucketFor(f.Name) == plc)
                    .ToList();
                if (expected.Count == 0) continue;

                var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);
                var sysres = sysdev == null ? null : EaeProjectLayout.FindSysresFor(sysdev);
                if (string.IsNullOrEmpty(sysres) || !File.Exists(sysres))
                {
                    violations.Add(new(label,
                        $"{expected.Count} syslay FB(s) bucket here but the {label} sysres was not found ({deviceType})"));
                    continue;
                }

                Dictionary<string, XElement> sysresByName;
                try
                {
                    var doc = XDocument.Load(sysres);
                    XNamespace ns = doc.Root!.GetDefaultNamespace();
                    sysresByName = doc.Descendants(ns + "FB")
                        .Where(e => !string.IsNullOrEmpty((string?)e.Attribute("Name")))
                        .GroupBy(e => (string)e.Attribute("Name")!, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    violations.Add(new(label, $"could not read the {label} sysres '{Path.GetFileName(sysres)}': {ex.Message}"));
                    continue;
                }

                foreach (var fb in expected)
                {
                    if (!sysresByName.TryGetValue(fb.Name, out var sfb))
                    {
                        violations.Add(new(label,
                            $"syslay FB '{fb.Name}' ({fb.Type}) is MISSING from the {label} sysres — the mirror did not carry it onto the deployable resource"));
                        continue;
                    }

                    if (string.Equals(fb.Type, "Process1_Generic", StringComparison.Ordinal))
                    {
                        var want = RecipeSignature(fb.Parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));
                        var got = RecipeSignature(ReadSysresParams(sfb));
                        if (!string.Equals(want, got, StringComparison.Ordinal))
                            violations.Add(new(label,
                                $"recipe MISMATCH for process '{fb.Name}' — the {label} sysres recipe LAGS the syslay " +
                                $"({DescribeRecipe(fb)} vs {DescribeRecipe(sfb)})"));
                    }
                }
            }

            // HCF parity: when the cross-PLC discharge tail is active the M262 hcf MUST bind the
            // four discharge channels (PartAtAssembly report + Ejector/Robot commands + Robot done),
            // or the ejector/robot never actuate even though the recipe commands them.
            if (CodeGen.Translation.HandoffPlanner.DischargeActive)
                ValidateDischargeHcf(eaeRoot, violations);

            return violations;
        }

        static Dictionary<string, string> ReadSysresParams(XElement fb)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in fb.Elements().Where(e => e.Name.LocalName == "Parameter"))
            {
                var n = (string?)p.Attribute("Name");
                if (!string.IsNullOrEmpty(n)) d[n!] = (string?)p.Attribute("Value") ?? string.Empty;
            }
            return d;
        }

        // Stable signature of just the recipe-bearing parameters (struct or arrays), so the
        // comparison is unaffected by unrelated params or their order.
        static string RecipeSignature(IReadOnlyDictionary<string, string> parameters)
        {
            var sb = new StringBuilder();
            foreach (var n in RecipeParamNames)
                if (parameters.TryGetValue(n, out var val))
                    sb.Append(n).Append('=').Append(val).Append('\n');
            return sb.ToString();
        }

        static string DescribeRecipe(SysresFbMirror.SyslayFb fb) =>
            DescribeRecipe(fb.Parameters.FirstOrDefault(p => p.Name == "Recipe")?.Value);

        static string DescribeRecipe(XElement sysresFb) =>
            DescribeRecipe(ReadSysresParams(sysresFb).TryGetValue("Recipe", out var v) ? v : null);

        // Human-readable one-liner for a recipe STRUCT blob — the row-0 wait and whether the
        // ejector/robot tail is present, the two things that diverged in the stale-tree bug.
        static string DescribeRecipe(string? recipe)
        {
            if (string.IsNullOrEmpty(recipe)) return "no-Recipe-param";
            var w = Regex.Match(recipe, @"Wait1Id:=(\d+)");
            return $"firstWait1Id={(w.Success ? w.Groups[1].Value : "?")}," +
                   $"ejector={recipe.Contains("'ejector'")},robot={recipe.Contains("'robot'")}";
        }

        static void ValidateDischargeHcf(string eaeRoot, List<Violation> violations)
        {
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, "M262_dPAC");
            if (string.IsNullOrEmpty(sysdev))
            {
                violations.Add(new("M262-HCF", "discharge tail active but the M262 sysdev was not found"));
                return;
            }
            var folder = Path.Combine(Path.GetDirectoryName(sysdev)!, Path.GetFileNameWithoutExtension(sysdev));
            var hcf = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.hcf", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (hcf == null)
            {
                violations.Add(new("M262-HCF", "discharge tail active but the M262 hcf was not found"));
                return;
            }

            Dictionary<string, string> bound;
            try
            {
                var doc = XDocument.Load(hcf);
                bound = doc.Descendants().Where(e => e.Name.LocalName == "ParameterValue")
                    .Where(e => !string.IsNullOrEmpty((string?)e.Attribute("Name")))
                    .GroupBy(e => (string)e.Attribute("Name")!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key,
                                  g => (((string?)g.First().Attribute("Value")) ?? string.Empty).Trim().Trim('\'').Trim(),
                                  StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                violations.Add(new("M262-HCF", $"unreadable M262 hcf '{Path.GetFileName(hcf)}': {ex.Message}"));
                return;
            }

            foreach (var (channel, meaning) in DischargeChannels)
                if (!bound.TryGetValue(channel, out var val) || string.IsNullOrWhiteSpace(val))
                    violations.Add(new("M262-HCF",
                        $"discharge tail active but {channel} ({meaning}) is BLANK in '{Path.GetFileName(hcf)}' — " +
                        "the ejector/robot will not actuate on the rig"));
        }
    }
}
