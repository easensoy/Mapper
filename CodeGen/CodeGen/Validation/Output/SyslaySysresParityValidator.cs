using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace CodeGen.Devices.Core
{
    // syslay <-> sysres parity guard: every runtime parameter the syslay sets on an FB must mirror
    // byte-identically onto that FB in the owning PLC's (deployed) sysres. EAE deploys the sysres,
    // so a lagging sysres runs stale logic. Any divergence fails generation.
    public static class SyslaySysresParityValidator
    {
        public sealed record Violation(string Scope, string Detail)
        {
            public override string ToString() => $"[{Scope}] {Detail}";
        }

        // Logical PLC <-> EAE sysdev (Type + optional Name) <-> short label, in deploy order. The Feed
        // station is hosted on M262 (default) or the RevPi Soft_dPAC; M580/BX1 are fixed. A non-null Name
        // disambiguates two devices of the same Type (BX1 vs Revolution_Pi, both Soft_dPAC).
        static IEnumerable<(string DeviceType, string? DeviceName, PlcAssignment Plc, string Label)> Devices()
        {
            yield return MapperConfig.FeedStationController == FeedController.RevPi
                ? ("Soft_dPAC", "Revolution_Pi", PlcAssignment.RevPi, "RevPi")
                : ("M262_dPAC", null,            PlcAssignment.M262,  "M262");
            yield return ("M580_dPAC", null,  PlcAssignment.M580, "M580");
            yield return ("Soft_dPAC", "BX1", PlcAssignment.BX1,  "BX1");
        }

        static string? LocateSysdev(string eaeRoot, string deviceType, string? deviceName) =>
            deviceName == null
                ? EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType)
                : EaeProjectLayout.FindSysdevByDeviceTypeAndName(eaeRoot, deviceType, deviceName);

        // Recipe is carried either as a single STRUCT param or as the six parallel arrays.
        static readonly string[] RecipeParamNames =
            { "Recipe", "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep" };

        static readonly string[] RuntimeParamNames =
        {
            "Recipe", "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep",
            "RuleTable", "RuleCount", "RuleFromState", "RuleToState", "RuleSourceID", "RuleBlockedState",
            "Target", "TargetWork1State", "TargetWork2State", "TargetHomeState",
            "actuator_id", "actuator_name", "process_id", "process_state_name",
            "WorkSensorFitted", "HomeSensorFitted",
            "work1ToHomeTime", "work2ToHomeTime", "toWorkTime", "toHomeTime",
        };

        static string Short(string s) => s.Length <= 48 ? s : s.Substring(0, 45) + "...";

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

            foreach (var (deviceType, deviceName, plc, label) in Devices())
            {
                // The syslay FBs the mirror would project onto this PLC (MirroredCatTypes n bucket).
                var expected = syslayFbs
                    .Where(f => SysresFbMirror.MirroredCatTypes.Contains(f.Type) &&
                                SysresFbMirror.BucketFor(f.Name) == plc)
                    .ToList();
                if (expected.Count == 0) continue;

                var sysdev = LocateSysdev(eaeRoot, deviceType, deviceName);
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

                    var slParams = fb.Parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                    var srParams = ReadSysresParams(sfb);
                    foreach (var n in RuntimeParamNames)
                    {
                        if (!slParams.TryGetValue(n, out var want)) continue;
                        if (!srParams.TryGetValue(n, out var got))
                            violations.Add(new(label,
                                $"parameter '{n}' MISSING from '{fb.Name}' ({fb.Type}) in the {label} sysres — the mirror dropped a syslay runtime parameter"));
                        else if (!string.Equals(want, got, StringComparison.Ordinal))
                            violations.Add(new(label,
                                $"parameter '{n}' MISMATCH for '{fb.Name}' ({fb.Type}) — the {label} sysres LAGS the syslay: syslay='{Short(want)}' vs sysres='{Short(got)}'"));
                    }
                }
            }

            // When the cross-PLC discharge tail is active the Feed controller's IO must carry it. On M262
            // that is the four .hcf channels (ejector/robot/part-sensor); on RevPi the discharge tail is
            // FB-hosted on the RevPi sysres (its physical Modbus IO, PLC_RW_REVPI, is the documented
            // follow-up), so the RevPi path asserts the discharge FBs landed on the RevPi sysres.
            if (CodeGen.Translation.HandoffPlanner.DischargeActive)
            {
                if (MapperConfig.FeedStationController == FeedController.RevPi)
                    ValidateDischargeRevPi(eaeRoot, violations);
                else
                    ValidateDischargeHcf(eaeRoot, violations);
            }

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

            foreach (var dc in RigCatalog.Current.DischargeChannels)
                if (!bound.TryGetValue(dc.Channel, out var val) || string.IsNullOrWhiteSpace(val))
                    violations.Add(new("M262-HCF",
                        $"discharge tail active but {dc.Channel} ({dc.Meaning}) is BLANK in '{Path.GetFileName(hcf)}' — " +
                        "the ejector/robot will not actuate on the rig"));
        }

        // RevPi hosts the discharge tail on its own sysres (the physical Modbus IO — PLC_RW_REVPI — is the
        // documented follow-up, so there is no .hcf to bind yet). Assert each discharge FB (the FB-name part
        // of the discharge-channel Meanings — Ejector/Robot/PartAtAssembly) landed on the RevPi sysres.
        static void ValidateDischargeRevPi(string eaeRoot, List<Violation> violations)
        {
            var sysdev = EaeProjectLayout.FindSysdevByDeviceTypeAndName(eaeRoot, "Soft_dPAC", "Revolution_Pi");
            var sysres = sysdev == null ? null : EaeProjectLayout.FindSysresFor(sysdev);
            if (string.IsNullOrEmpty(sysres) || !File.Exists(sysres))
            {
                violations.Add(new("RevPi-Discharge", "discharge tail active but the RevPi sysres was not found"));
                return;
            }

            HashSet<string> fbNames;
            try
            {
                var doc = XDocument.Load(sysres);
                XNamespace ns = doc.Root!.GetDefaultNamespace();
                fbNames = doc.Descendants(ns + "FB")
                    .Select(e => (string?)e.Attribute("Name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                violations.Add(new("RevPi-Discharge", $"unreadable RevPi sysres '{Path.GetFileName(sysres)}': {ex.Message}"));
                return;
            }

            foreach (var fb in RigCatalog.Current.DischargeChannels
                         .Select(dc => dc.Meaning.Split('.')[0]).Distinct(StringComparer.Ordinal))
                if (!fbNames.Contains(fb))
                    violations.Add(new("RevPi-Discharge",
                        $"discharge tail active but '{fb}' is not on the RevPi sysres — the discharge tail did not land on the Feed controller"));
        }
    }
}
