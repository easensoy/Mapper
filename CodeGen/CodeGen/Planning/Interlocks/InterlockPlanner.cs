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
            IReadOnlyDictionary<string, int> scopedIds, GenerationContext ctx)
        {
            int cap = InterlockConfig.Current.RuleArraySize;
            var from = new int[cap];
            var to = new int[cap];
            var src = new int[cap];
            var blk = new int[cap];
            int n = 0;

            foreach (var r in Resolve(actuator, scopedIds, ctx))
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
            IReadOnlyDictionary<string, int> scopedIds, GenerationContext ctx)
            => Resolve(actuator, scopedIds, ctx)
                .Take(InterlockConfig.Current.RuleArraySize)
                .Count();

        private readonly record struct Rule(int From, int To, int Src, int Blocked);

        // The single translation pass: one rule per surviving <Interlock_Condition>. Every reference
        // is already bound by TwinModel, so a dangling one never reaches here.
        private static IEnumerable<Rule> Resolve(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, GenerationContext ctx)
        {
            var owner = ctx.Twin.ById(actuator.ComponentID);
            if (owner == null) yield break;

            foreach (var st in owner.States)
            {
                if (st.Interlocks.Count == 0) continue;

                var destination = st.Transitions.Select(tr => tr.Destination).FirstOrDefault(d => d != null);
                if (destination == null) continue;

                // RuleFromState = the resting predecessor the FB sees at REQ time (a rule matches only
                // when CurrentRawState == RuleFromState).
                int fromState = owner.States
                    .FirstOrDefault(p => p.Transitions.Any(tr => ReferenceEquals(tr.Destination, st)))
                    ?.Number ?? st.Number;

                foreach (var reference in st.Interlocks)
                {
                    if (!scopedIds.TryGetValue(reference.Component.Id, out var srcId)) continue;

                    int blocked = ActuatorStateEncoding.Settled(
                        reference.Component.Source, reference.State.Number);

                    // Drop "block-while-source-is-home": a SAME-controller source at rest is out of the
                    // collision crossing, so blocking on it is inverted and would deadlock the recipe.
                    // A cross-controller FEED source at rest is the genuine exception — it means "the
                    // workpiece is not delivered", which must keep blocking downstream work.
                    if (blocked == ActuatorStateEncoding.Home &&
                        !IsCrossControllerReadinessGate(actuator, reference.Component.Source, ctx)) continue;

                    yield return new Rule(fromState, destination.Number, srcId, blocked);
                }
            }
        }

        // A home-state interlock is normally an inverted "source is out of the way" no-op. The one
        // genuine exception is an upstream FEED-controller source: its home means "workpiece not yet
        // delivered", which must keep blocking the downstream station. A collision partner that merely
        // lives on another PLC is NOT a readiness gate — it returns home BEFORE the interlocked actuator
        // moves, so keeping its rule deadlocks. Data-driven; off when the rings are not merged.
        private static bool IsCrossControllerReadinessGate(VueOneComponent actuator, VueOneComponent? srcComp,
            GenerationContext ctx)
        {
            if (!ctx.RingsMerged || srcComp == null) return false;
            var allocation = ctx.Allocation;
            var source = allocation.Of(srcComp.Name);
            return source != allocation.Of(actuator.Name) && ControllerMap.IsFeedController(source);
        }
    }
}
