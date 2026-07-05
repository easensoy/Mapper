using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    // Pure transition-chain parsing: walk a Process's Control.xml state graph in EXECUTION order
    // (Initial_State -> transition.DestinationStateID, NOT State_Number order). No shared state / I/O.
    public static class TransitionChainParser
    {
        // The VueOne Initialisation boot-assertion state (InitialState=true OR Name "Initialisation"/
        // "Initialization"). Dropped from the recipe (boot precondition, not a work-cycle step).
        public static bool IsInitialisationState(VueOneState s)
        {
            if (s.InitialState) return true;
            var n = (s.Name ?? string.Empty).Trim();
            return n.Equals("Initialisation", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("Initialization", StringComparison.OrdinalIgnoreCase);
        }

        // Orders a Process's states in EXECUTION order by walking the transition chain from the initial
        // state, NOT by State_Number. Why: incrementally-authored VueOne models leave State_Number=0 on
        // later states, so State_Number order pulls them to the front and the recipe starts mid-cycle.
        // States unreachable from the initial chain are intentionally not serialized.
        public static List<VueOneState> OrderStatesByTransitionChain(IList<VueOneState> states)
        {
            if (states == null || states.Count == 0) return new List<VueOneState>();

            var byId = new Dictionary<string, VueOneState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in states)
                if (!string.IsNullOrEmpty(s.StateID) && !byId.ContainsKey(s.StateID))
                    byId[s.StateID] = s;

            var start = states.FirstOrDefault(IsInitialisationState) ?? states[0];
            var ordered = new List<VueOneState>(states.Count);
            var seen = new HashSet<VueOneState>();

            VueOneState? cur = start;
            while (cur != null && seen.Add(cur))
            {
                ordered.Add(cur);
                VueOneState? next = null;
                var tr = cur.Transitions?.FirstOrDefault();
                if (tr != null && !string.IsNullOrEmpty(tr.DestinationStateID))
                    byId.TryGetValue(tr.DestinationStateID, out next);
                cur = next;
            }

            return ordered;
        }

        public static IEnumerable<string> BuildTransitionTable(
            IList<VueOneState> allStates,
            IList<VueOneState> orderedStates)
        {
            var byId = new Dictionary<string, VueOneState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in allStates)
                if (!string.IsNullOrEmpty(s.StateID) && !byId.ContainsKey(s.StateID))
                    byId[s.StateID] = s;

            for (int i = 0; i < orderedStates.Count; i++)
            {
                var state = orderedStates[i];
                var tr = state.Transitions?.FirstOrDefault();
                if (tr == null)
                {
                    yield return $"{i}: {state.Name} -> END";
                    continue;
                }

                string dest = tr.DestinationStateID;
                if (!string.IsNullOrWhiteSpace(dest) &&
                    byId.TryGetValue(dest, out var destState))
                    dest = destState.Name;

                var cond = tr.Conditions?.FirstOrDefault();
                string on = cond == null || string.IsNullOrWhiteSpace(cond.Name)
                    ? "(no condition)"
                    : cond.Name.Trim();
                yield return $"{i}: {state.Name} -> {dest} on {on}";
            }
        }
    }
}
