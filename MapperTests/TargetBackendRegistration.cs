using System.Runtime.CompilerServices;
using CodeGen.Application;
using CodeGen.Mapping;

namespace MapperTests
{
    // The tests exercise the SAME composition root the generator does. There is deliberately no second
    // backend array here: a target half-added by editing one list and forgetting another is exactly
    // what the registration refusals exist to catch, and a private copy here would hide it.
    internal static class TargetBackendRegistration
    {
        [ModuleInitializer]
        internal static void Register() => TargetRegistry.UseBackends(GenerateProject.Backends());
    }
}
