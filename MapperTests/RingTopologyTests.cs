using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// A report ring spans however many targets share its domain. Nothing in the compiler may assume
    /// there are two of them: the hosts form ONE cycle, derived from the targets that actually carry
    /// ring members, ordered by the one order every target already has - its declaration in device.yml.
    ///
    /// The cycle arithmetic is proved for any number of hosts; the derivation of WHICH targets are
    /// hosts is proved against plants built here, on the registry the repository ships.
    public sealed class RingTopologyTests
    {
        // ---- the cycle, for any number of hosts ---------------------------------------------------

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(7)]
        [InlineData(20)]
        public void Hosts_of_one_domain_form_exactly_one_cycle(int hostCount)
        {
            var hosts = Enumerable.Range(0, hostCount).ToList();

            // Following the successor from any host must visit every host once and come back. That is
            // the whole property: one cycle, no host stranded, no host visited twice.
            foreach (var start in hosts)
            {
                var visited = new List<int>();
                int? at = start;
                while (at != null)
                {
                    visited.Add(at.Value);
                    var next = ResourceWiringPlanner.NextInCycle(hosts, at.Value);
                    at = next != null && !next.Value.Equals(start) ? next : null;
                }
                Assert.Equal(hosts.Count, visited.Count);
                Assert.Equal(hosts.OrderBy(h => h), visited.OrderBy(h => h));
            }
        }

        [Fact]
        public void A_lone_host_has_nowhere_to_hand_its_tail_and_closes_on_itself()
        {
            // Null is not "unknown" here - it is the answer. A single-host domain's ring returns to its
            // own head, which is what the renderer does when there is no next host.
            Assert.Null(ResourceWiringPlanner.NextInCycle(new List<int> { 5 }, 5));
        }

        [Fact]
        public void A_target_that_is_not_a_host_gets_no_successor()
        {
            Assert.Null(ResourceWiringPlanner.NextInCycle(new List<int> { 1, 2, 3 }, 9));
        }

        [Fact]
        public void Reordering_the_hosts_reorders_the_cycle_and_still_covers_them_all()
        {
            // The order is data - device.yml's declaration order - so a different registration order is
            // a different cycle over the same set, never a different set and never a broken chain.
            var forward = new List<int> { 0, 1, 2, 3 };
            var reversed = Enumerable.Reverse(forward).ToList();

            Assert.Equal(1, ResourceWiringPlanner.NextInCycle(forward, 0));
            Assert.Equal(2, ResourceWiringPlanner.NextInCycle(reversed, 3));

            foreach (var order in new[] { forward, reversed })
            {
                var reached = order.Select(h => ResourceWiringPlanner.NextInCycle(order, h)!.Value).ToList();
                Assert.Equal(order.OrderBy(x => x), reached.OrderBy(x => x));   // a permutation
            }
        }

        // ---- which targets are hosts, on real plants -----------------------------------------------

        [Fact]
        public void A_host_is_a_target_that_carries_ring_members_on_this_run()
        {
            // Not a capability flag: a target declared however you like is not a ring host if the plant
            // put nothing of its own on its ring.
            var ctx = Plant(OnA, OnB);
            foreach (var host in ResourceWiringPlanner.RingHostsSharing(ctx, ctx.Allocation.Of(OnA)))
            {
                Assert.True(ctx.Emits(host));
                Assert.NotEmpty(ResourceWiringPlanner.RingOf(ctx, host, ChainOrder.Application));
            }
        }

        [Fact]
        public void The_cycle_order_is_the_target_registration_order()
        {
            // Which host follows which is device.yml's declaration order filtered to the hosts - so
            // registering targets in another order reorders the cycle, with no pairing written down.
            var ctx = Plant(OnA, OnB);
            var hosts = ResourceWiringPlanner.RingHostsSharing(ctx, ctx.Allocation.Of(OnA));
            var declared = TargetRegistry.All.Select(t => t.Plc).ToList();

            Assert.Equal(hosts, declared.Where(hosts.Contains).ToList());
        }

        [Fact]
        public void Every_host_of_a_domain_shares_that_domain()
        {
            var ctx = Plant(OnA, OnB);
            var of = ctx.Allocation.Of(OnA);
            foreach (var host in ResourceWiringPlanner.RingHostsSharing(ctx, of))
                Assert.Equal(ctx.Rings.DomainOf(of), ctx.Rings.DomainOf(host));
        }

        [Fact]
        public void Renaming_every_component_leaves_the_topology_identical()
        {
            // A ring is a structure. Rename the plant and the same targets host the same number of
            // members in the same order - because nothing keyed on a name.
            var plain = Plant(OnA, OnB);
            var renamed = Plant(OnA, OnB, rename: true);

            var a = ResourceWiringPlanner.RingHostsSharing(plain, plain.Allocation.Of(OnA));
            var b = ResourceWiringPlanner.RingHostsSharing(renamed, renamed.Allocation.Of(OnA));
            Assert.Equal(a, b);

            foreach (var host in a)
                Assert.Equal(
                    ResourceWiringPlanner.RingOf(plain, host, ChainOrder.Application).Count,
                    ResourceWiringPlanner.RingOf(renamed, host, ChainOrder.Application).Count);
        }

        [Fact]
        public void Relocating_a_component_moves_it_between_rings_and_changes_nothing_else()
        {
            // A relocation is a roster decision, so it moves one member from one host's ring to
            // another's. The set of members across all hosts is unchanged.
            var placed = Plant(OnA, OnB);
            var moved = Plant(OnA, OnB, relocate: OnA);

            IReadOnlyList<string> All(GenerationContext c) =>
                TargetRegistry.All.SelectMany(t =>
                        c.Emits(t.Plc)
                            ? ResourceWiringPlanner.RingOf(c, t.Plc, ChainOrder.Resource).Select(m => m.Name)
                            : Enumerable.Empty<string>())
                    .OrderBy(n => n, StringComparer.Ordinal).ToList();

            Assert.Equal(All(placed), All(moved));
            Assert.NotEqual(placed.Allocation.Of(OnA), moved.Allocation.Of(OnA));
        }

        // ---- a plant, built from roster rows -------------------------------------------------------

        private const string ProcA = "Feed_Station";
        private const string ProcB = "Assembly_Station";
        private const string OnA = "Feeder";
        private const string OnB = "Clamp";
        private const string SensorA = "PartInHopper";

        private static GenerationContext Plant(string a, string b, bool rename = false, string? relocate = null)
        {
            var pa = Process("C-a", ProcA);
            var pb = Process("C-b", ProcB);
            var plant = new List<VueOneComponent>
            {
                pa, pb, Sensor("C-s", SensorA),
                Actuator("C-1", a, "C-a", "C-a-s1"),
                Actuator("C-2", b, "C-b", "C-b-s1"),
            };
            if (rename)
                foreach (var c in plant)
                    foreach (var s in c.States)
                        s.Name = "renamed-" + s.Name;

            var profile = relocate == null
                ? DeploymentProfile.AsPlaced(TestConfig.Cfg)
                : DeploymentProfile.Relocating(new[] { relocate }, TestConfig.Cfg);
            return GenerationContext.Plan(TestConfig.Cfg, plant, profile);
        }

        private static VueOneState State(string id, string name, int number, bool stop, bool initial = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = stop, InitialState = initial,
            Transitions = new List<VueOneTransition>(),
        };

        private static void Leads(VueOneState from, VueOneState to, params VueOneCondition[] guard) =>
            from.Transitions.Add(new VueOneTransition
            {
                TransitionID = "T-" + from.StateID + "-" + to.StateID,
                OriginStateID = from.StateID, DestinationStateID = to.StateID,
                Conditions = guard.ToList(),
            });

        private static VueOneComponent Actuator(string id, string name, string proc, string procState)
        {
            var home = State(id + "-s0", "Home", 0, true, initial: true);
            var moving = State(id + "-m", "Moving", 1, stop: false);
            var work = State(id + "-s2", "Work", 2, true);
            Leads(home, moving, new VueOneCondition { ComponentID = proc, ID = procState, Name = "cmd" });
            Leads(moving, work);
            Leads(work, home);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Actuator,
                States = new List<VueOneState> { home, moving, work },
            };
        }

        private static VueOneComponent Sensor(string id, string name)
        {
            var off = State(id + "-0", "Off", 0, true, initial: true);
            var on = State(id + "-1", "On", 1, true);
            Leads(off, on);
            Leads(on, off);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Sensor,
                States = new List<VueOneState> { off, on },
            };
        }

        private static VueOneComponent Process(string id, string name)
        {
            var entry = State(id + "-s0", "Entry", 0, true, initial: true);
            var drive = State(id + "-s1", "Drive", 1, true);
            Leads(entry, drive);
            Leads(drive, entry);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Process,
                States = new List<VueOneState> { entry, drive },
            };
        }
    }
}
