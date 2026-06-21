using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    /// <summary>
    /// Assembly_Station orchestration: emits the rows from Config/recipes.yml (recipe
    /// "Assembly_Station") under the gates below. Falls back to ApplyAssemblyBearingReleaseSequence
    /// when shaft/bearing ids are missing.
    /// </summary>
    internal static class AssemblyRecipe
    {
        public static void Apply(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            if (!string.Equals((process.Name ?? string.Empty).Trim(), "Assembly_Station",
                    StringComparison.OrdinalIgnoreCase))
                return;

            bool hasShaft =
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "shaft_vr", out _) &&
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "shaft_hr", out _) &&
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "shaft_gripper", out _);

            if (!ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "bearing_pnp", out _) ||
                !ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "bearing_gripper", out _) ||
                !hasShaft)
            {
                ProcessRecipeArrayGenerator.ApplyAssemblyBearingReleaseSequence(process, arrays, allComponents);
                return;
            }

            arrays.StepType.Clear();
            arrays.CmdTargetName.Clear();
            arrays.CmdStateArr.Clear();
            arrays.Wait1Id.Clear();
            arrays.Wait1State.Clear();
            arrays.NextStep.Clear();

            var b = new RecipeBuilder(arrays);
            var def = RecipeConfigLoader.Catalog.Recipe("Assembly_Station");

            // Row 0 material gate from the HandoffPlanner (PartAtAssembly across the cross-device
            // segment when discharge is active, else the M580-local BearingSensor).
            var asmStart = HandoffPlanner.AssemblyStart;
            if (asmStart.WaitId >= 0)
                b.AddWait(asmStart.WaitId, asmStart.WaitState);
            else if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, asmStart.SignalComponent, out var matGateBsId))
                b.AddWait(matGateBsId, asmStart.WaitState);

            if (MapperConfig.EnableSevenStateHomePreamble)
                RecipeStepEmitter.Emit(b, def.Block("homePreamble"), arrays, allComponents);

            bool hasClamp = ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "clamp", out _);
            if (hasClamp)
                RecipeStepEmitter.Emit(b, def.Block("clampClose"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("bearing"), arrays, allComponents);

            if (hasShaft)
                RecipeStepEmitter.Emit(b, def.Block("shaft"), arrays, allComponents);

            if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_vr", out _) &&
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_hr", out _) &&
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_gripper", out _))
                RecipeStepEmitter.Emit(b, def.Block("coverPlace"), arrays, allComponents);

            // The twin holds the clamp through assembly+disassembly: open it here only when Disassembly
            // is parked; otherwise publish the handshake sentinel and let Disassembly unclamp.
            if (hasClamp && !MapperConfig.UnparkDisassembly)
                RecipeStepEmitter.Emit(b, def.Block("clampOpen"), arrays, allComponents);
            else if (MapperConfig.UnparkDisassembly)
                RecipeStepEmitter.Emit(b, def.Block("handshake"), arrays, allComponents);

            int end = b.Count;
            b.AddEnd(end);

            arrays.Warnings.Add(
                "[Recipe] Assembly_Station runtime recipe (clean, Control.xml-faithful): " +
                "Clamp Close -> WAIT clamped -> Bearing_PnP Pick -> WAIT AtPick -> " +
                "Bearing_Gripper Grip -> WAIT AtWork -> " +
                "Bearing_PnP Place -> WAIT AtPlace -> Bearing_Gripper Release -> " +
                "WAIT gripper home -> Bearing_PnP Home -> WAIT AtHomeInit -> shaft_vr Work -> " +
                "shaft_gripper Grip -> shaft_vr Home -> shaft_hr Work -> shaft_vr Work (place) -> " +
                "shaft_gripper Release -> shaft_vr Home -> shaft_hr Home -> cover_vr down -> grip cover -> " +
                "cover_vr up -> cover_hr advance -> cover_vr down -> release cover -> cover_vr up -> " +
                "cover_hr home -> Clamp Open -> WAIT released -> " +
                "END. Every CMD has its own " +
                "WAIT; stable home-wait is AtHomeInit=0; no material-ready gate.");
        }
    }
}
