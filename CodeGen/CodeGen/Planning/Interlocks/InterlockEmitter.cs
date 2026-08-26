using System;
using System.Collections.Generic;
using System.Globalization;
using CodeGen.Models;
using CodeGen.Mapping;

namespace CodeGen.Translation.Interlocks
{
    // Interlock translation and emission: rule plan from Control.xml, the centre-home post-filters, the
    // param write and the inert-safety-net guard. The CAT shapes differ only in filters and guard text.
    public static class InterlockEmitter
    {
        // Every actuator's rules, planned once from the twin. A CAT with no rule interface is absent.
        public static IReadOnlyDictionary<string, InterlockPlan> PlanAll(
            IEnumerable<VueOneComponent> actuators, IReadOnlyDictionary<string, string> catTypes,
            IReadOnlyDictionary<string, int> scopedIds, Domain.Twin.TwinModel twin,
            List<string> findings)
        {
            var plans = new Dictionary<string, InterlockPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in actuators)
            {
                var name = (a.Name ?? string.Empty).Trim();
                if (name.Length == 0 || plans.ContainsKey(name)) continue;
                var plan = InterlockPlanner.BuildRules(a, scopedIds, catTypes, twin, findings);
                // Two CAT capabilities shape the rules, and each is DECLARED, never inferred from
                // the CAT's name: a core that publishes a narrow raw-state range cannot match a
                // rule outside it, and a CAT with a work stop either side of a centre reference
                // crosses the shared volume both ways. Nothing synthetic is added here: a start
                // gate belongs to the recipe.
                var protocol = catTypes.TryGetValue(name, out var cat)
                    ? TemplateManifest.ProtocolOrNull(cat) : null;
                if (protocol?.RawStateRange is { } range) AssertEveryMoveIsPublished(plan, range, name, cat);
                if (protocol?.CrossesBothWays == true) plan = WithReverseCrossings(plan);
                AssertEveryRuleIsEnforceable(name, cat, protocol, plan);
                // The inert-safety-net guard that stood here compared a second walk of the guard tree
                // against this plan. Nothing drops an alternative any more - the planner emits every one
                // the model states or the run stops - so a guard the model wrote can no longer reach
                // RuleCount=0, and the walk it compared against is gone with it.
                plans[name] = plan;
            }
            return plans;
        }

        // A verdict nobody reads is not a safety rule. The evaluator computes one verdict per TARGET
        // stop and the core gates a move only on the verdicts it takes as inputs, so a rule aimed at a
        // stop the CAT does not enforce - or at no declared target at all - would be computed and
        // discarded. That is exactly the shape of an inert safety rule, so generation STOPS. The remedy
        // is the CAT (give its core that input) or the twin (state the interlock on the move the CAT
        // does gate); silently emitting it is not one.
        private static void AssertEveryRuleIsEnforceable(
            string name, string? cat, CatProtocol? protocol, InterlockPlan plan)
        {
            if (plan.Count == 0) return;
            if (protocol?.Target is not { Count: > 0 })
                throw new InvalidOperationException(
                    $"[Interlock] '{name}' was planned {plan.Count} rule(s) but its CAT " +
                    $"'{cat ?? "(none)"}' declares no interlock target interface, so nothing would " +
                    "evaluate them.");

            foreach (var alternative in plan.Alternatives())
            {
                var stop = protocol.TargetStopFor(alternative.To);
                if (stop != null && protocol.Enforces(stop)) continue;
                throw new InvalidOperationException(
                    $"[Interlock] '{name}' is interlocked on the move to state {alternative.To}, which " +
                    (stop == null
                        ? $"CAT '{cat}' compares against no interlock target"
                        : $"CAT '{cat}' computes a '{stop}' verdict for but does not gate the move on") +
                    " - the rule could never fire. Refusing to generate an inert safety rule.");
            }
        }

        // A rule matches on CurrentRawState, so a move outside the range the core DECLARES it publishes
        // can never match. That is a rule the selected CAT cannot represent - not a rule to leave out,
        // because leaving it out ships an actuator the twin says is guarded and the plant is not.
        private static void AssertEveryMoveIsPublished(
            InterlockPlan plan, Configuration.RawStateRange r, string name, string? cat)
        {
            var outside = plan.Alternatives()
                .Where(a => a.From < r.Min || a.From > r.Max || a.To < r.Min || a.To > r.Max)
                .Select(a => $"{a.From} -> {a.To}")
                .Distinct(StringComparer.Ordinal).ToList();
            if (outside.Count == 0) return;

            throw new InvalidOperationException(
                $"[Interlock] '{name}' is interlocked on the move(s) {string.Join(", ", outside)}, which fall " +
                $"outside the raw-state range {r.Min}..{r.Max} its CAT '{cat ?? "(none)"}' publishes, so the " +
                "rule could never match. Either the twin numbers that branch outside the CAT's state space, " +
                "or the CAT selected for this actuator is not the one the twin describes. Generation stops " +
                "rather than emitting a rule that cannot fire.");
        }

        // The shared volume is crossed both ways, so every crossing alternative needs its reverse, with
        // the same terms: what blocks the move one way blocks it coming back.
        private static InterlockPlan WithReverseCrossings(InterlockPlan plan)
        {
            var b = new InterlockPlan.Builder();
            foreach (var a in plan.Alternatives()) b.Add(a);
            foreach (var a in plan.Alternatives())
                if (a.From != a.To)                                  // a crossing, not a self-loop
                    b.Add(a with { From = a.To, To = a.From });
            return b.ToPlan();
        }

        public static void Write(Dictionary<string, string> p, InterlockPlan plan, int capacity)
        {
                            p["RuleTable"] = Iec61499Literal.FormatInterlockTable(
                plan.From, plan.To, plan.Src, plan.Blocked, plan.TermCount, capacity);
            
        }

    }
}
