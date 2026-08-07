using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    public static class ControllerMap
    {
        // EAE resource name per PLC. M580 => RES0 (the EAE default, matching the authored
        // M580IO.hcf 'RES0.M580IO.*' symlinks). Unknown => empty string.
        public static string ResourceForPlc(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262  => "M262_RES",
            PlcAssignment.M580  => "RES0",
            PlcAssignment.BX1   => "BX1_RES",
            PlcAssignment.RevPi => "RevPi_RES",
            _ => string.Empty,
        };

        // The Feed station's controller: M262, or the RevPi for the components a run relocates onto it.
        // Topology questions ("is this component upstream of the assembly side?") ask here rather than
        // re-testing the enum, so the two cannot drift apart at one call site and be forgotten at another.
        public static bool IsFeedController(PlcAssignment plc) =>
            plc is PlcAssignment.M262 or PlcAssignment.RevPi;


    }
}
