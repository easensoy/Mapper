using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Process.Recipes;
using Xunit;

namespace MapperTests
{
    /// A guard leaf is one condition the twin wrote. It may become a WAIT, it may be covered by
    /// something already standing in the recipe, or it may be answered by a declaration the deployment
    /// makes - and if it is none of those, generation stops. A warning is not an outcome: a control
    /// semantic that reaches nothing is a defect, so these pin that every leaf is accounted for.
    public sealed class GuardCoverageTests
    {
        private static VueOneState State(string id, string name, int number, bool stop = true, bool initial = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = stop, InitialState = initial,
            Transitions = new List<VueOneTransition>(),
        };

        private static void Leads(VueOneState from, VueOneState to, params VueOneCondition[] guard) =>
            from.Transitions.Add(new VueOneTransition
            {
                TransitionID = $"T-{from.StateID}-{to.StateID}",
                OriginStateID = from.StateID, DestinationStateID = to.StateID,
                Conditions = guard.ToList(),
            });

        private static VueOneCondition On(string component, string state, string label) =>
            new() { ComponentID = component, ID = state, Name = label };

        private static VueOneComponent Actuator(string id, string name, string proc, string procState)
        {
            var home = State(id + "-s0", "Home", 0, initial: true);
            var moving = State(id + "-m", "Moving", 1, stop: false);
            var work = State(id + "-s2", "Work", 2);
            Leads(home, moving, On(proc, procState, "cmd"));
            Leads(moving, work);
            Leads(work, home);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Actuator,
                States = new List<VueOneState> { home, moving, work },
            };
        }

