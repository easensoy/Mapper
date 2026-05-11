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
                .Single(fb => (string?)fb.Attribute("Type") == "Process1_Generic");

        // ---------------------------------------------------------------
        // Direct generator tests — independent of the syslay path.
        // ---------------------------------------------------------------

        [Fact]
        public void GeneratorAllSixArraysSameLength()
        {
            // Phase 2 no longer guarantees one row per VueOne state — motion-verb
            // states unfold to a CMD+WAIT pair and an explicit END row is appended.
            // The invariant that all six arrays remain the same length still holds.
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
        public void GeneratorEmitsCmdWaitPairs_CmdStateMatchesFollowingWaitState()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            // Every CMD row (StepType=1) must be immediately followed by a WAIT row (StepType=2).
            // Bug-2-fix contract: CmdStateArr carries the actuator's canonical destination
            // state number (read by StateID lookup on the actuator), which equals Wait1State
            // on the following WAIT row.
            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                if (recipe.StepType[i] != 1) continue;
                Assert.True(i + 1 < recipe.StepType.Count, $"CMD at row {i} has no following row");
                Assert.Equal(2, recipe.StepType[i + 1]);
                Assert.Equal(recipe.Wait1State[i + 1], recipe.CmdStateArr[i]);
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
        public void Feed_Station_HasAtLeastThreeCmdRowsForFeederCheckerTransfer()
        {
            // Bug-1 verification: with classifier dispatching on source-state name,
            // FeederAdvancing / PartChecking / FeederReturning / TransferAdvancing
            // all match a motion verb and unfold to CMD+WAIT pairs. The fixture must
            // therefore produce at least 3 CMD rows targeting feeder, checker, transfer.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            int cmdCount = recipe.StepType.Count(s => s == 1);
            Assert.True(cmdCount >= 3, $"expected ≥ 3 CMD rows, got {cmdCount}; recipe = [{string.Join(",", recipe.StepType)}]");

            var nonEmptyTargets = recipe.CmdTargetName
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            Assert.Contains("feeder",   nonEmptyTargets);
            Assert.Contains("checker",  nonEmptyTargets);
            Assert.Contains("transfer", nonEmptyTargets);
        }

        [Fact]
        public void Feed_Station_CmdStateValues_AreActuatorCanonicalStateNumbers()
        {
            // Bug-2 verification: CmdStateArr at each CMD row must be a state number
            // that actually exists on the corresponding actuator (read by StateID, not
            // guessed by name). For Five_State_Actuator the canonical static states are
            // 0 (ReturnedHome), 2 (Advanced), 4 (ReturnedFinished). Every emitted
            // CmdState must be one of those.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                if (recipe.StepType[i] != 1) continue;
                int s = recipe.CmdStateArr[i];
                Assert.True(s == 0 || s == 2 || s == 4,
                    $"CMD row {i} target='{recipe.CmdTargetName[i]}' CmdState={s} " +
                    "is not a Five_State_Actuator canonical static state (0/2/4).");
            }
        }

        [Fact]
        public void Feed_Station_TotalRowCount_IsRoughlyMotionTimes2PlusSettledPlusEnd()
        {
            // Total rows = (motion states × 2) + settled states + 1 (END).
            // Bundled fixture: 4 motion states (FeederAdvancing, PartChecking,
            // FeederReturning, TransferAdvancing) + 4 settled states (Initialisation,
            // CheckPartInHopper, WaitingReleaseSt2, HandShake) + END = 4*2 + 4 + 1 = 13.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            int waits = recipe.StepType.Count(s => s == 2);
            int cmds  = recipe.StepType.Count(s => s == 1);
            int ends  = recipe.StepType.Count(s => s == 9);

            Assert.True(cmds >= 3,                $"need ≥ 3 CMDs, got {cmds}");
            Assert.True(waits >= cmds + 2,        $"need ≥ {cmds + 2} WAITs (one per CMD + settled states), got {waits}");
            Assert.Equal(1, ends);                // exactly one final END
            Assert.Equal(9, recipe.StepType[^1]);
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
        public void CmdTargetNameIsLowercased_MatchingFiveStateActuatorActuatorName()
        {
            // Phase-2 spec: CmdTargetName is lowercased on emission so it matches
            // Five_State_Actuator_CAT.actuator_name (which BuildActuatorParameters
            // also lowercases). The runtime's STRING comparison is case-sensitive.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            foreach (var n in recipe.CmdTargetName.Where(n => !string.IsNullOrEmpty(n)))
                Assert.Equal(n.ToLowerInvariant(), n);
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

            // Phase 2 update: process_name now derives from the Control.xml process
            // component's canonical Name (e.g. "Feed_Station") rather than a hardcoded
            // "Process1". Matches the FB instance Name on the canvas.
            Assert.Equal("'Feed_Station'", ValueOf("process_name"));
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
