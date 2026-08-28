using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeGen.Configuration;
using Xunit;

namespace MapperTests
{
    /// TWO RUNS, TWO PROFILES, NO LEAK.
    ///
    /// The compiler used to resolve its targets and its FB types through static classes that computed
    /// their derived sets on first touch. Whichever configuration reached them first decided what every
    /// later run in the process saw. That was invisible in normal use — one machine, one bundle, one
    /// answer — and wrong the moment a second profile existed: a run would compile its recipe against
    /// its own declarations and its device tree against somebody else's, with both halves individually
    /// valid and nothing to report.
    ///
    /// These tests are what that defect would fail. Each builds a real profile bundle on disk, edits
    /// one declaration in it, and proves the resolved answers follow the bundle rather than the order
    /// the bundles happened to be loaded in.
    public sealed class ProfileIsolationTests : IDisposable
    {
        readonly List<string> _roots = new();

        public void Dispose()
        {
            foreach (var r in _roots)
                try { if (Directory.Exists(r)) Directory.Delete(r, true); } catch { /* temp */ }
        }

        /// A copy of the shipped bundle, with an optional edit applied to one declaration file.
        string Bundle(string label, string? file = null, Func<string, string>? edit = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "profile_" + label + "_" + Guid.NewGuid().ToString("N")[..8]);
            var src = Path.Combine(AppContext.BaseDirectory, "Config");
            var dst = Path.Combine(root, "Config");
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            if (file != null && edit != null)
            {
                var path = Path.Combine(dst, file);
                File.WriteAllText(path, edit(File.ReadAllText(path)));
            }
            _roots.Add(root);
            return root;
        }

