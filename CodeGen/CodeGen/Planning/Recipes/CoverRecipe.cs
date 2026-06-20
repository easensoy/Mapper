using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    /// <summary>
    /// BX1-local Cover_Station orchestration (MapperConfig.DeployBx1CoverEngine): emits the rows from
    /// Config/recipes.yml (blocks "full"/"minimal"; cover ids in smc-rig.yml) with an END that loops
    /// to step 0 (RecipeRunOnce is exempted for Cover_Station in ProcessRecipeArrayGenerator.Generate).
    /// </summary>
    internal static class CoverRecipe
    {
        public static void Apply(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents,
            StationContents stationContents)
        {
            if (!string.Equals((process.Name ?? string.Empty).Trim(), "Cover_Station",
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (!MapperConfig.DeployBx1CoverEngine)
                return;

            arrays.StepType.Clear();
            arrays.CmdTargetName.Clear();
            arrays.CmdStateArr.Clear();
            arrays.Wait1Id.Clear();
            arrays.Wait1State.Clear();
            arrays.NextStep.Clear();

            var b = new RecipeBuilder(arrays);

            bool full = !MapperConfig.Bx1CoverMinimalCycle;
            var cover = RecipeConfigLoader.Catalog.Recipe("Cover_Station");
            RecipeStepEmitter.Emit(b, cover.Block(full ? "full" : "minimal"), arrays, allComponents);
            b.AddEnd(0);

            arrays.Warnings.Add(
                $"[Recipe] Cover_Station BX1-local cover recipe emitted ({(full ? "FULL 8-step" : "MINIMAL coverpnp_vr work->home")}); " +
                "END loops to step 0; all WAITs are the commanded actuator's own settled state (no sensor stall).");
        }
    }
}
