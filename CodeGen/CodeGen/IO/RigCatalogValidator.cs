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

            var processIds = new (string Name, int Id)[]
            {
                ("feedStation", c.ProcessIds.FeedStation),
                ("assembly", c.ProcessIds.Assembly),
                ("disassembly", c.ProcessIds.Disassembly),
            };
            foreach (var g in processIds.GroupBy(p => p.Id).Where(g => g.Count() > 1))
                errors.Add($"processIds collide on slot {g.Key}: {string.Join(", ", g.Select(p => p.Name))}");

            foreach (var g in c.CoverActuatorIds.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
                errors.Add($"coverActuatorIds collide on slot {g.Key}: {string.Join(", ", g.Select(kv => kv.Key))}");

            foreach (var s in c.SynthSensors)
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                    errors.Add("synthSensor with empty name");
                if (!Regex.IsMatch(s.Pin ?? string.Empty, @"^D[IO]\d+$"))
                    errors.Add($"synthSensor '{s.Name}' has invalid pin '{s.Pin}' (expected DInn/DOnn)");
            }
            foreach (var g in c.SynthSensors.GroupBy(s => s.Id).Where(g => g.Count() > 1))
                errors.Add($"synthSensors collide on state_table slot {g.Key}");

            foreach (var dc in c.DischargeChannels)
                if (!Regex.IsMatch(dc.Channel ?? string.Empty, @"^D[IO]\d+$"))
                    errors.Add($"dischargeChannel '{dc.Meaning}' has invalid channel '{dc.Channel}' (expected DInn/DOnn)");

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "smc-rig.yml is invalid:" + Environment.NewLine + "  - " +
                    string.Join(Environment.NewLine + "  - ", errors));
        }
    }
}
