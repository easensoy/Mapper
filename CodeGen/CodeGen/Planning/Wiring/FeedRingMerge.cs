using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using static CodeGen.Translation.Process.Recipes.RecipeComponentLookup;

namespace CodeGen.Translation.Process.Recipes
{
    // Wiring topology (not recipe): the Feed ring merges into the M580 ring when a Feed-controller process
    // has a motion state whose leave-condition targets the Disassembly process. Purely model-derived.
    public static class FeedRingMerge
    {
        private static readonly string[] MotionVerbs =
        {
            "advancing", "rising", "returning", "descending",
            "towork", "tohome", "gotowork", "gotohome", "checking",
        };

        public static bool Needed(IReadOnlyList<VueOneComponent> allComponents)
        {
            foreach (var proc in allComponents)
            {
                if (!ComponentType.IsProcess(proc)) continue;
                if (!ControllerMap.IsFeedController(HcfSymbolIndex.NameBasedPlcGuess(proc.Name)))
                    continue;
                foreach (var st in proc.States)
                {
                    if (!SuggestsMotion(st.Name)) continue;
                    foreach (var tr in st.Transitions)
                        foreach (var c in tr.Conditions)
                        {
                            if (string.IsNullOrEmpty(c.ComponentID)) continue;
                            var target = LookupComponent(c.ComponentID, allComponents);
                            if (target != null &&
                                string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                                (string.Equals(target.Name?.Trim(), "Disassembly", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(target.Name?.Trim(), "Disassembly_Station", StringComparison.OrdinalIgnoreCase)))
                                return true;
                        }
                }
            }
            return false;
        }

        private static bool SuggestsMotion(string? stateName)
        {
            var n = (stateName ?? string.Empty).Trim().ToLowerInvariant();
            foreach (var v in MotionVerbs)
                if (n.Contains(v, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
