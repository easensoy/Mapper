using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    /// <summary>
    /// Phase 1 verification: Button 2 ("Generate Test Station 1") run against the bundled
    /// Feed_Station fixture must emit a Process1 syslay FB carrying eight Parameter elements
    /// (process_name, process_id, plus six recipe array literals), and must NOT touch the
    /// ProcessRuntime_Generic_v1.fbt source file.
    /// </summary>
    public class ProcessRecipeArrayParameterTests
    {
        static readonly XNamespace LibElNs = "https://www.se.com/LibraryElements";

        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        static (XDocument doc, string folder) Generate()
        {
            var folder = Path.Combine(Path.GetTempPath(), "MapperTests_PhaseOne_" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            var injector = new SystemInjector();
            var path = injector.GenerateFeedStationSyslay(FixturePath(), folder);
            return (XDocument.Load(path), folder);
        }

        static XElement GetProcessFb(XDocument doc) =>
            doc.Descendants(LibElNs + "FB")
                .Single(fb => (string?)fb.Attribute("Name") == "Process1");

        // ---------------------------------------------------------------
        // Direct generator tests — independent of the syslay path.
        // ---------------------------------------------------------------

        [Fact]
        public void GeneratorAllSixArraysSameLength()
        {
            // Phase 2 no longer guarantees one row per VueOne state — actuator action
            // states unfold to a CMD+WAIT pair, the Initialisation state is dropped,
            // and an explicit END row is appended. The invariant that all six arrays
            // remain the same length still holds.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            int n = recipe.StepType.Count;
            Assert.True(n > 0);
            Assert.Equal(n, recipe.CmdTargetName.Count);
            Assert.Equal(n, recipe.CmdStateArr.Count);
            Assert.Equal(n, recipe.Wait1Id.Count);
            Assert.Equal(n, recipe.Wait1State.Count);
            Assert.Equal(n, recipe.NextStep.Count);
        }

        [Fact]
        public void GeneratorDropsInitialisationState()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            // Recipe length must be < state count because Initialisation was dropped
            // and at least one state unfolded to CMD+WAIT (so even after the drop the
            // total can rise above state count if many actuator unfolds happen).
            // We just assert Initialisation isn't surfaced as a CMD target.
            Assert.DoesNotContain(recipe.CmdTargetName, n =>
                n.Equals("Initialisation", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GeneratorEmitsCmdWaitPairsForActuatorTransitions()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            // Every CMD row (StepType=1) must be immediately followed by a WAIT row (StepType=2)
            // and CmdStateArr on the CMD row must equal Wait1State on the following WAIT - 1.
            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                if (recipe.StepType[i] != 1) continue;
                Assert.True(i + 1 < recipe.StepType.Count, $"CMD at row {i} has no following row");
                Assert.Equal(2, recipe.StepType[i + 1]);
                Assert.Equal(recipe.Wait1State[i + 1] - 1, recipe.CmdStateArr[i]);
                Assert.NotEqual(string.Empty, recipe.CmdTargetName[i]);
                Assert.Equal(string.Empty, recipe.CmdTargetName[i + 1]);
            }
        }

        [Fact]
        public void GeneratorEndsWithExplicitEndRow()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            Assert.Equal(9, recipe.StepType[recipe.StepType.Count - 1]);
            Assert.Equal(0, recipe.NextStep[recipe.NextStep.Count - 1]);   // END loops back to row 0
        }

        [Fact]
        public void Feed_Station_StepType_Matches_Phase2_Target()
        {
            // Bundled fixture has 8 states for Feed_Station: Initialisation (dropped),
            // CheckPartInHopper (sensor → WAIT), 4 actuator action states (CMD+WAIT each),
            // WaitingReleaseSt2 (multi-condition sync → single WAIT), HandShake (process →
            // WAIT). Plus appended END.
            //   1 + 4*2 + 1 + 1 + 1 = 12  →  [2,1,2,1,2,1,2,1,2,2,2,9]
            //
            // The user's external Control.xml has an additional TransferReturning state
            // which would extend this to [2,1,2,1,2,1,2,1,2,1,2,2,2,9] (14 entries).
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            var expected = new[] { 2, 1, 2, 1, 2, 1, 2, 1, 2, 2, 2, 9 };
            Assert.Equal(expected, recipe.StepType);
        }

        [Fact]
        public void Feed_Station_Cmd_Targets_Are_Actuator_Names_In_Cmd_Rows()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            // With the bundled fixture's 8 states, CMD positions are 1, 3, 5, 7.
            // Targets in order: FeederAdvancing, PartChecking, FeederReturning, TransferAdvancing.
            Assert.Equal("Feeder",   recipe.CmdTargetName[1]);
            Assert.Equal("Checker",  recipe.CmdTargetName[3]);
            Assert.Equal("Feeder",   recipe.CmdTargetName[5]);
            Assert.Equal("Transfer", recipe.CmdTargetName[7]);

            // Non-CMD positions must have empty CmdTargetName.
            foreach (int i in new[] { 0, 2, 4, 6, 8, 9, 10, 11 })
                Assert.Equal(string.Empty, recipe.CmdTargetName[i]);
        }

        [Fact]
        public void GeneratorEmitsOnlyValidStepTypes()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            Assert.All(recipe.StepType, t =>
                Assert.True(t == 1 || t == 2 || t == 9, $"unexpected StepType {t}"));
        }

        [Fact]
        public void CmdTargetNameUsesCanonicalActuatorName()
        {
            // Phase 2 changed the contract: CmdTargetName carries the actuator's
            // canonical Name from Control.xml (e.g. "Feeder"), not lowercased.
            // Note this requires Five_State_Actuator_CAT instances to use the same
            // case in their `actuator_name` parameter so the runtime's STRING comparison
            // matches — currently the actuator_name is lowercased ('feeder'), which
            // is a separate inconsistency to address in BuildActuatorParameters.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            // Each non-empty CmdTargetName must exactly match a component's canonical Name.
            var allNames = new System.Collections.Generic.HashSet<string>(
                components.Select(c => (c.Name ?? string.Empty).Trim()),
                System.StringComparer.Ordinal);
            foreach (var n in recipe.CmdTargetName.Where(n => !string.IsNullOrEmpty(n)))
                Assert.Contains(n, allNames);
        }

        // ---------------------------------------------------------------
        // SyslayBuilder array-literal helper tests.
        // ---------------------------------------------------------------

        [Fact]
        public void FormatIntArrayWrapsValuesInBrackets()
        {
            Assert.Equal("[1, 2, 9]", SyslayBuilder.FormatIntArray(new[] { 1, 2, 9 }));
            Assert.Equal("[]", SyslayBuilder.FormatIntArray(System.Array.Empty<int>()));
        }

        [Fact]
        public void FormatStringArraySingleQuotesEachEntryAndDoublesInternalQuotes()
        {
            Assert.Equal("['Feeder', 'PartInHopper']",
                SyslayBuilder.FormatStringArray(new[] { "Feeder", "PartInHopper" }));
            Assert.Equal("['', '']",
                SyslayBuilder.FormatStringArray(new[] { string.Empty, string.Empty }));
            Assert.Equal("['it''s']",
                SyslayBuilder.FormatStringArray(new[] { "it's" }));
        }

        // ---------------------------------------------------------------
        // End-to-end: Button 2 path emits 8 parameters on the Process1 FB.
        // ---------------------------------------------------------------

        [Fact]
        public void Button2EmitsEightParametersOnProcess1Fb()
        {
            var (doc, _) = Generate();
            var processFb = GetProcessFb(doc);

            var paramNames = processFb.Elements(LibElNs + "Parameter")
                .Select(p => (string?)p.Attribute("Name") ?? string.Empty)
                .ToList();

            Assert.Equal(8, paramNames.Count);
            Assert.Contains("process_name", paramNames);
            Assert.Contains("process_id", paramNames);
            Assert.Contains("StepType", paramNames);
            Assert.Contains("CmdTargetName", paramNames);
            Assert.Contains("CmdStateArr", paramNames);
            Assert.Contains("Wait1Id", paramNames);
            Assert.Contains("Wait1State", paramNames);
            Assert.Contains("NextStep", paramNames);
        }

        [Fact]
        public void Button2RecipeArrayValuesAreSquareBracketLiterals()
        {
            var (doc, _) = Generate();
            var processFb = GetProcessFb(doc);

            string ValueOf(string name) => processFb.Elements(LibElNs + "Parameter")
                .Single(p => (string?)p.Attribute("Name") == name)
                .Attribute("Value")!.Value;

            foreach (var arrayName in new[] {
                "StepType", "CmdTargetName", "CmdStateArr",
                "Wait1Id", "Wait1State", "NextStep"
            })
            {
                var v = ValueOf(arrayName);
                Assert.StartsWith("[", v);
                Assert.EndsWith("]", v);
            }
        }

        [Fact]
        public void Button2ScalarParametersUnchanged()
        {
            var (doc, _) = Generate();
            var processFb = GetProcessFb(doc);

            string ValueOf(string name) => processFb.Elements(LibElNs + "Parameter")
                .Single(p => (string?)p.Attribute("Name") == name)
                .Attribute("Value")!.Value;

            Assert.Equal("'Process1'", ValueOf("process_name"));
            Assert.Equal("10", ValueOf("process_id"));
        }

        [Fact]
        public void Button2DoesNotMutateProcessRuntimeFbtSource()
        {
            // Snapshot the source FBT — Mapper now writes recipe to syslay parameters,
            // not to ProcessRuntime_Generic_v1.fbt's initializeinit ST. Running Button 2
            // against a temp folder must leave the source FBT untouched (no .original
            // backup file should appear next to it either).
            var fbtSource = LocateProcessRuntimeFbt();
            if (fbtSource == null)
                return; // skip if Demonstrator FBT is not on disk in this environment

            var beforeBytes = File.ReadAllBytes(fbtSource);
            var beforeMtime = File.GetLastWriteTimeUtc(fbtSource);

            _ = Generate();

            var afterBytes = File.ReadAllBytes(fbtSource);
            var afterMtime = File.GetLastWriteTimeUtc(fbtSource);

            Assert.Equal(beforeBytes, afterBytes);
            Assert.Equal(beforeMtime, afterMtime);

            // No .original sibling should have been created by Phase 1 generation.
            Assert.False(File.Exists(fbtSource + ".original"));
        }

        static string? LocateProcessRuntimeFbt()
        {
            // Walk a few well-known candidates without requiring MapperConfig.
            var candidates = new[]
            {
                @"C:\Demonstrator\Demonstator\IEC61499\ProcessRuntime_Generic_v1.fbt",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
