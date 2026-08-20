using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Hmi;
using Xunit;

namespace MapperTests
{
    // Does the EMITTED SOURCE agree with the verdict?
    //
    // Every other HMI test reasons about the plan. These reason about the files EAE compiles, because
    // that is the only thing an operator's finger reaches. Reporting a control as disabled while its
    // handler still raises the event was the defect that made this file necessary, so the assertions
    // are deliberately made against faceplate text after HmiFaceplatePatcher has run - not against
    // synthetic HmiCapabilityResolver records, which can agree with themselves while the panel lies.
    //
    // The faceplates are the REAL ones from Template Library\HMI\Faceplates; only the controller
    // evidence is synthesised, so a template change that reintroduces a live command fails here.
    public class HmiOperatorActionTests : IDisposable
    {
        private readonly string _staging =
            Path.Combine(Path.GetTempPath(), "hmi-action-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_staging)) Directory.Delete(_staging, recursive: true);
        }

        // ---- the shipped configuration and the shipped faceplates ----------------------------

        private static string RepoFile(params string[] parts)
        {
            var beside = Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());
            if (File.Exists(beside) || Directory.Exists(beside)) return beside;
            var repo = Path.GetFullPath(Path.Combine(
                new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));
            Assert.True(File.Exists(repo) || Directory.Exists(repo), $"not found: {repo}");
            return repo;
        }

        private static HmiDefinition Def() =>
            HmiDefinitionLoader.Parse(File.ReadAllText(RepoFile("CodeGen", "CodeGen", "Config", "hmi.yml")));

        private static IReadOnlyList<HmiCatTemplate> Templates() =>
            HmiTemplateLibrary.Load(RepoFile("Template Library"));

        private static HmiCatTemplate Cat(string catType) =>
            Templates().FirstOrDefault(t => t.CatType == catType)
            ?? throw new InvalidOperationException($"no faceplate template for '{catType}'");

        private static HmiSymbol Sym(string catType, string symbol) =>
            Cat(catType).Symbols.FirstOrDefault(s => s.Name == symbol)
            ?? throw new InvalidOperationException($"'{catType}' ships no symbol '{symbol}'");

        // ---- synthetic controller evidence ---------------------------------------------------

