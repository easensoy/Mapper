using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Domain.Twin;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// A model the backend cannot render has to stop the compiler, not produce an artefact that looks
    /// right and behaves wrongly on a rig. Each of these is a model defect the compiler must refuse, and
    /// every one is checked through GenerationContext.Plan, which GenerateProject.Execute calls BEFORE it
    /// touches the tree - so a refusal here is a refusal before any file exists.
    public sealed class NegativeFixtureTests
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

        private static VueOneCondition On(string componentId, string stateId, string label) =>
            new() { ComponentID = componentId, ID = stateId, Name = label };

        private static VueOneComponent Process(string id, string name, int steps)
        {
            var states = new List<VueOneState> { State(id + "-s0", name + "_Entry", 0, true, initial: true) };
            for (int i = 1; i <= steps; i++) states.Add(State($"{id}-s{i}", $"{name}_Step{i}", i, true));
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = ComponentType.Process, States = states };
        }

        private static VueOneComponent Actuator(string id, string name, int stops)
        {
            var states = new List<VueOneState> { State(id + "-s0", name + "_Home", 0, true, initial: true) };
            states.Add(State(id + "-m", name + "_Moving", 1, stop: false));
            for (int i = 1; i < stops; i++) states.Add(State($"{id}-s{i}", $"{name}_Stop{i}", i * 2, true));
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = ComponentType.Actuator, States = states };
        }

        private static GenerationContext Compile(params VueOneComponent[] plant) =>
            GenerationContext.Plan(new MapperConfig(), plant, DeploymentProfile.M262Only(LayoutCatalog.Load()));

        // A plant that DOES compile, so each fixture below differs from it in exactly one defect.
        private static List<VueOneComponent> Working()
        {
            var proc = Process("C-p", "Line", 2);
            var act = Actuator("C-a", "Pusher", 2);
            Leads(act.States[0], act.States[1], On("C-p", "C-p-s1", "Line/Step1"));
            Leads(act.States[1], act.States[2]);
            Leads(act.States[2], act.States[0], On("C-p", "C-p-s2", "Line/Step2"));
            Leads(proc.States[0], proc.States[1]);
            Leads(proc.States[1], proc.States[2], On("C-a", "C-a-s1", "Pusher/Stop1"));
            return new List<VueOneComponent> { proc, act };
        }

        [Fact]
        public void The_baseline_plant_compiles_so_each_fixture_differs_by_one_defect()
        {
            var plan = Compile(Working().ToArray());
            Assert.Single(plan.Recipes);
        }

        [Fact]
        public void A_condition_naming_a_component_the_model_does_not_declare_is_refused()
        {
            var plant = Working();
            Leads(plant[0].States[2], plant[0].States[0], On("C-ghost", "C-ghost-s0", "Ghost/Idle"));

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.ToArray()));
            Assert.Contains("[Twin]", ex.Message, StringComparison.Ordinal);
            Assert.Contains("C-ghost", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_condition_naming_a_state_its_target_does_not_declare_is_refused()
        {
            var plant = Working();
            Leads(plant[0].States[2], plant[0].States[0], On("C-a", "S-not-a-state", "Pusher/Nowhere"));

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.ToArray()));
            Assert.Contains("[Twin]", ex.Message, StringComparison.Ordinal);
            Assert.Contains("S-not-a-state", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_transition_leaving_for_a_state_its_component_does_not_declare_is_refused()
        {
            var plant = Working();
            plant[1].States[2].Transitions.Add(new VueOneTransition
            {
                TransitionID = "T-dangling", OriginStateID = plant[1].States[2].StateID,
                DestinationStateID = "S-missing", Conditions = new List<VueOneCondition>(),
            });

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.ToArray()));
            Assert.Contains("[Twin]", ex.Message, StringComparison.Ordinal);
            Assert.Contains("S-missing", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_state_graph_no_CAT_protocol_serves_is_refused_rather_than_defaulted()
        {
            var plant = Working();
            // Nine stops: no protocol row in Config/smc-rig.yml declares that shape.
            var odd = Actuator("C-odd", "Odd", 9);
            Leads(odd.States[0], odd.States[1], On("C-p", "C-p-s1", "Line/Step1"));
            plant.Add(odd);

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.ToArray()));
            Assert.Contains("[CAT]", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Odd", ex.Message, StringComparison.Ordinal);
            // The refusal has to say what would have to change, not merely that it failed.
            Assert.Contains("protocol", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_stop_the_selected_CAT_cannot_be_commanded_to_is_refused()
        {
            var plant = Working();
            // A settled stop the one-work-stop protocol has no command for: the CAT is incompatible with
            // what this transition asks of it.
            var act = plant[1];
            var extra = State("C-a-s9", "Pusher_Third", 6, stop: true);
            act.States.Add(extra);
            Leads(act.States[2], extra, On("C-p", "C-p-s2", "Line/Step2"));

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.ToArray()));
            Assert.Contains("Pusher", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_interlock_naming_a_source_this_plan_cannot_report_is_refused()
        {
            // A rule reads state_table[SourceID], which only a RING REPORTER writes. A process is not
            // one: it commands the ring, it does not publish its own component state onto it. Guarding
            // on a process phase would therefore pass on whatever the table happens to hold, so the
            // compiler refuses rather than shipping a safety net that is not one.
            var plant = Working();
            plant[1].States[1].InterlockConditions = new[]
            {
                On("C-p", "C-p-s1", "Line/Step1"),
            };

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.ToArray()));
            Assert.Contains("Interlock", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Line", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_refusal_happens_before_the_generator_touches_a_tree()
        {
            // The property that makes the refusals above safe: planning reads the model and the declared
            // catalogs and writes nothing, so a model defect cannot leave a half-generated project. The
            // directory a generation would write into stays as it was found.
            var probe = Path.Combine(Path.GetTempPath(), "mapper-plan-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            try
            {
                var plant = Working();
                Leads(plant[0].States[2], plant[0].States[0], On("C-ghost", "C-ghost-s0", "Ghost/Idle"));
                var cfg = new MapperConfig { SyslayPath2 = Path.Combine(probe, "app.syslay") };

                Assert.Throws<InvalidOperationException>(() => GenerationContext.Plan(
                    cfg, plant, DeploymentProfile.M262Only(LayoutCatalog.Load())));

                Assert.Empty(Directory.EnumerateFileSystemEntries(probe));
            }
            finally { Directory.Delete(probe, recursive: true); }
        }
    }
}
