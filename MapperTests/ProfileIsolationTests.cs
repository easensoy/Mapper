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
    }
}
