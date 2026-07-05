using System;
using System.Collections.Generic;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // Translates a VueOne Component Name into the IEC 61499 FB instance name (used in .syslay/.sysres/wiring).
    // Resolution order (first wins): ComponentID override, VueOne Name override, suffix-strip convention,
    // rig alias, raw Name. Overrides come from the xlsx Instance_Name_Overrides sheet.
    public static class InstanceNameResolver
    {
        public static string Resolve(VueOneComponent comp,
            IReadOnlyDictionary<string, string>? byComponentId = null,
            IReadOnlyDictionary<string, string>? byVueOneName = null)
        {
            if (comp == null) return string.Empty;

            if (byComponentId != null &&
                !string.IsNullOrEmpty(comp.ComponentID) &&
                byComponentId.TryGetValue(comp.ComponentID.Trim(), out var byId) &&
                !string.IsNullOrWhiteSpace(byId))
            {
                return byId.Trim();
            }

            var name = (comp.Name ?? string.Empty).Trim();
            if (name.Length == 0) return string.Empty;

            if (byVueOneName != null &&
                byVueOneName.TryGetValue(name, out var byName) &&
                !string.IsNullOrWhiteSpace(byName))
            {
                return byName.Trim();
            }

            var type = (comp.Type ?? string.Empty).Trim();
            if (string.Equals(type, "Process", StringComparison.OrdinalIgnoreCase))
            {
                return StripSuffix(name, "_process") ?? StripSuffix(name, "_Process") ?? name;
            }

            if (RigAliases.TryGetValue(name, out var hardwareName))
                return hardwareName;

            return name;
        }

        // MUST stay empty: the FB instance name must equal the Control.xml component name, because each CAT's
        // SYMLINKMULTIVARDST/SRC $${PATH}<pin> macro resolves to {ResourceName}.{InstancePath}.{Pin} at deploy
        // and the IO bindings xlsx + .hcf channel symlinks key off that component name. Renaming an FB strands
        // every channel symlink. Per-component aliases must go via the xlsx (which rewrites bindings + .hcf too).
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