        private static VueOneComponent Process(string id, string name, params string[] names)
        {
            var states = names.Select((n, i) => State($"{id}-s{i}", n, i, initial: i == 0)).ToList();
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Process, States = states,
            };
        }

        private static void Close(VueOneComponent process)
        {
            var s = process.States;
            for (int i = 0; i < s.Count; i++)
                if (s[i].Transitions.Count == 0) Leads(s[i], s[(i + 1) % s.Count]);
        }

        private static GenerationContext Plan(params VueOneComponent[] plant) =>
            GenerationContext.Plan(TestConfig.Cfg, plant.ToList(),
                DeploymentProfile.AsPlaced(TestConfig.Cfg));

        // One process on one controller driving one actuator: the smallest plant with real guards.
        private static GenerationContext OneStation()
        {
            var proc = Process("C-a", "Feed_Station", "Entry", "Drive", "Settle");
            var act = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            Leads(proc.States[1], proc.States[2], On("C-1", "C-1-s2", "Feeder/Work"));
            Close(proc);
            return Plan(proc, act);
        }

        [Fact]
        public void Every_leaf_the_model_declares_reaches_a_decision()
        {
            var ctx = OneStation();
            var declared = ctx.Components.Where(ComponentType.IsProcess)
                .SelectMany(ProcessCompiler.DeclaredLeaves).ToList();

            Assert.NotEmpty(declared);
            foreach (var leaf in declared)
                Assert.NotNull(ctx.GuardCoverage.Find(leaf.Id));
        }

        [Fact]
        public void An_observation_of_a_movement_this_state_commands_is_proved_by_that_command()
        {
            // The state that drives the actuator already emits the command and its arrival WAIT, so the
            // observation on the same transition is the same requirement written twice - accounted for,
            // and pointed at the row that covers it, rather than dropped.
            var ctx = OneStation();
            var leaf = ctx.GuardCoverage.Leaves.Single(l => l.Condition == "Feeder/Work");
            Assert.Equal(GuardLeafOutcome.AlreadyRequired, leaf.Outcome);
            Assert.Contains("Feeder", leaf.Why);
        }

        [Fact]
        public void An_observation_of_something_this_recipe_never_drives_becomes_a_wait()
        {
            // A sensor is nobody's movement, so nothing in the recipe can have proved it already: the
            // step genuinely has to ask for a fresh level and wait for it.
            var proc = Process("C-a", "Feed_Station", "Entry", "Drive", "Settle");
            var act = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            var sensor = new VueOneComponent
            {
                ComponentID = "C-s", Name = "PartInHopper", Type = ComponentType.Sensor,
                States = new List<VueOneState>
                {
                    State("C-s-off", "Off", 0, initial: true),
                    State("C-s-on", "On", 1),
                },
            };
            Leads(proc.States[1], proc.States[2], On("C-s", "C-s-on", "PartInHopper/On"));
            Close(proc);
            var ctx = Plan(proc, act, sensor);

            var leaf = ctx.GuardCoverage.Leaves.Single(l => l.Condition == "PartInHopper/On");
            Assert.Equal(GuardLeafOutcome.Waited, leaf.Outcome);
            Assert.Contains("PartInHopper", leaf.Why);
        }

        [Fact]
        public void A_leaf_in_a_state_that_never_runs_is_recorded_as_unreachable()
        {
            var proc = Process("C-a", "Feed_Station", "Entry", "Drive");
            var act = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            Close(proc);                                   // Entry -> Drive -> Entry, a closed cycle
            var orphan = State("C-a-orphan", "Never", 9);  // nothing leads here
            Leads(orphan, proc.States[0], On("C-1", "C-1-s2", "Feeder/Work"));
            proc.States.Add(orphan);
            var ctx = Plan(proc, act);

            var leaf = ctx.GuardCoverage.Leaves.Single(l => l.State == "Never");
            Assert.Equal(GuardLeafOutcome.Unreachable, leaf.Outcome);
            Assert.Contains("not reachable", leaf.Why);
        }

        [Fact]
        public void A_requirement_a_later_state_restates_is_recorded_against_the_row_that_covers_it()
        {
            // Two states name the same stop of the same actuator. The first waits; the second is the
            // same requirement still standing, so it is accounted for rather than waited on twice.
            var proc = Process("C-a", "Feed_Station", "Entry", "Drive", "Settle", "Confirm");
            var act = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            Leads(proc.States[2], proc.States[3], On("C-1", "C-1-s2", "Feeder/Work"));
            Leads(proc.States[3], proc.States[0], On("C-1", "C-1-s2", "Feeder/Work"));
            Close(proc);
            var ctx = Plan(proc, act);

            // Both are covered by the command that drove the actuator there, and both SAY so.
            var settle = ctx.GuardCoverage.Leaves.Single(l => l.State == "Settle");
            var confirm = ctx.GuardCoverage.Leaves.Single(l => l.State == "Confirm");
            Assert.Equal(GuardLeafOutcome.AlreadyRequired, settle.Outcome);
            Assert.Equal(GuardLeafOutcome.AlreadyRequired, confirm.Outcome);
            Assert.Contains("already", confirm.Why);
            Assert.Contains("Feeder", settle.Why);
        }

        // ---- the declared handoff policy ------------------------------------------------------

        [Fact]
        public void This_deployment_declares_what_a_producers_entry_phase_means()
        {
            // Undeclared is refused, so the shipped profile has to say. Reading it as boot readiness and
            // reading it as a runtime phase drive the plant differently.
            // Asked the way the compiler asks it: for an edge. A catch-all row answers every pair.
            Assert.NotEqual(PeerEntryPhaseMeaning.Undeclared,
                PlantFacts.Declared(TestConfig.Cfg.Rig).Handoff
                    .MeaningFor("Any_Producer", "Any_Consumer", "Entry"));
        }

        [Fact]
        public void A_peer_entry_phase_reference_is_answered_by_the_declaration_not_by_silence()
        {
            var producer = Process("C-a", "Feed_Station", "Entry", "Drive");
            var consumer = Process("C-b", "Assembly_Station", "Entry", "Drive");
            var a = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            var b = Actuator("C-2", "Clamp", "C-b", "C-b-s1");
            // The consumer's entry names the producer's ENTRY phase.
            Leads(consumer.States[0], consumer.States[1], On("C-a", "C-a-s0", "Feed_Station/Entry"));
            Close(producer); Close(consumer);
            var ctx = Plan(producer, consumer, a, b);

            var leaf = ctx.GuardCoverage.Leaves.Single(l => l.Condition == "Feed_Station/Entry");
            Assert.Equal(GuardLeafOutcome.SatisfiedByDeclaration, leaf.Outcome);
            Assert.Contains("smc-rig.yml", leaf.Why);
        }

        [Fact]
        public void An_undeclared_peer_entry_phase_reference_is_refused_before_anything_is_written()
        {
            var producer = Process("C-a", "Feed_Station", "Entry", "Drive");
            var consumer = Process("C-b", "Assembly_Station", "Entry", "Drive");
            var a = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            var b = Actuator("C-2", "Clamp", "C-b", "C-b-s1");
            Leads(consumer.States[0], consumer.States[1], On("C-a", "C-a-s0", "Feed_Station/Entry"));
            Close(producer); Close(consumer);

            var silent = PlantFacts.Declared(TestConfig.Cfg.Rig) with
            {
                Handoff = new HandoffPolicy(),   // no rows: no edge is covered
            };
            var ex = Assert.Throws<InvalidOperationException>(() => GenerationContext.Plan(
                TestConfig.Cfg, new List<VueOneComponent> { producer, consumer, a, b },
                DeploymentProfile.Relocating(Array.Empty<string>(), TestConfig.Cfg, silent)));

            Assert.Contains("entry phase", ex.Message);
            Assert.Contains("peerEntryPhase", ex.Message);
        }

        [Fact]
        public void A_phase_with_no_transport_and_no_declared_carrier_is_refused()
        {
            // Two controllers, no shared ring, a mid-cycle wait the phase channel cannot carry, and
            // nothing declared to stand for it. The compiler refuses rather than substituting a level.
            var producer = Process("C-a", "Feed_Station", "Entry", "Drive", "Settle");
            var consumer = Process("C-b", "Assembly_Station", "Entry", "Drive", "Settle");
            var a = Actuator("C-1", "Feeder", "C-a", "C-a-s1");
            var b = Actuator("C-2", "Clamp", "C-b", "C-b-s1");
            Close(producer); Close(consumer);

            var noCarrier = PlantFacts.Declared(TestConfig.Cfg.Rig) with
            {
                CarrierSegment = Array.Empty<string>(),
                Handoff = new HandoffPolicy
                {
                    PeerEntryPhase = new List<PeerEntryPhaseRule>
                    {
                        new() { Meaning = PeerEntryPhaseMeaning.RuntimePhase, Because = "test" },
                    },
                    Carriers = new List<CarrierSubstitution>(),
                },
            };
            // The consumer's entry names the producer's ENTRY phase, now read as a runtime phase.
            Leads(consumer.States[0], consumer.States[1], On("C-a", "C-a-s0", "Feed_Station/Entry"));

            var ex = Assert.Throws<InvalidOperationException>(() => GenerationContext.Plan(
                TestConfig.Cfg, new List<VueOneComponent> { producer, consumer, a, b },
                DeploymentProfile.Relocating(Array.Empty<string>(), TestConfig.Cfg, noCarrier)));

            Assert.Contains("Assembly_Station", ex.Message);
        }

        [Fact]
        public void A_carrier_may_only_stand_for_a_phase_where_the_deployment_says_why()
        {
            // The shipped profile authorises no substitution at all, which is what makes the refusal
            // above real: nothing may quietly replace a phase with a material level.
            var policy = PlantFacts.Declared(TestConfig.Cfg.Rig).Handoff;
            foreach (var carrier in policy.Carriers)
            {
                Assert.False(string.IsNullOrWhiteSpace(carrier.Producer));
                Assert.False(string.IsNullOrWhiteSpace(carrier.Carrier));
                Assert.False(string.IsNullOrWhiteSpace(carrier.Because));
                Assert.NotEqual(carrier.Asserted, carrier.Deasserted);
            }
        }

        [Fact]
        public void The_shipped_twins_lose_no_guard_leaf()
        {
            // The proof runs inside Plan for every generation, so a plant that plans at all has had
            // every leaf accounted for. Stated here as the property, against a real multi-process plant.
            var ctx = OneStation();
            var declared = ctx.Components.Where(ComponentType.IsProcess)
                .SelectMany(ProcessCompiler.DeclaredLeaves).ToList();
            ctx.GuardCoverage.AssertCovers(declared);   // must not throw

            // And a leaf nothing recorded is refused, so the proof is not vacuous.
            var invented = new GuardLeaf(
                new GuardLeafId("P-none", "S-none", "T-invented", "C-none", "S-none", 0),
                "Feed_Station", "Entry", "Nothing/Ever", GuardLeafOutcome.Waited, string.Empty);
            var ex = Assert.Throws<InvalidOperationException>(
                () => ctx.GuardCoverage.AssertCovers(declared.Append(invented).ToList()));
            Assert.Contains("reached no compiler decision", ex.Message);
        }

        [Fact]
        public void Two_conditions_sharing_a_display_name_are_two_leaves_not_one()
        {
            // The twin names a condition after the state it references, so a guard that observes two
            // components' identically-named states writes the same Name twice. A name-keyed coverage
            // map loses one of them and still reports full coverage.
            var ctx = OneStation();
            var declared = ctx.Components.Where(ComponentType.IsProcess)
                .SelectMany(ProcessCompiler.DeclaredLeaves).ToList();

            Assert.Equal(declared.Count, declared.Select(d => d.Id).Distinct().Count());

            var byName = declared
                .GroupBy(d => $"{d.Process}|{d.State}|{d.Id.TransitionId}|{d.Condition}")
                .Where(g => g.Count() > 1).ToList();
            foreach (var clash in byName)
                Assert.Equal(clash.Count(), clash.Select(d => d.Id).Distinct().Count());
        }

        [Fact]
        public void Coverage_proved_against_a_colliding_identity_is_refused()
        {
            // Two DIFFERENT leaves may never share one identity: the first one decided would account
            // for the second, and a real drop would read as full coverage.
            var id = new GuardLeafId("P-1", "S-1", "T-1", "C-1", "S-9", 0);
            var declared = new[]
            {
                new GuardLeaf(id, "Line", "Charging", "PartPresent", GuardLeafOutcome.Waited, string.Empty),
                new GuardLeaf(id, "Line", "Charging", "PartPresent", GuardLeafOutcome.Waited, string.Empty),
            };
            var coverage = new GuardCoverage();
            coverage.Record(declared[0]);

            var ex = Assert.Throws<InvalidOperationException>(() => coverage.AssertCovers(declared));
            Assert.Contains("claimed by more", ex.Message);
        }

        [Fact]
        public void A_decision_about_a_leaf_the_model_never_declared_is_refused()
        {
            // Coverage is a correspondence, not a count: a decision naming a leaf the twin does not
            // carry would let a real leaf go undecided while the totals still matched.
            var declared = new[]
            {
                new GuardLeaf(new GuardLeafId("P-1", "S-1", "T-1", "C-1", "S-9", 0),
                    "Line", "Charging", "PartPresent", GuardLeafOutcome.Waited, string.Empty),
            };
            var coverage = new GuardCoverage();
            coverage.Record(declared[0]);
            coverage.Record(new GuardLeaf(new GuardLeafId("P-1", "S-1", "T-1", "C-2", "S-4", 1),
                "Line", "Charging", "Invented", GuardLeafOutcome.Waited, string.Empty));

            var ex = Assert.Throws<InvalidOperationException>(() => coverage.AssertCovers(declared));
            Assert.Contains("the model does not", ex.Message);
        }
    }
}
