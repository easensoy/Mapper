using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Domain.Twin;
using CodeGen.Models;
using Xunit;

namespace MapperTests
{
    /// A process in Control.xml is a state machine; the deployed recipe engine executes a linear row
    /// list with one NextStep per row. ProcessGraph is where those two meet: it resolves the control
    /// flow ONCE, and refuses - by name, before any file is written - anything the engine cannot carry.
    /// These pin what the compiler accepts and what it refuses, so neither can drift into a guess.
    public sealed class ProcessGraphTests
    {
        private static VueOneState State(string id, string name, int number, bool initial = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = true, InitialState = initial,
            Transitions = new List<VueOneTransition>(),
        };

        private static void Leads(VueOneState from, VueOneState to, int priority = 0,
            params VueOneCondition[] guard) =>
            from.Transitions.Add(new VueOneTransition
            {
                TransitionID = $"T-{from.StateID}-{to.StateID}-{priority}",
                OriginStateID = from.StateID, DestinationStateID = to.StateID,
                Priority = priority, Conditions = guard.ToList(),
            });

        private static VueOneCondition On(string label) =>
            new() { ComponentID = "C-x", ID = "C-x-s1", Name = label };

        private static VueOneComponent Process(string name, params VueOneState[] states) => new()
        {
            ComponentID = "C-" + name, Name = name, Type = ComponentType.Process,
            States = states.ToList(),
        };

        // ---- accepted -------------------------------------------------------------------------

        [Fact]
        public void A_linear_process_compiles_in_execution_order()
        {
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Soak", 1);
            var c = State("s2", "Draw", 2);
            Leads(a, b); Leads(b, c);
            var g = ProcessGraph.Build(Process("Kiln_Line", a, b, c));

            Assert.Equal(new[] { "Charge", "Soak", "Draw" }, g.Ordered.Select(s => s.Name));
            Assert.Empty(g.Unreachable);
            Assert.Equal("Charge", g.Entry.Name);
            Assert.Null(g.Successor(c));
        }

        [Fact]
        public void A_cycle_is_a_loop_and_not_an_error()
        {
            // A back-edge is exactly what NextStep expresses, so a cyclic line is representable.
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Draw", 1);
            Leads(a, b); Leads(b, a);
            var g = ProcessGraph.Build(Process("Kiln_Line", a, b));

            Assert.Equal(new[] { "Charge", "Draw" }, g.Ordered.Select(s => s.Name));
            Assert.Same(a, g.TerminalDestination);
            Assert.True(g.IsEntry(g.TerminalDestination));
        }

        [Fact]
        public void An_OR_guard_and_an_AND_guard_are_both_carried_on_one_edge()
        {
            // Guard SHAPE is not the graph's business - it is one edge either way - but the graph must
            // not lose the expression, because the lowering reads it back off the transition.
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Draw", 1);
            Leads(a, b, 0, On("Ram/Forward"), On("Gate/Open"));
            var g = ProcessGraph.Build(Process("Kiln_Line", a, b));

            var edge = g.Leaving(a);
            Assert.NotNull(edge);
            Assert.Equal(2, edge!.Guard?.References().Count);
        }

        [Fact]
        public void An_unreachable_state_is_reported_and_not_silently_forgotten()
        {
            // It cannot execute, so it lays down no rows - but that is a MODEL fact, and the compiler
            // has to say it rather than quietly walk past it.
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Draw", 1);
            var orphan = State("s2", "Purge", 2);
            Leads(a, b); Leads(b, a); Leads(orphan, a);
            var g = ProcessGraph.Build(Process("Kiln_Line", a, b, orphan));

            Assert.DoesNotContain(g.Ordered, s => s.Name == "Purge");
            Assert.Equal(new[] { "Purge" }, g.Unreachable.Select(s => s.Name));
            Assert.Equal(3, g.AllStates.Count);
        }

        // ---- refused --------------------------------------------------------------------------

