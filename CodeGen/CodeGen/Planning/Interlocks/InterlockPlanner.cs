using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    public static class InterlockPlanner
    {
        // Control.xml STATE <Interlock_Condition> -> InterlockManager rules. VueOne writes a guard as
        // ConditionValue -> ConditionGroup* -> Condition*, so it is a SUM OF PRODUCTS: the conditions in
        // one group hold TOGETHER and the groups are alternatives. The rule table has the same shape - it
        // blocks when any ONE alternative is wholly satisfied - so an alternative is emitted as a head row
        // carrying its term count followed by the rest of its terms.
        //
        // BuildRules and CountInScopeAlternatives both consume Resolve(), so the guard can never disagree
        // with what was emitted, and neither stops early: an alternative the model states is either
        // planned or the generation fails, never dropped to fit a fixed array.
        public static InterlockPlan BuildRules(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin,
            ReportGraph rings, ControllerAllocation allocation,
            IReadOnlyDictionary<string, int> slots, List<string> findings)
        {
            var plan = new InterlockPlan.Builder();
            foreach (var alternative in
                     Resolve(actuator, scopedIds, catTypes, twin, rings, allocation, slots, findings))
                plan.Add(alternative);
            return plan.ToPlan();
        }

        // Feeds the inert-safety-net guard: alternatives present but nothing emitted aborts.
        public static int CountInScopeAlternatives(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin,
            ReportGraph rings, ControllerAllocation allocation)
            => Resolve(actuator, scopedIds, catTypes, twin, rings, allocation, null, null).Count();

        // One alternative of one state's guard: the transition it blocks, and the terms that must all
        // hold for it to block.
        public readonly record struct Alternative(int From, int To, IReadOnlyList<Term> Terms);

        public readonly record struct Term(int Src, int Blocked);

        private static IEnumerable<Alternative> Resolve(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin,
            ReportGraph rings, ControllerAllocation allocation,
            IReadOnlyDictionary<string, int>? slots, List<string>? findings)
        {
            var owner = twin.ById(actuator.ComponentID);
            if (owner == null) yield break;

            foreach (var st in owner.States)
            {
                if (st.InterlockGuard == null) continue;

                var destination = st.Transitions.Select(tr => tr.Destination).FirstOrDefault(d => d != null);
                if (destination == null) continue;

                // A rule matches only when CurrentRawState == FromState, i.e. the resting predecessor.
                int fromState = owner.States
                    .FirstOrDefault(p => p.Transitions.Any(tr => ReferenceEquals(tr.Destination, st)))
                    ?.Number ?? st.Number;

                foreach (var product in st.InterlockGuard.SumOfProducts())
                {
                    var terms = new List<Term>();
                    foreach (var leaf in product)
                    {
                        var reference = st.ResolvedInterlock(leaf.Condition);
                        if (reference == null) continue;
                        if (!scopedIds.TryGetValue(reference.Component.Id, out var srcId))
                            throw new InvalidOperationException(
                                $"[Interlock] '{actuator.Name}' is interlocked on " +
                                $"'{reference.Component.Name}', which this plan gives no state_table slot, " +
                                "so it can never publish the state the rule names.");

                        int blocked = ActuatorStateEncoding.Settled(
                            reference.Component.Source, reference.State.Number, catTypes);

                        // "Block while the source is at rest" is inverted: a source at rest is OUT of the
                        // crossing. Alone it deadlocks the move; inside a conjunction with a work position
                        // of the SAME source it is worse - the alternative can never hold, so the rule is
                        // inert and the actuator is guarded by nothing. Neither is shippable, so the term
                        // is dropped and REPORTED. The one real at-rest guard is a readiness gate.
                        if (blocked == ActuatorStateEncoding.Home &&
                            !IsReadinessGate(actuator, reference.Component.Source, rings, allocation))
                        {
                            findings?.Add(
                                $"'{actuator.Name}' state '{st.Name}' is interlocked on " +
                                $"'{reference.Component.Name}/{reference.State.Name}', which settles at the " +
                                "source's rest position. A rest position is out of the crossing, so the term " +
                                "would block a move the twin means to allow; it is not emitted.");
                            continue;
                        }

                        Report(actuator, reference, srcId, rings, slots, findings);
                        terms.Add(new Term(srcId, blocked));
                    }

                    if (terms.Count == 0) continue;
                    yield return new Alternative(fromState, destination.Number, terms);
                }
            }
        }

        // Having a slot proves nothing: the slot is read on the CONSUMER's ring, so a source reporting
        // elsewhere leaves the rule guarding whichever reporter holds that id there.
        private static void Report(VueOneComponent actuator, Domain.Twin.TwinRef reference, int srcId,
            ReportGraph rings, IReadOnlyDictionary<string, int>? slots, List<string>? findings)
        {
            if (findings == null || slots == null) return;
            if (rings.SameDomain(reference.Component.Name, actuator.Name)) return;
            var actuallyRead = slots
                .Where(kv => kv.Value == srcId && rings.SameDomain(kv.Key, actuator.Name))
                .Select(kv => $"'{kv.Key}'").ToList();
            findings.Add(
                $"'{actuator.Name}' is interlocked on '{reference.Component.Name}', which does not report " +
                $"onto the ring '{actuator.Name}' reads. The rule reads state_table[{srcId}] there, " +
                "which is " +
                (actuallyRead.Count > 0
                    ? string.Join(" / ", actuallyRead) + " - a different component."
                    : "written by nothing on that ring, so it holds its initial value."));
        }

        // The one genuine at-rest interlock: an upstream FEED-controller source at home means "workpiece
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
