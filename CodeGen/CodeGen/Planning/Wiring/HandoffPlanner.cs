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
        // The sensor filling the MATERIAL role: the part-present level that rides the cross-controller
        // segment and can therefore stand in for a Feed-side handoff. The profile names it because the
        // twin has no way to say "this is the one that crosses"; its slot comes from the synth
        // reservation and its physical channel from dischargeChannels.
        public static (string Name, int Id) PartAtAssembly
        {
            get
            {
                var role = RigCatalog.Current.Roles.MaterialSensor;
                foreach (var s in MapperConfig.M262SynthSensors)
                    if (string.Equals(s.Name, role, System.StringComparison.OrdinalIgnoreCase)) return s;
                throw new System.InvalidOperationException(
                    $"[Rig] roles.materialSensor names '{role}', which smc-rig.yml does not reserve a " +
                    "synthSensor slot for, so the cross-controller material bridge has no sensor to ride.");
            }
        }

        // A twin may declare the material sensor itself instead of leaving it to the synth injection. It
        // then keeps the SAME reserved slot, so ids stay identical either way.
        public static bool IsPartAtAssembly(string name) =>
            RigCatalog.Current.Roles.Is(RigCatalog.Current.Roles.MaterialSensor, name);

    }
}
