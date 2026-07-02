using System;
using System.Collections.Generic;
using System.Globalization;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    /// <summary>
    /// Owns interlock translation + emission: builds the rule plan from Control.xml
    /// (<see cref="InterlockPlanner"/>), applies generation policy (BX1 cover-zeroing, the
    /// centre-home raw-state range filter, the bench drop), writes the RuleCount + Rule* params,
    /// and runs the inert-safety-net guards. <see cref="SystemLayoutInjector"/> consumes the
    /// result and holds no interlock translation of its own.
    /// </summary>
    public static class InterlockEmitter
    {
        private static int Cap => InterlockConfig.Current.RuleArraySize;

        // ── Five_State_Actuator_CAT ──────────────────────────────────────────────────────────────

        /// <summary>Write the rule params for a Five_State actuator (plan + BX1 cover-zeroing).</summary>
        public static void ApplyFiveState(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
            => Write(p, FiveStatePlan(actuator, allComponents, scopedIds));

        /// <summary>
        /// Refuse an inert safety net (in-scope Control.xml conditions but RuleCount=0), except the
        /// deliberately-zeroed BX1 covers. Records the bound interlock count.
        /// </summary>
        public static void GuardFiveState(Dictionary<string, string> p, VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds,
            List<(string Component, string Detail)> bound)
        {
            int emitted = EmittedCount(p);
            // BX1 cover-detour actuators are deliberately zeroed in FiveStatePlan (their interlocks are
            // cross-PLC and unsound on the BX1 evaluator; the ground-truth ships covers with RuleCount=0).
            // Exempt them from the inert-safety-net guard so the intentional RuleCount=0 is not mistaken
            // for a failed translation.
            if (HandoffPlanner.IsCoverDetourActuator(actuator.Name))
            {
                if (emitted > 0)
                    bound.Add((actuator.Name, $"interlock RuleCount={emitted}"));
                return;
            }
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

        /// <summary>Write the rule params for the centre-home swivel (plan + range filter + bench drop).</summary>
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

        /// <summary>Write a zeroed rule set (RuleCount=0 + empty arrays).</summary>
        public static void ApplyZero(Dictionary<string, string> p) => Write(p, InterlockPlan.Empty(Cap));

        // ── Plans ────────────────────────────────────────────────────────────────────────────────

        private static InterlockPlan FiveStatePlan(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
        {
            // BX1 cover-detour actuators (CoverPNP_Hr/Vr/Gripper) are the one exception. Their
            // Control.xml interlocks reference M580 actuators (Shaft_Hr, Bearing_PnP), but a BX1
            // actuator's CommonInterlockEvaluator reads the BX1 state_table, which the cross-device
            // ring does not reliably feed with M580 states. The rule then either deadlocks (mutual
            // shaft_hr<->coverpnp_hr, each blocking while the other is at work) or blocks on a stale
            // cross-PLC slot, stalling the cover sequence at the M580->BX1 seam. The collision the rule
            // guards cannot occur in the cover-detour layout (the cover replaces the shaft's horizontal
            // motion and the recipe sequences them), and the rig-proven ground-truth ships covers with
            // RuleCount=0. A cross-PLC interlock on a BX1 actuator is structurally unsound -> zero it.
            if (HandoffPlanner.IsCoverDetourActuator(actuator.Name))
                return InterlockPlan.Empty(Cap);

            // Every other Five_State actuator emits whatever interlock the Control.xml defines.
            return scopedIds != null
                ? InterlockPlanner.BuildRules(actuator, allComponents, scopedIds)
                : InterlockPlan.Empty(Cap);
        }

        private static InterlockPlan CentreHomePlan(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int>? scopedIds)
        {
            var plan = scopedIds != null
                ? InterlockPlanner.BuildRules(actuator, allComponents, scopedIds)
                : InterlockPlan.Empty(Cap);
            return FilterToCentreHomeRange(plan);
        }

        /// <summary>
        /// Keep only rules whose From/To fall in the core's CurrentRawState range (a "2"-suffixed
        /// Disassembly route numbers outside the range and is inert). The Blocked==0 decision is NOT
        /// re-made here: InterlockPlanner.BuildRules already dropped the inverted same-controller
        /// home-rest rules and kept the genuine cross-controller readiness gates (MergeFeedRing).
        /// Re-dropping Blocked==0 here would discard those kept gates (e.g. Bearing_PnP blocked while
        /// Transfer is home / the workpiece is undelivered).
        /// </summary>
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

        // ── Param IO ─────────────────────────────────────────────────────────────────────────────

        private static void Write(Dictionary<string, string> p, InterlockPlan plan)
        {
            if (InterlockConfig.Current.UseStruct)
            {
                // One encapsulated input: RuleTable : InterlockTable = (Count, Rules[]). No CAT-level RuleCount.
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

        // The emitted rule count, read from whichever form Write produced (the InterlockTable's Count
        // in struct mode, the standalone RuleCount param in array mode).
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
