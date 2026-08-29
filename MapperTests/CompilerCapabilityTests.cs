using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Domain.Twin;
using CodeGen.Models;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// FOUR ANSWERS THAT LOOK ALIKE AND ARE NOT.
    ///
    /// "It did not compile" is not a useful thing to tell an engineer. A graph no CAT serves is a twin to
    /// change; a CAT with no declared command vocabulary is a YAML line to add; a declared target nothing
    /// implements is a backend to write. Each needs a different person to do a different thing, and the
    /// compiler's own refusals — correct as they are — say only that something failed, one at a time.
    ///
    /// So the report classifies. These tests are what stops the classification drifting into decoration:
    /// each one produces a model or a profile in a state that genuinely has that problem, and asserts the
    /// report both names it and files it under the thing that would fix it.
    public sealed class CompilerCapabilityTests : IDisposable
    {
        readonly List<string> _roots = new();

        public void Dispose()
        {
            foreach (var r in _roots)
                try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch { /* temp */ }
        }

        static IEnumerable<PlcAssignment> AllTargets => TestConfig.Cfg.Targets.All.Select(t => t.Plc);

        // ---- model fixtures ---------------------------------------------------------------------

        static VueOneState State(string id, string name, int number, bool entry = false) => new()
        {
            StateID = id, Name = name, StateNumber = number, InitialState = entry,
            StaticState = true, Transitions = new List<VueOneTransition>(),
        };

        /// An actuator with the given number of stops, chained home -> ... -> home.
        static VueOneComponent Actuator(string id, string name, int stops)
        {
            var states = Enumerable.Range(0, stops)
                .Select(i => State($"{id}-s{i}", "S" + i, i)).ToList();
            for (int i = 0; i < states.Count - 1; i++)
                states[i].Transitions.Add(new VueOneTransition { DestinationStateID = states[i + 1].StateID });
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = "Actuator", States = states };
        }

        /// A process whose entry state has TWO outgoing transitions — a branch the recipe engine has no
        /// row to express, because RecipeStep carries one NextStep.
        static VueOneComponent BranchingProcess(string id, string name)
        {
            var entry = State(id + "-s0", "Entry", 0, entry: true);
            var a = State(id + "-a", "A", 1);
            var b = State(id + "-b", "B", 2);
            entry.Transitions.Add(new VueOneTransition { DestinationStateID = a.StateID });
            entry.Transitions.Add(new VueOneTransition { DestinationStateID = b.StateID });
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = "Process", States = new List<VueOneState> { entry, a, b } };
        }

        static VueOneComponent LinearProcess(string id, string name)
        {
            var entry = State(id + "-s0", "Entry", 0, entry: true);
            var next = State(id + "-s1", "Close", 1);
            entry.Transitions.Add(new VueOneTransition { DestinationStateID = next.StateID });
            return new VueOneComponent
            { ComponentID = id, Name = name, Type = "Process", States = new List<VueOneState> { entry, next } };
        }

        static CompilerCapabilityReport Report(IEnumerable<VueOneComponent> components,
            CompilerConfiguration? cfg = null, IEnumerable<PlcAssignment>? backends = null) =>
            CompilerCapabilityReport.For(
                TwinModel.Build(components.ToList(), TestConfig.Cfg.Twin), cfg ?? TestConfig.Cfg, backends ?? AllTargets);

        // ---- the four answers -------------------------------------------------------------------

        [Fact]
        public void A_plant_the_compiler_can_render_reports_only_supported()
        {
            var report = Report(new[]
            {
                LinearProcess("P1", "Line"),
                Actuator("A1", "Cylinder", stops: 5),
            });

            Assert.True(report.CanCompile);
            Assert.Empty(report.Blocking);
            Assert.Contains(report.Findings, f => f.Subject == "Cylinder" && f.Kind == CapabilityKind.Supported);
            Assert.Contains(report.Findings, f => f.Subject == "Line" && f.Kind == CapabilityKind.Supported);
        }

        [Fact]
        public void A_graph_no_CAT_serves_is_the_MODEL_to_change()
        {
            // Nine stops: no shipped CAT's protocol declares that shape, and inventing one would mean
            // driving the actuator with a command vocabulary nobody wrote down.
            var report = Report(new[] { LinearProcess("P1", "Line"), Actuator("A1", "Odd", stops: 9) });

            var finding = Assert.Single(report.Blocking, f => f.Subject == "Odd");
            Assert.Equal(CapabilityKind.UnsupportedModelSemantics, finding.Kind);
            Assert.Contains("9-state", finding.Detail, StringComparison.Ordinal);
            Assert.False(report.CanCompile);
        }

        [Fact]
        public void A_branching_process_is_the_MODEL_to_change()
        {
            // The deployed engine executes a linear row list with ONE NextStep per row. It can loop, but
            // it has no branch row — so a state with two ways out cannot be lowered without discarding
            // one, and discarding one ships a plant that ignores half its own model.
            var report = Report(new[] { BranchingProcess("P1", "Forks"), Actuator("A1", "Cylinder", 5) });

            var finding = Assert.Single(report.Blocking, f => f.Subject == "Forks");
            Assert.Equal(CapabilityKind.UnsupportedModelSemantics, finding.Kind);
        }

        [Fact]
        public void A_declared_target_that_no_backend_renders_is_a_BACKEND_to_write()
        {
            // Everything is declared and the model is fine. What is missing is C# that emits an EAE
            // device — which is not something a YAML edit or a twin edit can supply.
            var withoutOne = AllTargets.Skip(1).ToList();
            var absent = AllTargets.First();

            var report = Report(new[] { LinearProcess("P1", "Line"), Actuator("A1", "Cylinder", 5) },
                backends: withoutOne);

            var finding = Assert.Single(report.Blocking, f => f.Subject == absent.ToString());
            Assert.Equal(CapabilityKind.BackendUnavailable, finding.Kind);
            Assert.Contains("no backend", finding.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void A_semantic_role_no_template_serves_is_a_DECLARATION_to_add()
        {
            // The task arm is chosen by the ROLE the profile assigns it, not by its state graph — VueOne
            // types the arm and the jaws alike. So when no template serves that role the model is fine
            // and the graph is fine; what is absent is an infraRoles entry. Filing that under "the twin
            // is wrong" would send the engineer to the wrong file entirely.
            var root = Bundle(f =>
            {
                var path = Path.Combine(f, "templates.yml");
                var text = File.ReadAllText(path);
                Assert.Contains("taskArm", text);            // pins the fixture to the shipped shape
                File.WriteAllText(path, text.Replace("taskArm", "taskArmRetired"));
            });

            var cfg = CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone(), root);
            var arm = Actuator("R1", cfg.Rig.Roles.TaskArm, stops: 5);
            arm.Type = "Robot";

            var report = Report(new[] { LinearProcess("P1", "Line"), arm }, cfg);

            var finding = Assert.Single(report.Blocking, f => f.Subject == cfg.Rig.Roles.TaskArm);
            Assert.Equal(CapabilityKind.MissingProfileDeclaration, finding.Kind);
            Assert.Contains("taskArm", finding.Detail, StringComparison.Ordinal);
        }

        string Bundle(Action<string> edit)
        {
            var root = Path.Combine(Path.GetTempPath(), "caps_" + Guid.NewGuid().ToString("N")[..8]);
            var dst = Path.Combine(root, "Config");
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Config")))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            edit(dst);
            _roots.Add(root);
            return root;
        }

        [Fact]
        public void The_refusal_lists_every_blocking_finding_at_once()
        {
            // The point of the report: three separate problems in one model take ONE attempt to learn
            // about, not three. Each downstream refusal still stands; this is what stops the engineer
            // discovering them one deploy at a time.
            var report = Report(
                new[] { BranchingProcess("P1", "Forks"), Actuator("A1", "Odd", 9) },
                backends: AllTargets.Skip(1).ToList());

            var boom = Assert.Throws<InvalidOperationException>(() => report.AssertCompilable());
            Assert.Contains("Forks", boom.Message, StringComparison.Ordinal);
            Assert.Contains("Odd", boom.Message, StringComparison.Ordinal);
            Assert.Contains(AllTargets.First().ToString(), boom.Message, StringComparison.Ordinal);

            // and it says what would fix each kind, not merely that it failed
            Assert.Contains("change the twin", boom.Message, StringComparison.Ordinal);
            Assert.Contains("backend", boom.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ABORTED before anything was written", boom.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_shipped_twin_that_generates_reports_no_blocking_finding()
        {
            // The report must agree with the gate: whatever generates must classify clean, or the
            // report is a second opinion rather than a summary of the one the compiler holds.
            foreach (var suffix in new[] { "_sw5", "_sw5_noclamp" })
            {
                var components = new CodeGen.IO.SystemXmlReader(TestConfig.Cfg.Twin)
                    .ReadAllComponents(TestTwin.CompilableFixturePath(suffix));
                var report = Report(components);
                Assert.True(report.CanCompile,
                    suffix + " generates in the gate but the capability report blocks it: " +
                    string.Join("; ", report.Blocking));
            }
        }
    }
}
