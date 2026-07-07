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

            // Assembly-start gate. Clamp model (MergeFeedRing false): HandoffPlanner -- WAIT(PartAtAssembly)
            // then the clamp close+wait firmly fixes the part. No-clamp _vc: the twin gates Assembly on the
            // holding transport (Control.xml Assembly Initialisation -> Transfer/Advancing), emitted just
            // below as a fresh rising edge -- so NO separate PartAtAssembly level here (it can hold TRUE
            // across cycles; the fresh Transfer edge is the valid, model-derived handoff).
            if (!MapperConfig.MergeFeedRing)
            {
                var asmStart = HandoffPlanner.AssemblyStart;
                if (asmStart.WaitId >= 0)
                    b.AddWait(asmStart.WaitId, asmStart.WaitState);
                else if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, asmStart.SignalComponent, out var matGateBsId))
                    b.AddWait(matGateBsId, asmStart.WaitState);
            }

            // Transport-holding barrier -- the no-clamp replacement for the clamp's close+wait, derived from
            // the twin's Assembly Initialisation condition (Control.xml: Transfer/Advancing). Emit a FRESH
            // RISING EDGE: WAIT the transport's advance-start state (the transient it passes through each
            // cycle -> can NEVER be a stale held level) THEN its settled/holding state. A bare WAIT(Advanced)
            // level missed this: the transport HOLDS Advanced all cycle, so a held Advanced let Bearing_PnP
            // pick before the part freshly settled -> the swivel never landed cleanly at AtWork1, its atwork
            // DI never re-toggled, and the gripper never grasped (the "manual lift/drop" that fixed it was
            // just a hand-made sensor edge). Requiring the fresh advance-start first makes the settled wait a
            // fresh landing. Fully model-derived (component + both states from Control.xml); clamp path keeps
            // its own clamp close+wait.
            if (MapperConfig.MergeFeedRing &&
                Recipes.RecipeStateClassifier.TryGetInitialConditionEdgeGate(
                    process, arrays, allComponents, out var holdId, out var holdAdvancing, out var holdSettled))
            {
                if (holdAdvancing != holdSettled)
                    b.AddWait(holdId, holdAdvancing);  // fresh advance-start (Transfer/Advancing) -- not stale
                b.AddWait(holdId, holdSettled);        // settled + holding the part (Transfer/Advanced)
            }

            // SAFETY mutual exclusion: do not begin assembling until Disassembly is idle (it has
            // published {DisassemblyProcessId, 7} at its row 0). This keeps Assembly's bearing_pnp and
            // Disassembly's cover_hr from ever moving in the shared collision volume at the same time.
            // M580-local handshake, so it cannot deadlock across the M580<->BX1 seam.
            if (MapperConfig.SerializeAssemblyDisassembly && MapperConfig.UnparkDisassembly)
                RecipeStepEmitter.Emit(b, def.Block("disassemblyClear"), arrays, allComponents);

            // No home preamble: the Ground Truth (rig-proven) goes STRAIGHT from the part gate to
            // bearing_pnp=1 (Pick) -- an extra bearing_pnp=5 (Home) here makes the swivel do a spurious
            // first move toward home/atWork1 (no pick) before the real Pick, the observed double motion.
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
