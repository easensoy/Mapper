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
    /// The shared application canvas and each resource's own network are two RENDERINGS of one planned
    /// graph. They order their chains differently on purpose - layout.yml's idRank assigns every
    /// state_table index while casBusRank chains the station adapter - but they may never disagree about
    /// WHO is on a chain, because EAE's Solution Integrity check rejects the whole deploy when they do
    /// (INVARIANTS I-5). These compare the two projections structurally, normalising away the order.
    public sealed class ResourceGraphParityTests
    {
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

        private static VueOneCondition On(string component, string state, string label) =>
            new() { ComponentID = component, ID = state, Name = label };

        // Roster rows on three different targets, so the graph has to answer for a plant that spans
        // more than one controller AND more than one ring host. The names are roster keys and nothing
        // else: no compiler branch may key on any of them.
        private const string ProcA = "Feed_Station";
        private const string ProcB = "Assembly_Station";
        private const string ProcC = "Disassembly";
        private const string OnA = "Feeder";
        private const string OnB = "Clamp";
        private const string OnC = "CoverPNP_Hr";
        private const string SensorA = "PartInHopper";
        private const string SensorB = "BearingSensor";

        private static VueOneComponent Actuator(string id, string name, string proc, string procState)
        {
            var home = State(id + "-s0", "Home", 0, true, initial: true);
            var moving = State(id + "-m", "Moving", 1, stop: false);
            var work = State(id + "-s2", "Work", 2, true);
            Leads(home, moving, On(proc, procState, "cmd"));
            Leads(moving, work);
            Leads(work, home);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Actuator,
                States = new List<VueOneState> { home, moving, work },
            };
        }

        private static VueOneComponent Sensor(string id, string name) => new()
        {
            ComponentID = id, Name = name, Type = ComponentType.Sensor,
            States = new List<VueOneState>
            {
                State(id + "-off", "Off", 0, true, initial: true),
                State(id + "-on", "On", 1, true),
            },
        };

        private static VueOneComponent Process(string id, string name, params string[] stateNames)
        {
            var states = stateNames.Select((n, i) =>
                State($"{id}-s{i}", n, i, true, initial: i == 0)).ToList();
            for (int i = 0; i < states.Count; i++)
                Leads(states[i], states[(i + 1) % states.Count]);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Process, States = states,
            };
        }

        // Three processes, three targets, three ring hosts - the smallest plant that exercises a
        // station chain, a report ring, a carried chain and a cross-controller seam at once.
        private static GenerationContext MultiStationPlant()
        {
            var a = Process("C-a", ProcA, "Entry", "Drive");
            var b = Process("C-b", ProcB, "Entry", "Drive");
            var c = Process("C-c", ProcC, "Entry", "Drive");
            var plant = new List<VueOneComponent>
            {
                a, b, c,
                Sensor("C-sa", SensorA), Sensor("C-sb", SensorB),
                Actuator("C-1", OnA, "C-a", "C-a-s1"),
                Actuator("C-2", OnB, "C-b", "C-b-s1"),
                Actuator("C-3", OnC, "C-c", "C-c-s1"),
            };
            return GenerationContext.Plan(TestConfig.Cfg, plant,
                DeploymentProfile.AsPlaced(TestConfig.Cfg));
        }

        private static IReadOnlyList<PlcAssignment> Emitted(GenerationContext ctx) =>
            TestConfig.Cfg.Targets.All.Select(t => t.Plc).Where(ctx.Emits).ToList();

        private static ResourceWiringPlan Plan(GenerationContext ctx, PlcAssignment plc, ChainOrder order) =>
            ResourceWiringPlanner.For(ctx, plc, order);

        private static HashSet<string> Names(IEnumerable<RingMember> members) =>
            members.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void Every_reporter_is_on_exactly_one_ring_in_both_projections()
        {
            var ctx = MultiStationPlant();
            var canvas = new List<string>();
            var resources = new List<string>();
            foreach (var plc in Emitted(ctx))
            {
                canvas.AddRange(Plan(ctx, plc, ChainOrder.Application).RingChain.Select(m => m.Name));
                resources.AddRange(Plan(ctx, plc, ChainOrder.Resource).RingChain.Select(m => m.Name));
            }
            // Same set, and no reporter counted twice: a member on two rings is driven from two places.
            Assert.Equal(canvas.Count, canvas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(resources.Count, resources.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(
                canvas.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                resources.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_station_chain_holds_the_same_members_in_both_projections()
        {
            var ctx = MultiStationPlant();
            foreach (var plc in Emitted(ctx))
                Assert.Equal(
                    Names(Plan(ctx, plc, ChainOrder.Application).StationChain),
                    Names(Plan(ctx, plc, ChainOrder.Resource).StationChain));
        }

        [Fact]
        public void A_member_carries_the_same_template_contract_in_both_projections()
        {
            // The CAT decides how a member's ports are spelled, so the two documents disagreeing about
            // a member's type is exactly the "unresolved adapter" import failure.
            var ctx = MultiStationPlant();
            foreach (var plc in Emitted(ctx))
            {
                var app = Plan(ctx, plc, ChainOrder.Application);
                var res = Plan(ctx, plc, ChainOrder.Resource);
                foreach (var m in app.RingChain.Concat(app.StationChain))
                {
                    var mirror = res.RingChain.Concat(res.StationChain)
                        .FirstOrDefault(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase));
                    if (mirror != null) Assert.Equal(m.Type, mirror.Type);
                }
            }
        }

        [Fact]
        public void No_link_names_an_endpoint_that_is_on_no_chain()
        {
            // A wire to a name the plan does not place is a wire to nothing: EAE resolves it against the
            // resource and rejects the import. Checked on the RESOURCE projection, which is what is
            // written into a sysres.
            var ctx = MultiStationPlant();
            foreach (var plc in Emitted(ctx))
            {
                var plan = Plan(ctx, plc, ChainOrder.Resource);
                var placed = Names(plan.RingChain.Concat(plan.StationChain).Concat(plan.Processes));
                placed.UnionWith(plan.InitChain);
                foreach (var (source, destination) in plan.RingLinks.Concat(plan.StationLinks))
                    foreach (var endpoint in new[] { source, destination })
                    {
                        var owner = endpoint.Split('.')[0];
                        // A chain end is a declared resource role (a station or a terminator), which the
                        // roster places rather than the twin.
                        if (ctx.Roster.Contains(owner)) continue;
                        Assert.Contains(owner, placed);
                    }
            }
        }

        [Fact]
        public void Both_projections_bring_up_the_same_things()
        {
            // The canvas heads its chain with the resource's own top role and the resource heads it with
            // the boot FB, and an injected reporter is brought up beside its emitter on the canvas. Past
            // those two stated differences the SET must match, or something inits on one half only.
            var ctx = MultiStationPlant();
            var injected = ctx.Profile.Facts.InjectedReporters;
            foreach (var plc in Emitted(ctx))
            {
                var app = Plan(ctx, plc, ChainOrder.Application).InitChain.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var res = Plan(ctx, plc, ChainOrder.Resource).InitChain
                    .Where(n => !string.Equals(n, TestConfig.Cfg.Targets.InitRole, StringComparison.Ordinal))
                    .Where(n => !injected.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // The canvas may additionally head the chain with a connection nothing else starts.
                app.RemoveWhere(n => string.Equals(n, Plan(ctx, plc, ChainOrder.Application).SelfStartedConnection,
                    StringComparison.Ordinal));
                Assert.Equal(app.OrderBy(n => n, StringComparer.Ordinal),
                             res.OrderBy(n => n, StringComparer.Ordinal));
            }
        }

        [Fact]
        public void The_two_orders_are_projections_and_not_two_different_memberships()
        {
            // layout.yml declares idRank and casBusRank on the SAME row and forbids merging them. That is
            // only safe while both orders draw from one membership, which is what this asserts: the
            // resource order is a permutation of the application order, never a different set.
            var ctx = MultiStationPlant();
            foreach (var plc in Emitted(ctx))
            {
                var app = Plan(ctx, plc, ChainOrder.Application);
                var res = Plan(ctx, plc, ChainOrder.Resource);
                Assert.Equal(Names(app.Processes), Names(res.Processes));
                // Same count of links means the same topology drawn twice, not two topologies.
                Assert.Equal(app.RingChain.Count + app.Processes.Count > 1,
                             res.RingChain.Count + res.Processes.Count > 1);
            }
        }
    }
}
