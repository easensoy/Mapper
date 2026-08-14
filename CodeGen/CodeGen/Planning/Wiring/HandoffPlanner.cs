using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;

namespace CodeGen.Translation
{
    // The facts the recipe generator, ring wiring and HCF binder share about the cross-station handoffs:
    // whether the M262<->M580 discharge is active, which cover actuators splice onto the M580 ring, and
    // where the part-present sensor sits. The handoff ROWS themselves are emitted by ProcessCompiler from
    // the twin, not declared here.
    public static class HandoffPlanner
    {
        // Master switch for the M262<->M580 cross-device discharge + part-present handoffs. RIG-VERIFY:
        // the M262<->M580 cross-device adapter transport (only M580<->BX1 is rig-proven). OFF =
        // decoupled local rings (Assembly gates on the local BearingSensor).
        public static bool DischargeActive => true;

        // The M262 part-present proximity sensor (DI08); id/pin from MapperConfig.M262SynthSensors
        // (the rig wires it; the twin does not model it).
        public static (string Name, string Pin, int Id) PartAtAssembly =>
            System.Array.Find(MapperConfig.M262SynthSensors,
                s => string.Equals(s.Name, "PartAtAssembly", System.StringComparison.OrdinalIgnoreCase));

        // A twin may declare the part-present sensor itself instead of leaving it to the synth
        // injection. It then keeps the SAME reserved slot, so ids stay identical either way.
        public static bool IsPartAtAssembly(string name) =>
            string.Equals(name, PartAtAssembly.Name, System.StringComparison.OrdinalIgnoreCase);

    }
}
