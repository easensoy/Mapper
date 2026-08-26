using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeGen.Configuration
{
    internal static class RigCatalogValidator
    {
        // A command sequence IS the machine a component runs, so a row that claims nothing, claims what
        // another row claims, or declares a sequence its mode cannot execute is refused here rather than
        // producing a recipe that drives an actuator the wrong way.
        private static IEnumerable<string> ExecutionErrors(IReadOnlyList<CatExecutionDeclaration> rows)
        {
            foreach (var r in rows)
            {
                var claim = $"'{(r.Cat.Length > 0 ? r.Cat : "any cat")}'/" +
                            $"'{(r.ComponentType.Length > 0 ? r.ComponentType : "any type")}'";
                if (r.Cat.Length == 0 && r.ComponentType.Length == 0)
                    yield return "an execution row claims neither a cat nor a componentType, so it claims " +
                                 "every component there is";
                if (r.Mode == ExecutionMode.StopDriven)
                {
                    if (r.Steps.Count > 0)
                        yield return $"the execution row claiming {claim} is stopDriven but declares " +
                                     $"{r.Steps.Count} step(s), which nothing would run";
                    continue;
                }
                if (r.Steps.Count == 0)
                {
                    yield return $"the execution row claiming {claim} declares no steps, so there is " +
                                 "nothing for it to execute";
                    continue;
                }
                // Resumption finds the step whose arrival value it is resting at, so two steps sharing
                // one would make where it resumes depend on which was found first.
                foreach (var g in r.Steps.GroupBy(s => s.Settled).Where(g => g.Count() > 1))
                    yield return $"the execution row claiming {claim} settles at {g.Key} on " +
                                 $"{g.Count()} steps, so where it resumes is undecided";
                if (r.Mode == ExecutionMode.Alternate && r.Steps.Count < 2)
                    yield return $"the execution row claiming {claim} alternates over " +
                                 $"{r.Steps.Count} step, which has nothing to alternate with";
            }

            // Two rows claiming one component would leave the machine it runs to declaration order.
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                    if (Overlap(rows[i].Cat, rows[j].Cat) &&
                        Overlap(rows[i].ComponentType, rows[j].ComponentType))
                        yield return $"execution rows {i + 1} and {j + 1} both claim " +
                                     $"'{Shown(rows[i].Cat)}'/'{Shown(rows[i].ComponentType)}' and " +
                                     $"'{Shown(rows[j].Cat)}'/'{Shown(rows[j].ComponentType)}'";
        }

        // An undeclared field claims everything, so it overlaps with any value.
        private static bool Overlap(string a, string b) =>
            a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string Shown(string s) => s.Length > 0 ? s : "any";


        public static void Validate(RigCatalog c)
        {
            var errors = new List<string>();

            errors.AddRange(ExecutionErrors(c.Execution));

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
