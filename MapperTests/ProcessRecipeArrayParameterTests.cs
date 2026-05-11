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
            // Phase-3 architectural truth (verified against the actuator's ECC):
            //   CmdStateArr   = TRANSIENT motion state (what the actuator's ECC fires on)
            //   Wait1State    = SETTLED state (what the actuator publishes after motion)
            // For Five_State_Actuator: CmdStateArr=Wait1State-1 (1↔2 extend, 3↔4 retract).
            // We assert the asymmetry — CmdStateArr is NOT equal to the following Wait1State.
            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                if (recipe.StepType[i] != 1) continue;
                Assert.True(i + 1 < recipe.StepType.Count, $"CMD at row {i} has no following row");
                Assert.Equal(2, recipe.StepType[i + 1]);
                // CMD is the transient (1 or 3); WAIT is the settled (2 or 4) — never equal.
                Assert.NotEqual(recipe.Wait1State[i + 1], recipe.CmdStateArr[i]);
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
        public void Feed_Station_CmdStateValues_AreActuatorTransientStateNumbers()
        {
            // Phase-3 fix (verified against FiveStateActuator.fbt's ECC):
            // CmdStateArr carries the TRANSIENT motion state number that the
            // receiving actuator's ECC reacts to (state_val=1 fires
            // AtHomeInit -> ToWork; state_val=3 fires AtWork -> ToHome).
            // Static settled states (0/2/4) are what the actuator PUBLISHES on
            // the ring after motion completes — they belong in Wait1State, NOT
            // CmdStateArr.
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                if (recipe.StepType[i] != 1) continue;
                int s = recipe.CmdStateArr[i];
                Assert.True(s == 1 || s == 3,
                    $"CMD row {i} target='{recipe.CmdTargetName[i]}' CmdState={s} " +
                    "is not a Five_State_Actuator transient command (1=extend, 3=retract).");
            }
        }

        [Fact]
        public void Feed_Station_TotalRowCount_IsRoughlyMotionTimes2PlusSettledPlusEnd()
        {
            // Phase-3 scope-filter update: source states whose only conditions reference
            // out-of-scope components (Assembly_Station, Disassembly, etc.) now collapse
            // to a single END row each, so the recipe can have MULTIPLE END rows in
            // addition to the appended-final END. The exact count depends on how the
            // station-grouping service treats cross-process references.
            //
            // Invariants that still hold:
            //   - At least 3 CMD rows (Feeder×2, Transfer or Checker)
            //   - For every CMD, exactly one matching WAIT
            //   - The recipe ends with at least one END row (NextStep loops to 0)
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);

            var recipe = ProcessRecipeArrayGenerator.Generate(process, contents, components);

            int waits = recipe.StepType.Count(s => s == 2);
            int cmds  = recipe.StepType.Count(s => s == 1);
            int ends  = recipe.StepType.Count(s => s == 9);

            Assert.True(cmds >= 3,         $"need ≥ 3 CMDs, got {cmds}");
            Assert.True(waits >= cmds,     $"need ≥ {cmds} WAITs (one per CMD + extras for settled states), got {waits}");
            Assert.True(ends >= 1,         $"need ≥ 1 END, got {ends}");
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
