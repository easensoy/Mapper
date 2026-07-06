using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    // Assembly_Station orchestration: emits recipes.yml rows; falls back to
    // ApplyAssemblyBearingReleaseSequence when shaft/bearing ids are missing.
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

            // MergeFeedRing: publish the Assembly idle sentinel {AssemblyProcessId, ProcessIdleSentinel}
            // at row 0 so Feed_Station's readiness gate (which WAITs on the SAME value) is satisfiable
            // each cycle -- Assembly declares "idle/ready" here and Feed holds until it sees it. The
            // value is the shared idle constant (a value no recipe ever issues as a CMD state), NOT the
            // twin's design-time State_Number, so the sentinel and the gate always match. Before the
            // material gate; clamp model unchanged (MergeFeedRing false).
            if (MapperConfig.MergeFeedRing)
                b.AddCmd("assembly_idle", MapperConfig.ProcessIdleSentinelState);

            // Row 0 material gate. Under MergeFeedRing (the no-clamp twin) Transfer REPLACES the clamp:
            // Feed advances the Transfer and HOLDS it at work through Assembly+Disassembly, so Assembly
            // must start from Transfer ADVANCED (the part is delivered and held), a single level wait.
            // Do NOT require Transfer HOME first -- Feed holds it advanced and never cycles it home until
            // Disassembly is done, so a home-first gate deadlocks Assembly (the observed "only Feed
            // works"). Run-once order (EnableCyclicRestart=false) already keeps Assembly and Disassembly
            // from overlapping, so no edge gate is needed for mutual exclusion. Otherwise (clamp model)
            // keep the HandoffPlanner gate (PartAtAssembly / the M580-local BearingSensor). Clamp model
            // is unchanged: MergeFeedRing is false there.
            if (MapperConfig.MergeFeedRing &&
                Recipes.RecipeStateClassifier.TryGetInitialConditionEdgeGate(
                    process, arrays, allComponents, out var gateId, out _, out var gateAdvanced))
            {
                b.AddWait(gateId, gateAdvanced);  // Transfer advanced = part delivered and held (clamp's job)
            }
            else
            {
                var asmStart = HandoffPlanner.AssemblyStart;
                if (asmStart.WaitId >= 0)
                    b.AddWait(asmStart.WaitId, asmStart.WaitState);
                else if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, asmStart.SignalComponent, out var matGateBsId))
                    b.AddWait(matGateBsId, asmStart.WaitState);
            }

            // SAFETY mutual exclusion: do not begin assembling until Disassembly is idle (it has
            // published {DisassemblyProcessId, 7} at its row 0). This keeps Assembly's bearing_pnp and
            // Disassembly's cover_hr from ever moving in the shared collision volume at the same time.
            // M580-local handshake, so it cannot deadlock across the M580<->BX1 seam.
            if (MapperConfig.SerializeAssemblyDisassembly && MapperConfig.UnparkDisassembly)
                RecipeStepEmitter.Emit(b, def.Block("disassemblyClear"), arrays, allComponents);

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
