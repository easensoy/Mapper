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
    /// A report domain is the set of targets that share one ring in the FINISHED topology. It decides
    /// whether two reporters land in the same state_table, so it is derived from allocation, carriers and
    /// the merge decision - never from what a station is called. These pin that: the same structure has to
    /// come out whatever the components are named.
    public sealed class ReportDomainTests
    {
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

        private static VueOneComponent Process(string id, string name) => new()
        {
            ComponentID = id, Name = name, Type = "Process",
            States = new List<VueOneState> { Stop(id + "-s0", "Initialisation", 0) },
        };

        private static ControllerAllocation Allocation(TwinModel twin)
        {
            var roster = new DeploymentRoster(DeploymentProfile.M262Only(LayoutCatalog.Load()));
            roster.PlaceUnlisted(twin);
            return new ControllerAllocation(roster);
        }

        private static ReportGraph Build(params VueOneComponent[] components)
        {
            var twin = TwinModel.Build(components);
            return ReportGraph.Build(twin, Allocation(twin),
                RigCatalog.Current.CrossRingSegment, Array.Empty<string>());
        }

        // Two roster rows on the feed target and two on the assembly target. Nothing here is a Feed or an
        // Assembly: the structural outcome has to be the same either way, which the last test proves.
        private const string FeedProcess = "Feed_Station";
        private const string AsmProcess = "Assembly_Station";
        private const string OnFeed = "Feeder";
        private const string AlsoOnFeed = "Checker";
        private const string OnAsm = "Clamp";
        private const string AlsoOnAsm = "Shaft_Hr";
        private const string OnProcesslessDevice = "CoverPNP_Hr";

        [Fact]
        public void Two_controllers_that_each_run_a_process_keep_their_own_domains()
        {
            var g = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-f", "C-f-s0"),
                Actuator("C-2", OnAsm, "C-a", "C-a-s0"));

            Assert.False(g.RingsMerged);
            Assert.False(g.SameDomain(OnFeed, OnAsm));
            Assert.NotEqual(g.DomainId(OnFeed), g.DomainId(OnAsm));
        }

        [Fact]
        public void Components_on_one_controller_share_one_domain()
        {
            var g = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-f", "C-f-s0"),
                Actuator("C-2", AlsoOnFeed, "C-f", "C-f-s0"),
                Actuator("C-3", OnAsm, "C-a", "C-a-s0"),
                Actuator("C-4", AlsoOnAsm, "C-a", "C-a-s0"));

            Assert.True(g.SameDomain(OnFeed, AlsoOnFeed));
            Assert.True(g.SameDomain(OnAsm, AlsoOnAsm));
            Assert.False(g.SameDomain(AlsoOnFeed, AlsoOnAsm));
        }

        [Fact]
        public void A_device_that_runs_no_process_joins_the_ring_of_whatever_drives_it()
        {
            // Its components cannot act alone, so they detour onto the commanding target's ring - which
            // makes them reachable from that target's state_table without merging anything else.
            var g = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-f", "C-f-s0"),
                Actuator("C-2", OnAsm, "C-a", "C-a-s0"),
                Actuator("C-3", OnProcesslessDevice, "C-a", "C-a-s0"));

            Assert.False(g.RingsMerged);
            Assert.True(g.SameDomain(OnProcesslessDevice, OnAsm));
            Assert.False(g.SameDomain(OnProcesslessDevice, OnFeed));
        }

        [Fact]
        public void A_declared_carrier_puts_a_component_on_the_commanding_ring_without_moving_it()
        {
            // A discharge member stays allocated to its own target and still reports onto the ring of the
            // target that commands it, so its own neighbours are on a different ring than it is.
            var carried = RigCatalog.Current.CrossRingSegment.FirstOrDefault();
            Assert.False(string.IsNullOrWhiteSpace(carried));

            var g = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-f", "C-f-s0"),
                Actuator("C-2", OnAsm, "C-a", "C-a-s0"),
                Actuator("C-3", carried!, "C-a", "C-a-s0"));

            Assert.Contains(carried!, g.DischargeSegment);
            Assert.True(g.SameDomain(carried, OnAsm));
            Assert.False(g.SameDomain(carried, OnFeed));
        }

        [Fact]
        public void A_merge_puts_every_target_in_one_domain()
        {
            // An interlock whose source reports on another ring is what selects the merge.
            var blocked = Actuator("C-2", OnAsm, "C-a", "C-a-s0");
            blocked.States[0].InterlockConditions = new[]
            {
                new VueOneCondition { ComponentID = "C-1", ID = "C-1-work", Name = "blocked" },
            };
            var g = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-f", "C-f-s0"), blocked);

            Assert.True(g.RingsMerged);
            Assert.True(g.SameDomain(OnFeed, OnAsm));
            Assert.Equal(g.DomainId(OnFeed), g.DomainId(OnAsm));
        }

        [Fact]
        public void A_crossing_no_carrier_can_serve_stops_generation_and_names_it()
        {
            var twin = TwinModel.Build(new[]
            {
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-a", "C-a-s0"),
            });

            var ex = Assert.Throws<InvalidOperationException>(() => ReportGraph.Build(
                twin, Allocation(twin), Array.Empty<string>(), Array.Empty<string>()));

            Assert.Contains("[Transport]", ex.Message, StringComparison.Ordinal);
            Assert.Contains(OnFeed, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_component_the_roster_places_nowhere_shares_a_domain_with_nothing()
        {
            var g = Build(Process("C-f", FeedProcess), Actuator("C-1", OnFeed, "C-f", "C-f-s0"));

            Assert.Equal(ReportDomainId.Unplaced, g.DomainId("a name no roster row carries"));
            Assert.False(g.SameDomain("a name no roster row carries", OnFeed));
        }

        [Fact]
        public void Renaming_the_stations_leaves_the_same_structure()
        {
            // The SAME topology under names that suggest nothing about a feed or an assembly. Domains are
            // compared structurally - which components share one - because the ids themselves are opaque.
            var byStationName = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-1", OnFeed, "C-f", "C-f-s0"),
                Actuator("C-2", AlsoOnFeed, "C-f", "C-f-s0"),
                Actuator("C-3", OnAsm, "C-a", "C-a-s0"),
                Actuator("C-4", OnProcesslessDevice, "C-a", "C-a-s0"));

            // Same roster rows, but every component's ROLE in the twin swapped onto a different instance:
            // the processes trade which actuators they command, so the graph is rebuilt from scratch.
            var again = Build(
                Process("C-f", FeedProcess), Process("C-a", AsmProcess),
                Actuator("C-9", AlsoOnFeed, "C-f", "C-f-s0"),
                Actuator("C-8", OnFeed, "C-f", "C-f-s0"),
                Actuator("C-7", OnAsm, "C-a", "C-a-s0"),
                Actuator("C-6", OnProcesslessDevice, "C-a", "C-a-s0"));

            foreach (var (a, b) in new[]
                     {
                         (OnFeed, AlsoOnFeed), (OnFeed, OnAsm), (OnAsm, OnProcesslessDevice),
                         (OnFeed, OnProcesslessDevice),
                     })
                Assert.Equal(byStationName.SameDomain(a, b), again.SameDomain(a, b));
        }
    }
}
