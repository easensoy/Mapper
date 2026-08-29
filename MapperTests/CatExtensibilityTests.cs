using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// A NEW CAT IS AN ARCHIVE AND A DECLARATION, NOT A CODE CHANGE.
    ///
    /// The plant here is a kiln. None of its names appears anywhere in the compiler, and neither does
    /// the CAT it is served by: both are invented in this file. If any of these pass only because a
    /// C# branch happens to recognise an SMC name, they fail.
    ///
    /// The catalogue used to make this impossible for a shape an existing CAT already served: it
    /// refused ANY two CATs sharing a state count, unconditionally, while telling the reader to
    /// declare a `priority` it then never read. Adding a CAT meant editing the row already there.
    public sealed class CatExtensibilityTests : IDisposable
    {
        readonly List<string> _roots = new();

        public void Dispose()
        {
            foreach (var r in _roots)
                try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch { /* temp */ }
        }

        /// A copy of the shipped bundle with one declaration file edited.
        string Bundle(string file, Func<string, string> edit)
        {
            var root = Path.Combine(Path.GetTempPath(), "cat_" + Guid.NewGuid().ToString("N")[..8]);
            var dst = Path.Combine(root, "Config");
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Config")))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            var path = Path.Combine(dst, file);
            File.WriteAllText(path, edit(File.ReadAllText(path)));
            _roots.Add(root);
            return root;
        }

        static CompilerConfiguration Load(string root) =>
            CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone(), root);

        // A CAT this repository has never heard of, serving a shape nothing else serves.
        const string ThreeStopCat = @"
  - name: Kiln_Damper_CAT
    kind: cat
    role: actuator
    deploy: true
    mirrorToSysres: true
    emitted: true
    ports: [stationAdptr_in, stateRprtCmd_in, stationAdptr_out, stateRprtCmd_out]
    nameParameter: actuator_name
    protocol:
      stateCounts: [9]
      command:   { work: 1, home: 3 }
      settled:   { work: 2, home: 0 }
      target:    { work1: 2, home: 4 }
      stops:
        home: [0]
        work: [2]
      legs: { work: 1, home: 3 }
      enforcedTargets: [work1, home]
