using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeGen.Application;
using CodeGen.Configuration;
using Xunit;

namespace MapperTests
{
    /// A generator that destroys the working project and then fails is worse than one that just fails.
    /// These build a project tree, run a transaction over it, and prove what survives.
    public sealed class TransactionalGenerationTests : IDisposable
    {
        readonly string _root;
        readonly string _live;

        public TransactionalGenerationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "txn_" + Guid.NewGuid().ToString("N")[..10]);
            _live = Path.Combine(_root, "Demonstrator");
            // The shape the compiler derives everything else from: a .dfbproj beside the syslay's tree.
            Directory.CreateDirectory(Path.Combine(_live, "Demonstrator", "IEC61499", "System", "app"));
            File.WriteAllText(Path.Combine(_live, "Demonstrator", "IEC61499", "IEC61499.dfbproj"), "<Project />");
            File.WriteAllText(SyslayPath, "<SystemConfiguration>previous</SystemConfiguration>");
            File.WriteAllText(Path.Combine(_live, "Demonstrator", "keep.txt"), "a file no generation touches");
        }

        string SyslayPath => Path.Combine(_live, "Demonstrator", "IEC61499", "System", "app", "app.syslay");

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* temp */ }
        }

        CompilerConfiguration Config()
        {
            var paths = TestConfig.Cfg.Paths.Clone();
            paths.SyslayPath2 = SyslayPath;
            paths.SysresPath2 = Path.Combine(_live, "Demonstrator", "IEC61499", "System", "app", "app.sysres");
            return TestConfig.Cfg.With(paths);
        }

        // Content hash of every file, so "unchanged" means bytes, not timestamps or counts.
        static Dictionary<string, string> Fingerprint(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
                f => f[dir.Length..],
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.OrdinalIgnoreCase);

        static void AssertSame(Dictionary<string, string> before, Dictionary<string, string> after, string what)
        {
            var added = after.Keys.Except(before.Keys).ToList();
            var gone = before.Keys.Except(after.Keys).ToList();
            var changed = before.Keys.Intersect(after.Keys).Where(k => before[k] != after[k]).ToList();
            Assert.True(added.Count == 0 && gone.Count == 0 && changed.Count == 0,
                what + Environment.NewLine +
                "  added:   " + string.Join(", ", added) + Environment.NewLine +
                "  removed: " + string.Join(", ", gone) + Environment.NewLine +
                "  changed: " + string.Join(", ", changed));
        }

        [Fact]
        public void A_failed_run_leaves_the_previous_project_byte_for_byte()
        {
            var before = Fingerprint(_live);
            var log = new List<string>();

            var boom = Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var txn = ProjectTransaction.Begin(Config(), log.Add);
                // Everything a generation would do, done to the staging copy...
                var staged = Path.GetDirectoryName(txn.Configuration.Paths.SyslayPath2)!;
                File.WriteAllText(txn.Configuration.Paths.SyslayPath2, "<SystemConfiguration>NEW</SystemConfiguration>");
                foreach (var f in Directory.EnumerateFiles(staged)) File.Delete(f);
                // ...and then a validator refuses it. No Commit.
                throw new InvalidOperationException("a validator refused the staged tree");
            }));

            Assert.Contains("refused", boom.Message);
            AssertSame(before, Fingerprint(_live), "a failed generation modified the previous project");
            Assert.Contains(log, l => l.Contains("rolled back", StringComparison.Ordinal));
        }

        [Fact]
        public void A_successful_run_publishes_the_staged_tree_and_leaves_no_staging_behind()
        {
            var log = new List<string>();
            string published;
            using (var txn = ProjectTransaction.Begin(Config(), log.Add))
            {
                File.WriteAllText(txn.Configuration.Paths.SyslayPath2, "<SystemConfiguration>NEW</SystemConfiguration>");
                published = txn.Commit(txn.Configuration.Paths.SyslayPath2);
            }

            // The caller is handed the LIVE path, not the staging one it was written to.
            Assert.Equal(SyslayPath, published);
            Assert.Equal("<SystemConfiguration>NEW</SystemConfiguration>", File.ReadAllText(SyslayPath));
            // A file the run never touched survives the swap: publishing replaces the tree, not its contents.
            Assert.True(File.Exists(Path.Combine(_live, "Demonstrator", "keep.txt")));
            Assert.Empty(Directory.EnumerateDirectories(_root, "*.staging-*"));
            Assert.Empty(Directory.EnumerateDirectories(_root, "*.replaced"));
        }

        [Fact]
        public void Staging_is_a_sibling_on_the_same_volume_so_publishing_is_a_move_not_a_copy()
        {
            var log = new List<string>();
            using var txn = ProjectTransaction.Begin(Config(), log.Add);
            var staged = txn.Configuration.Paths.SyslayPath2;

            Assert.NotEqual(SyslayPath, staged);
            Assert.Equal(Path.GetPathRoot(SyslayPath), Path.GetPathRoot(staged));
            Assert.StartsWith(Path.Combine(_root, "Demonstrator.staging-"), staged, StringComparison.OrdinalIgnoreCase);
            // The previous tree was copied in, so copy-if-absent and preserve-existing behave as they
            // would against the live project.
            Assert.Equal("<SystemConfiguration>previous</SystemConfiguration>", File.ReadAllText(staged));
        }

        [Fact]
        public void An_output_that_already_holds_artefacts_but_no_project_file_is_refused()
        {
            // "No project root derivable" and "no project there" are different states, and the second
            // is the only one where writing without a staging copy is free. A tree whose .dfbproj is
            // missing, renamed or unreadable is still somebody's work: staging cannot be placed beside
            // a root that cannot be derived, so the run must stop rather than overwrite it in place.
            File.Delete(Path.Combine(_live, "Demonstrator", "IEC61499", "IEC61499.dfbproj"));
            var before = Fingerprint(_live);

            var boom = Assert.Throws<InvalidOperationException>(() => ProjectTransaction.Begin(Config(), _ => { }));
            Assert.Contains("is already an emitted artefact", boom.Message);

            Assert.Equal(before, Fingerprint(_live));      // and nothing was touched saying so
        }

        [Fact]
        public void A_first_generation_into_an_empty_output_needs_no_staging()
        {
            // The other side of the same rule: with nothing there, all-or-nothing is vacuous, and
            // demanding a sibling would make the very first generation impossible.
            var empty = Path.Combine(_root, "fresh", "Demonstrator", "IEC61499", "System", "app");
            Directory.CreateDirectory(empty);
            var paths = TestConfig.Cfg.Paths.Clone();
            paths.SyslayPath2 = Path.Combine(empty, "app.syslay");
            paths.SysresPath2 = Path.Combine(empty, "app.sysres");
            Directory.Delete(Path.Combine(_root, "fresh"), true);

            var notes = new List<string>();
            using var txn = ProjectTransaction.Begin(TestConfig.Cfg.With(paths), notes.Add);

            Assert.Contains(notes, n => n.Contains("nothing to protect", StringComparison.Ordinal));
            Assert.Equal(paths.SyslayPath2, txn.Configuration.Paths.SyslayPath2);   // written where told
        }

        [Fact]
        public void An_abandoned_staging_directory_from_a_killed_run_is_swept()
        {
            var abandoned = Path.Combine(_root, "Demonstrator.staging-deadbeef");
            Directory.CreateDirectory(abandoned);
            File.WriteAllText(Path.Combine(abandoned, "leftover.txt"), "from a process that was killed");

            var log = new List<string>();
            using var txn = ProjectTransaction.Begin(Config(), log.Add);

            Assert.False(Directory.Exists(abandoned));
            Assert.Contains(log, l => l.Contains("abandoned staging", StringComparison.Ordinal));
        }
    }
}
