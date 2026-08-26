using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Domain.Twin;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// A dependency the model places across a controller boundary has to be CARRIED. These pin the two
    /// outcomes that must never be silent: a carrier is selected because the model needs it, and an edge
    /// nothing can carry stops generation naming it rather than emitting a command with no delivery.
    public sealed class ReportGraphTests
    {
        // A process states its own entry; ProcessGraph refuses a process that does not, because
        // starting a recipe at an arbitrary state runs the plant mid-cycle.
        private static VueOneState Entry(string id, string name, int number)
        {
            var s = Stop(id, name, number);
            s.InitialState = true;
            return s;
        }

        private static VueOneState Stop(string id, string name, int number) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = true,
            Transitions = new List<VueOneTransition>(),
        };

        // An actuator whose transition names Process/State: the model saying that process commands it.
        private static VueOneComponent Actuator(string id, string name, string commandedBy, string procStateId)
        {
            var home = Stop(id + "-home", "Home", 0);
            var work = Stop(id + "-work", "Work", 2);
            home.Transitions.Add(new VueOneTransition
            {
                DestinationStateID = work.StateID,
                Conditions = new List<VueOneCondition>
                {
                    new() { ComponentID = commandedBy, ID = procStateId, Name = "cmd" },
                },
            });
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = "Actuator",
                States = new List<VueOneState> { home, work },
            };
        }

        private static VueOneComponent Process(string id, string name) => new()
        {
            ComponentID = id, Name = name, Type = "Process",
            States = new List<VueOneState> { Entry(id + "-s0", "Initialisation", 0) },
        };

        // ProcessGraph is where a process's control flow is resolved and validated; ReportGraph asks it
        // for the chain rather than walking the state machine a second time.
        private static IReadOnlyDictionary<string, CodeGen.Domain.Twin.ProcessGraph> Graphs(TwinModel twin) =>
            twin.Processes.ToDictionary(p => p.Name,
                p => CodeGen.Domain.Twin.ProcessGraph.Build(p.Source), StringComparer.OrdinalIgnoreCase);

        private static ControllerAllocation Allocation(TwinModel twin)
        {
            var roster = new DeploymentRoster(DeploymentProfile.AsPlaced(TestConfig.Cfg));
            roster.PlaceUnlisted(twin);
            return new ControllerAllocation(roster);
        }

        // Layout-pinned names, so the two ends genuinely land on different controllers.
        private const string FeedProcess = "Feed_Station";
        private const string AssemblyProcess = "Assembly_Station";
        private const string FeedActuator = "Feeder";
        private const string AssemblyActuator = "Clamp";

        [Fact]
        public void A_command_across_controllers_with_no_carrier_fails_and_names_the_edge()
        {
            // Both controllers run a process, so neither device's components detour onto the other's ring,
            // and the commanded actuator is not on the declared discharge segment.
            var feed = Process("C-feed", FeedProcess);
            var assembly = Process("C-asm", AssemblyProcess);
            var driven = Actuator("C-act", FeedActuator, "C-asm", "C-asm-s0");
            var twin = TwinModel.Build(new[] { feed, assembly, driven });

            var ex = Assert.Throws<InvalidOperationException>(() => ReportGraph.Build(
                twin, Allocation(twin),
                RigCatalog.Current.CrossRingSegment, Array.Empty<string>(), Graphs(twin), TestConfig.Cfg.Targets));

            Assert.Contains("[Transport]", ex.Message, StringComparison.Ordinal);
            Assert.Contains(FeedActuator, ex.Message, StringComparison.Ordinal);
            Assert.Contains(AssemblyProcess, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_interlock_across_the_rings_is_carried_rather_than_reading_a_foreign_slot()
        {
            // A rule reads its source's slot in the CONSUMER's state_table, so a source reporting on
            // another ring would guard whichever component holds that slot there. Folding the rings into
            // one is the declared carrier that spans them, and the graph applies it rather than emitting
            // a rule that does not mean what the model says.
            var feed = Process("C-feed", FeedProcess);
            var assembly = Process("C-asm", AssemblyProcess);
            var here = Actuator("C-act", FeedActuator, "C-feed", "C-feed-s0");
            var there = Actuator("C-far", AssemblyActuator, "C-asm", "C-asm-s0");
            // The assembly-side actuator is blocked while the feed-side one is at work.
            there.States[0].InterlockConditions = new[]
            {
                new VueOneCondition { ComponentID = "C-act", ID = "C-act-work", Name = "blocked" },
            };
            var twin = TwinModel.Build(new[] { feed, assembly, here, there });

            var g = ReportGraph.Build(twin, Allocation(twin),
                RigCatalog.Current.CrossRingSegment, Array.Empty<string>(), Graphs(twin), TestConfig.Cfg.Targets);

            Assert.True(g.RingsMerged);
            Assert.True(g.SameDomain(FeedActuator, AssemblyActuator));
        }

        [Fact]
        public void The_discharge_segment_is_selected_by_the_model_not_by_its_members_existing()
        {
            // A twin that commands none of the segment's members gets no splice, however many of them the
            // rig declares -- presence is not a reason to wire a cross-controller tail.
            var feed = Process("C-feed", FeedProcess);
            var driven = Actuator("C-act", FeedActuator, "C-feed", "C-feed-s0");
            var twin = TwinModel.Build(new[] { feed, driven });

            var g = ReportGraph.Build(twin, Allocation(twin),
                RigCatalog.Current.CrossRingSegment, Array.Empty<string>(), Graphs(twin), TestConfig.Cfg.Targets);

            Assert.Empty(g.DischargeSegment);
        }
    }
}
