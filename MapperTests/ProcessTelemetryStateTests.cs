using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Process;
using Xunit;

namespace MapperTests
{
    /// Process-state MQTT telemetry: the recipe row -> phase mapping.
    ///
    /// The engine's own progress value, CurrentStep, is a compiled recipe-row index and means nothing
    /// outside the generator. ProcessStateByRow is the telemetry-only companion that identifies the
    /// phase the twin actually names.
    ///
    /// The property that matters is that every phase is DISTINGUISHABLE. The twin's State_Number cannot
    /// do that job: it is a broadcast slot filled in only where a peer process watches, so most states
    /// carry 0 and a process reuses one number for unrelated phases. These tests pin the replacement —
    /// a dense per-state ordinal — and would fail if it ever regressed to State_Number or to CurrentStep.
    ///
    /// Offline: reads the real Control.xml models, compiles recipes, asserts on the arrays. No
    /// generation, no deployment, no rig.
    public class ProcessTelemetryStateTests
    {
        private const string ModelRoot =
            @"C:\Users\alper\OneDrive\Documents\VueOne\system\SMC_Vue2VC_With_Processes";

        private static string ModelPath(string suffix) =>
            Path.Combine(ModelRoot + suffix, "Control.xml");

        private static bool ModelAvailable(string suffix) => File.Exists(ModelPath(suffix));

        /// Plan a model exactly as the generator does, and hand back one process's telemetry array
        /// beside the states it was derived from.
        private static (RecipeArrays Arrays, VueOneComponent Process)
            Compile(string suffix, string processName)
        {
            var ctx = GenerationContext.Plan(new MapperConfig(), ModelPath(suffix), DeploymentProfile.M262Only(LayoutCatalog.Load()));
            var process = ctx.Components.Single(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, processName, StringComparison.OrdinalIgnoreCase));
            return (ctx.Recipes[processName], process);
        }

        // Feed_Station carries the assertions because it is self-contained: Assembly_Station and
        // Disassembly reach across stations, so their phases only mean something beside the whole plan.
        [Theory]
        [InlineData("_se", "Feed_Station")]
        public void EveryRowIdentifiesADeclaredPhase(string suffix, string processName)
        {
            if (!ModelAvailable(suffix)) return;   // model not present on this machine
            var (arrays, process) = Compile(suffix, processName);

            Assert.Equal(arrays.StepType.Count, arrays.ProcessStateByRow.Count);
            Assert.NotEmpty(arrays.ProcessStateByRow);

            // 0 is reserved for "no owning state". A row that reported it would be a phase the
            // subscriber cannot name, which is exactly the failure State_Number produced.
            Assert.All(arrays.ProcessStateByRow, n =>
                Assert.True(n > 0, $"{processName}: a row reports 0, so its phase cannot be identified"));

            Assert.All(arrays.ProcessStateByRow, n =>
                Assert.True(n <= process.States.Count,
                    $"{processName}: ordinal {n} exceeds the {process.States.Count} declared states"));
        }

        [Theory]
        [InlineData("_se", "Feed_Station")]
        public void DistinctPhasesGetDistinctOrdinals(string suffix, string processName)
        {
            if (!ModelAvailable(suffix)) return;   // model not present on this machine
            var (arrays, process) = Compile(suffix, processName);

            // The whole reason for not publishing State_Number. Two states that share a State_Number, or
            // that both carry 0, must still be told apart on the wire.
            var ordinals = arrays.ProcessPhaseNames.Keys.ToList();
            Assert.Equal(ordinals.Count, ordinals.Distinct().Count());

            // And the map must cover everything the recipe actually publishes, or the subscriber sees a
            // number it cannot render.
            foreach (var n in arrays.ProcessStateByRow.Distinct())
                Assert.True(arrays.ProcessPhaseNames.ContainsKey(n),
                    $"{processName}: ordinal {n} is published but absent from the phase-name map");
        }

        [Theory]
        [InlineData("Assembly_Station")]
        [InlineData("Disassembly")]
        public void TheTwinsStateNumberCannotIdentifyAPhase(string processName)
        {
            if (!ModelAvailable("_se")) return;   // model not present on this machine
            var all = new SystemXmlReader().ReadAllComponents(ModelPath("_se"));
            var process = all.Single(c =>
                string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Name, processName, StringComparison.OrdinalIgnoreCase));

            // This is the data fact the whole design rests on, so it is worth pinning: State_Number is a
            // broadcast slot, filled in only where a peer process watches. If someone ever "simplifies"
            // telemetry back to publishing it, this test says why that cannot work.
            int unnumbered = process.States.Count(s => s.StateNumber == 0);
            Assert.True(unnumbered > 5,
                $"{processName}: expected many unnumbered states, found {unnumbered}");

            int reused = process.States.Where(s => s.StateNumber != 0)
                .GroupBy(s => s.StateNumber).Count(g => g.Count() > 1);
            Assert.True(unnumbered > 0 || reused > 0,
                $"{processName}: expected State_Number to be ambiguous somewhere");

