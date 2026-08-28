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
    /// The topology a plant compiles to must come from its STRUCTURE - who runs what, on which target,
    /// and which carriers the model selects - and not from what anything is called or which controller
    /// it happens to be. These build plants in code and compare structures, so a name appearing here is
    /// only ever a roster key.
    public sealed class GenericTopologyTests
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

        // A process whose MIDDLE state waits on another process: a wait inside a cycle rather than at
        // its edge, which a boundary handshake cannot carry.
        private static VueOneComponent ProcessWaitingMidCycle(
            string id, string name, string peerId, string peerStateId)
        {
            var entry = Entry(id + "-s0", "Entry", 0);
            var middle = Stop(id + "-s1", "Middle", 1);
            var last = Stop(id + "-s2", "Close", 2);
            entry.Transitions.Add(new VueOneTransition { DestinationStateID = middle.StateID });
            middle.Transitions.Add(new VueOneTransition
            {
                DestinationStateID = last.StateID,
                Conditions = new List<VueOneCondition>
                {
                    new() { ComponentID = peerId, ID = peerStateId, Name = "peer" },
                },
            });
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = "Process",
                States = new List<VueOneState> { entry, middle, last },
            };
        }

        private static VueOneComponent Process(string id, string name) => new()
        {
            ComponentID = id, Name = name, Type = "Process",
            States = new List<VueOneState> { Entry(id + "-s0", "Initialisation", 0) },
        };

        private static ControllerAllocation Allocation(TwinModel twin)
        {
            var roster = new DeploymentRoster(DeploymentProfile.AsPlaced(TestConfig.Cfg));
            roster.PlaceUnlisted(twin);
            return new ControllerAllocation(roster);
        }

        // ProcessGraph is where a process's control flow is resolved and validated; ReportGraph asks it
        // for the chain rather than walking the state machine a second time.
        private static IReadOnlyDictionary<string, CodeGen.Domain.Twin.ProcessGraph> Graphs(TwinModel twin) =>
            twin.Processes.ToDictionary(p => p.Name,
                p => CodeGen.Domain.Twin.ProcessGraph.Build(p.Source), StringComparer.OrdinalIgnoreCase);

        private static ReportGraph Build(params VueOneComponent[] components)
        {
            var twin = TwinModel.Build(components);
            return ReportGraph.Build(twin, Allocation(twin),
                RigCatalog.Current.CrossRingSegment, Array.Empty<string>(), Graphs(twin), TestConfig.Cfg.Targets);
        }

        // Roster rows on two targets that each run a process. Which target is which is not the point.
        private const string ProcA = "Feed_Station";
        private const string ProcB = "Assembly_Station";
        private const string OnA = "Feeder";
        private const string OnB = "Clamp";

        [Fact]
        public void A_process_on_either_controller_can_force_the_rings_together()
        {
            // The process doing the mid-cycle waiting is on the ASSEMBLY side here, which the merge
            // decision used to look straight past. The topology is what decides, not whose process it is.
            var a = Process("C-a", ProcA);
            var b = ProcessWaitingMidCycle("C-b", ProcB, "C-a", "C-a-s0");
            var g = Build(a, b, Actuator("C-1", OnA, "C-a", "C-a-s0"),
                          Actuator("C-2", OnB, "C-b", "C-b-s0"));

            Assert.True(g.RingsMerged);
            Assert.True(g.SameDomain(OnA, OnB));
        }

        [Fact]
        public void A_wait_at_a_cycle_boundary_does_not_force_a_merge()
        {
            // The entry state and the closing link are boundary handshakes; the phase transport carries
            // those, so nothing has to be folded together for them.
            var g = Build(Process("C-a", ProcA), Process("C-b", ProcB),
                          Actuator("C-1", OnA, "C-a", "C-a-s0"),
                          Actuator("C-2", OnB, "C-b", "C-b-s0"));

            Assert.False(g.RingsMerged);
            Assert.False(g.SameDomain(OnA, OnB));
        }

        [Fact]
        public void A_target_that_hosts_no_process_is_a_detour_only_where_it_declares_one()
        {
            // Running no process of its own is not what makes a target reachable from another: a target
            // names the one that commands its chain, and one that names none is not silently spliced on.
            var carriers = TestConfig.Cfg.Targets.All
                .Where(t => t.ChainCommandedBy != null).Select(t => t.Plc).ToList();
            Assert.NotEmpty(carriers);
            foreach (var t in TestConfig.Cfg.Targets.All.Where(t => t.StandsInFor != null))
                Assert.DoesNotContain(t.Plc, carriers);
            // Both ends of the relationship resolve, and the commanding end is DERIVED from the carrying
            // one - so a chain nobody commands, or a seam nobody carries, cannot be stated at all.
            foreach (var plc in carriers)
                Assert.True(TestConfig.Cfg.Targets.CommandsACarriedChain(
                    TestConfig.Cfg.Targets.Of(plc).ChainCommandedBy!.Value));
        }

        [Fact]
        public void Relocation_declares_the_target_it_receives_onto_and_never_falls_back_to_one()
        {
            // A stand-in NAMES the target it relieves, so "moved here" and "lives here" are the same
            // relationship read from its two ends rather than two flags that must be kept in step.
            var standIns = TestConfig.Cfg.Targets.All.Where(t => t.StandsInFor != null).ToList();
            Assert.NotEmpty(standIns);
            foreach (var r in standIns)
            {
                Assert.NotEqual(r.Plc, r.StandsInFor!.Value);
                Assert.True(TestConfig.Cfg.Targets.IsRegistered(r.StandsInFor!.Value));
                // It reports on that target's ring rather than owning one.
                Assert.False(TargetIndex.OwnsRing(r));
                Assert.Equal(r.StandsInFor!.Value, TestConfig.Cfg.Targets.RingHostOf(r));
                Assert.Contains(r.Plc, TestConfig.Cfg.Targets.RingMembers(r.StandsInFor!.Value));
            }
        }

        [Fact]
        public void Every_target_answers_the_same_questions_and_none_is_a_special_case()
        {
            // The registry is the only enumeration of targets, and every capability is answerable for
            // each: nothing downstream needs to know which controller it is looking at.
            Assert.NotEmpty(TestConfig.Cfg.Targets.All);
            foreach (var t in TestConfig.Cfg.Targets.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(t.ResourceName));
                Assert.False(string.IsNullOrWhiteSpace(t.DeviceType));
                Assert.NotEmpty(t.BootFbs);
            }
            // Every ring has an owner: a stand-in borrows one, and the target it borrows from owns its.
            foreach (var t in TestConfig.Cfg.Targets.All)
                Assert.True(TargetIndex.OwnsRing(
                    TestConfig.Cfg.Targets.Of(TestConfig.Cfg.Targets.RingHostOf(t))));
            // At most one carried chain per commanding target, or a ring would close across two seams.
            foreach (var g in TestConfig.Cfg.Targets.All.Where(t => t.ChainCommandedBy != null)
                         .GroupBy(t => t.ChainCommandedBy!.Value))
                Assert.Single(g);
        }
    }
}
