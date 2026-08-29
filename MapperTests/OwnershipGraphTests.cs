using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Domain.Twin;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    // Placement decides which controller drives a component, so it is safety-relevant. These pin the two
    // outcomes that must never be silent: a contradiction must throw, and a thing the model does not
    // anchor must land on the one declared default rather than on whatever a call site reached for.
    public class OwnershipGraphTests
    {
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

        private static VueOneComponent Process(string id, string name)
        {
            var only = Stop(id + "-s0", "Initialisation", 0);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = "Process",
                States = new List<VueOneState> { only },
            };
        }

        private static TwinModel Twin(params VueOneComponent[] cs) => TwinModel.Build(cs, TestConfig.Cfg.Twin);


        private static PlcAssignment Placed(IReadOnlyDictionary<string, PlcAssignment> g, string name) =>
            g.TryGetValue(name, out var t) ? t : PlcAssignment.Unknown;

        [Fact]
        public void A_process_inherits_the_target_of_a_pinned_component_it_commands()
        {
            var p = Process("C-p", "Fitting");
            var a = Actuator("C-a", "Clamp_X", "C-p", "C-p-s0");
            var pins = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase)
                { ["Clamp_X"] = PlcAssignment.Named("M580") };

            var g = DeploymentRoster.ResolvePlacement(Twin(p, a),
                n => pins.TryGetValue(n, out var t) ? t : PlcAssignment.Unknown,
                PlcAssignment.Named("M262"));

            Assert.Equal(PlcAssignment.Named("M580"), Placed(g, "Fitting"));
            Assert.Equal(PlcAssignment.Named("M580"), Placed(g, "Clamp_X"));
        }

        [Fact]
        public void A_component_inherits_the_target_of_the_process_that_commands_it()
        {
            var p = Process("C-p", "Fitting");
            var a = Actuator("C-a", "Clamp_X", "C-p", "C-p-s0");
            var pins = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase)
                { ["Fitting"] = PlcAssignment.Named("BX1") };

            var g = DeploymentRoster.ResolvePlacement(Twin(p, a),
                n => pins.TryGetValue(n, out var t) ? t : PlcAssignment.Unknown,
                PlcAssignment.Named("M262"));

            Assert.Equal(PlcAssignment.Named("BX1"), Placed(g, "Clamp_X"));
        }

        [Fact]
        public void A_process_commanding_components_pinned_to_two_targets_fails_and_names_them()
        {
            var p = Process("C-p", "Fitting");
            var a = Actuator("C-a", "Clamp_X", "C-p", "C-p-s0");
            var b = Actuator("C-b", "Clamp_Y", "C-p", "C-p-s0");
            var pins = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Clamp_X"] = PlcAssignment.Named("M580"),
                ["Clamp_Y"] = PlcAssignment.Named("BX1"),
            };

            var ex = Assert.Throws<InvalidOperationException>(() => DeploymentRoster.ResolvePlacement(
                Twin(p, a, b),
                n => pins.TryGetValue(n, out var t) ? t : PlcAssignment.Unknown,
                PlcAssignment.Named("M262")));

            Assert.Contains("Fitting", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Clamp_X", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Clamp_Y", ex.Message, StringComparison.Ordinal);
            Assert.Contains("M580", ex.Message, StringComparison.Ordinal);
            Assert.Contains("BX1", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_component_commanded_by_two_processes_on_different_targets_fails_and_names_them()
        {
            var p1 = Process("C-p1", "Fitting");
            var p2 = Process("C-p2", "Packing");
            // One actuator, two commanding processes, each pinned to a different controller.
            var shared = Actuator("C-a", "Clamp_X", "C-p1", "C-p1-s0");
            shared.States[0].Transitions.Add(new VueOneTransition
            {
                DestinationStateID = shared.States[1].StateID,
                Conditions = new List<VueOneCondition>
                {
                    new() { ComponentID = "C-p2", ID = "C-p2-s0", Name = "cmd" },
                },
            });
            var pins = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fitting"] = PlcAssignment.Named("M580"),
                ["Packing"] = PlcAssignment.Named("BX1"),
            };

            var ex = Assert.Throws<InvalidOperationException>(() => DeploymentRoster.ResolvePlacement(
                Twin(p1, p2, shared),
                n => pins.TryGetValue(n, out var t) ? t : PlcAssignment.Unknown,
                PlcAssignment.Named("M262")));

            Assert.Contains("Clamp_X", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Fitting", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Packing", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unanchored_process_takes_the_declared_default_not_a_guess()
        {
            var p = Process("C-p", "Orphan_Station");

            var g = DeploymentRoster.ResolvePlacement(Twin(p),
                _ => PlcAssignment.Unknown,
                PlcAssignment.Named("BX1"));

            Assert.Equal(PlcAssignment.Named("BX1"), Placed(g, "Orphan_Station"));
        }

        [Fact]
        public void A_command_relationship_outranks_an_observation()
        {
            // Commanded by a process on M580, observed by one on BX1: it runs where it is COMMANDED.
            var commander = Process("C-p1", "Fitting");
            var observer = Process("C-p2", "Watcher");
            var a = Actuator("C-a", "Clamp_X", "C-p1", "C-p1-s0");
            observer.States[0].Transitions.Add(new VueOneTransition
            {
                DestinationStateID = observer.States[0].StateID,
                Conditions = new List<VueOneCondition>
                {
                    new() { ComponentID = "C-a", ID = "C-a-work", Name = "observe" },
                },
            });
            var pins = new Dictionary<string, PlcAssignment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fitting"] = PlcAssignment.Named("M580"),
                ["Watcher"] = PlcAssignment.Named("BX1"),
            };

            var g = DeploymentRoster.ResolvePlacement(Twin(commander, observer, a),
                n => pins.TryGetValue(n, out var t) ? t : PlcAssignment.Unknown,
                PlcAssignment.Named("M262"));

            Assert.Equal(PlcAssignment.Named("M580"), Placed(g, "Clamp_X"));
        }
    }
}
