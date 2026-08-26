using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    public static class TemplateMap
    {
        // The ring's claim test is CASE-SENSITIVE, so recipe CmdTargetName and the instance's actuator_name
        // must both come from here; drift leaves the command circling unclaimed and the engine parked, silently.
        public static string RingKey(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

        // One constant so CAT deploy, parameters and I/O binding cannot spell the swivel CAT differently.
        public const string SevenStateCentreHomeCat = "Seven_State_Actuator_Centre_Home_CAT";

        // True when one state has both a PARALLEL and an ALTERNATIVE outgoing transition.
        public static bool IsBranchedSevenState(VueOneComponent component)
        {
            if (component is null || component.States is null) return false;
            foreach (var state in component.States)
            {
                bool hasParallel = false, hasAlternative = false;
                if (state.Transitions is null) continue;
                foreach (var tr in state.Transitions)
                {
                    if (string.Equals(tr.TransitionType, "PARALLEL", StringComparison.OrdinalIgnoreCase))
                        hasParallel = true;
                    else if (string.Equals(tr.TransitionType, "ALTERNATIVE", StringComparison.OrdinalIgnoreCase))
                        hasAlternative = true;
                }
                if (hasParallel && hasAlternative) return true;
            }
            return false;
        }
    }
}
