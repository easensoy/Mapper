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
        // blocks the state's From->To transition while the source holds the blocking state.
        // BuildRules and CountInScopeConditions BOTH consume Resolve(), so the guard can never disagree
        // with what was emitted.
        public static InterlockPlan BuildRules(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin,
            ReportGraph rings, ControllerAllocation allocation,
            IReadOnlyDictionary<string, int> slots, List<string> findings)
        {
            int cap = InterlockConfig.Current.RuleArraySize;
            var from = new int[cap];
            var to = new int[cap];
            var src = new int[cap];
            var blk = new int[cap];
            int n = 0;

            foreach (var r in Resolve(actuator, scopedIds, catTypes, twin, rings, allocation, slots, findings))
            {
                if (n >= cap) break;
                from[n] = r.From; to[n] = r.To; src[n] = r.Src; blk[n] = r.Blocked;
                n++;
            }
            return new InterlockPlan(n, from, to, src, blk);
        }

        // Feeds the inert-safety-net guard: conditions present but RuleCount==0 aborts.
        public static int CountInScopeConditions(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin,
            ReportGraph rings, ControllerAllocation allocation)
            => Resolve(actuator, scopedIds, catTypes, twin, rings, allocation, null, null)
                .Take(InterlockConfig.Current.RuleArraySize)
                .Count();

        private readonly record struct Rule(int From, int To, int Src, int Blocked);

        // One rule per surviving <Interlock_Condition>. TwinModel has already bound every reference.
        private static IEnumerable<Rule> Resolve(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin,
            ReportGraph rings, ControllerAllocation allocation,
            IReadOnlyDictionary<string, int>? slots, List<string>? findings)
        {
            var owner = twin.ById(actuator.ComponentID);
            if (owner == null) yield break;

            foreach (var st in owner.States)
            {
                if (st.Interlocks.Count == 0) continue;

                var destination = st.Transitions.Select(tr => tr.Destination).FirstOrDefault(d => d != null);
                if (destination == null) continue;

                // A rule matches only when CurrentRawState == RuleFromState, i.e. the resting predecessor.
                int fromState = owner.States
                    .FirstOrDefault(p => p.Transitions.Any(tr => ReferenceEquals(tr.Destination, st)))
                    ?.Number ?? st.Number;

                foreach (var reference in st.Interlocks)
                {
                    if (!scopedIds.TryGetValue(reference.Component.Id, out var srcId))
                        throw new InvalidOperationException(
                            $"[Interlock] '{actuator.Name}' is interlocked on "+
                            $"'{reference.Component.Name}', which this plan gives no state_table slot, "+
                            "so it can never publish the state the rule names.");

                    int blocked = ActuatorStateEncoding.Settled(
                        reference.Component.Source, reference.State.Number, catTypes);

                    // Drop "block-while-source-is-home": a same-controller source at rest is out of the
                    // collision crossing, so the rule is inverted and would deadlock the recipe.
                    if (blocked == ActuatorStateEncoding.Home &&
                        !IsReadinessGate(actuator, reference.Component.Source, rings, allocation)) continue;

                    // Having a slot proves nothing: the slot is read on the CONSUMER's ring, so a
                    // source reporting elsewhere leaves the rule guarding whichever reporter holds
                    // that id there. Reported, not corrected: the remedy is transport or the twin.
                    if (findings != null && slots != null &&
                        !rings.SameDomain(reference.Component.Name, actuator.Name))
                    {
                        var actuallyRead = slots
                            .Where(kv => kv.Value == srcId && rings.SameDomain(kv.Key, actuator.Name))
                            .Select(kv => $"'{kv.Key}'").ToList();
                        findings.Add(
                            $"'{actuator.Name}' is interlocked on '{reference.Component.Name}', which "+
                            $"does not report onto the ring '{actuator.Name}' reads. The rule reads "+
                            $"state_table[{srcId}] there, which is "+
                            (actuallyRead.Count > 0
                                ? string.Join(" / ", actuallyRead) + " - a different component."
                                : "written by nothing on that ring, so it holds its initial value."));
                    }
                    yield return new Rule(fromState, destination.Number, srcId, blocked);
                }
            }
        }

        // The one genuine home-state interlock: an upstream FEED-controller source at home means "workpiece
        // not yet delivered", which must keep blocking downstream. A collision partner that merely lives on
        // another PLC is NOT a readiness gate; it returns home before the interlocked actuator moves.
        private static bool IsReadinessGate(VueOneComponent actuator, VueOneComponent? srcComp,
            ReportGraph rings, ControllerAllocation allocation)
        {
            if (!rings.RingsMerged || srcComp == null) return false;
            var source = allocation.Of(srcComp.Name);
            return source != allocation.Of(actuator.Name) && ControllerMap.IsFeedController(source);
        }
    }
}
