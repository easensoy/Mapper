using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // A NAMED LENS onto the target registry, kept for the separately-owned HMI module, which compiles
    // against these names. It holds no facts: every answer is the registry's, so the panel and the
    // compiler cannot describe a target differently.
    //
    // The compiler itself asks TargetRegistry directly. Nothing new should be added here.
    public static class ControllerMap
    {
        public static string ResourceForPlc(PlcAssignment plc) => TargetRegistry.Of(plc).ResourceName;
    }
}
