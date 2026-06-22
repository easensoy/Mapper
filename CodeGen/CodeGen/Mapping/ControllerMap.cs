using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Translation;

namespace CodeGen.Mapping
{
    /// <summary>
    /// Controller lens over <see cref="ComponentRegistry"/> — answers "which PLC
    /// runs this?", "what EAE resource does it map to?", and "which Process FB
    /// commands it?". Replaces the three hardcoded fallback tables in
    /// <c>HcfSymbolIndex.NameBasedPlcGuess</c>, <c>SysresFbMirror.BucketFor</c>, and
    /// the implicit ownership scattered inside <c>SystemLayoutInjector</c>.
    /// </summary>
    public static class ControllerMap
    {
        /// <summary>
        /// EAE resource name per PLC: M262 → M262_RES, M580 → RES0 (the EAE default, matching the
        /// authored M580IO.hcf 'RES0.M580IO.*' symlinks), BX1 → BX1_RES.
        /// Returns empty string for <see cref="PlcAssignment.Unknown"/>.
        /// </summary>
        public static string ResourceForPlc(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262 => "M262_RES",
            PlcAssignment.M580 => "RES0",
            PlcAssignment.BX1  => "BX1_RES",
            _ => string.Empty,
        };

        /// <summary>
        /// PLC that runs the component <paramref name="name"/>, or
        /// <see cref="PlcAssignment.Unknown"/> when it is not registered. This is
        /// the canonical name-based fallback — IO-binding-based resolution still
        /// goes through <see cref="HcfSymbolIndex"/>; this delegates the
        /// hardcoded-name table to the registry.
        /// </summary>
        public static PlcAssignment PlcOf(string? name)
        {
            var e = ComponentRegistry.Get(name);
            return e?.Plc ?? PlcAssignment.Unknown;
        }

        /// <summary>EAE resource name for this component, or empty string when unknown.</summary>
        public static string ResourceOf(string? name)
        {
            var e = ComponentRegistry.Get(name);
            return e?.Resource ?? string.Empty;
        }

    }
}
