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

            errors.AddRange(HandoffErrors(c.Handoff));

            // An unverified physical assumption is only useful if it says what goes wrong and how to
            // settle it. A row with neither is a note, and a note is what this replaced.
            for (int i = 0; i < c.UnresolvedPhysicalFacts.Count; i++)
            {
                var u = c.UnresolvedPhysicalFacts[i];
                if (string.IsNullOrWhiteSpace(u.Fact))
                    errors.Add($"unresolvedPhysicalFacts[{i}] states no fact");
                if (string.IsNullOrWhiteSpace(u.Risk))
                    errors.Add($"unresolvedPhysicalFacts[{i}] ('{u.Fact}') states no risk; an assumption " +
                               "whose consequence nobody wrote down is one nobody assessed");
                if (string.IsNullOrWhiteSpace(u.VerifyBy))
                    errors.Add($"unresolvedPhysicalFacts[{i}] ('{u.Fact}') says how to verify nothing, " +
                               "so it would stay unresolved forever");
            }

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "smc-rig.yml is invalid:" + Environment.NewLine + "  - " +
                    string.Join(Environment.NewLine + "  - ", errors));
        }

        // Both lists here decide whether a plant WAITS for something, and both are matched by "the
        // first row that covers this edge". Two rows of equal specificity that can both match would
        // make that answer depend on file order, so they are refused rather than resolved by position.
        private static IEnumerable<string> HandoffErrors(HandoffPolicy h)
        {
            for (int i = 0; i < h.PeerEntryPhase.Count; i++)
            {
                var r = h.PeerEntryPhase[i];
                var who = $"peerEntryPhase[{i}] ({Shown(r.Producer)} -> {Shown(r.Consumer)}" +
                          (r.ProducerState.Length > 0 ? $", state '{r.ProducerState}'" : "") + ")";

                if (r.Meaning == PeerEntryPhaseMeaning.Undeclared)
                    yield return $"{who} declares no meaning; use readinessAssertion or runtimePhase";
                if (string.IsNullOrWhiteSpace(r.Because))
                    yield return $"{who} gives no 'because'. This row decides whether the plant waits " +
                                 "for a phase its own model names, so the reason is required";
            }

            for (int i = 0; i < h.PeerEntryPhase.Count; i++)
                for (int j = i + 1; j < h.PeerEntryPhase.Count; j++)
                {
                    var a = h.PeerEntryPhase[i];
                    var b = h.PeerEntryPhase[j];
                    if (a.Specificity != b.Specificity) continue;   // most-specific wins, unambiguously
                    if (Overlap(a.Producer, b.Producer) && Overlap(a.Consumer, b.Consumer) &&
                        Overlap(a.ProducerState, b.ProducerState))
                        yield return
                            $"peerEntryPhase[{i}] and peerEntryPhase[{j}] are equally specific and both " +
                            $"cover {Shown(a.Producer)} -> {Shown(a.Consumer)}, so which reading applies " +
                            "would depend on the order they are written in";
                }

            // The same hazard on the carrier list, which had no overlap check at all: a wildcard row
            // written above a specific one silently swallowed it, and a carrier decides whether a
            // material level is allowed to stand in for a producer's phase.
            for (int i = 0; i < h.Carriers.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(h.Carriers[i].Producer))
                    yield return $"carriers[{i}] names no producer";
                if (string.IsNullOrWhiteSpace(h.Carriers[i].Carrier))
                    yield return $"carriers[{i}] names no carrier";
                if (string.IsNullOrWhiteSpace(h.Carriers[i].Because))
                    yield return $"carriers[{i}] gives no 'because'; a substitution nobody can explain " +
                                 "is one nobody checked";
                for (int j = i + 1; j < h.Carriers.Count; j++)
                    if (Overlap(h.Carriers[i].Producer, h.Carriers[j].Producer) &&
                        Overlap(h.Carriers[i].ProducerState, h.Carriers[j].ProducerState))
                        yield return $"carriers[{i}] and carriers[{j}] both cover " +
                                     $"{Shown(h.Carriers[i].Producer)}/{Shown(h.Carriers[i].ProducerState)}, " +
                                     "so which one substitutes would depend on their order";
            }
        }
    }
}
