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

            // 4. Rig-canonical hardware aliases. VueOne uses logical names
            //    ("Feeder"); the physical SMC rig + HCF channel bindings + HMI
            //    faceplate captions all use hardware names ("Pusher"). These
            //    fallbacks only apply when no xlsx override is supplied —
            //    add a row to Instance_Name_Overrides to change this.
            if (RigAliases.TryGetValue(name, out var hardwareName))
                return hardwareName;

            return name;
        }

        /// <summary>
        /// Rig-canonical name aliases used when the xlsx Instance_Name_Overrides
        /// sheet does not provide one. Intentionally EMPTY today.
        ///
        /// <para>The Feeder → Pusher alias was removed on 2026-05-21 because
        /// renaming the FB instance broke the symbolic-link PATH expansion:
        /// every CAT's <c>SYMLINKMULTIVARDST/SRC</c> uses
        /// <c>$${PATH}athome</c> / <c>$${PATH}atwork</c> / <c>$${PATH}OutputToWork</c>
        /// macros that resolve to <c>{ResourceName}.{InstancePath}.{Pin}</c>
        /// at deploy time. With the FB instance renamed to Pusher the macros
        /// expanded to <c>Pusher.athome</c> etc. — but the IO bindings xlsx
        /// + the deployed .hcf channel symlinks still reference the
        /// VueOne Control.xml component name <c>Feeder</c>. Renaming
        /// the FB stranded every channel symlink. Keep the FB instance
        /// name = Control.xml component name.</para>
        ///
        /// <para>If you need an alias for a SPECIFIC component, add a row to
        /// the xlsx <c>Instance_Name_Overrides</c> sheet (ComponentID or
        /// VueOne Name → IEC Instance Name) — that path also rewrites the
        /// IO bindings + .hcf in lockstep, which the hard-coded RigAliases
        /// fallback never did.</para>
        /// </summary>
        private static readonly Dictionary<string, string> RigAliases =
            new(StringComparer.OrdinalIgnoreCase);

        private static string? StripSuffix(string s, string suffix)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
                return s.Substring(0, s.Length - suffix.Length);
            return null;
        }
    }
}