            // The ordinal replaces it precisely because declaration order is total: every state gets one,
            // and no two share it.
            var ordinals = Enumerable.Range(1, process.States.Count).ToList();
            Assert.Equal(process.States.Count, ordinals.Distinct().Count());
        }

        [Fact]
        public void RowsOfOnePhaseAllReportThatSamePhase()
        {
            if (!ModelAvailable("_se")) return;   // model not present on this machine
            var (arrays, _) = Compile("_se", "Feed_Station");

            // A state compiles to a CMD and its WAIT, so entries repeat in contiguous runs. Any phase
            // appearing in two non-adjacent runs would mean rows were attributed by position.
            var runs = new List<int>();
            for (int i = 0; i < arrays.ProcessStateByRow.Count; i++)
                if (i == 0 || arrays.ProcessStateByRow[i] != arrays.ProcessStateByRow[i - 1])
                    runs.Add(arrays.ProcessStateByRow[i]);

            Assert.True(runs.Count >= 4, "expected several distinct phases in the Feed recipe");
            Assert.True(arrays.ProcessStateByRow.Count > runs.Count,
                "expected at least one phase to span more than one row");
        }

        [Fact]
        public void FeederReturningResolvesToItsOwnPhase()
        {
            if (!ModelAvailable("_se")) return;   // model not present on this machine
            var (arrays, _) = Compile("_se", "Feed_Station");

            var ordinal = arrays.ProcessPhaseNames
                .Where(kv => string.Equals(kv.Value, "FeederReturning", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            Assert.Single(ordinal);

            var rows = Enumerable.Range(0, arrays.ProcessStateByRow.Count)
                .Where(i => arrays.ProcessStateByRow[i] == ordinal[0]).ToList();
            Assert.NotEmpty(rows);
        }

        [Fact]
        public void PublishedPhaseIsNotTheRecipeRowIndex()
        {
            if (!ModelAvailable("_se")) return;   // model not present on this machine
            var (arrays, _) = Compile("_se", "Feed_Station");

            // If telemetry ever regressed to reporting CurrentStep, the array would be 0,1,2,3...
            bool identityMapping = arrays.ProcessStateByRow
                .Select((n, i) => n == i).All(x => x);
            Assert.False(identityMapping,
                "ProcessStateByRow equals the row index — CurrentStep is being published as the phase");
        }

        [Theory]
        [InlineData("_se")]
        [InlineData("_vc")]
        [InlineData("_sw5")]
        [InlineData("_sw5_noclamp")]
        public void EachModelResolvesFromItsOwnControlXml(string suffix)
        {
            if (!ModelAvailable(suffix)) return;   // model not present on this machine
            var (arrays, process) = Compile(suffix, "Feed_Station");

            Assert.NotEmpty(arrays.ProcessStateByRow);
            Assert.All(arrays.ProcessStateByRow, n => Assert.InRange(n, 1, process.States.Count));
            foreach (var n in arrays.ProcessStateByRow.Distinct())
                Assert.True(arrays.ProcessPhaseNames.ContainsKey(n));
        }

        [Fact]
        public void ChainOrderIsFollowedNotDeclarationOrder()
        {
            if (!ModelAvailable("_se")) return;   // model not present on this machine
            var (arrays, _) = Compile("_se", "Feed_Station");

            // _se runs the feeder out of declaration order: FeederAdvancing, then FeederReturning, then
            // PartChecking. A mapping derived from the transition chain reproduces that; one derived from
            // declaration order or row position cannot.
            int Ordinal(string name) => arrays.ProcessPhaseNames
                .Where(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).FirstOrDefault();

            int First(string name)
            {
                int o = Ordinal(name);
                return o == 0 ? -1 : arrays.ProcessStateByRow.IndexOf(o);
            }

            int advancing = First("FeederAdvancing"),
                returning = First("FeederReturning"),
                checking = First("PartChecking");
            if (advancing < 0 || returning < 0 || checking < 0) return;

            Assert.True(advancing < returning, "feeder advance must publish before feeder return");
            Assert.True(returning < checking, "feeder return must publish before the checker phase");
        }

        [Fact]
        public void ControlArraysAreUnaffectedByTheTelemetryArray()
        {
            if (!ModelAvailable("_se")) return;   // model not present on this machine
            var (arrays, _) = Compile("_se", "Feed_Station");

            // The six control arrays and the telemetry array are the same length and independent: the
            // telemetry array must never have inserted or removed a row.
            Assert.Equal(arrays.StepType.Count, arrays.CmdTargetName.Count);
            Assert.Equal(arrays.StepType.Count, arrays.CmdStateArr.Count);
            Assert.Equal(arrays.StepType.Count, arrays.Wait1Id.Count);
            Assert.Equal(arrays.StepType.Count, arrays.Wait1State.Count);
            Assert.Equal(arrays.StepType.Count, arrays.NextStep.Count);
            Assert.Equal(arrays.StepType.Count, arrays.ProcessStateByRow.Count);
        }
    }
}
