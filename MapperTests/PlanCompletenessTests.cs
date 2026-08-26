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
    /// An emitter renders the plan. If the plan is missing an answer the emitter has to invent one, and an
    /// invented answer is exactly what stops a compiler being a compiler. These pin that the plan answers
    /// every question the backend asks, for a plant nothing in the code has heard of.
    public sealed class PlanCompletenessTests
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

        private static GenerationContext UnfamiliarPlant()
        {
            var proc = new VueOneComponent
            {
                ComponentID = "C-line", Name = "Kiln_Line", Type = ComponentType.Process,
                States = new List<VueOneState>
                {
                    State("C-line-s0", "Kiln_Entry", 0, true, initial: true),
                    State("C-line-s1", "Kiln_Charge", 1, true),
                    State("C-line-s2", "Kiln_Discharge", 2, true),
                },
            };
            var act = new VueOneComponent
            {
                ComponentID = "C-ram", Name = "Charge_Ram", Type = ComponentType.Actuator,
                States = new List<VueOneState>
                {
                    State("C-ram-s0", "Ram_Back", 0, true, initial: true),
                    State("C-ram-m", "Ram_Moving", 1, stop: false),
                    State("C-ram-s2", "Ram_Forward", 2, true),
                },
            };
            Leads(act.States[0], act.States[1], On("C-line", "C-line-s1", "Kiln_Line/Kiln_Charge"));
            Leads(act.States[1], act.States[2]);
            Leads(act.States[2], act.States[0], On("C-line", "C-line-s2", "Kiln_Line/Kiln_Discharge"));
            Leads(proc.States[0], proc.States[1]);
            Leads(proc.States[1], proc.States[2], On("C-ram", "C-ram-s2", "Charge_Ram/Ram_Forward"));

            return GenerationContext.Plan(TestConfig.Cfg, new[] { proc, act },
                DeploymentProfile.AsPlaced(TestConfig.Cfg));
        }

        [Fact]
        public void The_plan_answers_every_question_the_backend_asks_of_an_actuator()
        {
            var plan = UnfamiliarPlant();

            foreach (var actuator in plan.Station.Actuators)
            {
                var name = actuator.Name.Trim();
                Assert.True(plan.CatTypes.ContainsKey(name), $"no CAT chosen for '{name}'");
                Assert.True(plan.Slots.ContainsKey(name), $"no state_table slot for '{name}'");
                Assert.True(plan.Interlocks.ContainsKey(name), $"no interlock plan for '{name}'");
                Assert.True(plan.ActuatorTiming.ContainsKey(name), $"no motion contract for '{name}'");

                var timing = plan.ActuatorTiming[name];
                Assert.True(timing.ToWorkMs > 0 && timing.ToHomeMs > 0, "a leg with no duration cannot time out");
                Assert.Equal(timing.ToWorkMs * 2, timing.FaultWorkMs);
            }
        }

        [Fact]
        public void The_plan_answers_every_question_the_backend_asks_of_a_process()
        {
            var plan = UnfamiliarPlant();

            foreach (var process in plan.Station.Processes)
            {
                Assert.True(plan.Slots.ContainsKey(process), $"no state_table slot for '{process}'");
                Assert.True(plan.Recipes.ContainsKey(process), $"no compiled recipe for '{process}'");
                var recipe = plan.Recipes[process];
                Assert.NotEmpty(recipe.StepType);
                Assert.Equal(9, recipe.StepType[^1]);   // terminated, so the engine cannot run past the end
            }
        }

        [Fact]
        public void The_parameter_set_an_instance_carries_is_the_one_its_CAT_declares()
        {
            var plan = UnfamiliarPlant();
            var actuator = plan.Station.Actuators.Single();
            var cat = plan.CatTypes[actuator.Name.Trim()];
            var declared = TemplateManifest.Find(cat);

            var p = SystemInjector.BuildActuatorParameters(
                actuator, plan.Slots[actuator.Name.Trim()], cat, plan);

            // Identity is always carried; the rest is what the CAT says it accepts.
            Assert.True(p.ContainsKey("actuator_name"));
            Assert.True(p.ContainsKey("actuator_id"));
            Assert.Equal(declared!.SensorTimed, p.ContainsKey("WorkSensorFitted"));
            Assert.Equal(declared.SensorTimed, p.ContainsKey("toWorkTime"));

            var protocol = TemplateManifest.ProtocolOrNull(cat);
            bool declaresTargets = protocol?.Target is { Count: > 0 };
            Assert.Equal(declaresTargets, p.ContainsKey("Target") || p.ContainsKey("TargetHomeState"));
            Assert.Equal(protocol?.CrossesBothWays == true, p.ContainsKey("faultTimeoutWork1"));
        }

        [Fact]
        public void An_unfamiliar_plant_plans_without_naming_a_controller_or_a_familiar_instance()
        {
            var plan = UnfamiliarPlant();

            // It planned at all, and every reporter got a distinct slot on its ring.
            Assert.Single(plan.Recipes);
            Assert.Equal(plan.Slots.Count, plan.Slots.Values.Distinct().Count());
            // The recipe commands the actuator this plant declares, by its own name.
            Assert.Contains(plan.Recipes["Kiln_Line"].CmdTargetName,
                n => string.Equals(n, TemplateMap.RingKey("Charge_Ram"), StringComparison.Ordinal));
        }
    }
}
