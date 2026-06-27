using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    /// <summary>
    /// Pure transition-chain parsing for recipe generation: walk a Process's
    /// Control.xml state graph in EXECUTION order (Initial_State →
    /// transition.DestinationStateID, NOT State_Number order), render that walk as
    /// the human-readable transition-table metadata, and identify the
    /// Initialisation boot state. Every method is a pure function of its
    /// <see cref="VueOneState"/> arguments — no RecipeArrays, no MapperConfig, no
    /// I/O, no shared state.
    /// </summary>
    public static class TransitionChainParser
    {
        /// <summary>
        /// True if the state is the VueOne Initialisation boot-assertion state.
        /// Detected via InitialState=true OR Name matching "Initialisation"/
        /// "Initialization" (case-insensitive). Used to drop the state from the
        /// recipe regardless of whether its first condition resolves in scope —
        /// Initialisation is a boot precondition, not a work-cycle step.
        /// </summary>
        public static bool IsInitialisationState(VueOneState s)
        {
            if (s.InitialState) return true;
            var n = (s.Name ?? string.Empty).Trim();
            return n.Equals("Initialisation", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("Initialization", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Orders a Process's states in EXECUTION order by walking the transition
        /// chain from the initial state, rather than by State_Number.
        ///
        /// <para>Why: VueOne models authored incrementally leave State_Number = 0
        /// on every state added after the first pass. Ordering by State_Number then
        /// pulls all the 0-numbered states to the front, and because the runtime
        /// starts at CurrentStep 0 the recipe begins mid-cycle. Observed on
        /// Assembly_Station: the shaft-place and cover steps all carry
        /// State_Number = 0, so the recipe started with shaft_vr instead of the
        /// real first step (Clamping_Part). Walking Initial_State -&gt;
        /// transition.DestinationStateID reproduces the true sequence.</para>
        ///
        /// <para>States not reachable from the initial chain are intentionally not
        /// serialized. A Process recipe is a linear executable plan; appending
        /// unreachable authoring leftovers after a loop back to Initialisation would
        /// create motions that the Control.xml transition graph never reaches.</para>
        /// </summary>
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
