using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// An interlock is a safety rule. What the twin declares is what must be emitted: no term dropped
    /// to make a guard fire, no alternative dropped to fit an array, no branch left unguarded because
    /// another branch sorted first. Where the model states something the plant cannot carry, the run
    /// stops; where the model states something that cannot FIRE, the rule is emitted as written and the
    /// defect is named. These pin all of that.
    public sealed class InterlockFidelityTests
    {
        // ---- nothing is dropped ---------------------------------------------------------------------

        [Fact]
        public void A_term_naming_a_source_at_its_rest_position_is_emitted_not_dropped()
        {
            // "Block while the source is at home" used to be discarded as an inverted rule. Whether it
            // is inverted is a judgement about the MODEL; a compiler that makes it silently ships an
            // actuator guarded by a rule the engineer never wrote.
            var ctx = Plant(guardBlockedState: 0);
            var plan = ctx.Interlocks[Guarded];

            Assert.Equal(1, plan.Count);
            Assert.Equal(ctx.Slots[Blocker], plan.Src[0]);
            Assert.Equal(0, plan.Blocked[0]);
        }

        [Fact]
        public void A_term_naming_a_source_at_work_is_emitted_the_same_way()
        {
            var plan = Plant(guardBlockedState: 2).Interlocks[Guarded];
            Assert.Equal(1, plan.Count);
            Assert.Equal(2, plan.Blocked[0]);
        }

        [Fact]
        public void Every_branch_out_of_a_guarded_state_is_resolved_not_just_the_first()
        {
            // A guard sits on a STATE, so it protects every move leaving it. Taking the first
            // destination left the other branches unguarded while the report still read as compiled.
            //
            // Here the second branch returns to a stop this CAT does not gate, so resolving it is what
            // makes the run stop. That refusal IS the proof: with only the first destination taken, the
            // second move would have been silently unguarded and generation would have succeeded.
            var ex = Assert.Throws<InvalidOperationException>(
                () => Plant(guardBlockedState: 2, branchGuardedState: true));
            Assert.Contains("compares against no interlock target", ex.Message);

            // Without the branch the same plant compiles, so the refusal is the branch and nothing else.
            Assert.Equal(1, Plant(guardBlockedState: 2).Interlocks[Guarded].Count);
        }

        [Fact]
        public void A_CAT_that_gates_both_directions_gets_a_rule_for_each()
        {
            // The swivel crosses one shared volume both ways and its twin numbers the two branches
            // separately. Both directions have to appear, or one crossing is guarded and its return is
            // not - which is the failure the first-destination-only walk used to produce.
            var plan = Twin("_se").Interlocks["Bearing_PnP"];
            var moves = Enumerable.Range(0, plan.Count)
                .Select(i => (plan.From[i], plan.To[i])).Distinct().ToList();

            Assert.Contains((2, 4), moves);
            Assert.Contains((4, 2), moves);
        }

        // ---- what cannot be represented stops the run ------------------------------------------------

        [Fact]
        public void A_guard_on_a_state_with_no_way_out_is_refused()
        {
            // A rule blocks a MOVE. A guard on a state with no transition states a safety rule about
            // nothing, and emitting nothing for it would be the silent drop in another guise.
            var ex = Assert.Throws<InvalidOperationException>(
                () => Plant(guardBlockedState: 2, guardedStateHasNoExit: true));
            Assert.Contains("no transition out of it", ex.Message);
        }

        [Fact]
        public void Every_emitted_move_is_one_the_CAT_actually_publishes()
        {
            // A rule matches on CurrentRawState, so a move outside the range the core publishes could
            // never fire. Proved on the shipped twins, where the swivel's second branch is numbered
            // outside its CAT's vocabulary and has to be resolved through the CAT's own stop
            // vocabulary rather than dropped.
            foreach (var suffix in new[] { "_se", "_vc", "_sw5", "_sw5_noclamp" })
            {
                var ctx = Twin(suffix);
                foreach (var (name, plan) in ctx.Interlocks)
                {
                    var range = TemplateManifest
                        .ProtocolOrNull(ctx.CatTypes.TryGetValue(name, out var cat) ? cat : string.Empty)
                        ?.RawStateRange;
                    if (range == null) continue;
                    for (int i = 0; i < plan.Count; i++)
                    {
                        Assert.InRange(plan.From[i], range.Min, range.Max);
                        Assert.InRange(plan.To[i], range.Min, range.Max);
                    }
                }
            }
        }

        // ---- what cannot FIRE is emitted and named ---------------------------------------------------

        [Fact]
        public void A_conjunction_naming_one_source_at_two_stops_is_emitted_and_reported()
        {
            // The twin states it as one ConditionGroup, which is a conjunction, and a source cannot be
            // at two stops at once. The compiler does not reinterpret that as an OR and does not drop a
            // term to make it fire - it emits what the model says and names the defect.
            var ctx = Twin("_se");

            var finding = ctx.SemanticFindings.FirstOrDefault(f => f.StartsWith("UNSATISFIABLE INTERLOCK"));
            Assert.NotNull(finding);
            Assert.Contains("Shaft_Hr", finding);
            Assert.Contains("ConditionGroup", finding);          // the remedy, in the twin's own terms

            // Emitted, not dropped: both stops of that source are in the rule table.
            var plan = ctx.Interlocks["Shaft_Hr"];
            var swivel = ctx.Slots["Bearing_PnP"];
            var stops = Enumerable.Range(0, plan.Count)
                .Where(i => plan.Src[i] == swivel).Select(i => plan.Blocked[i]).Distinct().ToList();
            Assert.True(stops.Count > 1, "both declared stops of the source must be present");
        }

        [Fact]
        public void A_model_that_states_one_stop_reports_nothing()
        {
            // The same plant modelled with a two-position swivel names one stop, so there is no
            // contradiction to report. The finding is about the MODEL, not about the compiler.
            Assert.DoesNotContain(Twin("_sw5").SemanticFindings,
                f => f.StartsWith("UNSATISFIABLE INTERLOCK"));
        }

        // ---- one owner for "which number names this stop" --------------------------------------------

        [Fact]
        public void A_geometric_CAT_gives_two_states_at_one_place_one_number()
        {
            // A twin may re-visit one place under two branch numberings. Which of those numbers names
            // the stop is the CAT's declaration, and the recipe and the interlock must read it the same
            // way or a rule would be written against a number the core never publishes.
            var ctx = Twin("_se");
            var swivel = ctx.Components.First(c =>
                string.Equals(c.Name, "Bearing_PnP", StringComparison.OrdinalIgnoreCase));
            Assert.True(CodeGen.Translation.Interlocks.ActuatorStateEncoding.Geometric(swivel, ctx.CatTypes));

            foreach (var group in swivel.States.Where(s => s.StaticState).GroupBy(s => s.Position))
            {
                var numbers = group
                    .Select(s => CodeGen.Translation.Interlocks.ActuatorStateEncoding
                        .CanonicalNumber(swivel, s, ctx.CatTypes))
                    .Distinct().ToList();
                Assert.Single(numbers);
            }
        }

        // ---- fixtures ---------------------------------------------------------------------------------

        private const string Proc = "Assembly_Station";
        private const string Guarded = "Shaft_Hr";
        private const string Blocker = "Clamp";

        private static GenerationContext Twin(string suffix)
        {
            var path = System.IO.Path.Combine(RepoRoot(), "Gate", "fixtures", "models",
                "SMC_Vue2VC_With_Processes" + suffix, "Control.xml");
            return GenerationContext.Plan(TestConfig.Cfg, path,
                DeploymentProfile.AsPlaced(TestConfig.Cfg));
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Gate")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? AppContext.BaseDirectory;
        }

        private static VueOneState State(string id, string name, int number, bool stop, bool initial = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, StaticState = stop, InitialState = initial,
            Transitions = new List<VueOneTransition>(),
        };

        private static void Leads(VueOneState from, VueOneState to, params VueOneCondition[] guard) =>
            from.Transitions.Add(new VueOneTransition
            {
                TransitionID = "T-" + from.StateID + "-" + to.StateID,
                OriginStateID = from.StateID, DestinationStateID = to.StateID,
                Conditions = guard.ToList(),
            });

        private static GenerationContext Plant(
            int guardBlockedState, bool branchGuardedState = false, bool guardedStateHasNoExit = false)
        {
            var proc = Process("C-p", Proc);
            var blocker = Actuator("C-b", Blocker, "C-p", "C-p-s1");
            var guarded = Actuator("C-g", Guarded, "C-p", "C-p-s1");

            // The guard sits on the MOTION state, which is where the twin puts it: the rule blocks the
            // move that leaves the resting predecessor.
            var moving = guarded.States.First(s => !s.StaticState);
            if (guardedStateHasNoExit) moving.Transitions.Clear();
            // A second way out of the SAME guarded state. Branching to a state the actuator already
            // has keeps its state count, so the CAT selected for it does not change with the fixture.
            if (branchGuardedState)
                Leads(moving, guarded.States.First(s => s.StateNumber == 0));

            var blockedState = blocker.States.First(s => s.StateNumber == guardBlockedState);
            moving.InterlockConditions = new List<VueOneCondition>
            {
                new() { ComponentID = "C-b", ID = blockedState.StateID, Name = blockedState.Name },
            };

            return GenerationContext.Plan(TestConfig.Cfg,
                new List<VueOneComponent> { proc, blocker, guarded },
                DeploymentProfile.AsPlaced(TestConfig.Cfg));
        }

        private static VueOneComponent Actuator(string id, string name, string proc, string procState)
        {
            var home = State(id + "-s0", "Home", 0, true, initial: true);
            var moving = State(id + "-m", "Moving", 1, stop: false);
            var work = State(id + "-s2", "Work", 2, true);
            Leads(home, moving, new VueOneCondition { ComponentID = proc, ID = procState, Name = "cmd" });
            Leads(moving, work);
            Leads(work, home);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Actuator,
                States = new List<VueOneState> { home, moving, work },
            };
        }

        private static VueOneComponent Process(string id, string name)
        {
            var entry = State(id + "-s0", "Entry", 0, true, initial: true);
            var drive = State(id + "-s1", "Drive", 1, true);
            Leads(entry, drive);
            Leads(drive, entry);
            return new VueOneComponent
            {
                ComponentID = id, Name = name, Type = ComponentType.Process,
                States = new List<VueOneState> { entry, drive },
            };
        }
    }
}