        static CompilerConfiguration Load(string root) =>
            CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone(), root);

        // The resource name is the one every artefact addresses a target by, so it is what a leak
        // would show up in most loudly: a device emitted under one bundle's name and a .hcf bound
        // under another's produces a project that opens and resolves nothing.
        static string RenameFirstResource(string yml, string to)
        {
            var i = yml.IndexOf("resourceName:", StringComparison.Ordinal);
            Assert.True(i > 0, "device.yml declares no resourceName; this fixture assumes the shipped shape");
            var eol = yml.IndexOf('\n', i);
            return yml[..i] + "resourceName: " + to + yml[eol..];
        }

        [Fact]
        public void Two_profiles_loaded_in_one_process_each_resolve_their_own_targets()
        {
            var a = Load(Bundle("a"));
            var b = Load(Bundle("b", "device.yml", y => RenameFirstResource(y, "RENAMED_RES")));

            Assert.Equal("RENAMED_RES", b.Targets.All[0].ResourceName);
            Assert.NotEqual("RENAMED_RES", a.Targets.All[0].ResourceName);

            // And the indexes are distinct objects: a shared one would make the assertion above pass
            // by accident whenever the two bundles happened to agree.
            Assert.NotSame(a.Targets, b.Targets);
            Assert.NotSame(a.Manifest, b.Manifest);
        }

        [Fact]
        public void Loading_A_then_B_then_A_again_gives_A_its_own_answer_back()
        {
            // The frozen-registry defect was ORDER-DEPENDENT: the first load won and later ones
            // inherited it. So the third load is the one that matters — if anything cached, A would
            // come back carrying B's declarations.
            var rootA = Bundle("a");
            var rootB = Bundle("b", "device.yml", y => RenameFirstResource(y, "RENAMED_RES"));

            var first = Load(rootA).Targets.All[0].ResourceName;
            var middle = Load(rootB).Targets.All[0].ResourceName;
            var again = Load(rootA).Targets.All[0].ResourceName;

            Assert.Equal("RENAMED_RES", middle);
            Assert.Equal(first, again);
            Assert.NotEqual(middle, again);
        }

        [Fact]
        public async Task Two_profiles_resolved_concurrently_do_not_see_each_other()
        {
            // The registry it replaced kept ONE list behind a lock, keyed on the declaration list's
            // object identity. Under concurrency that is not merely stale — whichever thread got there
            // first decided, and the other silently compiled against it.
            var rootA = Bundle("a");
            var rootB = Bundle("b", "device.yml", y => RenameFirstResource(y, "RENAMED_RES"));

            var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            {
                var root = i % 2 == 0 ? rootA : rootB;
                var cfg = Load(root);
                return (Even: i % 2 == 0, Name: cfg.Targets.All[0].ResourceName);
            })));

            Assert.All(results.Where(r => r.Even), r => Assert.NotEqual("RENAMED_RES", r.Name));
            Assert.All(results.Where(r => !r.Even), r => Assert.Equal("RENAMED_RES", r.Name));
        }

        [Fact]
        public void A_profile_that_declares_a_different_template_set_selects_a_different_CAT()
        {
            // CAT SELECTION is the decision furthest from the file: templates.yml declares which graph
            // shapes each type serves, and the run picks from that. Widening one bundle's declaration
            // must change that bundle's answer and no other's — and this is the decision whose command
            // vocabulary drives the plant, so a leak here is a machine driven by the wrong protocol.
            var shipped = Load(Bundle("shipped"));
            const int Unserved = 11;                 // no shipped CAT declares an 11-state graph
            Assert.Null(shipped.Manifest.ForGraph(Unserved, branched: false));

            var widened = Load(Bundle("widened", "templates.yml",
                y => y.Replace("stateCounts: [3, 5, 6]", "stateCounts: [3, 5, 6, " + Unserved + "]")));

            Assert.NotNull(widened.Manifest.ForGraph(Unserved, branched: false));
            // The first bundle still refuses the shape, which is the whole point: the answer follows
            // the declaration, not whichever bundle was loaded first.
            Assert.Null(shipped.Manifest.ForGraph(Unserved, branched: false));
            Assert.Null(Load(Bundle("shipped2")).Manifest.ForGraph(Unserved, branched: false));
        }

        [Fact]
        public void A_ports_answer_follows_the_bundle_that_declared_it()
        {
            // A port spelling reaches the emitted resource directly: a wire to a port a type does not
            // declare is what EAE rejects the whole resource for. Two bundles, two spellings, and the
            // index must answer each from its own.
            var shipped = Load(Bundle("shipped"));
            var withRing = shipped.Manifest.Types.First(t => t.Ports.Any(p => p.StartsWith("stateR", StringComparison.Ordinal)));
            var port = withRing.Ports.First(p => p.StartsWith("stateR", StringComparison.Ordinal));

            var renamed = Load(Bundle("renamed", "templates.yml", y => y.Replace(port, port + "X")));

            Assert.Contains(port + "X", renamed.Manifest.Find(withRing.Name)!.Ports);
            Assert.Contains(port, shipped.Manifest.Find(withRing.Name)!.Ports);
            Assert.DoesNotContain(port + "X", shipped.Manifest.Find(withRing.Name)!.Ports);
        }

        [Fact]
        public void Two_runs_on_the_shipped_bundle_share_one_declaration_instance()
        {
            // Not a defect - it is the mtime cache doing its job, and it is why a run never re-reads a
            // file mid-generation. But it is also WHY nothing may write through the snapshot: an Add()
            // here would reach every other run in the process. The architecture rule that forbids it is
            // warranted by this, so the sharing is asserted rather than assumed.
            var one = CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone());
            var two = CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone());

            Assert.Same(one.Telemetry, two.Telemetry);
            Assert.Same(one.Devices, two.Devices);

            // The RESOLVED views are per snapshot even so, which is the distinction that matters: the
            // parsed declaration is shared, the answers derived from it are not.
            Assert.NotSame(one.Targets, two.Targets);
            Assert.NotSame(one.Manifest, two.Manifest);
        }

        [Fact]
        public void An_invalid_profile_is_refused_without_disturbing_a_valid_one()
        {
            // Fail-closed has to be per-profile too: a bundle whose declarations contradict each other
            // must stop ITS run, not poison a concurrent one that is fine.
            var good = Load(Bundle("good"));
            var badRoot = Bundle("bad", "device.yml", y => y.Replace("bootSequence:", "bootSequenceDisabled:"));

            Assert.ThrowsAny<Exception>(() => Load(badRoot));

            // The valid profile still answers, and still answers from its own declarations.
            Assert.NotEmpty(good.Targets.All);
            Assert.Equal(TestConfig.Cfg.Targets.All[0].ResourceName, good.Targets.All[0].ResourceName);
        }

        // ------------------------------------------------------------------------------------
        // The four things a run must not inherit: its BACKENDS, its IO, its TELEMETRY, its BROKER.
        //
        // Each of these was resolved from a process-wide read rather than from the run's own snapshot,
        // so the first run through decided the answer for every later one. The tests below run two
        // profiles SEQUENTIALLY in one process — the exact shape that hid the defect — and require the
        // second to answer from its own bundle.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Two_sequential_runs_compose_backends_from_their_own_declarations()
        {
            // `Backends()` read the device declaration singleton while every other stage used the run's
            // snapshot, so a second profile got its own targets everywhere except here.
            var a = Load(Bundle("bk_a"));
            var b = Load(Bundle("bk_b", "device.yml",
                y => y.Replace("backendEmitOrder: [M262, RevPi, M580, BX1]",
                               "backendEmitOrder: [BX1, M580, RevPi, M262]")));

            var first = TargetBackends.For(a).Select(x => x.Target.ToString()).ToArray();
            var second = TargetBackends.For(b).Select(x => x.Target.ToString()).ToArray();

            Assert.Equal(new[] { "M262", "RevPi", "M580", "BX1" }, first);
            Assert.Equal(new[] { "BX1", "M580", "RevPi", "M262" }, second);

            // And back again: the first profile is not left holding the second's order.
            Assert.Equal(first, TargetBackends.For(a).Select(x => x.Target.ToString()).ToArray());
        }

        [Fact]
        public void Two_sequential_runs_resolve_telemetry_from_their_own_declarations()
        {
            var a = Load(Bundle("tel_a"));
            var b = Load(Bundle("tel_b", "telemetry.yml", y => y.Replace("topicRoot: smc", "topicRoot: other")));

            Assert.Equal("smc", a.Telemetry.TopicRoot);
            Assert.Equal("other", b.Telemetry.TopicRoot);
            Assert.Equal("smc", a.Telemetry.TopicRoot);
        }

        // A snapshot whose workbook path points nowhere. The coupler cross-checks the coupler type
        // against the workbook, so the workbook is the half a run genuinely owns: the template library
        // is additionally discoverable from the working tree, which is what lets a test find it at all.
        CompilerConfiguration WithoutWorkbook(string label)
        {
            var paths = TestConfig.Cfg.Paths.Clone();
            paths.IoBindingsPath = Path.Combine(
                Path.GetTempPath(), "no_such_workbook_" + Guid.NewGuid().ToString("N")[..8] + ".xlsx");
            return CompilerConfiguration.Load(paths, Bundle(label));
        }

        [Fact]
        public void Two_sequential_runs_resolve_the_io_broker_from_their_own_declarations()
        {
            // The coupler was memoised on first touch from whatever configuration happened to be beside
            // the DLL. A run whose own declarations cannot resolve it must FAIL rather than be handed
            // the previous run's signals - which is exactly what a memoised answer did.
            var a = Load(Bundle("io_a"));
            var covered = CodeGen.Devices.RevPi.RevPiIoBrokerInjector.CoveredComponents(a);
            Assert.NotEmpty(covered);

            Assert.ThrowsAny<Exception>(() =>
                CodeGen.Devices.RevPi.RevPiIoBrokerInjector.CoveredComponents(WithoutWorkbook("io_b")));

            // The first profile still answers, and answers the same as before.
            Assert.Equal(covered.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                CodeGen.Devices.RevPi.RevPiIoBrokerInjector.CoveredComponents(a)
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void A_targets_servable_set_follows_the_run_that_asked()
        {
            // The UI, the selection validator and the run all ask the BACKEND what a target can serve.
            // Stored on the backend it was resolved once per process; asked of a snapshot it is the
            // answer for that snapshot, which is what makes the three views agree per run.
            var a = Load(Bundle("srv_a"));
            var relocation = a.Targets.All.First(t => t.StandsInFor != null).Plc;
            var backend = TargetBackends.For(a).First(x => x.Target == relocation);

            Assert.NotEmpty(backend.ServableComponents(a));

            // Same backend instance, different snapshot: the answer follows the argument, not the object.
            Assert.ThrowsAny<Exception>(() => backend.ServableComponents(WithoutWorkbook("srv_b")));
            Assert.NotEmpty(backend.ServableComponents(a));
        }

        // ------------------------------------------------------------------------------------
        // A SECOND CONTROLLER OF AN EXISTING KIND IS A ROW, NOT A CLASS.
        //
        // Every device emitter used to read its ids through a static keyed on a controller NAME, so a
        // second target of the same kind could only ever be emitted with the first one's sysdev,
        // resource and equipment ids - three identities EAE requires to be unique per device. It would
        // have generated, and then collided on import with nothing to say why.
        // ------------------------------------------------------------------------------------

        // A second target of an existing backend kind, with its own identities and its own resource.
        const string SecondM580 = @"
  - plc: M580b
    backendKind: M580
    network:
      targetIp: 192.168.1.21
    deviceName: M580b
    identity:
      sysdev: 00000000-0000-0000-0000-000000000007
      resource: 3E5C2B7F1A4D6C8F
      equipment: 11111111-2222-3333-4444-000000000070
      runtime: 11111111-2222-3333-4444-000000000071
      runtimeType: 7fd313c7-1da3-4618-9a5d-9ff3596aff7f
      rack: 11111111-2222-3333-4444-000000000072
      cps: 11111111-2222-3333-4444-000000000073
      cpu: 11111111-2222-3333-4444-000000000074
    simulationDeployPort: 51600
    simulationArchivePort: 51601
    resourceName: RES1
    deviceType: M580_dPAC
    hcfTemplate: M580IO.hcf
    deviceLocalCanvas: true
    bootFbs:
      - { role: FB1, id: 66C40EEF3F39D96A }
      - { role: FB2, id: ACED009B79DFCE6A }
";

        static string WithSecondM580(string yml) =>
            yml.Replace("backendEmitOrder: [M262, RevPi, M580, BX1]",
                        "backendEmitOrder: [M262, RevPi, M580, M580b, BX1]")
               .Replace("\ntargets:\n", "\ntargets:\n" + SecondM580);

        [Fact]
        public void Two_targets_of_one_backend_kind_each_carry_their_own_identity()
        {
            var cfg = Load(Bundle("twokind", "device.yml", WithSecondM580));

            var first = cfg.Targets.All.First(t => t.Plc.Name == "M580");
            var second = cfg.Targets.All.First(t => t.Plc.Name == "M580b");

            // Same KIND, so the same emitter renders both...
            var backends = TargetBackends.For(cfg);
            Assert.Equal(5, backends.Count);
            var b1 = backends.First(b => b.Target == first.Plc);
            var b2 = backends.First(b => b.Target == second.Plc);
            Assert.Equal(b1.GetType(), b2.GetType());
            Assert.NotSame(b1, b2);

            // ...and every identity that reaches an artefact is its OWN.
            Assert.NotEqual(first.Identity.Sysdev, second.Identity.Sysdev);
            Assert.NotEqual(first.Identity.Resource, second.Identity.Resource);
            Assert.NotEqual(first.Identity.Equipment, second.Identity.Equipment);
            Assert.NotEqual(first.Identity.Runtime, second.Identity.Runtime);
            Assert.NotEqual(first.Identity.Cpu, second.Identity.Cpu);
            Assert.NotEqual(first.ResourceName, second.ResourceName);
            Assert.NotEqual(first.SimulationDeployPort, second.SimulationDeployPort);

            // Its boot ids are its own too: a shared boot id is a duplicate-key load failure in EAE.
            Assert.Empty(cfg.Targets.BootFor(first.Plc, cfg.Layout)
                .Select(b => b.Id)
                .Intersect(cfg.Targets.BootFor(second.Plc, cfg.Layout).Select(b => b.Id)));

            // And the shipped profile is unaffected: adding a row to one bundle changes no other.
            Assert.Equal(4, TestConfig.Cfg.Targets.All.Count);
        }

        [Fact]
        public void A_second_target_reusing_another_targets_identity_is_refused()
        {
            // The whole point of per-target identities is that they are unique. Two devices claiming one
            // sysdev is a project EAE cannot load, so it is refused at load rather than emitted.
            var clash = WithSecondM580(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Config", "device.yml")))
                .Replace("sysdev: 00000000-0000-0000-0000-000000000007",
                         "sysdev: 00000000-0000-0000-0000-000000000003");

            var ex = Assert.ThrowsAny<Exception>(() =>
                Load(Bundle("clash", "device.yml", _ => clash)));
            Assert.Contains("sysdev", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
