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
        // ONE traversal. Nothing here drops a term or an alternative, so what the model states and what
        // is planned are the same set by construction - there is no second walk to check the first
        // against, and no count for a guard to compare with.
        public static InterlockPlan BuildRules(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin, List<string> findings)
        {
            var plan = new InterlockPlan.Builder();
            foreach (var alternative in Resolve(actuator, scopedIds, catTypes, twin, findings))
                plan.Add(alternative);
            return plan.ToPlan();
        }

        // One alternative of one state's guard: the transition it blocks, and the terms that must all
        // hold for it to block.
        public readonly record struct Alternative(int From, int To, IReadOnlyList<Term> Terms);

        public readonly record struct Term(int Src, int Blocked);

        private static IEnumerable<Alternative> Resolve(VueOneComponent actuator,
            IReadOnlyDictionary<string, int> scopedIds, IReadOnlyDictionary<string, string> catTypes,
            Domain.Twin.TwinModel twin, List<string>? findings)
        {
            var owner = twin.ById(actuator.ComponentID);
            if (owner == null) yield break;

            foreach (var st in owner.States)
            {
                if (st.InterlockGuard == null) continue;

                // The rule blocks a MOVE. A guarded state may branch, and the guard protects EVERY move
                // leaving it - so each destination gets its own rule. Taking the first would leave the
                // other branches unguarded while the report still read as a compiled interlock.
                if (st.Transitions.Count == 0)
                    throw new InvalidOperationException(
                        $"[Interlock] '{actuator.Name}' state '{st.Name}' carries an interlock guard but has " +
                        "no transition out of it, so there is no move for the rule to block. Give the state " +
                        "a destination, or take the guard off it.");
                var destinations = new List<Domain.Twin.TwinState>();
                foreach (var tr in st.Transitions)
                    destinations.Add(tr.Destination
                        ?? throw new InvalidOperationException(
                            $"[Interlock] '{actuator.Name}' state '{st.Name}' carries an interlock guard and a " +
                            "transition that resolves to no destination, so one of the moves the guard " +
                            "protects cannot be named in a rule. Resolve the destination, or take the guard " +
                            "off the state."));

                // A rule matches only when CurrentRawState == FromState, i.e. the resting predecessor.
                // Named through the CAT's stop vocabulary, exactly like the state the move ends at and
                // like every term: a rule written against a branch numbering the core never publishes
                // would compile, deploy, and match nothing.
                var predecessor = owner.States
                    .FirstOrDefault(p => p.Transitions.Any(tr => ReferenceEquals(tr.Destination, st)))
                    ?? st;
                int fromState = ActuatorStateEncoding.CanonicalNumber(
                    actuator, predecessor.Source, catTypes);

                foreach (var product in st.InterlockGuard.SumOfProducts())
                {
                    var terms = new List<Term>();
                    // What each term came from, for a refusal to be able to name it. Planning-stage
                    // context: the rule table carries only the numbers.
                    var from = new List<(Term Term, string Source, string State)>();
                    foreach (var leaf in product)
                    {
                        var reference = st.ResolvedInterlock(leaf.Condition)
                            ?? throw new InvalidOperationException(
                                $"[Interlock] '{actuator.Name}' state '{st.Name}' is interlocked on " +
                                $"'{leaf.Condition.Name}', which resolves to no component and state of this " +
                                "model, so the rule names something that does not exist.");
                        if (!scopedIds.TryGetValue(reference.Component.Id, out var srcId))
                            throw new InvalidOperationException(
                                $"[Interlock] '{actuator.Name}' is interlocked on " +
                                $"'{reference.Component.Name}', which this plan gives no state_table slot, " +
                                "so it can never publish the state the rule names.");

                        int blocked = ActuatorStateEncoding.StopAt(
                            reference.Component.Source, reference.State.Source, catTypes);

                        var term = new Term(srcId, blocked);
                        terms.Add(term);
                        from.Add((term, reference.Component.Name, reference.State.Name));
                    }

                    // The alternative is emitted exactly as the twin states it - nothing here drops a
                    // term to make a guard fire. But the evaluator reads ONE state per source and needs
                    // every term to hold at once, so two terms naming one source at different stops can
                    // never both be true, and such an alternative blocks nothing however it is written.
                    // That is a defect in the MODEL, not something for the compiler to reinterpret: an
                    // AND is what a single ConditionGroup means. It is reported in the strongest terms
                    // the report has, because the actuator is then guarded by nothing on that move.
                    var clash = from.GroupBy(t => t.Term.Src)
                        .FirstOrDefault(g => g.Select(t => t.Term.Blocked).Distinct().Count() > 1);
                    if (clash != null)
                        findings?.Add(
                            $"UNSATISFIABLE INTERLOCK: '{actuator.Name}' state '{st.Name}' is interlocked on " +
                            string.Join(" AND ", clash.Select(t =>
                                $"'{t.Source}/{t.State}' (settles at {t.Term.Blocked})")) +
                            $" - '{clash.First().Source}' at several stops at once, which it can never be. " +
                            "The rule is emitted as the twin states it and can therefore never fire, so " +
                            $"'{actuator.Name}' is guarded by nothing on this move. VueOne writes one " +
                            "ConditionGroup as a conjunction; stating these in SEPARATE ConditionGroups " +
                            "makes them alternatives, which is what a guard naming two ends of one axis " +
                            "means.");

                    foreach (var destination in destinations)
                        yield return new Alternative(fromState,
                            ActuatorStateEncoding.CanonicalNumber(actuator, destination.Source, catTypes),
                            terms);
                }
            }
        }

        // NOTE: whether an interlock source reaches its consumer's ring is NOT asked here. ReportGraph
        // decides the finished topology and REFUSES a run in which any interlock source cannot reach the
        // ring its consumer reads, so by the time rules are planned that question is already settled.
        // Asking it again would be a second owner of the same answer, free to disagree with the first.
    }
}
