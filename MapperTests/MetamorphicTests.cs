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
    /// A compiler that reads the model cannot care what the model CALLS things. These build a plant in
    /// code, compile it, then compile a systematically renamed copy with the identical graph and require
    /// the two plans to be the same plan under the renaming. A name test anywhere in the compiler shows
    /// up here as a difference, because a renamed plant is exactly the case a name test cannot survive.
    public sealed class MetamorphicTests
    {
        // ---- a plant, written as the twin writes one -------------------------------------------------

        // A STOP is a place the plant rests; a MOTION state is passed through. The twin says which is
        // which with <StaticState>, and the compiler drives to stops, never to a state in transit.
        private static VueOneState Stop(string id, string name, int number, bool initial = false) =>
            State(id, name, number, isStop: true, initial);

        private static VueOneState Moving(string id, string name, int number) =>
            State(id, name, number, isStop: false);

        private static VueOneState State(string id, string name, int number, bool isStop, bool initial = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = isStop, InitialState = initial,
            Transitions = new List<VueOneTransition>(),
        };

        private static void Leads(VueOneState from, VueOneState to, params VueOneCondition[] guard) =>
            from.Transitions.Add(new VueOneTransition
            {
                TransitionID = "T-" + from.StateID + "-" + to.StateID,
                OriginStateID = from.StateID,
                DestinationStateID = to.StateID,
                Conditions = guard.ToList(),
            });

        private static VueOneCondition On(VueOneComponent c, VueOneState s) => new()
        {
            ComponentID = c.ComponentID, ID = s.StateID, Name = c.Name + "/" + s.Name,
        };

        // Three stops: the shape Config/smc-rig.yml declares a one-work-stop CAT for.
        private static VueOneComponent Actuator(string id, string name)
        {
            var home = Stop(id + "-s0", name + "_Home", 0, initial: true);
            var moving = Moving(id + "-s1", name + "_Moving", 1);
            var work = Stop(id + "-s2", name + "_Work", 2);
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
                Stop(id + "-off", name + "_Off", 0, initial: true), Stop(id + "-on", name + "_On", 1),
            },
        };

        private static VueOneComponent Process(string id, string name, int steps)
        {
            var states = new List<VueOneState> { Stop(id + "-s0", name + "_Entry", 0, initial: true) };
            for (int i = 1; i <= steps; i++) states.Add(Stop($"{id}-s{i}", $"{name}_Step{i}", i));
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Process, States = states,
            };
        }

        // One process driving one actuator and watching one sensor: the smallest thing with a recipe.
        // The actuator's own transition naming Process/State is the model saying that state commands it.
        private static List<VueOneComponent> Cell(string tag)
        {
            var proc = Process($"C-{tag}-p", $"{tag}_Line", 2);
            var act = Actuator($"C-{tag}-a", $"{tag}_Pusher");
            var sen = Sensor($"C-{tag}-s", $"{tag}_Present");

            Leads(act.States[0], act.States[1], On(proc, proc.States[1]));   // step 1 drives it out
            Leads(act.States[1], act.States[2]);
            Leads(act.States[2], act.States[0], On(proc, proc.States[2]));   // step 2 drives it back

            Leads(proc.States[0], proc.States[1], On(sen, sen.States[1]));   // start on material
            Leads(proc.States[1], proc.States[2], On(act, act.States[2]));   // wait for it to arrive
            return new List<VueOneComponent> { proc, act, sen };
        }

        private static GenerationContext Compile(IReadOnlyList<VueOneComponent> plant) =>
            GenerationContext.Plan(new MapperConfig(), plant, DeploymentProfile.M262Only(LayoutCatalog.Load()));

        // ---- the renaming ----------------------------------------------------------------------------

        // Every component, process and state renamed; ids, types, numbers and edges untouched. A pure
        // relabelling: the same plant, spelled differently.
        private static (List<VueOneComponent> Plant, Dictionary<string, string> Names) Rename(
            IReadOnlyList<VueOneComponent> plant)
        {
            string Swap(string n) => "Zx" + new string(n.Reverse().ToArray());
            var names = plant.ToDictionary(c => c.Name, c => Swap(c.Name), StringComparer.Ordinal);

            var copy = plant.Select(c => new VueOneComponent
            {
                ComponentID = c.ComponentID, Name = Swap(c.Name), Type = c.Type, VcID = c.VcID,
                States = c.States.Select(s => new VueOneState
                {
                    StateID = s.StateID, Name = Swap(s.Name), StateNumber = s.StateNumber,
                    InitialState = s.InitialState, StaticState = s.StaticState, Position = s.Position,
                    Time = s.Time, Counter = s.Counter,
                    Transitions = s.Transitions.Select(t => new VueOneTransition
                    {
                        TransitionID = t.TransitionID, OriginStateID = t.OriginStateID,
                        DestinationStateID = t.DestinationStateID, Priority = t.Priority,
                        TransitionType = t.TransitionType,
                        Conditions = t.Conditions.Select(x => new VueOneCondition
                        {
                            ComponentID = x.ComponentID, ID = x.ID, Operator = x.Operator,
                            Name = Swap(x.Name),
                        }).ToList(),
                    }).ToList(),
                }).ToList(),
            }).ToList();
            return (copy, names);
        }

        // A recipe compared symbolically: the row shape as-is, and every name mapped through the renaming
        // so the two plans are compared as PLANS, not as strings.
        private static List<string> Shape(GenerationContext ctx, string process,
            IReadOnlyDictionary<string, string> rename, IReadOnlyDictionary<int, int> slot)
        {
            var r = ctx.Recipes[process];
            var rows = new List<string>();
            for (int i = 0; i < r.StepType.Count; i++)
                rows.Add(string.Join("|",
                    r.StepType[i],
                    // An actuator is addressed by its lowercased ring key and a sensor refresh by its
                    // verbatim name, so both sides are canonicalised before comparing: the CASE is a
                    // CAT parameter convention, not a difference between two plans.
                    TemplateMap.RingKey(rename.TryGetValue(r.CmdTargetName[i], out var t)
                        ? t : r.CmdTargetName[i]),
                    r.CmdStateArr[i],
                    slot.TryGetValue(r.Wait1Id[i], out var s) ? s : r.Wait1Id[i],
                    r.Wait1State[i],
                    r.NextStep[i]));
            return rows;
        }

        // ---- the properties --------------------------------------------------------------------------

        [Fact]
        public void A_renamed_plant_compiles_to_the_same_plan()
        {
            var plant = Cell("Alpha");
            var (renamed, names) = Rename(plant);

            var before = Compile(plant);
            var after = Compile(renamed);

            // Ring keys are lowercased, so the recipe's CmdTargetName is compared in the same key space.
            var byRingKey = names.ToDictionary(
                kv => TemplateMap.RingKey(kv.Key), kv => TemplateMap.RingKey(kv.Value),
                StringComparer.OrdinalIgnoreCase);
            // Slots are positional, so a renaming may renumber them; the MAPPING has to be a bijection
            // and every wait has to follow it.
            var slotMap = names.Where(kv => before.Slots.ContainsKey(kv.Key) && after.Slots.ContainsKey(kv.Value))
                .ToDictionary(kv => before.Slots[kv.Key], kv => after.Slots[kv.Value]);

            Assert.Equal(before.Slots.Count, after.Slots.Count);
            Assert.Equal(slotMap.Count, slotMap.Values.Distinct().Count());   // a bijection, not a collapse

            foreach (var (original, swapped) in names.Select(kv => (kv.Key, kv.Value)))
            {
                // The CAT is chosen by the graph, so the same graph chooses the same CAT.
                if (before.CatTypes.TryGetValue(original, out var catBefore))
                    Assert.Equal(catBefore, after.CatTypes[swapped]);
                // Rules come from the twin's constraints, so their count cannot move either.
                if (before.Interlocks.TryGetValue(original, out var rulesBefore))
                    Assert.Equal(rulesBefore.Count, after.Interlocks[swapped].Count);
            }

            Assert.Equal(before.Recipes.Count, after.Recipes.Count);
            foreach (var process in before.Recipes.Keys)
                Assert.Equal(
                    Shape(before, process, byRingKey, slotMap),
                    Shape(after, names[process], byRingKey, slotMap));
        }

        [Fact]
        public void A_second_process_with_unfamiliar_names_needs_no_new_branch()
        {
            var one = Cell("Alpha");
            var two = Cell("Beta").Concat(Cell("Gamma")).ToList();   // two more lines, names nothing knows

            var plan = Compile(one.Concat(two).ToList());

            // Three processes, each with its own compiled recipe that actually commands its own actuator.
            Assert.Equal(3, plan.Recipes.Count);
            foreach (var tag in new[] { "Alpha", "Beta", "Gamma" })
            {
                var recipe = plan.Recipes[$"{tag}_Line"];
                Assert.Contains(recipe.CmdTargetName, n =>
                    string.Equals(n, TemplateMap.RingKey($"{tag}_Pusher"), StringComparison.Ordinal));
                Assert.Contains(recipe.StepType, t => t == 2);   // a WAIT row
            }
            // Every reporter got its own slot: nothing collided because a name was unfamiliar.
            Assert.Equal(plan.Slots.Count, plan.Slots.Values.Distinct().Count());
        }

        [Fact]
        public void An_actuator_shape_no_CAT_serves_is_refused_rather_than_defaulted()
        {
            var plant = Cell("Alpha");
            var odd = Actuator("C-odd-a", "Alpha_Odd");
            odd.States.Add(Stop("C-odd-a-s3", "Alpha_Odd_Extra", 3));   // a 4-stop graph
            odd.States.Add(Stop("C-odd-a-s4", "Alpha_Odd_More", 4));
            odd.States.Add(Stop("C-odd-a-s5", "Alpha_Odd_Yet", 5));
            odd.States.Add(Stop("C-odd-a-s6", "Alpha_Odd_Still", 6));
            odd.States.Add(Stop("C-odd-a-s7", "Alpha_Odd_Again", 7));  // 8 stops: no declared protocol
            var proc = plant[0];
            Leads(odd.States[0], odd.States[1], On(proc, proc.States[1]));

            var ex = Assert.Throws<InvalidOperationException>(() => Compile(plant.Append(odd).ToList()));
            Assert.Contains("Alpha_Odd", ex.Message, StringComparison.Ordinal);
            Assert.Contains("CAT", ex.Message, StringComparison.Ordinal);
        }
    }
}
