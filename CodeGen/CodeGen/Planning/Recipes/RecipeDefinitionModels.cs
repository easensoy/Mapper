using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Translation.Process
{
    // Root of Config/recipes.yml (loaded + cached by RecipeConfigLoader).
    public sealed class RecipeCatalog
    {
        public List<RecipeDefinition> Recipes { get; set; } = new();

        // The recipe with this Process name (case-insensitive); throws if absent (config error).
        public RecipeDefinition Recipe(string name) =>
            Recipes.FirstOrDefault(r =>
                string.Equals((r.Name ?? string.Empty).Trim(), (name ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"[Recipe] No recipe definition named '{name}' in recipes.yml.");
    }

    // One process recipe: a Process name plus named row-BLOCKS. The station class
    // (AssemblyRecipe / DisassemblyRecipe) owns which blocks to emit + order + gate; blocks (not one
    // flat list) so a station can conditionally include a block without the rows moving back into C#.
    public sealed class RecipeDefinition
    {
        public string Name { get; set; } = string.Empty;

        public Dictionary<string, List<RecipeStepDefinition>> Blocks { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // The named block's rows; throws if absent (config error).
        public IReadOnlyList<RecipeStepDefinition> Block(string key) =>
            Blocks != null && Blocks.TryGetValue(key, out var rows) && rows != null
                ? rows
                : throw new InvalidOperationException(
                    $"[Recipe] Recipe '{Name}' has no block '{key}' in recipes.yml.");
    }

    // One recipe ROW. EXACTLY ONE discriminator field is set:
    //   Cmd        — a COMMAND row: AddCmd(Cmd, State).
    //   WaitId     — a WAIT row on a LITERAL id (BX1 covers, fixed synthesized constants).
    //   WaitRef    — a WAIT row on a component resolved BY NAME (TryGetComponentId).
    //   WaitConfig — a WAIT row on a MapperConfig constant (AssemblyProcessId/DisassemblyProcessId/RobotActuatorId).
    // State = cmd/wait state value. END rows are appended by the station class (NextStep is computed, not static).
    public sealed class RecipeStepDefinition
    {
        public string? Cmd { get; set; }
        public int? WaitId { get; set; }
        public string? WaitRef { get; set; }
        public string? WaitConfig { get; set; }
        public int State { get; set; }
    }
}
