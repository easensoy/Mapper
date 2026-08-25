using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using CodeGen.Models;
using CodeGen.Translation.Process;
using Xunit;

namespace MapperTests
{
    // The recipe an operator READS must be the recipe the controller EXECUTES.
    //
    // These tests build a small plant in code - no SMC names, no fixture file - and run the
    // production presenter over the SAME typed RecipeArrays the compiler hands the emitters. A row
    // that renders a plausible label for something it could not resolve is the failure mode being
    // pinned: it is indistinguishable, on the panel, from a row that is right.
    public class HmiRecipePresentationTests
    {
        // ---- a plant with no SMC in it -------------------------------------------------------

        private const string Ring = "line";

        private static HmiComponent Comp(string name, string ring = Ring, int slot = 0,
                                         params (int Value, string Name)[] states) =>
            new(ComponentId: "C-" + name,
                InstanceName: name,
                DisplayName: CodeGen.Hmi.HmiPlanner.Humanise(name),
                CatType: "Five_State_Actuator_CAT",
                TagName: "TAG" + name,
                Controller: CodeGen.Translation.PlcAssignment.M262,
                Resource: "RES0",
                Slot: slot,
                States: states.Select(s => new HmiStateName(s.Value, s.Name)).ToList(),
                Interlocks: Array.Empty<HmiInterlockRule>(),
                Capabilities: Array.Empty<HmiCapability>(),
                Ring: ring);

        // The real slot index, built the way the builder builds it.
        private static HmiSlotIndex Slots(params (string Name, int Slot)[] slots) =>
            HmiSlotIndex.Build(_ => Ring, slots.ToDictionary(x => x.Name, x => x.Slot, StringComparer.Ordinal));

