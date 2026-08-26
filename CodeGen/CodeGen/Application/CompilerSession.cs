using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices;
using CodeGen.Mapping;
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

        /// A target is IMPLEMENTED because a backend claims it and DECLARED because device.yml has a
        /// row for it. The two must agree exactly: a declared target with no backend is a device the
        /// deployment expects and the run silently skips, and a backend with no row has no resource
        /// name to emit under. Checked here, once, before anything is planned.
        public static CompilerSession Begin(CompilerConfiguration cfg, IReadOnlyList<ITargetBackend> backends)
        {
            if (cfg is null) throw new ArgumentNullException(nameof(cfg));
            if (backends is null || backends.Count == 0)
                throw new ArgumentException(
                    "no target backends were registered, so no device could be emitted", nameof(backends));

            var errors = new List<string>();
            foreach (var g in backends.GroupBy(b => b.Target).Where(g => g.Count() > 1))
                errors.Add($"two backends both claim target '{g.Key}', so which one emits it is undecided");

            var implemented = backends.Select(b => b.Target).ToList();
            var declared = cfg.Devices.Targets;
            foreach (var d in declared)
                if (!implemented.Contains(d.Plc))
                    errors.Add($"device.yml declares target '{d.Plc}', which no backend implements");
            foreach (var plc in implemented)
                if (declared.All(d => d.Plc != plc))
                    errors.Add($"backend '{plc}' has no device.yml targets entry, so it has no resource name");

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Target registration is inconsistent:" + Environment.NewLine +
                    "  - " + string.Join(Environment.NewLine + "  - ", errors));

            return new CompilerSession(cfg, backends);
        }

        /// The same session against a different configuration — used when the run is redirected into a
        /// staging tree. A NEW session, so nothing that already holds one sees the change.
        public CompilerSession With(CompilerConfiguration cfg) =>
            new(cfg ?? throw new ArgumentNullException(nameof(cfg)), Backends);

        public ITargetBackend For(PlcAssignment target) =>
            Backends.FirstOrDefault(b => b.Target == target)
            ?? throw new InvalidOperationException($"no backend implements target '{target}'.");
    }
}
