using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices;
using CodeGen.Translation;

namespace CodeGen.Application
{
    /// One run, and everything that run is compiled against.
    ///
    /// The configuration snapshot, the plan and the set of backends that will emit it are decided once
    /// at the composition root and travel together from there. Nothing below reaches a mutable global
    /// for any of them, so two generations running at once — a different twin, a different target
    /// selection, a different profile — cannot see each other's state.
    ///
    /// The backends live HERE rather than on the plan because a backend is an EAE emitter and the
    /// planning layer is not allowed to know one exists; CompilerBoundaryTests fails the build if that
    /// reverses. The session is the seam: the plan says what to build, the session says who builds it.
    internal sealed class CompilerSession
    {
        public CompilerConfiguration Cfg { get; }
        public IReadOnlyList<ITargetBackend> Backends { get; }

        private CompilerSession(CompilerConfiguration cfg, IReadOnlyList<ITargetBackend> backends)
        {
            Cfg = cfg;
            Backends = backends;
        }

        /// A run composes its OWN backends, from its own declarations.
        ///
        /// Backend-vs-declaration agreement used to be checked here as well as at the factory, which
        /// meant two owners of one question - and every check here was already unreachable, because a
        /// list built by walking backendEmitOrder over the declared targets cannot contain a duplicate,
        /// an unimplemented target or a backend with no row. Taking only the snapshot is what makes a
        /// mismatched pairing unrepresentable rather than merely rejected twice.
        public static CompilerSession Begin(CompilerConfiguration cfg) =>
            new(cfg ?? throw new ArgumentNullException(nameof(cfg)), GenerateProject.Backends(cfg));

        /// The same session against a different configuration — used when the run is redirected into a
        /// staging tree. A NEW session, so nothing that already holds one sees the change.
        public CompilerSession With(CompilerConfiguration cfg) =>
            new(cfg ?? throw new ArgumentNullException(nameof(cfg)), Backends);

        public ITargetBackend For(PlcAssignment target) =>
            Backends.FirstOrDefault(b => b.Target == target)
            ?? throw new InvalidOperationException($"no backend implements target '{target}'.");
    }
}
