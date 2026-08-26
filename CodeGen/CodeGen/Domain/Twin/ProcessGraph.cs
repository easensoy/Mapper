using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Domain.Twin
{
    // One process's control flow, resolved once from the twin and validated before anything is written.
    //
    // A process in Control.xml is a state machine: states, and transitions carrying a guard, a priority
    // and a destination. The deployed recipe engine executes a LINEAR row list with one NextStep per
    // row - it can loop (NextStep is an arbitrary index, so a back-edge is a cycle) but it has no branch
    // row: nothing in RecipeStep can say "go to X if this guard holds, otherwise to Y". So a state with
    // two outgoing transitions has no faithful lowering, and this REFUSES it by name rather than
    // serializing one branch and silently discarding the other.
    //
    // Everything downstream asks this for the successor, the entry state and the execution order, so
    // there is one answer to "what runs next" instead of one per caller.
    public sealed class ProcessGraph
    {
        private readonly Dictionary<string, VueOneState> _byId;
        private readonly Dictionary<string, VueOneTransition?> _successorOf;

        public string ProcessName { get; }

        // Every state the twin declares, in declaration order. Nothing is filtered out here: what is
        // unreachable is REPORTED, because a state that cannot execute is a model fact worth stating
        // and not the same thing as a state the compiler quietly forgot.
        public IReadOnlyList<VueOneState> AllStates { get; }

        // Execution order: the entry state, then its successor, and so on until the chain closes or
        // ends. This is the order rows are laid down in.
        public IReadOnlyList<VueOneState> Ordered { get; }

        // Declared but not reachable from the entry state. Their guards can never be evaluated, so they
        // contribute no rows - stated, so a coverage check can account for every leaf they hold.
        public IReadOnlyList<VueOneState> Unreachable { get; }

        public VueOneState Entry => Ordered[0];

        // The state the chain closes on, when it closes on one. A terminal chain has none.
        public VueOneState? TerminalDestination { get; }

        private ProcessGraph(string name, IReadOnlyList<VueOneState> all, IReadOnlyList<VueOneState> ordered,
            IReadOnlyList<VueOneState> unreachable, Dictionary<string, VueOneState> byId,
            Dictionary<string, VueOneTransition?> successorOf, VueOneState? terminal)
        {
            ProcessName = name;
            AllStates = all;
            Ordered = ordered;
            Unreachable = unreachable;
            _byId = byId;
            _successorOf = successorOf;
            TerminalDestination = terminal;
        }

        // The single transition leaving a state, or null where it leaves none. Single because Build
        // refused anything else, which is what lets every caller ask this one question.
        public VueOneTransition? Leaving(VueOneState state) =>
            state?.StateID != null && _successorOf.TryGetValue(state.StateID, out var t) ? t : null;

        public VueOneState? Successor(VueOneState state)
        {
            var id = Leaving(state)?.DestinationStateID;
            return string.IsNullOrEmpty(id) ? null : _byId.GetValueOrDefault(id!);
        }

        public bool IsEntry(VueOneState? state) => state != null && state.InitialState;

        // The chain diagnostic the generation report carries: one line per executed state, naming what
        // it leaves for and on which condition. A projection of this graph, never a second walk of it.
        public IEnumerable<string> TransitionTable()
        {
            for (int i = 0; i < Ordered.Count; i++)
            {
                var state = Ordered[i];
                var tr = Leaving(state);
                if (tr == null)
                {
                    yield return $"{i}: {state.Name} -> END";
                    continue;
                }

                string dest = tr.DestinationStateID;
                if (!string.IsNullOrWhiteSpace(dest) && _byId.TryGetValue(dest, out var destState))
                    dest = destState.Name;

                var cond = tr.Guard?.References().FirstOrDefault();
                string on = cond == null || string.IsNullOrWhiteSpace(cond.Name)
                    ? "(no condition)"
                    : cond.Name.Trim();
                yield return $"{i}: {state.Name} -> {dest} on {on}";
            }
        }

        public static ProcessGraph Build(VueOneComponent process)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            var name = (process.Name ?? string.Empty).Trim();
            var states = process.States ?? new List<VueOneState>();
            if (states.Count == 0)
                throw Fail(name, null, "declares no states, so it has no control flow to compile");

            var byId = new Dictionary<string, VueOneState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in states)
                if (!string.IsNullOrEmpty(s.StateID) && !byId.ContainsKey(s.StateID))
                    byId[s.StateID] = s;

            // ENTRY. The twin flags its own entry, so a process whose entry is not called
            // "Initialisation" is read correctly - which the shipped twins need, one naming it
            // "Initialize". Guessing at the first declared state instead would start a recipe
            // mid-cycle, so no entry and two entries are both refused.
            var entries = states.Where(s => s.InitialState).ToList();
            if (entries.Count == 0)
                throw Fail(name, null,
                    "flags no state as its Initial_State, so where its cycle begins is undecidable; " +
                    "a recipe started at an arbitrary state would run the plant mid-cycle");
            if (entries.Count > 1)
                throw Fail(name, null,
                    $"flags {entries.Count} states as Initial_State ({Names(entries)}), so where its " +
                    "cycle begins is ambiguous");

            // ONE SUCCESSOR PER STATE. The recipe engine has one NextStep per row and no branch row, so
            // a state offering a choice of destinations cannot be lowered without discarding one.
            var successorOf = new Dictionary<string, VueOneTransition?>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in states)
            {
                var outgoing = (s.Transitions ?? new List<VueOneTransition>())
                    .OrderBy(t => t.Priority).ToList();
                if (outgoing.Count > 1)
                    throw Fail(name, s,
                        $"has {outgoing.Count} outgoing transitions (" +
                        string.Join(", ", outgoing.Select(t =>
                            $"'{t.TransitionID}' -> '{Dest(byId, t)}'")) +
                        "). The recipe engine carries one NextStep per row and has no branch row, so a " +
                        "choice of destinations cannot be lowered without discarding one of them");

                var only = outgoing.FirstOrDefault();
                if (only != null)
                {
                    if (string.IsNullOrWhiteSpace(only.DestinationStateID))
                        throw Fail(name, s,
                            $"transition '{only.TransitionID}' declares no destination state, so what " +
                            "runs after it is undefined");
                    if (!byId.ContainsKey(only.DestinationStateID))
                        throw Fail(name, s,
                            $"transition '{only.TransitionID}' names destination '{only.DestinationStateID}', " +
                            "which is not a state of this process");
                }
                if (!string.IsNullOrEmpty(s.StateID)) successorOf[s.StateID] = only;
            }

            // EXECUTION ORDER. Out-degree is at most one, so the walk from the entry is total and
            // unambiguous: there is exactly one chain and it either ends or closes into a cycle.
            var ordered = new List<VueOneState>(states.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VueOneState? cur = entries[0];
            VueOneState? terminal = null;
            while (cur != null && !string.IsNullOrEmpty(cur.StateID) && seen.Add(cur.StateID))
            {
                ordered.Add(cur);
                var next = successorOf.GetValueOrDefault(cur.StateID);
                terminal = next?.DestinationStateID is { Length: > 0 } d ? byId.GetValueOrDefault(d) : null;
                cur = terminal;
            }

            var unreachable = states.Where(s =>
                !string.IsNullOrEmpty(s.StateID) && !seen.Contains(s.StateID)).ToList();

            return new ProcessGraph(name, states, ordered, unreachable, byId, successorOf, terminal);
        }

        private static string Dest(Dictionary<string, VueOneState> byId, VueOneTransition t) =>
            string.IsNullOrWhiteSpace(t.DestinationStateID) ? "(none)"
            : byId.TryGetValue(t.DestinationStateID, out var s) ? s.Name : t.DestinationStateID;

        private static string Names(IEnumerable<VueOneState> states) =>
            string.Join(", ", states.Select(s => $"'{s.Name}'"));

        private static InvalidOperationException Fail(string process, VueOneState? state, string why) =>
            new($"[Process] '{process}'{(state == null ? "" : $" state '{state.Name}'")}: {why}.");
    }
}
