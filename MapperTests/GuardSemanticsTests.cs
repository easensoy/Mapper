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
    /// VueOne nests a guard as ConditionValue -> ConditionGroup* -> Condition*: the groups are
    /// alternatives, the conditions inside one hold together. These pin that the compiler keeps that
    /// meaning rather than flattening it into one long list of sequential waits, and that a combinator it
    /// does not understand stops the compiler instead of being guessed at.
    public sealed class GuardSemanticsTests
    {
        private static VueOneCondition Leaf(string component, string state) =>
            new() { ComponentID = component, ID = state, Name = component + "/" + state };

        private static ConditionExpr.Ref R(string component, string state) =>
            new(Leaf(component, state));

        [Fact]
        public void A_group_of_conditions_is_a_conjunction_and_the_groups_are_alternatives()
        {
            // (a AND b) OR (c)
            var guard = ConditionExpr.Disjunction(new ConditionExpr[]
            {
                ConditionExpr.Conjunction(new ConditionExpr[] { R("A", "s1"), R("B", "s1") })!,
                R("C", "s1"),
            })!;

            var any = Assert.IsType<ConditionExpr.Any>(guard);
            Assert.Equal(2, any.Operands.Count);
            var all = Assert.IsType<ConditionExpr.All>(any.Operands[0]);
            Assert.Equal(2, all.Operands.Count);
            Assert.IsType<ConditionExpr.Ref>(any.Operands[1]);
        }

        [Fact]
        public void Nesting_survives_to_any_depth_and_the_leaves_keep_document_order()
        {
            var guard = ConditionExpr.Disjunction(new ConditionExpr[]
            {
                ConditionExpr.Conjunction(new ConditionExpr[]
                {
                    R("A", "s1"),
                    ConditionExpr.Disjunction(new ConditionExpr[] { R("B", "s1"), R("C", "s1") })!,
                })!,
                R("D", "s1"),
            })!;

            Assert.Equal(
                new[] { "A/s1", "B/s1", "C/s1", "D/s1" },
                guard.References().Select(c => c.Name));
            Assert.True(guard.HasAlternatives);
        }

        [Fact]
        public void A_single_operand_is_that_operand_so_a_plain_guard_is_never_wrapped()
        {
            var one = ConditionExpr.Conjunction(new ConditionExpr[] { R("A", "s1") });
            Assert.IsType<ConditionExpr.Ref>(one);
            Assert.False(one!.HasAlternatives);
        }

        [Fact]
        public void A_guard_with_no_alternatives_is_not_reported_as_offering_a_choice()
        {
            var guard = ConditionExpr.Conjunction(new ConditionExpr[] { R("A", "s1"), R("B", "s1") })!;
            Assert.False(guard.HasAlternatives);
        }

        [Fact]
        public void A_bare_condition_list_means_all_of_them()
        {
            var flat = ConditionExpr.FromFlat(new[] { Leaf("A", "s1"), Leaf("B", "s1") });
            Assert.IsType<ConditionExpr.All>(flat);
            Assert.Equal(2, flat!.References().Count);
        }

        [Fact]
        public void A_group_combinator_the_compiler_does_not_understand_stops_it_before_any_file_is_written()
        {
            // VueOne leaves ConditionGroup@Operator empty. A populated one is a combinator this compiler
            // has no reading for, and guessing would silently change what a guard means.
            string xml = Twin(groupOperator: "XOR");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "mapper-guard-" + Guid.NewGuid().ToString("N") + ".xml");
            System.IO.File.WriteAllText(path, xml);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => new CodeGen.IO.SystemXmlReader().ReadAllComponents(path));
                Assert.Contains("XOR", ex.Message, StringComparison.Ordinal);
                Assert.Contains("ConditionGroup", ex.Message, StringComparison.Ordinal);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void The_same_twin_with_no_group_operator_reads_as_one_alternative_per_group()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "mapper-guard-" + Guid.NewGuid().ToString("N") + ".xml");
            System.IO.File.WriteAllText(path, Twin(groupOperator: ""));
            try
            {
                var read = new CodeGen.IO.SystemXmlReader().ReadAllComponents(path);
                var guard = read.Single().States.First().Transitions.Single().Guard;
                var any = Assert.IsType<ConditionExpr.Any>(guard);
                Assert.Equal(2, any.Operands.Count);
                Assert.Equal(new[] { "P/one", "P/two" }, guard!.References().Select(c => c.Name));
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void Alternatives_a_linear_recipe_cannot_choose_between_are_required_together_and_reported()
        {
            // The engine tests one (slot, value) per row, so it cannot express "either". Requiring both
            // can only make the step wait LONGER than the twin asks, never release it earlier - the safe
            // direction - and the choice the recipe could not make is reported rather than lost.
            VueOneState Stop(string id, string n, int num, bool init = false) => new()
            {
                StateID = id, Name = n, StateNumber = num, StaticState = true, InitialState = init,
                Transitions = new List<VueOneTransition>(),
            };
            VueOneComponent Sensor(string id, string n) => new()
            {
                ComponentID = id, Name = n, Type = ComponentType.Sensor,
                States = new List<VueOneState> { Stop(id + "-off", n + "_Off", 0, true), Stop(id + "-on", n + "_On", 1) },
            };

            var left = Sensor("C-l", "Left_Eye");
            var right = Sensor("C-r", "Right_Eye");
            var proc = new VueOneComponent
            {
                ComponentID = "C-cell", Name = "Sorter_Cell", Type = ComponentType.Process,
                States = new List<VueOneState>
                {
                    Stop("C-cell-s0", "Cell_Entry", 0, init: true), Stop("C-cell-s1", "Cell_Run", 1),
                },
            };
            // EITHER eye releases the step, which is what the twin says and what a row cannot say.
            proc.States[0].Transitions.Add(new VueOneTransition
            {
                TransitionID = "T-cell", OriginStateID = "C-cell-s0", DestinationStateID = "C-cell-s1",
                Guard = ConditionExpr.Disjunction(new ConditionExpr[]
                {
                    R("C-l", "C-l-on"), R("C-r", "C-r-on"),
                }),
            });

            var plan = GenerationContext.Plan(new MapperConfig(), new[] { proc, left, right },
                DeploymentProfile.M262Only(LayoutCatalog.Load()));

            // Both alternatives are required...
            var recipe = plan.Recipes["Sorter_Cell"];
            Assert.Equal(2, recipe.Wait1Id.Where((_, i) => recipe.StepType[i] == 2).Distinct().Count());
            // ...and the compiler says so, rather than leaving the choice silently discarded.
            Assert.Contains(plan.SemanticFindings, f =>
                f.Contains("Sorter_Cell", StringComparison.Ordinal) &&
                f.Contains("alternative", StringComparison.OrdinalIgnoreCase));
        }

        // Two ConditionGroups under one ConditionValue: the shape the shipped twins use for a choice.
        private static string Twin(string groupOperator) => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<vueOne_SystemDefinition Type=""System"">
  <System>
    <Name>GuardFixture</Name>
    <SystemID>SYS-guard</SystemID>
    <Component>
      <ComponentID>C-p</ComponentID>
      <Name>Loader</Name>
      <Type>Actuator</Type>
      <State>
        <StateID>S-0</StateID><Name>Rest</Name><State_Number>0</State_Number>
        <Initial_State>true</Initial_State><StaticState>true</StaticState>
        <Transition>
          <TransitionID>T-0</TransitionID><Type>SINGLE</Type>
          <Origin_State>S-0</Origin_State><Destination_State>S-1</Destination_State>
          <Sequence_Condition>
            <ConditionValue>
              <ConditionGroup Operator=""{groupOperator}"" GroupName=""Group_1"">
                <Condition Operator="""" ID=""S-a"" Name=""P/one"" ComponentID=""C-x"" />
              </ConditionGroup>
              <ConditionGroup Operator=""{groupOperator}"" GroupName=""Group_2"">
                <Condition Operator="""" ID=""S-b"" Name=""P/two"" ComponentID=""C-x"" />
              </ConditionGroup>
            </ConditionValue>
          </Sequence_Condition>
          <Priority>0</Priority>
        </Transition>
      </State>
      <State>
        <StateID>S-1</StateID><Name>Done</Name><State_Number>2</State_Number>
        <StaticState>true</StaticState>
      </State>
    </Component>
  </System>
</vueOne_SystemDefinition>";
    }
}
