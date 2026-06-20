using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    /// <summary>
    /// Pure component / name lookup primitives for recipe generation: resolve a
    /// Control.xml ComponentID to its <see cref="VueOneComponent"/>, and compare
    /// names case-insensitively (trimmed). Every method is a pure function of its
    /// arguments — no RecipeArrays, no MapperConfig, no I/O, no shared state.
    ///
    /// Extracted verbatim from <c>ProcessRecipeArrayGenerator</c> (2026-06-18,
    /// behaviour-preserving). Logic UNCHANGED.
    /// </summary>
    public static class RecipeComponentLookup
    {
        public static VueOneComponent? LookupComponent(string componentId,
            IReadOnlyList<VueOneComponent> all)
        {
            if (string.IsNullOrEmpty(componentId)) return null;
            var key = componentId.Trim();
            return all.FirstOrDefault(c =>
                string.Equals((c.ComponentID ?? string.Empty).Trim(), key,
                    StringComparison.OrdinalIgnoreCase));
        }

        public static bool NameEquals(string? a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), b, StringComparison.OrdinalIgnoreCase);
    }
}
