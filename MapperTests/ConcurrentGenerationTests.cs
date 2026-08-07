using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Process;
using CodeGen.Translation.Process.Recipes;
using Xunit;

namespace MapperTests
{
    /// Planning must be a pure function of (twin, profile), so two runs in one process cannot see each
    /// other. It used to be the opposite: the routing mode, the component roster, the ring-merge decision
    /// and the top-cover slot all travelled as statics, so a second generation started from the first
    /// one's answers and two generations could not run at once at all. These tests fail if any of that
    /// state comes back.
    public sealed class ConcurrentGenerationTests
    {
        private static readonly string[] Models = { "_se", "_vc", "_sw5", "_sw5_noclamp" };

        private static string ModelPath(string suffix) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Documents", "VueOne", "system",
            "SMC_Vue2VC_With_Processes" + suffix, "Control.xml");

        // Same prerequisite the other model-driven tests in this project use: the VueOne source models.
        private static string Require(string suffix)
        {
            var path = ModelPath(suffix);
            if (!File.Exists(path))
                throw new FileNotFoundException($"VueOne source model '{suffix}' not found at {path}.", path);
            return path;
        }

        // A plan reduced to the facts a later step reads, so two plans can be compared for equality.
        private sealed record Fingerprint(
            string Profile, bool RingsMerged, int TopCoverSlot,
            string Allocation, string Recipes, string ReceiverSlots);

        private static Fingerprint Plan(string model, DeploymentProfile profile)
        {
            var ctx = GenerationContext.Plan(new MapperConfig(), Require(model), profile);

            var allocation = string.Join(";", ctx.Components
                .Select(c => c.Name + "=" + ctx.Allocation.Of(c.Name))
                .OrderBy(x => x, StringComparer.Ordinal));

            var recipes = string.Join(" | ", ctx.Recipes
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + ":" + string.Join(",", Enumerable.Range(0, kv.Value.Count)
                    .Select(i => $"{kv.Value.StepType[i]}/{kv.Value.CmdTargetName[i]}/{kv.Value.CmdStateArr[i]}" +
                                 $"/{kv.Value.Wait1Id[i]}/{kv.Value.Wait1State[i]}/{kv.Value.NextStep[i]}"))));

            var slots = string.Join(";", ctx.Recipes.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => k + "=" + (ctx.Handoffs.ReceiverSlotOf(k)?.ToString() ?? "-")));

            return new Fingerprint(profile.ToString(), ctx.RingsMerged, ctx.TopCoverSensorSlot,
                allocation, recipes, slots);
        }

        [Fact] // four models planned at once must each get their own answers
        public void Concurrent_plans_of_different_models_do_not_leak_into_each_other()
        {
            var sequential = Models.ToDictionary(m => m, m => Plan(m, DeploymentProfile.M262Only(LayoutCatalog.Load())));

            // 8 planners over 4 models, interleaved, so any shared mutable state is very likely to be
            // observed by a run that did not write it.
            var concurrent = new Fingerprint[Models.Length * 2];
            Parallel.For(0, concurrent.Length,
                i => concurrent[i] = Plan(Models[i % Models.Length], DeploymentProfile.M262Only(LayoutCatalog.Load())));

            for (int i = 0; i < concurrent.Length; i++)
                Assert.Equal(sequential[Models[i % Models.Length]], concurrent[i]);
        }

        [Fact] // the same model planned under two profiles at once must not blend them
        public void Concurrent_plans_of_different_profiles_do_not_leak_into_each_other()
        {
            var m262 = DeploymentProfile.M262Only(LayoutCatalog.Load());
            var revPi = new DeploymentProfile(new[] { "Feeder", "Checker" }, LayoutCatalog.Load());
            var expectedM262 = Plan("_se", m262);
            var expectedRevPi = Plan("_se", revPi);

            // The two profiles genuinely differ, so a leak would be visible.
            Assert.NotEqual(expectedM262.Allocation, expectedRevPi.Allocation);

            var results = new Fingerprint[16];
            Parallel.For(0, results.Length,
                i => results[i] = Plan("_se", i % 2 == 0 ? m262 : revPi));

            for (int i = 0; i < results.Length; i++)
                Assert.Equal(i % 2 == 0 ? expectedM262 : expectedRevPi, results[i]);
        }

        [Fact] // planning twice in one process must give the same answer the first time gave
        public void Replanning_the_same_model_is_stable()
        {
            foreach (var model in Models)
            {
                var first = Plan(model, DeploymentProfile.M262Only(LayoutCatalog.Load()));
                _ = Plan("_vc", new DeploymentProfile(new[] { "Feeder" }, LayoutCatalog.Load()));   // a different run in between
                Assert.Equal(first, Plan(model, DeploymentProfile.M262Only(LayoutCatalog.Load())));
            }
        }

        [Fact] // a roster is a value: building one cannot disturb another that already exists
        public void Rosters_built_concurrently_under_different_profiles_stay_independent()
        {
            var m262 = new DeploymentRoster(DeploymentProfile.M262Only(LayoutCatalog.Load()));
            var expected = m262.All.ToDictionary(e => e.Name, e => e.Plc, StringComparer.Ordinal);

            Parallel.For(0, 64, i =>
            {
                var other = new DeploymentRoster(new DeploymentProfile(new[] { "Feeder", "Checker" }, LayoutCatalog.Load()));
                Assert.Equal(PlcAssignment.RevPi, other.Get("Feeder")!.Plc);
                Assert.Equal(PlcAssignment.M262, m262.Get("Feeder")!.Plc);
            });

            foreach (var (name, plc) in expected)
                Assert.Equal(plc, m262.Get(name)!.Plc);
        }

        [Fact] // a twin component the roster does not place must fail, not be skipped
        public void An_unallocated_twin_component_fails_planning_and_names_it()
        {
            var doctored = Path.Combine(Path.GetTempPath(), "unallocated_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                // Rename one actuator to a name no roster row and no alias covers.
                File.WriteAllText(doctored,
                    File.ReadAllText(Require("_se")).Replace(
                        "<Name>Checker</Name>", "<Name>Widget_Nobody_Allocated</Name>"));

                var ex = Assert.Throws<InvalidOperationException>(
                    () => GenerationContext.Plan(new MapperConfig(), doctored, DeploymentProfile.M262Only(LayoutCatalog.Load())));
                Assert.Contains("Widget_Nobody_Allocated", ex.Message, StringComparison.Ordinal);
                Assert.Contains("layout.yml", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                try { File.Delete(doctored); } catch { }
            }
        }
    }
}
