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

        public static PlcAssignment PlcOf(string? name)
        {
            var e = ComponentRegistry.Get(name);
            return e?.Plc ?? PlcAssignment.Unknown;
        }

        public static string ResourceOf(string? name)
        {
            var e = ComponentRegistry.Get(name);
            return e?.Resource ?? string.Empty;
        }

    }
}
