using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Interlocks;
using Xunit;

namespace MapperTests
{
    /// What the compiler PROMISES about meaning: a guard keeps its truth, a rule guards what it names, a
    /// model larger than the declared arrays grows them rather than losing rows, and a process runs on
    /// whichever target owns it. Every plant here is built in code with names nothing in the compiler has
    /// heard of, so a passing test cannot be a name test in disguise.
    public sealed class SemanticFidelityTests
    {
        private static VueOneState Stop(string id, string name, int number, bool initial = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = true, InitialState = initial,
            Transitions = new List<VueOneTransition>(),
        };

        private static VueOneComponent Sensor(string id, string name) => new()
        {
            ComponentID = id, Name = name, Type = ComponentType.Sensor,
            States = new List<VueOneState>
            {
                Stop(id + "-off", name + "_Clear", 0, initial: true), Stop(id + "-on", name + "_Made", 1),
            },
        };

        private static VueOneComponent Actuator(string id, string name)
        {
            var home = Stop(id + "-s0", name + "_Back", 0, initial: true);
            var moving = new VueOneState
            {
                StateID = id + "-m", Name = name + "_Moving", StateNumber = 1, StaticState = false,
                Transitions = new List<VueOneTransition>(),
            };
            var work = Stop(id + "-s2", name + "_Out", 2);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Actuator,
                States = new List<VueOneState> { home, moving, work },
            };
        }

