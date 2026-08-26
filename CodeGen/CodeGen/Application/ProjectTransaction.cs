using System;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Services;

namespace CodeGen.Application
{
    /// Generation is all-or-nothing.
    ///
    /// Everything a run writes - the wipe, the template deploy, every emitter, every patch - happens in
    /// a staging copy of the project on the same volume. The live project is replaced only once the
    /// whole tree has been emitted AND every output validator has passed. A failure anywhere leaves the
    /// previous project byte-for-byte as it was, which is the difference between "the generator failed"
    /// and "the generator destroyed the working project and then failed".
    ///
    /// The whole tree relocates by rebinding two paths: every other path the compiler resolves is
    /// derived from the syslay's own directory (EaeProjectLayout walks up to the .dfbproj), so staging
    /// needs no cooperation from any emitter. An architecture test pins that those two remain the only
    /// output-rooted settings.
    internal sealed class ProjectTransaction : IDisposable
    {
        // The staged configuration: identical to the caller's except that its output paths address the
        // staging copy. This is what the pipeline runs against.
        public CompilerConfiguration Configuration { get; }

        readonly string? _liveRoot;      // null when there is no previous project to replace
        readonly string _stagingRoot;
        readonly Action<string> _log;
        bool _committed;

        ProjectTransaction(CompilerConfiguration staged, string? liveRoot, string stagingRoot, Action<string> log)
        {
            Configuration = staged;
            _liveRoot = liveRoot;
            _stagingRoot = stagingRoot;
            _log = log;
        }

        /// Copies the live project into a staging sibling and returns a configuration addressing it.
        public static ProjectTransaction Begin(CompilerConfiguration cfg, Action<string> log)
        {
            var eae = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
            var liveRoot = string.IsNullOrEmpty(eae) ? null : Path.GetDirectoryName(eae);

            if (string.IsNullOrWhiteSpace(liveRoot))
            {
                // No .dfbproj was found above the configured syslay, so there is no EAE project here.
                // That is the FIRST generation into an empty output, where all-or-nothing is vacuous:
                // nothing exists that a failure could cost.
                //
                // But "no root derivable" and "no project there" are NOT the same thing, and treating
                // them alike is how the guarantee quietly stops holding: a tree that already has
                // artefacts in it but whose .dfbproj is missing, renamed or unreadable would take this
                // branch and be written over in place. So an output that already holds SOMETHING is
                // refused rather than staged into nothing.
                var occupied = Occupied(cfg);
                if (occupied != null)
                    throw new InvalidOperationException(
                        $"[Txn] '{occupied}' is already an emitted artefact, but no .dfbproj could be " +
                        "found above the configured syslay, so the project root cannot be derived and a " +
                        "staging sibling cannot be placed beside it. Generating in place would overwrite " +
                        "that tree with no way back. Restore or name the project file, or point the " +
                        "output at an empty directory.");

                log("[Txn] no existing project — writing in place (nothing to protect).");
                return new ProjectTransaction(cfg, null, string.Empty, log);
            }

            var parent = Path.GetDirectoryName(liveRoot!)
                ?? throw new InvalidOperationException(
                    $"[Txn] '{liveRoot}' has no parent directory, so a staging sibling cannot be placed " +
                    "on the same volume. An atomic publish needs one.");

            // A SIBLING, not %TEMP%: the publish is a directory move, which is only atomic within a
            // volume. A staging directory on another drive would silently become a copy.
            var stagingRoot = Path.Combine(parent,
                Path.GetFileName(liveRoot!) + ".staging-" + Guid.NewGuid().ToString("N")[..8]);

            SweepAbandoned(parent, Path.GetFileName(liveRoot!), log);

            if (Directory.Exists(liveRoot))
            {
                CopyTree(liveRoot!, stagingRoot);
                log($"[Txn] staging: {Path.GetFileName(stagingRoot)} " +
                    $"({Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).Count()} files copied)");
            }
            else
            {
                Directory.CreateDirectory(stagingRoot);
                log($"[Txn] staging: {Path.GetFileName(stagingRoot)} (no previous project to copy)");
            }

            var paths = cfg.Paths.Clone();
            paths.SyslayPath2 = Restage(paths.SyslayPath2, liveRoot!, stagingRoot);
            paths.SysresPath2 = Restage(paths.SysresPath2, liveRoot!, stagingRoot);
            return new ProjectTransaction(cfg.With(paths), liveRoot, stagingRoot, log);
        }

