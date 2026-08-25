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
            ReportGraph rings, ControllerAllocation allocation,
            IReadOnlyDictionary<string, int> slots, List<string> findings)
        {
            var plans = new Dictionary<string, InterlockPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in actuators)
            {
                var name = (a.Name ?? string.Empty).Trim();
                if (name.Length == 0 || plans.ContainsKey(name)) continue;
                var plan = InterlockPlanner.BuildRules(a, scopedIds, catTypes, twin, rings, allocation, slots, findings);
                // Two CAT capabilities shape the rules, and each is DECLARED, never inferred from
                // the CAT's name: a core that publishes a narrow raw-state range cannot match a
                // rule outside it, and a CAT with a work stop either side of a centre reference
                // crosses the shared volume both ways. Nothing synthetic is added here: a start
                // gate belongs to the recipe.
                var protocol = catTypes.TryGetValue(name, out var cat)
                    ? TemplateManifest.ProtocolOrNull(cat) : null;
                if (protocol?.RawStateRange is { } range) plan = FilterToRawStateRange(plan, range);
                if (protocol?.CrossesBothWays == true) plan = WithReverseCrossings(plan);
                AssertEveryRuleIsEnforceable(name, cat, protocol, plan);
                // Never ship an InterlockManager that passes everything through: if conditions
                // survived translation but nothing was emitted, the safety net is false.
                int inScope = InterlockPlanner.CountInScopeAlternatives(a, scopedIds, catTypes, twin, rings, allocation);
                if (inScope > 0 && plan.Count == 0)
                    throw new InvalidOperationException(
                        $"[Recipe] Actuator '{name}' has {inScope} in-scope Control.xml interlock " +
                        "alternative(s) but emitted RuleCount=0 - refusing to generate code whose " +
                        "InterlockManager passes everything through (false safety net).");
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

        // Keep only alternatives inside the core DECLARED CurrentRawState range; outside it a rule can
        // never match. Judged per alternative, because an alternative IS one rule.
        // Do NOT re-drop at-rest terms here: BuildRules keeps the cross-controller readiness gates.
        private static InterlockPlan FilterToRawStateRange(
            InterlockPlan plan, Configuration.RawStateRange r)
        {
            var b = new InterlockPlan.Builder();
            foreach (var a in plan.Alternatives())
                if (a.From >= r.Min && a.From <= r.Max && a.To >= r.Min && a.To <= r.Max)
                    b.Add(a);
            return b.ToPlan();
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