        // An ECC index that guards on exactly the conditions given. This is the ONLY thing these
        // tests fake: the faceplates, the action table and the production resolver are all real.
        private static HmiEccIndex Ecc(params (string Type, string Condition)[] transitions) =>
            new(transitions.GroupBy(t => t.Type).ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Condition).ToList(),
                StringComparer.OrdinalIgnoreCase));

        // The real actuator mode arcs, reduced to what the payload proofs actually read: mode 2 is
        // never distinguishable from mode 1, and the mode-9 arc carries no interlock term.
        private static HmiEccIndex RealisticActuatorEcc() => Ecc(
            ("FiveStateActuator", "((pst_event AND mode = 1 AND state_val = 1) OR (pst_event AND mode = 2 " +
                                  "AND state_val = 1) OR (setup_event AND mode = 3 AND toWorkSetup)) " +
                                  "AND toWorkInterlock = FALSE"),
            ("FiveStateActuator", "mode = 9"),
            ("FiveStateActuator", "((pst_event AND mode = 1 AND state_val = 3) OR mode = 9 OR " +
                                  "(setup_event AND mode = 3 AND toHomeSetup)) AND toHomeInterlock = FALSE"));

        private static HmiCapability Supported(HmiCapabilityPurpose p, string evt, params string[] data) =>
            new(p, evt, data, true, HmiUnavailableReason.None, string.Empty);

        private static HmiCapability Withheld(HmiCapabilityPurpose p, string evt, string why, params string[] data) =>
            new(p, evt, data, false, HmiUnavailableReason.NotConsumed, why);

        private static IReadOnlyList<HmiActionVerdict> Verdicts(
            string catType, string symbol, params HmiCapability[] caps) =>
            HmiActionResolver.For("Inst", catType, Sym(catType, symbol), caps, RealisticActuatorEcc(), Def());

        // Stage the real faceplates and run the real suppressor over them.
        private string Patch(IReadOnlyList<HmiActionVerdict> verdicts, params string[] cats)
        {
            Directory.CreateDirectory(_staging);
            var deployed = cats.Select(Cat).ToList();
            foreach (var tpl in deployed)
                HmiTemplateLibrary.CopyDirectory(tpl.SourceDir, Path.Combine(_staging, tpl.CatType));

            HmiFaceplatePatcher.Suppress(_staging, deployed, verdicts, Def());
            return _staging;
        }

        private string Source(string catType, string symbol, string ext = ".cnv.cs") =>
            File.ReadAllText(Path.Combine(_staging, catType, $"{catType}_{symbol}{ext}"));

        // =========================== NEGATIVE ==================================================

        // The headline defect: CycleControl and CycleStop ride the SAME event from the SAME symbol.
        // Enabling the one must never leave the other callable.
        [Fact]
        public void SharedEventDoesNotEnableTheUnsupportedActionAlongsideTheSupportedOne()
        {
            var v = Verdicts("Area_CAT", "sDefault",
                Supported(HmiCapabilityPurpose.CycleControl, "CTCNF", "CycleType"),
                Withheld(HmiCapabilityPurpose.CycleStop, "CTCNF",
                         "the recipe engine never reads CycleType", "CycleType"));

            // Every cycle action in the shipped table is proved by CycleStop, so all four are refused
            // even though the station-level capability passed.
            Assert.All(v.Where(x => x.Call != null && x.Call.Contains("CTCNF")),
                       x => Assert.False(x.Effective, $"{x.ActionId} must not ride CycleControl's verdict"));

            Patch(v, "Area_CAT");
            var src = Source("Area_CAT", "sDefault");
            Assert.DoesNotContain("FireEvent_CTCNF(0);", src);
            Assert.DoesNotContain("FireEvent_CTCNF(1);", src);
        }

        // Area STOP specifically: the one command an operator would trust most.
        [Fact]
        public void AreaStopIsNotCallableWhenTheEngineIgnoresCycleType()
        {
            var v = Verdicts("Area_CAT", "sDefault",
                Withheld(HmiCapabilityPurpose.CycleStop, "CTCNF", "engine ignores CycleType", "CycleType"));

            Patch(v, "Area_CAT");
            var src = Source("Area_CAT", "sDefault");

            Assert.DoesNotContain("FireEvent_CTCNF(0);", src);
            Assert.Contains(Def().WithheldMarker, src);
            Assert.Contains("StopButton.Enabled = false;", src);
        }

        // Manual stepping: the engine implements no handshake, so neither operator event may survive.
        [Fact]
        public void ManualExecuteAndNextAreNotCallableWhenTheEngineHasNoHandshake()
        {
            var v = Verdicts("Process1_Generic", "sManual",
                Withheld(HmiCapabilityPurpose.ManualStep, "MREQO", "the engine implements no manual handshake"));

            Assert.NotEmpty(v);
            Assert.All(v, x => Assert.False(x.Effective));

            Patch(v, "Process1_Generic");
            var src = Source("Process1_Generic", "sManual");
            Assert.DoesNotContain("FireEvent_MREQO(true);", src);
            Assert.DoesNotContain("FireEvent_NSREQO(true);", src);
        }

        // Initial Position is refused by the PAYLOAD's own proof, not by the event's: MCNF is
        // honoured, and mode 9 still reverses an actuator with the interlock bypassed.
        [Fact]
        public void InitialPositionIsRefusedBecauseItsArcCarriesNoInterlockTerm()
        {
            var v = Verdicts("Area_CAT", "sDefault",
                Supported(HmiCapabilityPurpose.ModeSelection, "MCNF", "Mode"));

            var home = v.Single(x => x.ActionId == "InitialPosition");
            Assert.False(home.Effective);
            Assert.Contains("Interlock", home.Detail);

            Patch(v, "Area_CAT");
            Assert.DoesNotContain("FireEvent_MCNF(9);", Source("Area_CAT", "sDefault"));
        }

        // Manual MODE is refused because no transition tells it apart from Automatic.
        [Fact]
        public void ManualModeIsRefusedBecauseNoTransitionDistinguishesItFromAutomatic()
        {
            var v = Verdicts("Area_CAT", "sDefault",
                Supported(HmiCapabilityPurpose.ModeSelection, "MCNF", "Mode"));

            var manual = v.Single(x => x.ActionId == "ManualMode");
            Assert.False(manual.Effective);
            Assert.Contains("mode = 1", manual.Detail);

            Patch(v, "Area_CAT");
            Assert.DoesNotContain("FireEvent_MCNF(2);", Source("Area_CAT", "sDefault"));
        }

        // A tag-bound jog has no call to delete: it must be UNBOUND, or it still writes the tag.
        [Fact]
        public void AWithheldTagBoundJogIsUnboundNotMerelyReported()
        {
            var v = Verdicts("Five_State_Actuator_CAT", "sSetup",
                Withheld(HmiCapabilityPurpose.SetupJog, "cmd_event",
                         "the station mode chain does not reach this actuator", "toWork", "toHome"));

            Assert.All(v, x => Assert.False(x.Effective));

            Patch(v, "Five_State_Actuator_CAT");
            var designer = Source("Five_State_Actuator_CAT", "sSetup", ".cnv.Designer.cs");
            Assert.DoesNotContain("TagName = \"toWork\";", designer);
            Assert.DoesNotContain("TagName = \"toHome\";", designer);
        }

        // A symbol is not command-capable merely because its contract declares an output: the control
        // has to exist. Otherwise every monitoring tile would demand governance it cannot need.
        [Fact]
        public void ADeclaredOutputNoControlBindsIsNotACommand()
        {
            var monitoring = Sym("Process1_Generic", "sAutomatic");

            Assert.False(monitoring.CanRaiseAnything);
            Assert.Empty(Def().Actions.Where(monitoring.Presents));
        }

        // The reference ships its plant RESET hidden and never restores it. Reporting that as an
        // available command would be a lie the operator cannot check.
        [Fact]
        public void AControlTheFaceplateHidesForGoodIsNeverEffective()
        {
            var area = Sym("Area_CAT", "sDefault");
            Assert.Contains("Fault_Reset", area.DeadTags);

            var v = Verdicts("Area_CAT", "sDefault",
                Supported(HmiCapabilityPurpose.FaultReset, "FRCNF"));

            var reset = v.Single(x => x.ActionId == "FaultReset");
            Assert.False(reset.Effective);
        }

        // =========================== POSITIVE ==================================================

        // Suppression must not disarm a command the controller does honour.
        [Fact]
        public void SupportedAreaModeActionsRemainCallable()
        {
            var v = Verdicts("Area_CAT", "sDefault",
                Supported(HmiCapabilityPurpose.ModeSelection, "MCNF", "Mode"));

            Assert.True(v.Single(x => x.ActionId == "AutoMode").Effective);
            Assert.True(v.Single(x => x.ActionId == "SetupMode").Effective);

            Patch(v, "Area_CAT");
            var src = Source("Area_CAT", "sDefault");
            Assert.Contains("FireEvent_MCNF(1);", src);
            Assert.Contains("FireEvent_MCNF(3);", src);
        }

        [Fact]
        public void SupportedTwoPositionJogRemainsBound()
        {
            var v = Verdicts("Five_State_Actuator_CAT", "sSetup",
                Supported(HmiCapabilityPurpose.SetupJog, "cmd_event", "toWork", "toHome"));

            Assert.True(v.Single(x => x.ActionId == "JogWork").Effective);
            Assert.True(v.Single(x => x.ActionId == "JogHome").Effective);

            Patch(v, "Five_State_Actuator_CAT");
            var designer = Source("Five_State_Actuator_CAT", "sSetup", ".cnv.Designer.cs");
            Assert.Contains("TagName = \"toWork\";", designer);
            Assert.Contains("TagName = \"toHome\";", designer);
        }

        [Fact]
        public void SupportedThreePositionJogRemainsBound()
        {
            const string cat = "Seven_State_Actuator_Centre_Home_CAT";
            var v = Verdicts(cat, "sSetup",
                Supported(HmiCapabilityPurpose.SetupJog, "cmd_event", "toWork1", "toWork2", "toHome"));

            Assert.True(v.Single(x => x.ActionId == "JogWork1").Effective);
            Assert.True(v.Single(x => x.ActionId == "JogWork2").Effective);

            Patch(v, cat);
            var designer = Source(cat, "sSetup", ".cnv.Designer.cs");
            Assert.Contains("TagName = \"toWork1\";", designer);
            Assert.Contains("TagName = \"toWork2\";", designer);
        }

        // The three-position jog must honour the SAME mode gate the two-position one does, or a jog
        // button would be live while the plant ran Automatic.
        [Fact]
        public void ThreePositionJogIsDisabledUntilSetupModeIsConfirmed()
        {
            const string cat = "Seven_State_Actuator_Centre_Home_CAT";
            var src = File.ReadAllText(Path.Combine(Cat(cat).SourceDir, $"{cat}_sSetup.cnv.cs"));

            Assert.Contains("toWork1.Enabled = false;", src);
            Assert.Contains("toWork2.Enabled = false;", src);
            Assert.Contains("mode_event_Fired", src);
        }

        // Withholding every command must never cost the operator the monitoring.
        [Fact]
        public void MonitoringSurvivesWhenEveryCommandIsWithheld()
        {
            var v = Verdicts("Five_State_Actuator_CAT", "sSetup",
                Withheld(HmiCapabilityPurpose.SetupJog, "cmd_event", "unreachable", "toWork", "toHome"));

            Patch(v, "Five_State_Actuator_CAT");
            var designer = Source("Five_State_Actuator_CAT", "sSetup", ".cnv.Designer.cs");

            // The live feedback bindings are untouched - only the command bindings are removed.
            Assert.Contains("TagName = \"current_state_to_process\";", designer);
            Assert.Contains("TagName = \"atHome\";", designer);
            Assert.Contains("TagName = \"atWork\";", designer);
        }

        // Every declared action must name a capability the rule table proves, or a payload could be
        // offered with nothing behind it.
        [Fact]
        public void EveryDeclaredActionIsProvedByADeclaredCapability()
        {
            var def = Def();
            Assert.NotEmpty(def.Actions);
            Assert.All(def.Actions, a =>
                Assert.Contains(def.Capabilities, c => c.Purpose == a.ProvedBy));
            Assert.All(def.Actions, a =>
                Assert.True((a.Call != null) ^ (a.Writes != null),
                            $"action '{a.Id}' must fire exactly one way"));
        }
    }
}
