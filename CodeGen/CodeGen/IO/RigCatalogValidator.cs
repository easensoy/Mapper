using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeGen.Configuration
{
    internal static class RigCatalogValidator
    {
        public static void Validate(RigCatalog c)
        {
            var errors = new List<string>();

            foreach (var g in c.Protocols.GroupBy(p => p.Cat, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                errors.Add($"CAT '{g.Key}' declares {g.Count()} protocols");
            foreach (var p in c.Protocols)
            {
                if (p.Command.Count == 0)
                    errors.Add($"'{p.Cat}' declares no command values, so nothing can drive it");
                if (p.StateCounts.Count == 0 && !p.ServesBranched)
                    errors.Add($"'{p.Cat}' serves no state-graph shape, so it can never be selected");
                foreach (var stop in p.Command.Keys)
                {
                    if (!p.Settled.ContainsKey(stop))
                        errors.Add($"'{p.Cat}' commands '{stop}' but declares no settled value for it");
                    if (!p.Interlock.ContainsKey(stop))
                        errors.Add($"'{p.Cat}' commands '{stop}' but declares no interlock value for it");
                }
            }

            foreach (var s in c.SynthSensors)
                if (string.IsNullOrWhiteSpace(s.Name))
                    errors.Add("synthSensor with empty name");

            // These rows ARE the binding, so an invalid or repeated one would emit a wrong .hcf rather
            // than merely mis-report. Two rows on one channel is two drivers for one physical pin.
            foreach (var dc in c.DischargeChannels)
            {
                if (!Regex.IsMatch(dc.Channel ?? string.Empty, @"^D[IO]\d+$"))
                    errors.Add($"dischargeChannel '{dc.Meaning}' has invalid channel '{dc.Channel}' (expected DInn/DOnn)");
                if (string.IsNullOrWhiteSpace(dc.Component))
                    errors.Add($"dischargeChannel '{dc.Channel}' names no component");
                if (string.IsNullOrWhiteSpace(dc.Port))
                    errors.Add($"dischargeChannel '{dc.Channel}' names no CAT port");
            }
            foreach (var g in c.DischargeChannels.GroupBy(d => d.Channel, StringComparer.OrdinalIgnoreCase)
                              .Where(g => g.Count() > 1))
                errors.Add($"dischargeChannels claim '{g.Key}' {g.Count()} times: " +
                           string.Join(", ", g.Select(d => d.Meaning)));

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "smc-rig.yml is invalid:" + Environment.NewLine + "  - " +
                    string.Join(Environment.NewLine + "  - ", errors));
        }
    }
}
