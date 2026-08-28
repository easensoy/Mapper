using System.Collections.Generic;
using CodeGen.Application;
using CodeGen.Devices;

namespace MapperTests
{
    // The tests read the SAME backend list the generator composes. There is deliberately no second
    // array here: a target half-added by editing one list and forgetting another is exactly what the
    // registration refusals exist to catch, and a private copy would hide it.
    //
    // It is a method, not stored state: a run owns its backends through its CompilerSession, so there
    // is no global for a test to set or to leak into the next one.
    internal static class TargetBackends
    {
        internal static IReadOnlyList<ITargetBackend> All => GenerateProject.Backends(TestConfig.Cfg);

        // The same composition against a DIFFERENT snapshot, which is what proves the backend set
        // follows the run's own declarations rather than a process-wide one.
        internal static IReadOnlyList<ITargetBackend> For(CodeGen.Configuration.CompilerConfiguration cfg) =>
            GenerateProject.Backends(cfg);
    }
}