        // Whether an artefact ALREADY SITS AT THE CONFIGURED OUTPUT PATH. Asked of the exact two files
        // the run is about to write rather than of the surrounding directory: the output can live
        // anywhere, beside anything, and "some file exists somewhere above it" would refuse perfectly
        // ordinary first generations. A syslay or sysres already at the configured path, with no
        // .dfbproj found above it, is the one state that is neither a fresh output nor a derivable
        // project - and writing in place there would overwrite emitted work with no way back.
        static string? Occupied(CompilerConfiguration cfg)
        {
            foreach (var path in new[] { cfg.Paths.ActiveSyslayPath, cfg.Paths.SysresPath2 })
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            return null;
        }

        /// Replaces the live project with the staged one and returns the caller's path, mapped back to
        /// the live tree. Only reached when every validator has passed.
        public string Commit(string stagedPath)
        {
            if (_liveRoot == null) { _committed = true; return stagedPath; }

            var backup = _stagingRoot + ".replaced";
            var movedAside = false;
            try
            {
                if (Directory.Exists(_liveRoot))
                {
                    Directory.Move(_liveRoot, backup);
                    movedAside = true;
                }
                Directory.Move(_stagingRoot, _liveRoot);
            }
            catch (Exception ex)
            {
                // The previous project comes back before anything else happens: a half-published tree
                // is the one outcome this whole mechanism exists to prevent.
                if (movedAside && !Directory.Exists(_liveRoot))
                {
                    try { Directory.Move(backup, _liveRoot); }
                    catch (Exception rollback)
                    {
                        throw new InvalidOperationException(
                            $"[Txn] publishing failed ({ex.Message}) AND the previous project could not be " +
                            $"restored ({rollback.Message}). It is intact at '{backup}' — move it back to " +
                            $"'{_liveRoot}' by hand. Nothing was deleted.", ex);
                    }
                }
                throw new InvalidOperationException(
                    $"[Txn] publishing failed: {ex.Message}. The previous project is unchanged; the " +
                    $"generated tree is at '{_stagingRoot}'. This is usually EAE holding a file open — " +
                    "close it and generate again.", ex);
            }

            _committed = true;
            TryDelete(backup);
            _log("[Txn] published.");
            return Restage(stagedPath, _stagingRoot, _liveRoot);
        }

        /// Rollback. Uncommitted staging is removed; the live project was never touched.
        public void Dispose()
        {
            if (_committed || _liveRoot == null || string.IsNullOrEmpty(_stagingRoot)) return;
            TryDelete(_stagingRoot);
            _log("[Txn] rolled back — the previous project is unchanged.");
        }

        // A staging directory only survives a crash mid-run. Clearing them here rather than at exit
        // means a killed process cannot slowly fill the volume with abandoned copies.
        static void SweepAbandoned(string parent, string liveName, Action<string> log)
        {
            foreach (var d in Directory.EnumerateDirectories(parent, liveName + ".staging-*")
                         .Concat(Directory.EnumerateDirectories(parent, liveName + ".staging-*.replaced")))
            {
                TryDelete(d);
                log($"[Txn] removed an abandoned staging directory from an earlier run: {Path.GetFileName(d)}");
            }
        }

        static string Restage(string path, string fromRoot, string toRoot) =>
            string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith(fromRoot, StringComparison.OrdinalIgnoreCase)
                ? path
                : toRoot + path[fromRoot.Length..];

        static void CopyTree(string from, string to)
        {
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(to + d[from.Length..]);
            Directory.CreateDirectory(to);
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = to + f[from.Length..];
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
        }

        static void TryDelete(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { MapperLogger.Info($"[Txn] could not remove '{dir}': {ex.Message}"); }
        }
    }
}
