using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // A NAMED LENS onto the target registry, kept for the separately-owned HMI module, which compiles
    // against these names. It holds no facts: every answer is the registry's, so the panel and the
    // compiler cannot describe a target differently.
    //
    // It reads the PROCESS-WIDE snapshot of the shipped bundle, because the HMI calls it statically
    // and cannot be handed a run's own. The compiler itself asks its own snapshot's TargetIndex, and
    // an architecture test fails the build if that reverses. Nothing new should be added here.
    public static class ControllerMap
    {
        public static string ResourceForPlc(PlcAssignment plc) =>
            Configuration.CompilerConfiguration.Default.Targets.Of(plc).ResourceName;
    }
}
