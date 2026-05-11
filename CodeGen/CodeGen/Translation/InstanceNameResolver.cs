using System;
using System.Collections.Generic;
using CodeGen.Models;

namespace CodeGen.Translation
{
    /// <summary>
    /// Translates a VueOne <c>&lt;Component&gt;</c>'s <c>&lt;Name&gt;</c> into the
    /// IEC 61499 FB instance name used in the emitted .syslay / .sysres / wiring.
    ///
    /// Resolution order (first match wins):
    ///   1. Override by ComponentID (most specific — survives renames in VueOne)
    ///   2. Override by VueOne Name
    ///   3. Default convention: strip well-known VueOne suffixes
    ///        * "_process" / "_Process"  on Process components       — Feed_Station_process → Feed_Station
    ///        * trailing "_<verb>"-style suffix on Process components — left for future expansion
    ///   4. Fall back to the component's raw Name.
    ///
    /// Overrides are loaded from the Excel mapping workbook's
    /// <c>Instance_Name_Overrides</c> sheet (see <see cref="InstanceNameOverridesLoader"/>).
    /// </summary>
    public static class InstanceNameResolver
    {
        /// <summary>Resolve an instance name from a Component + optional override maps.</summary>
        /// <param name="comp">The VueOne component (Name + ComponentID + Type are read).</param>
        /// <param name="byComponentId">Optional override map keyed by ComponentID (GUID-stable).</param>
        /// <param name="byVueOneName">Optional override map keyed by VueOne Name (human-stable).</param>
        public static string Resolve(VueOneComponent comp,
            IReadOnlyDictionary<string, string>? byComponentId = null,
            IReadOnlyDictionary<string, string>? byVueOneName = null)
        {
            if (comp == null) return string.Empty;

            // 1. ComponentID override
            if (byComponentId != null &&
                !string.IsNullOrEmpty(comp.ComponentID) &&
                byComponentId.TryGetValue(comp.ComponentID.Trim(), out var byId) &&
                !string.IsNullOrWhiteSpace(byId))
            {
                return byId.Trim();
            }

            var name = (comp.Name ?? string.Empty).Trim();
            if (name.Length == 0) return string.Empty;

            // 2. VueOne-Name override
            if (byVueOneName != null &&
                byVueOneName.TryGetValue(name, out var byName) &&
                !string.IsNullOrWhiteSpace(byName))
            {
                return byName.Trim();
            }

            // 3. Default convention — type-aware suffix stripping.
            var type = (comp.Type ?? string.Empty).Trim();
            if (string.Equals(type, "Process", StringComparison.OrdinalIgnoreCase))
            {
                return StripSuffix(name, "_process") ?? StripSuffix(name, "_Process") ?? name;
            }
            // Actuator / Sensor / Robot pass through unchanged.

            return name;
        }

        private static string? StripSuffix(string s, string suffix)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
                return s.Substring(0, s.Length - suffix.Length);
            return null;
        }
    }
}
