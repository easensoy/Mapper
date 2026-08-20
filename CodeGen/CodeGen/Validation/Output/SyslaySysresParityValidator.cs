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

        // Logical PLC <-> EAE sysdev <-> short label, in deploy order. A non-null Name disambiguates two
        // devices of the same Type (BX1 and Revolution_Pi are both Soft_dPAC).
        static IEnumerable<(string DeviceType, string? DeviceName, PlcAssignment Plc, string Label)> Devices(
            GenerationContext ctx)
        {
            yield return (TargetRegistry.Of(CodeGen.Translation.PlcAssignment.M262).DeviceType, null, PlcAssignment.M262, "M262");
            // The partial swap leaves BOTH devices in place, so both need parity coverage.
            if (ctx.Profile.PartialRevPi)
                yield return (TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceType, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceName!, PlcAssignment.RevPi, "RevPi");
            yield return (TargetRegistry.Of(CodeGen.Translation.PlcAssignment.M580).DeviceType, null,  PlcAssignment.M580, "M580");
            yield return (TargetRegistry.Of(CodeGen.Translation.PlcAssignment.BX1).DeviceType, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.BX1).DeviceName!, PlcAssignment.BX1,  "BX1");
        }

        static string? LocateSysdev(string eaeRoot, string deviceType, string? deviceName) =>
            deviceName == null
                ? EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType)
                : EaeProjectLayout.FindSysdevByDeviceTypeAndName(eaeRoot, deviceType, deviceName);

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

        // A recipe CMD is claimed by the ONE component whose actuator_name equals its CmdTargetName, and
        // updateComponentState.BREQ compares them with case-sensitive ST string equality. A target nothing
        // answers to never faults: the command circles the ring unclaimed and the engine parks in silence.
        private static IEnumerable<Violation> ValidateCommandTargetsAreClaimable(List<SysresFbMirror.SyslayFb> fbs)
        {
            static string Unquote(string? v) => (v ?? string.Empty).Trim().Trim('\'');

            var claimable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in fbs.SelectMany(f => f.Parameters))
                // BREQ compares updateComponentState's own `name` input, which a Sensor_Bool_CAT passes
                // straight through, so a sensor is claimable under `name` and an addressed refresh reaches it.
                if (p.Name is "actuator_name" or "process_name" or "name") claimable.Add(Unquote(p.Value));
            // The runtime's own ready handshake, not a component.
            claimable.Add(Translation.Process.Recipes.ProcessPhaseTransport.CommandToken);

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
            try { syslayFbs = SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath); }
            catch (Exception ex)
            {
                violations.Add(new("syslay", $"could not read the generated syslay: {ex.Message}"));
                return violations;
            }

            violations.AddRange(ValidateCommandTargetsAreClaimable(syslayFbs));

            foreach (var (deviceType, deviceName, plc, label) in Devices(ctx))
            {
                var expected = syslayFbs
                    .Where(f => TemplateManifest.Mirrored.Contains(f.Type) &&
                                SysresFbMirror.BucketFor(f.Name, ctx.Allocation) == plc)
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

            // With the cross-PLC discharge tail active the Feed controller's IO must carry it: on M262 the
            // four .hcf channels, on RevPi the discharge FBs hosted on the RevPi sysres.
            if (ctx.CrossRingSegment.Count > 0)
                ValidateDischargeHcf(eaeRoot, violations);

            // Independent of the discharge tail, which stays on the M262 that owns its channels.
            if (ctx.Profile.PartialRevPi)
                ValidateRevPiIo(eaeRoot, syslayPath, violations);

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

        static void ValidateDischargeHcf(string eaeRoot, List<Violation> violations)
        {
            var sysdev = EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.M262).DeviceType);
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

        // RevPi hosts the discharge tail on its own sysres, with no .hcf to bind yet, so assert each
        // discharge FB named by Config/smc-rig.yml dischargeChannels landed there.
        static void ValidateDischargeRevPi(string eaeRoot, List<Violation> violations)
        {
            var sysdev = EaeProjectLayout.FindSysdevByDeviceTypeAndName(eaeRoot, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceType, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceName!);
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
                         .Select(dc => dc.Component).Distinct(StringComparer.Ordinal))
                if (!fbNames.Contains(fb))
                    violations.Add(new("RevPi-Discharge",
                        $"discharge tail active but '{fb}' is not on the RevPi sysres — the discharge tail did not land on the Feed controller"));

        }

        // RevPi Feed IO = the Modbus broker RevPI_IO on the sysres carrying a Mapping to a matching application
        // instance, plus the .hcf whose LinkNames resolve to it. Without all three the Feed IO does not bind.
        static void ValidateRevPiIo(string eaeRoot, string syslayPath, List<Violation> violations)
        {
            const string BrokerFbId = "A6B61E2425DB1C30";   // == RevPiIoBrokerInjector.BrokerFbId
            const string BrokerName = CodeGen.Devices.RevPi.RevPiIoBrokerInjector.BrokerName;

            var sysdev = EaeProjectLayout.FindSysdevByDeviceTypeAndName(eaeRoot, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceType, TargetRegistry.Of(CodeGen.Translation.PlcAssignment.RevPi).DeviceName!);
            var sysres = sysdev == null ? null : EaeProjectLayout.FindSysresFor(sysdev);
            if (string.IsNullOrEmpty(sysres) || !File.Exists(sysres))
            {
                violations.Add(new("RevPi-IO",
                    "a RevPi Feed station is selected but the RevPi sysres was not found — the Feed components have no deployable resource"));
                return;
            }

            XElement? broker;
            try
            {
                var doc = XDocument.Load(sysres);
                XNamespace ns = doc.Root!.GetDefaultNamespace();
                broker = doc.Descendants(ns + "FB").FirstOrDefault(e => (string?)e.Attribute("Name") == BrokerName);
            }
            catch (Exception ex)
            {
                violations.Add(new("RevPi-IO", $"unreadable RevPi sysres '{Path.GetFileName(sysres)}': {ex.Message}"));
                return;
            }

            if (broker == null)
            {
                violations.Add(new("RevPi-IO",
                    $"the {BrokerName} Modbus broker (PLC_RW_REVPI) is not on the RevPi sysres — the Feed actuators would have no physical IO"));
            }
            else
            {
                // A resource FB with no Mapping is an ORPHAN: EAE has no application instance to bind it to,
                // which is the documented "Repair Instances" class.
                if (string.IsNullOrWhiteSpace((string?)broker.Attribute("Mapping")))
                    violations.Add(new("RevPi-IO",
                        $"{BrokerName} on the RevPi sysres has no Mapping attribute — it is an orphan resource instance with no application-layer counterpart"));

                // The .hcf's LinkNames are <resourceId>.<fbId>.<port>, so the FB id is load-bearing.
                if (!string.Equals((string?)broker.Attribute("ID"), BrokerFbId, StringComparison.OrdinalIgnoreCase))
                    violations.Add(new("RevPi-IO",
                        $"{BrokerName}'s FB ID is not {BrokerFbId} — the Modbus .hcf LinkNames resolve against that id and would not bind"));
            }

            // The application layer must declare the broker too, else the sysres Mapping dangles.
            try
            {
                if (!SysresFbMirror.ReadTopLevelFbsWithSystemModelFallback(syslayPath)
                        .Any(f => string.Equals(f.Name, BrokerName, StringComparison.Ordinal)))
                    violations.Add(new("RevPi-IO",
                        $"{BrokerName} is missing from the generated syslay — the resource instance has no application counterpart to map onto"));
            }
            catch { /* the syslay read is already reported by the caller */ }

            var revpiFolder = Path.GetDirectoryName(sysres);
            var hcf = revpiFolder != null && Directory.Exists(revpiFolder)
                ? Directory.EnumerateFiles(revpiFolder, "*.hcf").FirstOrDefault() : null;
            if (hcf == null)
                violations.Add(new("RevPi-IO", "the RevPi Modbus .hcf is missing — EAE reports Missing Project Files"));
            else if (!File.ReadAllText(hcf).Contains(BrokerFbId, StringComparison.Ordinal))
                violations.Add(new("RevPi-IO",
                    "the RevPi .hcf's Modbus LinkNames do not resolve to the RevPI_IO broker FB — the Feed IO would not bind"));
        }
    }
}