        private static HmiDefinition Def()
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "Config", "hmi.yml");
            var path = File.Exists(beside)
                ? beside
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "CodeGen", "CodeGen", "Config", "hmi.yml"));
            return HmiDefinitionLoader.Parse(File.ReadAllText(path));
        }

        private sealed class Builder
        {
            private readonly RecipeArrays _a = new();

            internal Builder Cmd(string target, int state, int phase = -1)
                => Row(StepType.Cmd, target, state, 0, 0, phase);

            internal Builder Wait(int slot, int state, int phase = -1)
                => Row(StepType.Wait, string.Empty, 0, slot, state, phase);

            internal Builder End() => Row(StepType.End, string.Empty, 0, 0, 0, -1);

            internal Builder Phase(int value, string name)
            {
                _a.ProcessPhaseNames[value] = name;
                return this;
            }

            private Builder Row(int type, string target, int cmdState, int slot, int waitState, int phase)
            {
                _a.StepType.Add(type);
                _a.CmdTargetName.Add(target);
                _a.CmdStateArr.Add(cmdState);
                _a.Wait1Id.Add(slot);
                _a.Wait1State.Add(waitState);
                _a.NextStep.Add(_a.StepType.Count);
                _a.ProcessStateByRow.Add(phase);
                return this;
            }

            internal RecipeArrays Arrays => _a;
        }

        private static IReadOnlyList<HmiRecipeRow> Present(
            RecipeArrays arrays, IReadOnlyList<HmiComponent> components, HmiSlotIndex slots,
            out List<string> diagnostics,
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? phases = null,
            string owner = "Line_Process")
        {
            diagnostics = new List<string>();
            phases ??= new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [owner] = arrays.ProcessPhaseNames,
            };
            return HmiRecipePresenter.Rows(arrays, owner, components, phases, slots, Def(), diagnostics);
        }

        // ---- 1. CMD row: target and state name ------------------------------------------------

        [Fact]
        public void CommandRowResolvesItsTargetAndStateName()
        {
            var clamp = Comp("Holder", Ring, 3, (0, "Open"), (2, "Closed"));
            var rows = Present(new Builder().Cmd("holder", 2).Arrays,
                               new[] { clamp }, Slots(("Holder", 3)), out var diag);

            var r = rows[0];
            Assert.Equal(HmiRowKind.Command, r.Kind);
            Assert.Equal("Holder", r.CmdTargetInstance);
            Assert.Equal("Closed", r.CmdStateName);
            Assert.Equal("Command Holder to Closed", r.Text);
            Assert.True(r.Resolved);
            Assert.Empty(diag);
        }

        // ---- 2. WAIT row: ring/slot and expected state ----------------------------------------

        [Fact]
        public void WaitRowResolvesItsSlotOnTheConsumingRingAndNamesTheState()
        {
            var probe = Comp("Detector", Ring, 7, (0, "Clear"), (1, "Present"));
            var rows = Present(new Builder().Wait(7, 1).Arrays,
                               new[] { probe }, Slots(("Detector", 7)), out var diag);

            var r = rows[0];
            Assert.Equal(HmiRowKind.Wait, r.Kind);
            Assert.Equal("Detector", r.WaitSourceKey);
            Assert.Equal("Present", r.WaitStateName);
            Assert.Equal("Wait until Detector is Present", r.Text);
            Assert.False(r.WaitCrossRing);
            Assert.Empty(diag);
        }

        // A slot means one thing on ITS ring. Two rings reusing a number must never be resolved by
        // guessing, because a confidently wrong component name is worse than none.
        [Fact]
        public void AmbiguousSlotOnAnotherRingIsRefusedRatherThanGuessed()
        {
            var a = Comp("Alpha", "one", 6, (0, "Home"));
            var b = Comp("Beta", "two", 6, (0, "Home"));
            var slots = HmiSlotIndex.Build(
                n => n == "Alpha" ? "one" : n == "Beta" ? "two" : "three",
                new Dictionary<string, int> { ["Alpha"] = 6, ["Beta"] = 6 });

            // The observer sits on a third ring, so slot 6 is not its own - and two instances claim it.
            var diag = new List<string>();
            var rows = HmiRecipePresenter.Rows(
                new Builder().Wait(6, 0).Arrays, "Observer", new[] { a, b },
                new Dictionary<string, IReadOnlyDictionary<int, string>>(), slots, Def(), diag);

            Assert.False(rows[0].Resolved);
            Assert.Null(rows[0].WaitSourceKey);
            Assert.Contains(diag, d => d.Contains("more than one", StringComparison.Ordinal));
        }

        // ---- 3. process phase, through ProcessStateByRow --------------------------------------

        [Fact]
        public void PhaseRowIsNamedFromTheOwningProcessPhaseTable()
        {
            var arrays = new Builder().Phase(4, "Loading").Cmd("Line_Process", 4, phase: 4).Arrays;
            var rows = Present(arrays, Array.Empty<HmiComponent>(), Slots(), out var diag);

            var r = rows[0];
            Assert.Equal(HmiRowKind.Phase, r.Kind);
            Assert.Equal("Phase: Loading", r.Text);
            Assert.Equal(4, r.PhaseValue);
            Assert.Equal("Loading", r.PhaseName);
            Assert.Empty(diag);
        }

        // A handshake reads ANOTHER process's phase. That number is meaningless in the observer's
        // own table, so it must be named from the owner's.
        [Fact]
        public void CrossProcessWaitIsNamedFromTheOWNERSPhaseTable()
        {
            var arrays = new Builder().Phase(1, "MineOne").Wait(9, 3).Arrays;
            var phases = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Line_Process"] = arrays.ProcessPhaseNames,
                ["Peer_Process"] = new Dictionary<int, string> { [3] = "PeerReady" },
            };
            var rows = Present(arrays, Array.Empty<HmiComponent>(), Slots(("Peer_Process", 9)),
                               out var diag, phases);

            Assert.Equal("Wait until Peer Process is PeerReady", rows[0].Text);
            Assert.Empty(diag);
        }

        // ---- 4. Owned versus Observed ----------------------------------------------------------

        [Fact]
        public void OwnedIsWhatItCommandsAndObservedIsWhatItOnlyWatches()
        {
            var driven = Comp("Pusher", Ring, 1, (0, "Home"), (2, "Work"));
            var watched = Comp("Gate", Ring, 5, (0, "Shut"), (1, "Open"));

            var arrays = new Builder()
                .Phase(1, "Run")
                .Cmd("pusher", 2)
                .Wait(1, 2)
                .Wait(5, 1)
                .Cmd("Line_Process", 1, phase: 1)
                .End().Arrays;

            var rows = Present(arrays, new[] { driven, watched },
                               Slots(("Pusher", 1), ("Gate", 5)), out _);

            Assert.Equal(new[] { "Pusher" }, HmiRecipePresenter.Owned(rows));
            Assert.Equal(new[] { "Gate" }, HmiRecipePresenter.Observed(rows, new[] { driven, watched }));
            // The phase announcement is NOT a commanded component.
            Assert.DoesNotContain("Line_Process", HmiRecipePresenter.Owned(rows));
        }

        // ---- 5. END -----------------------------------------------------------------------------

        [Fact]
        public void EndRowUsesTheFixedModelIndependentPhrase()
        {
            var rows = Present(new Builder().End().Arrays, Array.Empty<HmiComponent>(), Slots(), out var diag);
            Assert.Equal(HmiRowKind.End, rows[0].Kind);
            Assert.Equal(Def().Screens.RecipeText.End, rows[0].Text);
            Assert.Empty(diag);
        }

        // ---- 6. unknown target / slot / state all diagnose, none guesses ------------------------

        [Fact]
        public void AnUnknownCommandTargetIsMarkedAndDiagnosed()
        {
            var rows = Present(new Builder().Cmd("no_such_thing", 1).Arrays,
                               Array.Empty<HmiComponent>(), Slots(), out var diag);

            Assert.False(rows[0].Resolved);
            Assert.Equal(HmiRowKind.Unresolved, rows[0].Kind);
            Assert.Equal(Def().Screens.RecipeText.Unresolved, rows[0].Text);
            Assert.Contains(diag, d => d.Contains("no_such_thing", StringComparison.Ordinal));
        }

        [Fact]
        public void AnUnknownStateNumberIsMarkedAndDiagnosed()
        {
            var c = Comp("Slide", Ring, 2, (0, "Home"));
            var rows = Present(new Builder().Wait(2, 42).Arrays, new[] { c }, Slots(("Slide", 2)), out var diag);

            Assert.False(rows[0].Resolved);
            Assert.Contains(diag, d => d.Contains("42", StringComparison.Ordinal));
        }

        // The raw control numbers survive on an unresolved row - that is the row someone has to
        // diagnose, and losing its slot and state would make the report useless exactly there.
        [Fact]
        public void AnUnresolvedRowStillCarriesTheRawControlNumbers()
        {
            var rows = Present(new Builder().Wait(11, 4).Arrays,
                               Array.Empty<HmiComponent>(), Slots(), out _);

            Assert.False(rows[0].Resolved);
            Assert.Equal(11, rows[0].WaitSlot);
            Assert.Equal(4, rows[0].WaitState);
            Assert.Equal(StepType.Wait, rows[0].StepType);
        }

        // ---- 11 + 12. clamp / no-clamp, from the SAME code ------------------------------------

        // The presenter is handed one model with an extra actuator and one without. No HMI source
        // differs between the two runs - the rows follow the compiled recipe, which is the whole
        // claim being made about clamp and no-clamp models.
        [Fact]
        public void AnExtraActuatorAppearsAndDisappearsWithTheModelAlone()
        {
            var pusher = Comp("Pusher", Ring, 1, (0, "Home"), (2, "Work"));
            var holder = Comp("Holder", Ring, 4, (0, "Open"), (2, "Closed"));

            var withHolder = new Builder().Cmd("holder", 2).Wait(4, 2).Cmd("pusher", 2).End().Arrays;
            var without = new Builder().Cmd("pusher", 2).End().Arrays;

            var a = Present(withHolder, new[] { pusher, holder }, Slots(("Pusher", 1), ("Holder", 4)), out var da);
            var b = Present(without, new[] { pusher }, Slots(("Pusher", 1)), out var db);

            Assert.Contains(a, r => r.Text == "Command Holder to Closed");
            Assert.Contains(a, r => r.Text == "Wait until Holder is Closed");
            Assert.DoesNotContain(b, r => (r.CmdTargetInstance ?? string.Empty).Contains("Holder", StringComparison.Ordinal));
            Assert.Equal(new[] { "Holder", "Pusher" }, HmiRecipePresenter.Owned(a).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "Pusher" }, HmiRecipePresenter.Owned(b));
            Assert.Empty(da);
            Assert.Empty(db);
        }

        // ---- 13. hmi.yml carries GRAMMAR, never model data -------------------------------------

        [Fact]
        public void ConfigurationCarriesTemplatesOnlyNotModelData()
        {
            var text = Def().Screens.RecipeText;

            foreach (var t in new[] { text.Command, text.Wait, text.Phase })
                Assert.Contains("{", t, StringComparison.Ordinal);

            var beside = Path.Combine(AppContext.BaseDirectory, "Config", "hmi.yml");
            var path = File.Exists(beside)
                ? beside
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "CodeGen", "CodeGen", "Config", "hmi.yml"));
            // Comments are prose ABOUT the design; only the data lines are the contract.
            var yaml = string.Join(Environment.NewLine, File.ReadAllLines(path)
                .Select(l => l.Split('#')[0])
                .Where(l => l.Trim().Length > 0));

            // No plant vocabulary of any kind may appear in configuration - not a component, not a
            // process, not a state, not a controller.
            foreach (var forbidden in new[]
                     {
                         "Feed_Station", "Assembly_Station", "Disassembly", "Clamp", "Ejector",
                         "Feeder", "Checker", "Transfer", "Bearing", "Shaft", "Cover", "Robot",
                         "M262", "M580", "BX1",
                     })
                Assert.DoesNotContain(forbidden, yaml, StringComparison.OrdinalIgnoreCase);
        }
    }
}
