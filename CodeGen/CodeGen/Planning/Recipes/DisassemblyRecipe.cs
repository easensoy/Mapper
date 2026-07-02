using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    /// <summary>
    /// Disassembly orchestration (MapperConfig.UnparkDisassembly): emits the rows from
    /// Config/recipes.yml (recipe "Disassembly") under the gates below. Parks (single END) if any
    /// cover/shaft/bearing id is missing.
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
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "bearing_gripper", out _);

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
                arrays.Warnings.Add("[Recipe] Disassembly parked: a cover/shaft/bearing id did not resolve.");
                return;
            }

            var def = RecipeConfigLoader.Catalog.Recipe("Disassembly");

            // Row 0: idle sentinel {DisassemblyProcessId, 7}. Published BEFORE the handshake wait so it
            // stands while Disassembly is idle; Assembly's disassemblyClear gate reads it and will not
            // begin a cycle until Disassembly is here (not driving the shared bearing_pnp / cover_hr).
            if (MapperConfig.SerializeAssemblyDisassembly)
                RecipeStepEmitter.Emit(b, def.Block("ready"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("handshake"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("coverRemove"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("shaftOut"), arrays, allComponents);

            // Bearing out: pick @ AtWork2 -> place @ AtWork1 -> (restage) -> Home.
            // The empty restage to AtWork2 (bearingStage) is emitted ONLY when the CAT brake is
            // OFF: without the brake the swivel must approach Home from the AtWork2 side so it
            // coasts AWAY from the ejector. With SwivelBrakeHome ON the swivel homes directly
            // from AtWork1 and the brake (reverse-coil pulse at centre) arrests it there, so the
            // restage is dropped -> the user-requested AtWork2 -> AtWork1 -> Home.
            RecipeStepEmitter.Emit(b, def.Block("bearingPick"), arrays, allComponents);
            if (!MapperConfig.SwivelBrakeHome)
                RecipeStepEmitter.Emit(b, def.Block("bearingStage"), arrays, allComponents);
            RecipeStepEmitter.Emit(b, def.Block("bearingHome"), arrays, allComponents);

            // MergeFeedRing: stamp {DisassemblyProcessId, 6} so Feed's TransferAdvancing WAIT releases
            // the swivel is home. Emitted straight after bearingHome, before anything else moves.
            if (MapperConfig.MergeFeedRing)
                RecipeStepEmitter.Emit(b, def.Block("bearingHomeSentinel"), arrays, allComponents);

            if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "clamp", out _))
                RecipeStepEmitter.Emit(b, def.Block("unclamp"), arrays, allComponents);

            // M262 ejector + robot tail, each gated on its own id resolving.
            if (MapperConfig.EnableRobotTaskTail)
            {
                // MergeFeedRing: gate the ejector on the Transfer, DERIVED from the Control.xml
                // Disassembly EjectorForward transition condition (data-driven, not hardcoded). The
                // twin owns the timing: Transfer/Returning (3) => eject WHILE Transfer is returning;
                // Transfer/ReturnedFinished (4->0) => eject after it is home.
                if (MapperConfig.MergeFeedRing &&
                    Recipes.RecipeStateClassifier.TryGetTransitionGate(process, "EjectorForward",
                        "Transfer", arrays, allComponents, out var ejId, out var ejState))
                    b.AddWait(ejId, ejState);
                if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "ejector", out _))
                    RecipeStepEmitter.Emit(b, def.Block("ejector"), arrays, allComponents);
                if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "robot", out _))
                    RecipeStepEmitter.Emit(b, def.Block("robot"), arrays, allComponents);
            }

            int end = b.Count;
            b.AddEnd(MapperConfig.EnableCyclicRestart ? 0 : end);

            arrays.Warnings.Add($"[Recipe] Disassembly emitted {b.Count} rows: handshake -> covers off " +
                "-> shaft out -> bearing out" +
                (MapperConfig.EnableRobotTaskTail ? " -> ejector -> robot" : "") + " -> END.");
        }
    }
}
