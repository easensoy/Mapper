using System;
using System.Collections.Generic;
using System.Globalization;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    // Owns interlock translation + emission: rule plan from Control.xml (InterlockPlanner), centre-home
    // range filter, RuleCount/Rule* param write, and the inert-safety-net guards.
    public static class InterlockEmitter
    {
        private static int Cap => InterlockConfig.Current.RuleArraySize;

        // ── Five_State_Actuator_CAT ──────────────────────────────────────────────────────────────

        public static void ApplyFiveState(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            => Write(p, FiveStatePlan(actuator, allComponents, scopedIds));

        // Hard-fail if in-scope Control.xml conditions survive but RuleCount=0 — never ship an
        // InterlockManager that passes everything through (a false safety net).
        public static void GuardFiveState(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            List<(string Component, string Detail)> bound)
        {
            int emitted = EmittedCount(p);
            int inScope = InScope(actuator, allComponents, scopedIds);
            if (inScope > 0 && emitted == 0)
                throw new InvalidOperationException(
                    $"[Recipe] Actuator '{actuator.Name}' has {inScope} in-scope Control.xml interlock " +
                    "condition(s) but emitted RuleCount=0 — refusing to generate code whose InterlockManager " +
                    "passes everything through (false safety net). Interlock rule translation failed for this actuator.");
            if (emitted > 0)
                bound.Add((actuator.Name, $"interlock RuleCount={emitted}"));
        }

        // ── Seven_State_Actuator_Centre_Home_CAT (Bearing_PnP) ───────────────────────────────────

        public static void ApplyCentreHome(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            => Write(p, CentreHomePlan(actuator, allComponents, scopedIds));

        public static void GuardCentreHome(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            List<(string Component, string Detail)> bound)
        {
            int count = EmittedCount(p);
            if (scopedIds != null)
            {
                int inScope = InterlockPlanner.CountInScopeConditions(actuator, allComponents, scopedIds);
                if (inScope > 0 && count == 0)
                    throw new InvalidOperationException(
                        $"[Recipe] Bearing_PnP '{actuator.Name}' has {inScope} in-scope interlock condition(s) but " +
                        "emitted RuleCount=0 — refusing to ship an inert safety net for the swivel that is the " +
                        "cross-process intersection.");
            }
            bound.Add((actuator.Name,
                $"centre-home interlock RuleCount={count} (blocks turn-to-Place when the crossing is occupied)"));
        }

        // ── Default (callers without a scoped map / non-interlock minimal path) ───────────────────

        public static void ApplyZero(Dictionary<string, string> p) => Write(p, InterlockPlan.Empty(Cap));

        // ── Plans ────────────────────────────────────────────────────────────────────────────────

        private static InterlockPlan FiveStatePlan(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            // Component ids are global (sensors-first), so a cover's cross-PLC SourceID indexes the same
            // state_table slot the bridged ring feeds; BuildRules drops genuinely out-of-scope sources.
            => scopedIds != null
                ? InterlockPlanner.BuildRules(actuator, allComponents, scopedIds)
                : InterlockPlan.Empty(Cap);

        private static InterlockPlan CentreHomePlan(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
        {
            var plan = scopedIds != null
                ? InterlockPlanner.BuildRules(actuator, allComponents, scopedIds)
                : InterlockPlan.Empty(Cap);
            return WithPartAtAssemblyGate(WithReverseCrossings(FilterToCentreHomeRange(plan)), actuator);
        }

        // No-clamp (_vc): the swivel must not turn OUT of home into the shared work volume until the part
        // is physically at assembly. The twin only interlocks the Work1<->Work2 crossing, NOT the initial
        // pick-out-of-home, so nothing hard-blocks Home->Work1 -- the recipe WAIT(PartAtAssembly) holds the
        // COMMAND but a boot/init turn is unguarded. Add ECC-level rules: Home(0)->Work1(2) AND
        // Home(0)->Work2(4) blocked while PartAtAssembly (a Feed-controller sensor on the merged ring) is
        // absent (state 0). Releases the instant PartAtAssembly reports present -- same signal the recipe
        // gate already waits on, so it cannot deadlock where that gate does not. Clamp model unchanged.
        private static InterlockPlan WithPartAtAssemblyGate(InterlockPlan plan, VueOneComponent actuator)
        {
            if (!CodeGen.Configuration.MapperConfig.MergeFeedRing) return plan;
            if (actuator.Name == null ||
                actuator.Name.IndexOf("Bearing", StringComparison.OrdinalIgnoreCase) < 0) return plan;
            var pa = HandoffPlanner.PartAtAssembly;
            if (pa.Name == null) return plan;

            int cap = Cap, n = 0;
            int[] f = new int[cap], t = new int[cap], s = new int[cap], b = new int[cap];
            for (int i = 0; i < plan.Count && n < cap; i++)
            { f[n] = plan.From[i]; t[n] = plan.To[i]; s[n] = plan.Src[i]; b[n] = plan.Blocked[i]; n++; }
            foreach (int work in new[] { 2, 4 })   // Home -> Work1 / Work2 blocked while the part is absent
                if (n < cap) { f[n] = 0; t[n] = work; s[n] = pa.Id; b[n] = 0; n++; }
            return new InterlockPlan(n, f, t, s, b);
        }

        // Keep only rules whose From/To fall in the core's CurrentRawState range. Do NOT re-drop Blocked==0
        // here: BuildRules already dropped the inverted same-controller home-rest rules and kept the genuine
        // cross-controller readiness gates; re-dropping would discard those kept gates.
        private static InterlockPlan FilterToCentreHomeRange(InterlockPlan plan)
        {
            var r = InterlockConfig.Current.CentreHome;
            int cap = Cap, kept = 0;
            int[] f = new int[cap], t = new int[cap], s = new int[cap], b = new int[cap];
            for (int i = 0; i < plan.Count && i < cap; i++)
            {
                if (plan.From[i] < r.MinState || plan.From[i] > r.MaxState ||
                    plan.To[i] < r.MinState || plan.To[i] > r.MaxState) continue;
                f[kept] = plan.From[i]; t[kept] = plan.To[i];
                s[kept] = plan.Src[i];  b[kept] = plan.Blocked[i];
                kept++;
            }
            return new InterlockPlan(kept, f, t, s, b);
        }

        // The centre-home swivel crosses the shared work volume BOTH ways (Work1->Work2 placing,
        // Work2->Work1 depositing), so emit the reverse (To->From) of every surviving crossing rule to block
        // the crossing whichever way it travels, guarded by the same source + blocked-state.
        private static InterlockPlan WithReverseCrossings(InterlockPlan plan)
        {
            int cap = Cap, n = 0;
            int[] f = new int[cap], t = new int[cap], s = new int[cap], b = new int[cap];
            void Add(int fr, int to, int src, int blk)
            {
                for (int j = 0; j < n; j++)
                    if (f[j] == fr && t[j] == to && s[j] == src && b[j] == blk) return; // dedup
                if (n >= cap) return;
                f[n] = fr; t[n] = to; s[n] = src; b[n] = blk; n++;
            }
            for (int i = 0; i < plan.Count && i < cap; i++)
                Add(plan.From[i], plan.To[i], plan.Src[i], plan.Blocked[i]);
            for (int i = 0; i < plan.Count && i < cap; i++)
                if (plan.From[i] != plan.To[i])                      // a crossing, not a self-loop
                    Add(plan.To[i], plan.From[i], plan.Src[i], plan.Blocked[i]);
            return new InterlockPlan(n, f, t, s, b);
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

        private static int InScope(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            => scopedIds == null ? 0 : InterlockPlanner.CountInScopeConditions(actuator, allComponents, scopedIds);
    }
}
