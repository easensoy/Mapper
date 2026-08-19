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

            if (c.ProcessSlots.Count == 0)
                errors.Add("processSlots is empty: no process has a state_table slot, so no handoff can be addressed");
            foreach (var g in c.ProcessSlots.GroupBy(p => p.Value).Where(g => g.Count() > 1))
                errors.Add($"processSlots collide on slot {g.Key}: {string.Join(", ", g.Select(p => p.Key))}");

            foreach (var s in c.SynthSensors)
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                    errors.Add("synthSensor with empty name");
            }
            foreach (var g in c.SynthSensors.GroupBy(s => s.Id).Where(g => g.Count() > 1))
                errors.Add($"synthSensors collide on state_table slot {g.Key}");

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