        private static VueOneComponent Process(string id, string name, int steps)
        {
            var states = new List<VueOneState> { Stop(id + "-s0", name + "_Entry", 0, initial: true) };
            for (int i = 1; i <= steps; i++) states.Add(Stop($"{id}-s{i}", $"{name}_Step{i}", i));
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = ComponentType.Process, States = states };
        }

        // Conditions is a PROJECTION of Guard and assigning it replaces the guard, so a transition is
        // given one or the other, never both.
        private static void Leads(VueOneState from, VueOneState to, ConditionExpr? guard = null,
            params VueOneCondition[] flat) =>
            from.Transitions.Add(new VueOneTransition
            {
                TransitionID = "T-" + from.StateID + "-" + to.StateID,
                OriginStateID = from.StateID, DestinationStateID = to.StateID,
                Guard = guard ?? ConditionExpr.FromFlat(flat),
            });

        private static VueOneCondition On(string component, string state) =>
            new() { ComponentID = component, ID = state, Name = component + "/" + state };

        private static ConditionExpr.Ref R(string component, string state) => new(On(component, state));

        private static GenerationContext Compile(IReadOnlyList<VueOneComponent> plant,
            LayoutCatalog? layout = null) =>
            GenerationContext.Plan(new MapperConfig(), plant,
                DeploymentProfile.M262Only(layout ?? LayoutCatalog.Load()));

        // ---- 1. a guard keeps its truth ----------------------------------------------------------

        [Fact]
        public void An_either_or_guard_releases_on_the_first_alternative_not_on_all_of_them()
        {
            // The twin says "either eye". A row tests one (slot, value), so the alternatives are laid down
            // as ONE wait group the engine evaluates as a disjunction. Requiring both would make the step
            // wait longer than the model asks - a different machine, quietly.
            var left = Sensor("C-l", "Left_Eye");
            var right = Sensor("C-r", "Right_Eye");
            var cell = Process("C-cell", "Sorter_Cell", 1);
            Leads(cell.States[0], cell.States[1],
                ConditionExpr.Disjunction(new ConditionExpr[] { R("C-l", "C-l-on"), R("C-r", "C-r-on") }));

            var recipe = Compile(new[] { cell, left, right }).Recipes["Sorter_Cell"];

            var head = HeadOfWaitGroup(recipe);
            Assert.Equal(2, recipe.AltCount[head]);          // two alternatives...
            Assert.Equal(1, recipe.TermCount[head]);         // ...of one term each
            Assert.Equal(1, recipe.TermCount[head + 1]);
            Assert.NotEqual(recipe.Wait1Id[head], recipe.Wait1Id[head + 1]);
            // The group is ONE requirement: the head steps past the whole of it.
            Assert.Equal(head + 2, recipe.NextStep[head]);
        }

        [Fact]
        public void An_alternative_that_is_itself_a_conjunction_keeps_both_of_its_terms()
        {
            // (a AND b) OR c. The first alternative holds only when both of its terms do.
            var a = Sensor("C-a", "Upper_Eye");
            var b = Sensor("C-b", "Lower_Eye");
            var c = Sensor("C-c", "Spare_Eye");
            var cell = Process("C-cell", "Sorter_Cell", 1);
            Leads(cell.States[0], cell.States[1], ConditionExpr.Disjunction(new ConditionExpr[]
            {
                ConditionExpr.Conjunction(new ConditionExpr[] { R("C-a", "C-a-on"), R("C-b", "C-b-on") })!,
                R("C-c", "C-c-on"),
            }));

            var recipe = Compile(new[] { cell, a, b, c }).Recipes["Sorter_Cell"];

            var head = HeadOfWaitGroup(recipe);
            Assert.Equal(2, recipe.AltCount[head]);
            Assert.Equal(2, recipe.TermCount[head]);         // the conjunction, held together
            Assert.Equal(1, recipe.TermCount[head + 2]);     // and the single alternative after it
            Assert.Equal(head + 3, recipe.NextStep[head]);
        }

        [Fact]
        public void Alternatives_that_reduce_to_one_requirement_stay_one_plain_row()
        {
            // The same requirement written twice is not a choice, so nothing is grouped and the row is
            // exactly what a guard with no alternatives produces.
            var eye = Sensor("C-e", "Only_Eye");
            var cell = Process("C-cell", "Sorter_Cell", 1);
            Leads(cell.States[0], cell.States[1],
                ConditionExpr.Disjunction(new ConditionExpr[] { R("C-e", "C-e-on"), R("C-e", "C-e-on") }));

            var recipe = Compile(new[] { cell, eye }).Recipes["Sorter_Cell"];

            Assert.All(recipe.AltCount, n => Assert.Equal(0, n));
            Assert.All(recipe.TermCount, n => Assert.Equal(0, n));
        }

        private static int HeadOfWaitGroup(CodeGen.Translation.Process.RecipeArrays recipe)
        {
            for (int i = 0; i < recipe.AltCount.Count; i++)
                if (recipe.AltCount[i] > 1) return i;
            throw new Xunit.Sdk.XunitException(
                "no wait group was emitted; the alternatives were flattened into requirements again.");
        }

        // ---- 2. a rule guards what it names, and means what the twin wrote -----------------------

        // An actuator whose MOVING state carries an interlock guard: the rule blocks home -> work.
        private static VueOneComponent Guarded(string id, string name, ConditionExpr guard)
        {
            var a = Actuator(id, name);
            Leads(a.States[0], a.States[1]);                 // home -> moving, so the rule's FromState is home
            Leads(a.States[1], a.States[2]);                 // moving -> work, so its ToState is work
            a.States[1].InterlockGuard = guard;
            return a;
        }

        // Reads the emitted table exactly as CommonInterlockEvaluator.Evaluate does: rows are a flattened
        // sum of products, a row with TermCount >= 1 heads an alternative, and the move is blocked when any
        // ONE alternative holds wholly. A term holds when its source reads the blocked state OR has never
        // reported at all. `reported` is the state_table: absent means the slot was never written.
        private static bool Blocks(InterlockPlan plan, int from, int to, IReadOnlyDictionary<int, int> reported)
        {
            foreach (var alternative in plan.Alternatives())
            {
                if (alternative.From != from || alternative.To != to) continue;
                if (alternative.Terms.All(t =>
                        !reported.TryGetValue(t.Src, out var state) || state == t.Blocked))
                    return true;
            }
            return false;
        }

        private static InterlockPlan PlanFor(GenerationContext ctx, string actuator) =>
            ctx.Interlocks.TryGetValue(actuator, out var plan)
                ? plan
                : throw new Xunit.Sdk.XunitException($"'{actuator}' was planned no interlock at all.");

        [Fact]
        public void A_conjunction_blocks_only_when_every_one_of_its_terms_holds()
        {
            // The twin says "blocked while the upper eye is made AND the lower eye is made". Flattening
            // that into two rules would make either one alone block - a different machine, quietly.
            var upper = Sensor("C-u", "Upper_Eye");
            var lower = Sensor("C-l", "Lower_Eye");
            var ram = Guarded("C-r", "Charge_Ram", ConditionExpr.Conjunction(
                new ConditionExpr[] { R("C-u", "C-u-on"), R("C-l", "C-l-on") })!);
            var ctx = Compile(new[] { Process("C-p", "Kiln_Line", 1), upper, lower, ram });

            var plan = PlanFor(ctx, "Charge_Ram");
            var alternative = Assert.Single(plan.Alternatives());
            Assert.Equal(2, alternative.Terms.Count);        // held together, not split into two rules
            Assert.Equal(2, plan.TermCount[0]);              // the head counts them...
            Assert.Equal(0, plan.TermCount[1]);              // ...and the second row continues it

            int u = ctx.Slots["Upper_Eye"], l = ctx.Slots["Lower_Eye"];
            int from = alternative.From, to = alternative.To;
            Assert.False(Blocks(plan, from, to, new Dictionary<int, int> { [u] = 1, [l] = 0 }));
            Assert.False(Blocks(plan, from, to, new Dictionary<int, int> { [u] = 0, [l] = 1 }));
            Assert.True(Blocks(plan, from, to, new Dictionary<int, int> { [u] = 1, [l] = 1 }));
        }

        [Fact]
        public void Either_complete_alternative_blocks_the_move()
        {
            // (upper AND lower) OR spare. Each alternative stands on its own; neither needs the other.
            var upper = Sensor("C-u", "Upper_Eye");
            var lower = Sensor("C-l", "Lower_Eye");
            var spare = Sensor("C-s", "Spare_Eye");
            var ram = Guarded("C-r", "Charge_Ram", ConditionExpr.Disjunction(new ConditionExpr[]
            {
                ConditionExpr.Conjunction(new ConditionExpr[] { R("C-u", "C-u-on"), R("C-l", "C-l-on") })!,
                R("C-s", "C-s-on"),
            })!);
            var ctx = Compile(new[] { Process("C-p", "Kiln_Line", 1), upper, lower, spare, ram });

            var plan = PlanFor(ctx, "Charge_Ram");
            var alternatives = plan.Alternatives().ToList();
            Assert.Equal(2, alternatives.Count);
            Assert.Equal(new[] { 2, 1 }, alternatives.Select(a => a.Terms.Count).ToArray());

            int u = ctx.Slots["Upper_Eye"], l = ctx.Slots["Lower_Eye"], s = ctx.Slots["Spare_Eye"];
            int from = alternatives[0].From, to = alternatives[0].To;
            Assert.True(Blocks(plan, from, to, new Dictionary<int, int> { [u] = 1, [l] = 1, [s] = 0 }));
            Assert.True(Blocks(plan, from, to, new Dictionary<int, int> { [u] = 0, [l] = 0, [s] = 1 }));
            Assert.False(Blocks(plan, from, to, new Dictionary<int, int> { [u] = 1, [l] = 0, [s] = 0 }));
        }

        [Fact]
        public void An_unwritten_source_cannot_permit_motion_at_runtime()
        {
            // state_table starts all zeroes, so a rule whose source has never reported would otherwise
            // read as a real position. An unverifiable term keeps holding and the move is refused.
            var eye = Sensor("C-u", "Upper_Eye");
            var ram = Guarded("C-r", "Charge_Ram", R("C-u", "C-u-on"));
            var ctx = Compile(new[] { Process("C-p", "Kiln_Line", 1), eye, ram });

            var plan = PlanFor(ctx, "Charge_Ram");
            var alternative = Assert.Single(plan.Alternatives());
            Assert.True(Blocks(plan, alternative.From, alternative.To, new Dictionary<int, int>()));

            // And that is what the shipped evaluator does: a term is cleared only against a slot that has
            // been written, and the recipe wait applies the same rule, so neither half can drift.
            var evaluator = Deployed("CommonInterlockEvaluator.fbt");
            Assert.Contains("state_table[sourceIndex].name <> ''", evaluator, StringComparison.Ordinal);
            Assert.Contains("FOR t := i TO lastTerm DO", evaluator, StringComparison.Ordinal);
            Assert.Contains("RuleTermCount[i] >= 1", evaluator, StringComparison.Ordinal);
            Assert.Contains(".name <> ''", Deployed("ProcessRuntime_Generic_v1.fbt"), StringComparison.Ordinal);
        }

        // The shipped template, read out of the library the deployer extracts. Reading the TEMPLATE is the
        // point: it is what every generated project gets.
        private static string Deployed(string fbt)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Template Library")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the Template Library from " + AppContext.BaseDirectory);
            foreach (var zip in Directory.EnumerateFiles(
                         Path.Combine(dir!.FullName, "Template Library"), "*.zip", SearchOption.AllDirectories))
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
                var entry = archive.Entries.FirstOrDefault(e => e.Name == fbt);
                if (entry == null) continue;
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }
            throw new Xunit.Sdk.XunitException($"{fbt} is in no template archive.");
        }

        // ---- 2b. a verdict nobody reads is not a safety rule -------------------------------------

        // Seven stops either side of a centre reference: the shape the manifest gives the centre-home CAT.
        private static VueOneComponent CentreHome(string id, string name)
        {
            var states = new List<VueOneState>();
            for (int i = 0; i <= 6; i++)
                states.Add(Stop($"{id}-s{i}", $"{name}_Stop{i}", i, initial: i == 0));
            var c = new VueOneComponent
            { ComponentID = id, Name = name, Type = ComponentType.Actuator, States = states };
            for (int i = 0; i < 6; i++) Leads(states[i], states[i + 1]);
            Leads(states[6], states[0]);
            return c;
        }

        [Fact]
        public void A_rule_the_cat_cannot_act_on_stops_generation_instead_of_shipping_inert()
        {
            // The centre-home core takes one PATH interlock per side and no to-home input of its own, so
            // a rule aimed at home would be computed by the evaluator and read by nothing. Shipping it
            // would report a guarded machine that is not guarded.
            var eye = Sensor("C-u", "Upper_Eye");
            var swivel = CentreHome("C-sw", "Charge_Swivel");
            swivel.States[5].InterlockGuard = R("C-u", "C-u-on");     // the move whose destination is home

            var failure = Assert.Throws<InvalidOperationException>(
                () => Compile(new[] { Process("C-p", "Kiln_Line", 1), eye, swivel }));

            Assert.Contains("Charge_Swivel", failure.Message, StringComparison.Ordinal);
            Assert.Contains("could never fire", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_home_direction_rule_is_kept_where_the_cat_gates_the_home_move()
        {
            // The five-state core takes toHomeInterlock, so the same guard on the return leg IS enforceable
            // and is planned rather than refused.
            var eye = Sensor("C-u", "Upper_Eye");
            var ram = Actuator("C-r", "Charge_Ram");
            Leads(ram.States[0], ram.States[1]);
            Leads(ram.States[1], ram.States[2]);
            var returning = new VueOneState
            {
                StateID = "C-r-m2", Name = "Charge_Ram_Returning", StateNumber = 3, StaticState = false,
                Transitions = new List<VueOneTransition>(),
                InterlockGuard = R("C-u", "C-u-on"),
            };
            var returned = Stop("C-r-s4", "Charge_Ram_Returned", 4);
            ram.States.Add(returning);
            ram.States.Add(returned);
            Leads(ram.States[2], returning);
            Leads(returning, returned);

            var plan = PlanFor(Compile(new[] { Process("C-p", "Kiln_Line", 1), eye, ram }), "Charge_Ram");
            Assert.Contains(plan.Alternatives(), a => a.To == 4);     // the CAT's declared home target

            // ...and the deployed CAT actually asks for that verdict. Without the request the evaluator
            // never runs the home branch and the input it feeds keeps its initial FALSE forever.
            var cat = Deployed("Five_State_Actuator_CAT.fbt");
            Assert.Contains("Destination=\"InterlockManager.REQ_HOME\"", cat, StringComparison.Ordinal);
            Assert.Contains("Source=\"InterlockManager.HomeInterlock\" Destination=\"ActuatorCore.toHomeInterlock\"",
                cat, StringComparison.Ordinal);
        }

        // ---- 3. nothing is truncated -------------------------------------------------------------

        [Fact]
        public void More_interlocks_than_the_declared_array_grow_it_rather_than_losing_rows()
        {
            int floor = InterlockConfig.Current.RuleArraySize;
            var plant = new List<VueOneComponent>();
            var line = Process("C-line", "Kiln_Line", 2);
            var guarded = Actuator("C-g", "Charge_Ram");
            Leads(guarded.States[0], guarded.States[1], flat: On("C-line", "C-line-s1"));
            Leads(guarded.States[1], guarded.States[2]);
            Leads(guarded.States[2], guarded.States[0], flat: On("C-line", "C-line-s2"));
            Leads(line.States[0], line.States[1]);
            Leads(line.States[1], line.States[2], flat: On("C-g", "C-g-s2"));
            plant.Add(line);
            plant.Add(guarded);

            // One blocking source per neighbour, comfortably past the declared floor.
            var blockers = new List<VueOneCondition>();
            for (int i = 0; i < floor + 3; i++)
            {
                var n = Sensor($"C-n{i}", $"Neighbour_{i}");
                plant.Add(n);
                blockers.Add(On($"C-n{i}", $"C-n{i}-on"));
            }
            guarded.States[1].InterlockConditions = blockers;

            var plan = Compile(plant);
            var rules = plan.Interlocks["Charge_Ram"];

            Assert.True(rules.Count > floor,
                $"only {rules.Count} of {blockers.Count} interlocks survived planning - rules were dropped.");
            Assert.True(plan.InterlockCapacity >= rules.Count,
                "the deployed rule array would be smaller than the plan it has to carry.");
        }

        [Fact]
        public void Every_wait_and_every_rule_reads_a_slot_its_source_reports_on()
        {
            var line = Process("C-line", "Kiln_Line", 2);
            var ram = Actuator("C-ram", "Charge_Ram");
            var eye = Sensor("C-eye", "Charge_Eye");
            Leads(ram.States[0], ram.States[1], flat: On("C-line", "C-line-s1"));
            Leads(ram.States[1], ram.States[2]);
            Leads(ram.States[2], ram.States[0], flat: On("C-line", "C-line-s2"));
            Leads(line.States[0], line.States[1], flat: On("C-eye", "C-eye-on"));
            Leads(line.States[1], line.States[2], flat: On("C-ram", "C-ram-s2"));

            var plan = Compile(new[] { line, ram, eye });
            var bySlot = plan.Slots.ToDictionary(kv => kv.Value, kv => kv.Key);

            foreach (var (process, recipe) in plan.Recipes)
                for (int i = 0; i < recipe.StepType.Count; i++)
                {
                    if (recipe.StepType[i] != 2 || recipe.Wait1Id[i] == 0) continue;
                    Assert.True(bySlot.ContainsKey(recipe.Wait1Id[i]),
                        $"'{process}' waits on slot {recipe.Wait1Id[i]}, which no reporter owns.");
                    Assert.True(plan.Rings.SameDomain(bySlot[recipe.Wait1Id[i]], process),
                        $"'{process}' waits on '{bySlot[recipe.Wait1Id[i]]}', which reports on another ring.");
                }

            foreach (var (actuator, rules) in plan.Interlocks)
                for (int i = 0; i < rules.Count; i++)
                {
                    Assert.True(bySlot.ContainsKey(rules.Src[i]),
                        $"'{actuator}' is interlocked on slot {rules.Src[i]}, which no reporter owns.");
                    Assert.True(plan.Rings.SameDomain(bySlot[rules.Src[i]], actuator),
                        $"'{actuator}' is interlocked on '{bySlot[rules.Src[i]]}', which reports elsewhere.");
                }
        }

        // ---- 4. a process runs on whichever target owns it ---------------------------------------

        [Theory]
        [InlineData(PlcAssignment.M262)]
        [InlineData(PlcAssignment.M580)]
        [InlineData(PlcAssignment.BX1)]
        [InlineData(PlcAssignment.RevPi)]
        public void A_process_the_roster_places_on_any_target_is_planned_onto_that_resource(
            PlcAssignment target)
        {
            // A roster row is DATA. Placing a process on a different target must not need a C# branch, so
            // the same plant is compiled four times and only the row moves. A target that exists only
            // when work is RELOCATED onto it is reached that way, which is how production reaches it.
            bool relocated = TargetRegistry.Of(target).ReceivesRelocatedComponents;
            var rostered = relocated ? TargetRegistry.FeedTarget : target;
            var layout = FreshLayout();
            layout.Components.Add(new RosterEntry
            { Name = "Kiln_Line", Plc = rostered, Column = 9, Row = "Process" });
            layout.Components.Add(new RosterEntry
            { Name = "Charge_Ram", Plc = rostered, Column = 9, Row = "Actuator" });

            var line = Process("C-line", "Kiln_Line", 2);
            var ram = Actuator("C-ram", "Charge_Ram");
            Leads(ram.States[0], ram.States[1], flat: On("C-line", "C-line-s1"));
            Leads(ram.States[1], ram.States[2]);
            Leads(ram.States[2], ram.States[0], flat: On("C-line", "C-line-s2"));
            Leads(line.States[0], line.States[1]);
            Leads(line.States[1], line.States[2], flat: On("C-ram", "C-ram-s2"));

            var plan = GenerationContext.Plan(new MapperConfig(), new[] { line, ram },
                new DeploymentProfile(
                    relocated ? new[] { "Kiln_Line", "Charge_Ram" } : System.Array.Empty<string>(), layout));

            Assert.Contains("Kiln_Line", plan.ResourceFor(target).Processes);
            Assert.Contains(plan.Recipes.Keys, k => k == "Kiln_Line");
            // and no other resource claims it
            foreach (var other in plan.Profile.Layout.Resources.Where(r => r.Plc != target))
                Assert.DoesNotContain("Kiln_Line", plan.ResourceFor(other.Plc).Processes);
        }

        // A fixture that varies the roster reads its OWN copy: LayoutCatalog.Load() hands back one shared
        // instance, and mutating that would change what every other plan in the run is compiled against.
        private static LayoutCatalog FreshLayout()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Config", "layout.yml");
            return new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<LayoutCatalog>(File.ReadAllText(path));
        }
    }
}
