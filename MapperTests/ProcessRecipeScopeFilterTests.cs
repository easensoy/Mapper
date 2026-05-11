using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.IO;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    /// <summary>
    /// Phase 3 scope-filter contract for Button 2 ("Generate Test Station 1").
    /// Verifies that:
    ///   1. Process1 has 8 Parameter elements (process_name, process_id, plus 6 arrays)
    ///   2. Every Wait1Id is either 0 or a valid in-scope component id
    ///   3. No Wait1Id ever equals process_id (the assertion in the generator
    ///      throws InvalidOperationException if this is violated, so we just
    ///      sanity-assert here)
    ///   4. Every non-empty CmdTargetName is the lowercase Name of an in-scope actuator
    ///   5. StepType contains at least one CMD row (value 1)
    ///   6. Skipped-conditions list contains entries for the out-of-scope component
    ///      references that exist in Feed_Station_Fixture.xml but are filtered out
    ///      by Button 2's scope (Assembly_Station, Checker, Transfer, Disassembly)
    /// </summary>
    public class ProcessRecipeScopeFilterTests
    {
        static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        static string FixturePath() =>
            Path.Combine(System.AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        // Replicate Button 2's scope filter (Feeder + PartInHopper only)
        // exactly the way SystemLayoutInjector.GenerateFeedStationSyslayToPath does.
        static StationContents Button2Scope(System.Collections.Generic.List<CodeGen.Models.VueOneComponent> all)
        {
            var process = all.First(c =>
                c.Type == "Process" &&
                c.Name.Equals("Feed_Station", System.StringComparison.OrdinalIgnoreCase));
            return new StationContents(
                process,
                all.Where(c => c.Type == "Actuator" && c.Name == "Feeder").ToList(),
                all.Where(c => c.Name == "PartInHopper").ToList());
        }

        [Fact]
        public void Process1_HasEightParameters_OnButton2_Generated_Syslay()
        {
            var (doc, _) = GenerateButton2();
            var process = GetProcessFb(doc);
            Assert.Equal(8, process.Elements(Ns + "Parameter").Count());
        }

        [Fact]
        public void EveryWait1Id_IsZeroOrInScopeComponentId()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);

            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            // Allowed: 0 (CMD/END row default) plus 0..N+M-1 where N=sensors, M=actuators.
            int sensorCount   = contents.Sensors.Count;
            int actuatorCount = contents.Actuators.Count;
            var allowedIds = new HashSet<int> { 0 };
            for (int i = 0; i < sensorCount + actuatorCount; i++) allowedIds.Add(i);

            foreach (var id in recipe.Wait1Id)
                Assert.Contains(id, allowedIds);
        }

        [Fact]
        public void NoWait1Id_EqualsProcessId()
        {
            // The generator's defensive assertion will throw if this contract is
            // violated. We exercise the generator here and additionally double-check
            // the array values for a belt-and-braces guarantee.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);

            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            Assert.DoesNotContain(10, recipe.Wait1Id);
        }

        [Fact]
        public void ProcessIdAssertion_ThrowsWhenWait1IdCollidesWithProcessId()
        {
            // Synthetic check: we don't have a way to force a collision in the
            // current code path, but the assertion exists. To prove it works,
            // call Generate with a deliberately-tiny processId that matches the
            // actuator's id (id=1 corresponds to Feeder when Sensors.Count=1).
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);

            // sensors.Count=1 (PartInHopper), actuators.Count=1 (Feeder) -> registry
            // ids are PartInHopper=0, Feeder=1. Setting processId=1 forces Wait1Id
            // to collide with process_id; the generator must throw.
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                ProcessRecipeArrayGenerator.Generate(contents.Process, contents, components, processId: 1));
            Assert.Contains("process_id", ex.Message);
        }

        [Fact]
        public void EveryCmdTargetName_IsLowercaseInScopeActuatorName()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);
            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            var allowedTargets = new HashSet<string>(
                contents.Actuators.Select(a => (a.Name ?? string.Empty).ToLowerInvariant()),
                System.StringComparer.Ordinal);

            foreach (var t in recipe.CmdTargetName.Where(t => !string.IsNullOrEmpty(t)))
                Assert.Contains(t, allowedTargets);
        }

        [Fact]
        public void StepType_ContainsAtLeastOneCmdRow()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);
            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            Assert.Contains(1, recipe.StepType);
        }

        [Fact]
        public void StepType_HasExactlyOneEndMarker_AtFinalIndex()
        {
            // Phase-3 revision: skipped out-of-scope rows are DROPPED ENTIRELY
            // (they used to emit a placeholder StepType=9 which halted the
            // engine). The recipe's StepType array must therefore contain
            // exactly one 9, and only at the last index.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);
            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            int endCount = recipe.StepType.Count(s => s == 9);
            Assert.Equal(1, endCount);
            Assert.Equal(9, recipe.StepType[^1]);

            for (int i = 0; i < recipe.StepType.Count - 1; i++)
                Assert.NotEqual(9, recipe.StepType[i]);
        }

        [Fact]
        public void Index0_IsTheFirstSurvivingRealRow_NotEndMarker()
        {
            // Bug being prevented: previously the recipe started with [9, 2, 1, 2, 9, ...]
            // — the index-0 END halted the engine before any meaningful row ran.
            // For Feed_Station Button-2 scope, the first surviving state is
            // CheckPartInHopper (settled WAIT on PartInHopper), so index 0 must be 2 (WAIT).
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);
            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            Assert.NotEqual(9, recipe.StepType[0]);
            Assert.True(recipe.StepType[0] == 1 || recipe.StepType[0] == 2,
                $"index 0 should be a real CMD or WAIT row; got StepType={recipe.StepType[0]}");
        }

        [Fact]
        public void SkippedConditions_ContainsOutOfScope_References()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var contents = Button2Scope(components);
            var recipe = ProcessRecipeArrayGenerator.Generate(
                contents.Process, contents, components, processId: 10);

            // The Feed_Station fixture references Assembly_Station, Checker, Transfer,
            // and Disassembly in various transition conditions. Button 2's scope strips
            // them, so each of those names should appear at least once in the skipped list.
            string skippedDump = string.Join("\n", recipe.SkippedConditions);
            foreach (var expected in new[] { "Assembly_Station", "Checker", "Transfer", "Disassembly" })
                Assert.Contains(expected, skippedDump);
        }

        // ----------------- helpers -----------------

        static (XDocument doc, string folder) GenerateButton2()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MapperTests_ScopeFilter_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var injector = new SystemInjector();
            var path = injector.GenerateFeedStationSyslay(FixturePath(), folder);
            return (XDocument.Load(path), folder);
        }

        static XElement GetProcessFb(XDocument doc) =>
            doc.Descendants(Ns + "FB")
                .Single(fb => (string?)fb.Attribute("Type") == "Process1_Generic");
    }
}
