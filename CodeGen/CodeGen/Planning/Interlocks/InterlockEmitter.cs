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
        private static int Cap => InterlockConfig.Current.RuleArraySize;


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
                // Never ship an InterlockManager that passes everything through: if conditions
                // survived translation but nothing was emitted, the safety net is false.
                int inScope = InterlockPlanner.CountInScopeConditions(a, scopedIds, catTypes, twin, rings, allocation);
                if (inScope > 0 && plan.Count == 0)
                    throw new InvalidOperationException(
                        $"[Recipe] Actuator '{name}' has {inScope} in-scope Control.xml interlock " +
                        "condition(s) but emitted RuleCount=0 - refusing to generate code whose " +
                        "InterlockManager passes everything through (false safety net).");
                plans[name] = plan;
            }
            return plans;
        }

        // Keep only rules inside the core's DECLARED CurrentRawState range; outside it a rule can never match.
        // Do NOT re-drop Blocked==0 here: BuildRules keeps the cross-controller readiness gates on purpose.
        private static InterlockPlan FilterToRawStateRange(
            InterlockPlan plan, Configuration.RawStateRange r)
        {
            var b = new Builder(Cap);
            for (int i = 0; i < plan.Count && i < Cap; i++)
            {
                if (plan.From[i] < r.Min || plan.From[i] > r.Max ||
                    plan.To[i] < r.Min || plan.To[i] > r.Max) continue;
                b.Add(plan.From[i], plan.To[i], plan.Src[i], plan.Blocked[i]);
            }
            return b.ToPlan();
        }

        // The shared volume is crossed both ways, so every crossing rule needs its reverse.
        private static InterlockPlan WithReverseCrossings(InterlockPlan plan)
        {
            var b = new Builder(Cap);
            for (int i = 0; i < plan.Count && i < Cap; i++)
                b.Add(plan.From[i], plan.To[i], plan.Src[i], plan.Blocked[i]);
            for (int i = 0; i < plan.Count && i < Cap; i++)
                if (plan.From[i] != plan.To[i])                       // a crossing, not a self-loop
                    b.Add(plan.To[i], plan.From[i], plan.Src[i], plan.Blocked[i]);
            return b.ToPlan();
        }

        // Fixed-capacity rule accumulator with dedup, as both post-filters need.
        private sealed class Builder
        {
            private readonly int[] _f, _t, _s, _b;
            private readonly int _cap;
            private int _n;

            public Builder(int cap)
            {
                _cap = cap;
                _f = new int[cap]; _t = new int[cap]; _s = new int[cap]; _b = new int[cap];
            }

            public void Add(int from, int to, int src, int blocked)
            {
                for (int j = 0; j < _n; j++)
                    if (_f[j] == from && _t[j] == to && _s[j] == src && _b[j] == blocked) return;
                if (_n >= _cap) return;
                _f[_n] = from; _t[_n] = to; _s[_n] = src; _b[_n] = blocked; _n++;
            }

            public InterlockPlan ToPlan() => new(_n, _f, _t, _s, _b);
        }


        public static void Write(Dictionary<string, string> p, InterlockPlan plan)
        {
            if (InterlockConfig.Current.UseStruct)
            {
                p["RuleTable"] = Iec61499Literal.FormatInterlockTable(plan.From, plan.To, plan.Src, plan.Blocked, plan.Count);
            }
            else
            {
                p["RuleCount"]        = Iec61499Literal.FormatInt(plan.Count);
                p["RuleFromState"]    = Iec61499Literal.FormatIntArray(plan.From);
                p["RuleToState"]      = Iec61499Literal.FormatIntArray(plan.To);
                p["RuleSourceID"]     = Iec61499Literal.FormatIntArray(plan.Src);
                p["RuleBlockedState"] = Iec61499Literal.FormatIntArray(plan.Blocked);
            }
        }

    }
}
