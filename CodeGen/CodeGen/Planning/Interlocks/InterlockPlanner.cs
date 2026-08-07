using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    public static class InterlockPlanner
    {
        // Control.xml STATE <Interlock_Condition> elements -> InterlockManager rules: each condition
        // blocks the state's From->To transition while the source holds the blocking state. Source ids
        // use the sensors-first scoped map; out-of-scope and home-rest rules are dropped.
        //
        // BuildRules and CountInScopeConditions BOTH consume Resolve(), so the guard can never disagree
        // with what was emitted. They used to be separate walks applying the drops independently, which
        // is how the guard came to omit the destination check and the array cap.
        public static InterlockPlan BuildRules(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int> scopedIds)
        {
            int cap = InterlockConfig.Current.RuleArraySize;
            var from = new int[cap];
            var to = new int[cap];
            var src = new int[cap];
            var blk = new int[cap];
            int n = 0;

            foreach (var r in Resolve(actuator, allComponents, scopedIds))
            {
                if (n >= cap) break;
                from[n] = r.From; to[n] = r.To; src[n] = r.Src; blk[n] = r.Blocked;
                n++;
            }
            return new InterlockPlan(n, from, to, src, blk);
        }

        // Count of in-scope conditions that survive the drops, for the inert-safety-net guard
        // (conditions present but RuleCount==0 => abort).
        public static int CountInScopeConditions(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int> scopedIds)
            => Resolve(actuator, allComponents, scopedIds)
                .Take(InterlockConfig.Current.RuleArraySize)
                .Count();

        private readonly record struct Rule(int From, int To, int Src, int Blocked);

        // The single translation pass: one rule per surviving <Interlock_Condition>.
        private static IEnumerable<Rule> Resolve(VueOneComponent actuator,
            IReadOnlyList<VueOneComponent> allComponents,
            IReadOnlyDictionary<string, int> scopedIds)
        {
            foreach (var st in actuator.States)
            {
                if (st.InterlockConditions.Count == 0) continue;

                int toState = FirstDestinationStateNumber(actuator, st);
                if (toState < 0) continue;

                // RuleFromState = the resting predecessor the FB sees at REQ time (a rule matches only
                // when CurrentRawState == RuleFromState).
                int fromState = PredecessorStateNumber(actuator, st);

                foreach (var c in st.InterlockConditions)
                {
                    var key = (c.ComponentID ?? string.Empty).Trim();
                    if (key.Length == 0 || !scopedIds.TryGetValue(key, out var srcId)) continue;

                    var srcComp = Lookup(allComponents, key);
                    int blockedState = StateNumberOf(srcComp, c.ID);
                    if (blockedState < 0) continue;

                    int blocked = ActuatorStateEncoding.Settled(srcComp, blockedState);

                    // Drop "block-while-source-is-home": a SAME-controller source at rest is out of the
                    // collision crossing, so blocking on it is inverted and would deadlock the recipe.
                    // A cross-controller FEED source at rest is the genuine exception — it means "the
                    // workpiece is not delivered", which must keep blocking downstream work.
                    if (blocked == ActuatorStateEncoding.Home &&
                        !IsCrossControllerReadinessGate(actuator, srcComp)) continue;

                    yield return new Rule(fromState, toState, srcId, blocked);
                }
            }
        }

        private static VueOneComponent? Lookup(IReadOnlyList<VueOneComponent> all, string componentId) =>
            all.FirstOrDefault(x =>
                string.Equals((x.ComponentID ?? string.Empty).Trim(), componentId,
                    StringComparison.OrdinalIgnoreCase));

        private static int StateNumberOf(VueOneComponent? comp, string? stateId) =>
            comp?.States.FirstOrDefault(s =>
                string.Equals((s.StateID ?? string.Empty).Trim(), (stateId ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))?.StateNumber ?? -1;

        private static int FirstDestinationStateNumber(VueOneComponent actuator, VueOneState st)
        {
            foreach (var tr in st.Transitions)
            {
                var dest = (tr.DestinationStateID ?? string.Empty).Trim();
                if (dest.Length == 0) continue;
                var ds = actuator.States.FirstOrDefault(s =>
                    string.Equals((s.StateID ?? string.Empty).Trim(), dest, StringComparison.OrdinalIgnoreCase));
                if (ds != null) return ds.StateNumber;
            }
            return -1;
        }

        private static int PredecessorStateNumber(VueOneComponent actuator, VueOneState st)
        {
            var ownStateId = (st.StateID ?? string.Empty).Trim();
            if (ownStateId.Length == 0) return st.StateNumber;
            var predecessor = actuator.States.FirstOrDefault(p =>
                p.Transitions.Any(t =>
                    string.Equals((t.DestinationStateID ?? string.Empty).Trim(), ownStateId,
                        StringComparison.OrdinalIgnoreCase)));
            return predecessor?.StateNumber ?? st.StateNumber;
        }

        // A home-state interlock is normally an inverted "source is out of the way" no-op. The one
        // genuine exception is an upstream FEED-controller source: its home means "workpiece not yet
        // delivered", which must keep blocking the downstream station. A collision partner that merely
        // lives on another PLC is NOT a readiness gate — it returns home BEFORE the interlocked actuator
        // moves, so keeping its rule deadlocks. Data-driven; off when the rings are not merged.
        private static bool IsCrossControllerReadinessGate(VueOneComponent actuator, VueOneComponent? srcComp)
        {
            if (!CodeGen.Translation.GenerationPlan.Current.RingsMerged || srcComp == null) return false;
            var allocation = ControllerAllocation.Current;
            var source = allocation.Of(srcComp.Name);
            return source != allocation.Of(actuator.Name) && ControllerMap.IsFeedController(source);
        }
    }
}