        [Fact]
        public void A_state_with_two_destinations_is_refused_and_names_both()
        {
            // The engine carries one NextStep per row. Serializing the higher-priority branch and
            // dropping the other is what this refuses: a branch is not a compiler's guess to make.
            var a = State("s0", "Charge", 0, initial: true);
            var hot = State("s1", "Hot_Draw", 1);
            var cold = State("s2", "Cold_Draw", 2);
            Leads(a, hot, 0, On("Kiln/Hot"));
            Leads(a, cold, 1, On("Kiln/Cold"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProcessGraph.Build(Process("Kiln_Line", a, hot, cold)));

            Assert.Contains("Kiln_Line", ex.Message);
            Assert.Contains("Charge", ex.Message);
            Assert.Contains("Hot_Draw", ex.Message);
            Assert.Contains("Cold_Draw", ex.Message);
            // The refusal has to say WHY the compiler will not choose, and what the modeller can do
            // instead - a diagnostic that only says "no" sends them to read the runtime.
            Assert.Contains("LANGUAGE LIMITATION", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Recipe[x].NextStep", ex.Message, StringComparison.Ordinal);
            Assert.Contains("loop", ex.Message, StringComparison.Ordinal);
            Assert.Contains("two processes", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_transition_with_no_destination_is_refused()
        {
            var a = State("s0", "Charge", 0, initial: true);
            a.Transitions.Add(new VueOneTransition
            { TransitionID = "T-dangling", OriginStateID = "s0", DestinationStateID = string.Empty });
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProcessGraph.Build(Process("Kiln_Line", a)));

            Assert.Contains("T-dangling", ex.Message);
            Assert.Contains("no destination", ex.Message);
        }

        [Fact]
        public void A_transition_naming_a_state_this_process_does_not_have_is_refused()
        {
            var a = State("s0", "Charge", 0, initial: true);
            a.Transitions.Add(new VueOneTransition
            { TransitionID = "T-away", OriginStateID = "s0", DestinationStateID = "s-elsewhere" });
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProcessGraph.Build(Process("Kiln_Line", a)));

            Assert.Contains("s-elsewhere", ex.Message);
            Assert.Contains("not a state of this process", ex.Message);
        }

        [Fact]
        public void A_process_flagging_no_entry_state_is_refused_rather_than_started_anywhere()
        {
            var a = State("s0", "Charge", 0);
            var b = State("s1", "Draw", 1);
            Leads(a, b);
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProcessGraph.Build(Process("Kiln_Line", a, b)));

            Assert.Contains("Initial_State", ex.Message);
            Assert.Contains("mid-cycle", ex.Message);
        }

        [Fact]
        public void A_process_flagging_two_entry_states_is_refused_as_ambiguous()
        {
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Draw", 1, initial: true);
            Leads(a, b); Leads(b, a);
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProcessGraph.Build(Process("Kiln_Line", a, b)));

            Assert.Contains("ambiguous", ex.Message);
            Assert.Contains("Charge", ex.Message);
            Assert.Contains("Draw", ex.Message);
        }

        [Fact]
        public void A_process_with_no_states_is_refused()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProcessGraph.Build(Process("Kiln_Line")));
            Assert.Contains("no states", ex.Message);
        }

        // ---- one owner ------------------------------------------------------------------------

        [Fact]
        public void The_successor_the_chain_walks_is_the_successor_NextStep_resolves()
        {
            // Two spellings of "what runs next" is how a recipe's rows and its NextStep pointers come to
            // disagree. There is one here, and this is what pins that.
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Soak", 1);
            var c = State("s2", "Draw", 2);
            Leads(a, b); Leads(b, c); Leads(c, a);
            var g = ProcessGraph.Build(Process("Kiln_Line", a, b, c));

            for (int i = 0; i < g.Ordered.Count - 1; i++)
                Assert.Same(g.Ordered[i + 1], g.Successor(g.Ordered[i]));
            Assert.Same(a, g.Successor(c));
        }

        [Fact]
        public void The_transition_table_describes_the_states_that_actually_execute()
        {
            var a = State("s0", "Charge", 0, initial: true);
            var b = State("s1", "Draw", 1);
            var orphan = State("s2", "Purge", 2);
            Leads(a, b, 0, On("Ram/Forward")); Leads(b, a); Leads(orphan, a);
            var lines = ProcessGraph.Build(Process("Kiln_Line", a, b, orphan)).TransitionTable().ToList();

            Assert.Equal(2, lines.Count);
            Assert.Equal("0: Charge -> Draw on Ram/Forward", lines[0]);
            Assert.Equal("1: Draw -> Charge on (no condition)", lines[1]);
        }
    }
}
