using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Translation;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Devices.Core
{
    // Every runtime parameter the syslay sets on an FB must mirror byte-identically onto that FB in the
    // owning PLC's sysres. EAE deploys the sysres, so a lagging one runs stale logic.
    public static class SyslaySysresParityValidator
    {
        public sealed record Violation(string Scope, string Detail)
        {
            public override string ToString() => $"[{Scope}] {Detail}";
        }

        // Every device this run emits, from the registry. A non-null Name disambiguates two devices of
        // the same Type; a target that only exists when something is relocated onto it is covered only
        // when something was, because otherwise the run writes no such device to check.
        static IEnumerable<(string DeviceType, string? DeviceName, PlcAssignment Plc, string Label)> Devices(
            GenerationContext ctx) =>
            ctx.Targets.All.Where(t => ctx.Emits(t.Plc))
                .Select(t => (t.DeviceType, t.DeviceName, t.Plc, t.Plc.ToString()));

        static string? LocateSysdev(string eaeRoot, string deviceType, string? deviceName) =>
            deviceName == null
                ? EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType)
                : EaeProjectLayout.FindSysdevByDeviceTypeAndName(eaeRoot, deviceType, deviceName);

        // The interlock half is the patcher's own contract rather than a second copy of it: the fold
        // is switchable, so which of those inputs an instance carries is that patcher's answer.
        static readonly string[] RuntimeParamNames = new[]
        {
            "Recipe", "StepType", "CmdTargetName", "CmdStateArr", "Wait1Id", "Wait1State", "NextStep",
            "actuator_id", "actuator_name", "process_id", "process_state_name",
            "WorkSensorFitted", "HomeSensorFitted",
            "work1ToHomeTime", "work2ToHomeTime", "toWorkTime", "toHomeTime",
        }.Concat(CodeGen.Services.InterlockCatPatcher.AllInterlockInputs).ToArray();

        static string Short(string s) => s.Length <= 48 ? s : s.Substring(0, 45) + "...";

        // A recipe CMD is claimed by the ONE component whose actuator_name equals its CmdTargetName, and
        // updateComponentState.BREQ compares them with case-sensitive ST string equality. A target nothing
        // answers to never faults: the command circles the ring unclaimed and the engine parks in silence.
        private static IEnumerable<Violation> ValidateCommandTargetsAreClaimable(Mapping.TemplateIndex manifest, List<SysresFbMirror.SyslayFb> fbs)
        {
            static string Unquote(string? v) => (v ?? string.Empty).Trim().Trim('\'');

            var claimable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in fbs.SelectMany(f => f.Parameters))
                // BREQ compares updateComponentState's own `name` input, which a Sensor_Bool_CAT passes
                // straight through, so a sensor is claimable under `name` and an addressed refresh reaches it.
                if (p.Name is "actuator_name" or "process_name" or "name") claimable.Add(Unquote(p.Value));
            // The runtime's own ready handshake, not a component.
            claimable.Add(manifest.PhaseTransport.CommandToken);

            foreach (var fb in fbs)
            {
                var recipe = fb.Parameters.FirstOrDefault(p => p.Name == "Recipe")?.Value;
                var targets = recipe != null
                    ? Regex.Matches(recipe, @"CmdTargetName:='([^']*)'").Select(m => m.Groups[1].Value)
                    : Regex.Matches(fb.Parameters.FirstOrDefault(p => p.Name == "CmdTargetName")?.Value ?? string.Empty,
                        @"'([^']*)'").Select(m => m.Groups[1].Value);

                foreach (var t in targets.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal))
                    if (!claimable.Contains(t))
                        yield return new("syslay",
                            $"'{fb.Name}' commands '{t}', which no component answers to — actuator_name is " +
                            $"case-sensitive on the ring, so this command would be silently dropped and the " +
                            $"recipe would park on the next WAIT. Claimable: {string.Join(", ", claimable.OrderBy(x => x, StringComparer.Ordinal))}");
            }
        }

        public static List<Violation> Validate(GenerationContext ctx, string? eaeRoot, string? syslayPath)
        {
            var violations = new List<Violation>();
            if (string.IsNullOrEmpty(eaeRoot) || string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath))
                return violations;

            List<SysresFbMirror.SyslayFb> syslayFbs;
            try { syslayFbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(
                syslayPath, ctx.Cfg.Generation.ProjectNamespace); }
            catch (Exception ex)
            {
                violations.Add(new("syslay", $"could not read the generated syslay: {ex.Message}"));
                return violations;
            }

            violations.AddRange(ValidateCommandTargetsAreClaimable(ctx.Manifest, syslayFbs));

            foreach (var (deviceType, deviceName, plc, label) in Devices(ctx))
            {
                var expected = syslayFbs
                    .Where(f => ctx.Manifest.Mirrored.Contains(f.Type) &&
                                SysresFbMirror.BucketFor(f.Name, ctx.Allocation, ctx.Cfg) == plc)
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

            // With the cross-controller discharge tail active, the IO of the target that segment
            // reports onto must carry it: either its declared .hcf channels or the FBs on its sysres.
            if (ctx.CrossRingSegment.Count > 0)
                ValidateDischargeHcf(ctx.Cfg.Rig, eaeRoot, violations,
                    ctx.Targets.Of(ctx.SegmentRingHost));

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

        // The discharge channels belong to the target the cross-controller segment reports onto -
        // which the PLAN answers from where it allocated that segment, not a flag naming a station.
        static void ValidateDischargeHcf(RigCatalog rig, string eaeRoot, List<Violation> violations,
            TargetDescriptor host)
        {
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, host.DeviceType);
            if (string.IsNullOrEmpty(sysdev))
            {
                violations.Add(new($"{host.Plc}-HCF",
                    $"discharge tail active but the {host.Plc} sysdev was not found"));
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

            foreach (var dc in rig.DischargeChannels)
                if (!bound.TryGetValue(dc.Channel, out var val) || string.IsNullOrWhiteSpace(val))
                    violations.Add(new("M262-HCF",
                        $"discharge tail active but {dc.Channel} ({dc.Meaning}) is BLANK in '{Path.GetFileName(hcf)}' — " +
                        "the ejector/robot will not actuate on the rig"));
        }

    }
}