";

        static string AppendCat(string yml, string block)
        {
            var i = yml.IndexOf("\n  - name: Sensor_Bool_CAT", StringComparison.Ordinal);
            Assert.True(i > 0, "templates.yml no longer has the shape this fixture appends to");
            return yml[..i] + "\n" + block.TrimEnd() + yml[i..];
        }

        [Fact]
        public void A_new_cat_declared_in_yaml_serves_a_shape_no_shipped_cat_serves()
        {
            // Nine states: nothing in the shipped catalogue claims it, so it is unserved today...
            Assert.Null(TestConfig.Cfg.Manifest.ForGraph(9, branched: false));

            // ...and becomes served by adding one YAML row. No C# is touched.
            var cfg = Load(Bundle("templates.yml", y => AppendCat(y, ThreeStopCat)));
            Assert.Equal("Kiln_Damper_CAT", cfg.Manifest.ForGraph(9, branched: false)?.Name);
        }

        [Fact]
        public void The_new_cat_drives_a_plant_whose_names_appear_nowhere_in_the_compiler()
        {
            var cfg = Load(Bundle("templates.yml", y => AppendCat(y, ThreeStopCat)));

            var damper = NineStopActuator("C-damper", "Kiln_Damper");
            var line = Process("C-line", "Kiln_Line", damper);
            var ctx = GenerationContext.Plan(cfg, new List<VueOneComponent> { line, damper },
                DeploymentProfile.Relocating(Array.Empty<string>(), cfg));

            // The CAT the plan selected is the declared one...
            Assert.Equal("Kiln_Damper_CAT", ctx.CatTypes["Kiln_Damper"]);

            // ...and the recipe it compiled uses THAT CAT's command vocabulary, not a default.
            var recipe = ctx.Recipes["Kiln_Line"];
            var commands = recipe.StepType
                .Select((s, i) => (s, i))
                .Where(x => x.s == CodeGen.Translation.Process.StepType.Cmd)
                .Select(x => (recipe.CmdTargetName[x.i], recipe.CmdStateArr[x.i]))
                .ToList();
            Assert.Contains(("kiln_damper", 1), commands);   // command.work, from the new declaration
        }

        [Fact]
        public void Two_cats_that_could_serve_one_graph_need_a_declared_priority()
        {
            // The new CAT claims a shape the shipped five-state CAT already serves, at the same
            // (default) priority. Which one a plant got would depend on row order, so it is refused.
            var clashing = ThreeStopCat.Replace("stateCounts: [9]", "stateCounts: [5]");
            var ex = Assert.Throws<InvalidOperationException>(
                () => Load(Bundle("templates.yml", y => AppendCat(y, clashing))));

            Assert.Contains("can both serve one twin graph", ex.Message, StringComparison.Ordinal);
            Assert.Contains("same protocol.priority", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Kiln_Damper_CAT", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Declaring_a_higher_priority_resolves_the_overlap_without_editing_the_other_row()
        {
            // THE CASE THE PREDECESSOR MADE IMPOSSIBLE. The old check refused any shared state count
            // outright, so a new CAT for a served shape required editing the CAT already there.
            var clashing = ThreeStopCat
                .Replace("stateCounts: [9]", "stateCounts: [5]")
                .Replace("      legs: { work: 1, home: 3 }", "      legs: { work: 1, home: 3 }\n      priority: 5");

            var cfg = Load(Bundle("templates.yml", y => AppendCat(y, clashing)));
            Assert.Equal("Kiln_Damper_CAT", cfg.Manifest.ForGraph(5, branched: false)?.Name);

            // The shipped row is untouched and still serves the shapes it alone claims.
            Assert.NotNull(cfg.Manifest.ForGraph(3, branched: false));
        }

        [Fact]
        public void A_branched_claim_and_a_state_count_claim_overlap_and_are_caught_at_load()
        {
            // A CAT declaring `servesBranched` and one declaring a state count BOTH match a branched
            // twin of that size. The predecessor compared state counts only, so this reached the plan
            // and failed mid-run; it is refused while the previous project is still intact.
            var branched = ThreeStopCat
                .Replace("stateCounts: [9]", "stateCounts: []\n      servesBranched: true");
            var ex = Assert.Throws<InvalidOperationException>(
                () => Load(Bundle("templates.yml", y => AppendCat(y, branched))));
            Assert.Contains("can both serve one twin graph", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_shape_no_cat_serves_is_refused_by_name_rather_than_defaulted()
        {
            var damper = NineStopActuator("C-damper", "Kiln_Damper");
            var line = Process("C-line", "Kiln_Line", damper);
            var ex = Assert.Throws<InvalidOperationException>(() => GenerationContext.Plan(
                TestConfig.Cfg, new List<VueOneComponent> { line, damper },
                DeploymentProfile.Relocating(Array.Empty<string>(), TestConfig.Cfg)));

            Assert.Contains("Kiln_Damper", ex.Message, StringComparison.Ordinal);
            Assert.Contains("9-state graph", ex.Message, StringComparison.Ordinal);
            Assert.Contains("will not guess", ex.Message, StringComparison.Ordinal);
        }

        // ---- the plant, built in code, named after nothing in this repository ----

        static VueOneState Stop(string id, string name, int number, bool initial = false) => new()
        { StateID = id, Name = name, StateNumber = number, InitialState = initial, StaticState = true };

        static VueOneComponent NineStopActuator(string id, string name)
        {
            var states = new List<VueOneState>();
            for (int i = 0; i < 9; i++)
                states.Add(Stop($"{id}-s{i}", $"P{i}", i, initial: i == 0));
            // Home <-> work, driven by the process below.
            states[0].Transitions.Add(new VueOneTransition
            {
                TransitionID = $"{id}-t0", OriginStateID = $"{id}-s0", DestinationStateID = $"{id}-s2",
                Conditions = new[] { new VueOneCondition { ComponentID = "C-line", ID = "C-line-s1" } },
            });
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = ComponentType.Actuator, Kind = ComponentKind.Actuator, States = states };
        }

        static VueOneComponent Process(string id, string name, VueOneComponent drives)
        {
            var s0 = Stop($"{id}-s0", "Entry", 0, initial: true);
            var s1 = Stop($"{id}-s1", "Drive", 1);
            var s2 = Stop($"{id}-s2", "Settle", 2);
            s0.Transitions.Add(new VueOneTransition
            { TransitionID = $"{id}-t0", OriginStateID = s0.StateID, DestinationStateID = s1.StateID });
            s1.Transitions.Add(new VueOneTransition
            {
                TransitionID = $"{id}-t1", OriginStateID = s1.StateID, DestinationStateID = s2.StateID,
                Conditions = new[] { new VueOneCondition { ComponentID = drives.ComponentID, ID = $"{drives.ComponentID}-s2" } },
            });
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Process, Kind = ComponentKind.Process,
                States = new List<VueOneState> { s0, s1, s2 },
            };
        }
    }
}
