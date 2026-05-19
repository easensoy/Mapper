using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.IO;
using CodeGen.Translation;
using MapperUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace MapperTests
{
    /// <summary>
    /// Defect 3: the recipe must be SERIALISED so that no actuator advances
    /// while a forgotten-retract actuator is still atwork. The earlier
    /// batch-all-retracts-at-the-end logic let Transfer fully cycle while
    /// Checker stayed atwork (Checker only retracted after Transfer's whole
    /// advance/retract) — a physical rig collision. These tests pin the
    /// collision-safe ordering against the SMC_Vue2VC fixture.
    /// </summary>
    public class RecipeSerialisedOrderingTests
    {
        readonly ITestOutputHelper _out;
        public RecipeSerialisedOrderingTests(ITestOutputHelper o) => _out = o;

        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Feed_Station_Fixture.xml");

        static RecipeArrays Recipe()
        {
            var components = new SystemXmlReader().ReadAllComponents(FixturePath());
            var process = components.First(c => c.Type == "Process");
            var contents = new StationGroupingService().GroupStationContents(process, components);
            return ProcessRecipeArrayGenerator.Generate(process, contents, components);
        }

        // First advance (CmdState=1) / retract (CmdState=3) CMD row index for a target.
        static int CmdRow(RecipeArrays r, string target, int cmdState)
        {
            for (int i = 0; i < r.StepType.Count; i++)
                if (r.StepType[i] == 1 &&
                    r.CmdStateArr[i] == cmdState &&
                    string.Equals((r.CmdTargetName[i] ?? "").Trim(), target,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        [Fact]
        public void Generates_WithoutStrandingAnyActuator()
        {
            var r = Recipe();
            var adv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.StepType.Count; i++)
            {
                if (r.StepType[i] != 1) continue;
                var t = (r.CmdTargetName[i] ?? "").Trim();
                if (t.Length == 0) continue;
                if (r.CmdStateArr[i] == 1) adv.Add(t);
                else if (r.CmdStateArr[i] == 3) ret.Add(t);
            }
            Assert.All(adv, a => Assert.Contains(a, ret));
        }

        [Fact]
        public void Checker_ReturnsHome_BeforeTransfer_Advances()
        {
            // THE collision the user reported: Transfer advancing while Checker
            // is still atwork. Checker's retract must complete (its athome WAIT
            // must appear) strictly before Transfer's advance CMD.
            var r = Recipe();
            _out.WriteLine("Ordering: " + r.OrderingSummary);

            int checkerRetract = CmdRow(r, "checker", 3);
            int transferAdvance = CmdRow(r, "transfer", 1);
            Assert.True(checkerRetract >= 0, "no checker retract CMD emitted");
            Assert.True(transferAdvance >= 0, "no transfer advance CMD emitted");

            // The checker athome WAIT is the row right after the retract CMD.
            int checkerAtHomeWait = checkerRetract + 1;
            Assert.Equal(2, r.StepType[checkerAtHomeWait]);
            Assert.Equal(4, r.Wait1State[checkerAtHomeWait]);   // 4 = athome

            Assert.True(checkerAtHomeWait < transferAdvance,
                $"Checker athome (row {checkerAtHomeWait}) must precede Transfer advance " +
                $"(row {transferAdvance}). Ordering: {r.OrderingSummary}");
        }

        [Fact]
        public void StrandedChecker_RetractIsNestedInPlace_NotBatchedAtEnd()
        {
            // Checker is the stranded actuator (Control.xml advances it but
            // never retracts it). Its auto-inserted retract CMD must sit
            // IMMEDIATELY after its atwork-confirmation WAIT — proving the
            // retract is nested in place, not appended at the global end.
            var r = Recipe();

            int checkerAdvance = CmdRow(r, "checker", 1);
            Assert.True(checkerAdvance >= 0, "no checker advance CMD emitted");

            // The WAIT the advance CMD leads to confirms Checker reached its
            // settled work position. (Checker's kinematics are Lowering→Down→
            // Rising, so its settled-work state number is Control.xml-derived
            // and NOT necessarily 2 — we assert the STRUCTURE, not the number.)
            int atworkWait = r.NextStep[checkerAdvance];
            Assert.Equal(2, r.StepType[atworkWait]);            // it is a WAIT row

            int checkerRetract = CmdRow(r, "checker", 3);
            Assert.Equal(atworkWait + 1, checkerRetract);       // nested right after

            // And it is NOT the last CMD before END (the old bug appended it last).
            int endRow = r.StepType.Count - 1;
            Assert.Equal(9, r.StepType[endRow]);
            int lastCmd = -1;
            for (int i = 0; i < r.StepType.Count; i++)
                if (r.StepType[i] == 1) lastCmd = i;
            Assert.NotEqual(checkerRetract, lastCmd);
        }

        [Fact]
        public void Checker_RetractsBeforeFeeder_LifoNesting()
        {
            // Feeder advances first and holds the part; Checker advances
            // (nested), then Checker must retract BEFORE Feeder retracts
            // (LIFO). The old batch-at-end logic put Feeder's retract before
            // the deferred Checker retract.
            var r = Recipe();
            int feederAdvance = CmdRow(r, "feeder", 1);
            int checkerAdvance = CmdRow(r, "checker", 1);
            int checkerRetract = CmdRow(r, "checker", 3);
            int feederRetract = CmdRow(r, "feeder", 3);

            Assert.True(feederAdvance < checkerAdvance,
                "feeder should advance before checker");
            Assert.True(checkerRetract < feederRetract,
                $"checker must retract (row {checkerRetract}) before feeder retracts " +
                $"(row {feederRetract}) — LIFO nesting. Ordering: {r.OrderingSummary}");
        }

        [Fact]
        public void OrderingSummary_IsPopulated_AndEndsWithEnd()
        {
            var r = Recipe();
            Assert.False(string.IsNullOrWhiteSpace(r.OrderingSummary));
            Assert.EndsWith("END", r.OrderingSummary.Trim());
            _out.WriteLine(r.OrderingSummary);
        }

        [Fact]
        public void NoActuatorAdvances_WhileAnEarlierStrandedActuatorIsStillAtwork()
        {
            // General serialisation invariant. Walk rows in order tracking the
            // set of actuators that have advanced but not yet retracted. When
            // any actuator issues an advance, assert no OTHER actuator that
            // advanced earlier is still atwork at that row UNLESS it retracts
            // later but before this one (the legitimate feeder-holds-checker
            // nest is LIFO, so the only allowed still-atwork actuator is one
            // that retracts AFTER the new actuator — i.e. strictly enclosing).
            var r = Recipe();
            var advRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var retRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.StepType.Count; i++)
            {
                if (r.StepType[i] != 1) continue;
                var t = (r.CmdTargetName[i] ?? "").Trim();
                if (t.Length == 0) continue;
                if (r.CmdStateArr[i] == 1 && !advRow.ContainsKey(t)) advRow[t] = i;
                if (r.CmdStateArr[i] == 3 && !retRow.ContainsKey(t)) retRow[t] = i;
            }

            foreach (var (actB, rB) in advRow)
            foreach (var (actA, rA) in advRow)
            {
                if (string.Equals(actA, actB, StringComparison.OrdinalIgnoreCase)) continue;
                if (rA >= rB) continue;                       // A advanced before B
                int aRet = retRow[actA];
                // Allowed iff A retracts before B advances (A home first) OR A
                // retracts AFTER B retracts (A strictly encloses B — LIFO hold).
                bool aHomeBeforeBAdvances = aRet < rB;
                bool aEnclosesB = aRet > retRow[actB];
                Assert.True(aHomeBeforeBAdvances || aEnclosesB,
                    $"'{actB}' advances at row {rB} while '{actA}' (advanced row {rA}, " +
                    $"retracts row {aRet}) is atwork and does NOT enclose it — collision. " +
                    $"Ordering: {r.OrderingSummary}");
            }
        }
    }
}
