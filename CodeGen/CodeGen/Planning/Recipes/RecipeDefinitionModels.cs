using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation.Process
{
    /// <summary>
    /// Root of <c>Config/recipes.yml</c> — the externalized recipe catalog.
    /// One typed model per layer so a malformed config fails with an obvious error, not a silent
    /// wrong recipe. Loaded + cached by <see cref="RecipeConfigLoader"/>.
    /// </summary>
    public sealed class RecipeCatalog
    {
        public List<RecipeDefinition> Recipes { get; set; } = new();

        /// <summary>The recipe with this Process name (case-insensitive). Throws if absent — a
        /// missing recipe is a config error, never a silent empty recipe.</summary>
        public RecipeDefinition Recipe(string name) =>
            Recipes.FirstOrDefault(r =>
                string.Equals((r.Name ?? string.Empty).Trim(), (name ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"[Recipe] No recipe definition named '{name}' in recipes.yml.");
    }

    /// <summary>
    /// One process recipe: a Process name plus named row-BLOCKS. The station class
    /// (CoverRecipe / AssemblyRecipe / DisassemblyRecipe) owns the orchestration — which blocks to
    /// emit, in what order, under which MapperConfig / HandoffPlanner gate — and pulls the row data
    /// from here. Blocks (not one flat list) so a station can conditionally include a block
    /// (e.g. the cover detour, the ejector/robot tail) without the rows moving back into C#.
    /// </summary>
    public sealed class RecipeDefinition
    {
        public string Name { get; set; } = string.Empty;

        public Dictionary<string, List<RecipeStepDefinition>> Blocks { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The named block's rows. Throws if absent (config error).</summary>
        public IReadOnlyList<RecipeStepDefinition> Block(string key) =>
            Blocks != null && Blocks.TryGetValue(key, out var rows) && rows != null
                ? rows
                : throw new InvalidOperationException(
                    $"[Recipe] Recipe '{Name}' has no block '{key}' in recipes.yml.");
    }

    /// <summary>
    /// One recipe ROW (the config row). EXACTLY ONE discriminator field must be set, and it
    /// determines the kind of row written via <see cref="RecipeBuilder"/>:
    /// <list type="bullet">
    ///   <item><c>Cmd</c> — a COMMAND row (RecipeCommand): <c>AddCmd(Cmd, State)</c>.</item>
    ///   <item><c>WaitId</c> — a WAIT row (RecipeWait) on a LITERAL id: <c>AddWait(WaitId, State)</c>
    ///         (used for the BX1 covers, whose ids are fixed synthesized constants).</item>
    ///   <item><c>WaitRef</c> — a WAIT row on a component resolved BY NAME via
    ///         <c>ProcessRecipeArrayGenerator.TryGetComponentId</c> (the same resolution the
    ///         hardcoded code used).</item>
    ///   <item><c>WaitConfig</c> — a WAIT row on a <c>MapperConfig</c> constant
    ///         (AssemblyProcessId / DisassemblyProcessId / RobotActuatorId).</item>
    /// </list>
    /// <c>State</c> is the cmd/wait state value (default 0). END rows (RecipeEnd) are NOT modelled
    /// here — the station class appends <c>AddEnd(...)</c> because the END NextStep is computed
    /// (cyclic restart vs run-once self-park vs Cover_Station's continuous loop), not static data.
    /// </summary>
    public sealed class RecipeStepDefinition
    {
        public string? Cmd { get; set; }
        public int? WaitId { get; set; }
        public string? WaitRef { get; set; }
        public string? WaitConfig { get; set; }
        public int State { get; set; }
    }
}
