using System;
using System.Collections.Generic;
using System.Globalization;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    // Owns interlock translation + emission: rule plan from Control.xml (InterlockPlanner), the
    // centre-home post-filters, the RuleTable/Rule* param write, and the inert-safety-net guard.
    //
    // The two CAT shapes differ only in the post-filters applied to the plan and in what the guard
    // reports, so each public entry point is a one-line call into the shared implementation.
    public static class InterlockEmitter
    {
        private static int Cap => InterlockConfig.Current.RuleArraySize;

        // ── Public entry points, one per CAT shape ───────────────────────────────────────────────

        public static void ApplyFiveState(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            => Write(p, Plan(actuator, allComponents, scopedIds, centreHome: false));

        public static void ApplyCentreHome(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            => Write(p, Plan(actuator, allComponents, scopedIds, centreHome: true));

        public static void GuardFiveState(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            List<(string Component, string Detail)> bound)
            => Guard(p, actuator, allComponents, scopedIds, bound,
                     label: "interlock", recordWhenEmpty: false);

        public static void GuardCentreHome(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            List<(string Component, string Detail)> bound)
            => Guard(p, actuator, allComponents, scopedIds, bound,
                     label: "centre-home interlock", recordWhenEmpty: true);

        // Callers without a scoped map / the non-interlock minimal path.
        public static void ApplyZero(Dictionary<string, string> p) => Write(p, InterlockPlan.Empty(Cap));

        // ── Plan ─────────────────────────────────────────────────────────────────────────────────

        // Component ids are global (sensors-first), so a cross-PLC SourceID indexes the same state_table
        // slot the bridged ring feeds; BuildRules drops genuinely out-of-scope sources.
        private static InterlockPlan Plan(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            bool centreHome)
        {
            if (scopedIds == null) return InterlockPlan.Empty(Cap);
            var plan = InterlockPlanner.BuildRules(actuator, allComponents, scopedIds);
            // Twin-faithful: the centre-home shape keeps only the crossing rules the twin declares, plus
            // their reverses. Nothing synthetic is added here — a start gate belongs to the recipe, not
            // to the interlock, because a transient sensor would otherwise refuse an already-gated move.
            return centreHome ? WithReverseCrossings(FilterToRawStateRange(plan)) : plan;
        }

        // Keep only rules whose From/To fall in the core's CurrentRawState range; anything outside can
        // never match. Blocked==0 is NOT re-dropped here — BuildRules already dropped the inverted
        // same-controller rules and deliberately kept the cross-controller readiness gates.
        private static InterlockPlan FilterToRawStateRange(InterlockPlan plan)
        {
            var r = InterlockConfig.Current.CentreHome;
            var b = new Builder(Cap);
            for (int i = 0; i < plan.Count && i < Cap; i++)
            {
                if (plan.From[i] < r.MinState || plan.From[i] > r.MaxState ||
                    plan.To[i] < r.MinState || plan.To[i] > r.MaxState) continue;
                b.Add(plan.From[i], plan.To[i], plan.Src[i], plan.Blocked[i]);
            }
            return b.ToPlan();
        }

        // The centre-home actuator crosses the shared volume BOTH ways, so emit the reverse of every
        // surviving crossing rule, guarded by the same source and blocked state.
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

        // Fixed-capacity rule accumulator with dedup — the shape both post-filters need.
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

        // ── Guard ────────────────────────────────────────────────────────────────────────────────

        // Hard-fail if in-scope Control.xml conditions survive translation but nothing was emitted —
        // never ship an InterlockManager that passes everything through (a false safety net).
        private static void Guard(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            List<(string Component, string Detail)> bound,
            string label, bool recordWhenEmpty)
        {
            int emitted = EmittedCount(p);
            int inScope = scopedIds == null
                ? 0
                : InterlockPlanner.CountInScopeConditions(actuator, allComponents, scopedIds);

            if (inScope > 0 && emitted == 0)
                throw new InvalidOperationException(
                    $"[Recipe] Actuator '{actuator.Name}' has {inScope} in-scope Control.xml interlock " +
                    "condition(s) but emitted RuleCount=0 — refusing to generate code whose InterlockManager " +
                    "passes everything through (false safety net). Interlock rule translation failed for this actuator.");

            if (emitted > 0 || recordWhenEmpty)
                bound.Add((actuator.Name, $"{label} RuleCount={emitted}"));
        }

        // ── Param IO ─────────────────────────────────────────────────────────────────────────────

        private static void Write(Dictionary<string, string> p, InterlockPlan plan)
        {
            if (InterlockConfig.Current.UseStruct)
            {
                p["RuleTable"] = SyslayBuilder.FormatInterlockTable(plan.From, plan.To, plan.Src, plan.Blocked, plan.Count);
            }
            else
            {
                p["RuleCount"]        = SyslayBuilder.FormatInt(plan.Count);
                p["RuleFromState"]    = SyslayBuilder.FormatIntArray(plan.From);
                p["RuleToState"]      = SyslayBuilder.FormatIntArray(plan.To);
                p["RuleSourceID"]     = SyslayBuilder.FormatIntArray(plan.Src);
                p["RuleBlockedState"] = SyslayBuilder.FormatIntArray(plan.Blocked);
            }
        }

        private static int EmittedCount(Dictionary<string, string> p)
        {
            if (p.TryGetValue("RuleCount", out var rc))
                return int.Parse(rc, CultureInfo.InvariantCulture);
            if (p.TryGetValue("RuleTable", out var rt))
            {
                var m = System.Text.RegularExpressions.Regex.Match(rt, @"Count:=(-?\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return 0;
        }
    }
}
