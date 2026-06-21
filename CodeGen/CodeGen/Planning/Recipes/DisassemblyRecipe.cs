using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    /// <summary>
    /// Disassembly orchestration (MapperConfig.UnparkDisassembly): emits the rows from
    /// Config/recipes.yml (recipe "Disassembly") under the gates below. Parks (single END) if any
    /// cover/shaft/bearing/clamp id is missing.
    /// </summary>
    internal static class DisassemblyRecipe
    {
        public static void Apply(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            if (!string.Equals((process.Name ?? string.Empty).Trim(), "Disassembly",
                    StringComparison.OrdinalIgnoreCase))
                return;

            // All ids must resolve (non-short-circuit &), else park.
            bool ok =
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_hr", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_vr", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_gripper", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "shaft_hr", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "shaft_vr", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "shaft_gripper", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "bearing_pnp", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "bearing_gripper", out _) &
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "clamp", out _);

            arrays.StepType.Clear();
            arrays.CmdTargetName.Clear();
            arrays.CmdStateArr.Clear();
            arrays.Wait1Id.Clear();
            arrays.Wait1State.Clear();
            arrays.NextStep.Clear();

            var b = new RecipeBuilder(arrays);

            if (!ok)
            {
                int e0 = b.Count;
                b.AddEnd(e0);
                arrays.Warnings.Add("[Recipe] Disassembly unpark requested but a cover/shaft/" +
                    "bearing/clamp id did not resolve — emitted a single END (parked). No change.");
                return;
            }

            var def = RecipeConfigLoader.Catalog.Recipe("Disassembly");

            RecipeStepEmitter.Emit(b, def.Block("handshake"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("coverRemove"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("shaftOut"), arrays, allComponents);
            RecipeStepEmitter.Emit(b, def.Block("bearingOut"), arrays, allComponents);
            RecipeStepEmitter.Emit(b, def.Block("unclamp"), arrays, allComponents);

            // M262 ejector + robot tail, each gated on its own id resolving.
            if (MapperConfig.EnableRobotTaskTail)
            {
                if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "ejector", out _))
                    RecipeStepEmitter.Emit(b, def.Block("ejector"), arrays, allComponents);
                if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "robot", out _))
                    RecipeStepEmitter.Emit(b, def.Block("robot"), arrays, allComponents);
            }

            int end = b.Count;
            b.AddEnd(MapperConfig.EnableCyclicRestart ? 0 : end);

            arrays.Warnings.Add(
                $"[Recipe] Disassembly_Station emitted: WAIT(Assembly proc {MapperConfig.AssemblyProcessId}, 7) " +
                "-> covers off (hr/vr/grip reverse, Control.xml-faithful) -> shaft out -> bearing out " +
                "(centre-home CAT work2->work1, rig-proven mapping) -> UNCLAMP (clamp home) -> " +
                (MapperConfig.EnableRobotTaskTail
                    ? "EJECTOR (EjectorForward->AtWork, EjectorBack->AtHomeInit) -> ROBOT (cmd1 start->" +
                      "WAIT done(2), cmd2 reset->WAIT ready(0)) -> END. Order is unclamp THEN eject THEN " +
                      "robot (release before push/pick). Ejector + Robot are M262, commanded by " +
                      "Disassembly over the stateRprtCmd ring extended to M262 (Stage 5b cross-PLC hops; " +
                      "EAE bridges them — NOT yet rig-verified)."
                    : "END. OMITTED — Ejector + Robot (M262 UR3e + ejector, " +
                      "orphan to Feed_Station): commanded only when EnableRobotTaskTail is ON (which " +
                      "extends the stateRprtCmd ring to M262). Off → left out so the M262 Feed ring is " +
                      "untouched and the recipe never stalls on an unreachable M262 WAIT."));
        }
    }
}
