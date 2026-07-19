using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Translation.Process
{
    // Disassembly orchestration (MapperConfig.UnparkDisassembly): emits Config/recipes.yml recipe
    // "Disassembly" under the gates below. Parks (single END) if any cover/shaft/bearing id is missing.
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

            // CycleReady boot (MapperConfig.CycleReadyActive): publish "robot clear" (7) once at boot so a
            // cold-start Feed can proceed before the first Disassembly cycle. The END loop-back targets row 1, so
            // this row fires only at cold start. Rides the CMDRDYST producer (cmd 'cycle_ready') -> ready_evt ->
            // the CrossReference connection -> Feed's state_table[DisassemblyProcessId].
            if (MapperConfig.CycleReadyActive)
                b.AddCmd("cycle_ready", MapperConfig.CycleReadyReadyState);

            // Row 0 idle sentinel {DisassemblyProcessId, 7}, before the handshake wait: Assembly's
            // disassemblyClear gate reads it and holds until Disassembly is idle (not on the shared swivel).
            if (MapperConfig.SerializeAssemblyDisassembly)
                RecipeStepEmitter.Emit(b, def.Block("ready"), arrays, allComponents);

            // Cyclic re-arm: the Assembly->Disassembly handshake {AssemblyProcessId, 7} is a HELD level (it never
            // clears on its own), so END->0 would re-fire Disassembly instantly on the stale 7 (running covers/
            // shaft/bearing on an empty assembly -> collision with the next Assembly cycle). Reconstruct a fresh
            // edge: wait the Assembly-idle RESET ({AssemblyProcessId, ProcessIdleSentinel}, which Assembly now
            // publishes at the start of each cycle) BEFORE the done-handshake. Held 7 -> holds here until Assembly
            // starts its next part -> 0 -> then 7. No re-entry on a stale handshake or an empty assembly volume.
            if (MapperConfig.EnableCyclicRestart)
                b.AddWait(MapperConfig.AssemblyProcessId, MapperConfig.ProcessIdleSentinelState);

            RecipeStepEmitter.Emit(b, def.Block("handshake"), arrays, allComponents);

            // CycleReady active: the handshake fired -> a cycle is now running and the robot will be busy, so
            // retract "robot clear" (0). Feed's WAIT(DisassemblyProcessId, 7) gate therefore HOLDS until this
            // cycle republishes 7 after the robot has homed (below).
            if (MapperConfig.CycleReadyActive)
                b.AddCmd("cycle_ready", 0);

            RecipeStepEmitter.Emit(b, def.Block("coverRemove"), arrays, allComponents);

            RecipeStepEmitter.Emit(b, def.Block("shaftOut"), arrays, allComponents);

            // Bearing out: pick @ AtWork2 -> place @ AtWork1 -> (restage) -> Home. The empty restage
            // (bearingStage) is emitted ONLY when SwivelBrakeHome is OFF: without the brake the swivel
            // must approach Home from the AtWork2 side so it coasts AWAY from the ejector. With the
            // brake ON it homes directly from AtWork1 and the reverse-coil pulse arrests it at centre.
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

            // Continuous-line completion sentinel: publish Disassembly idle {DisassemblyProcessId,
            // ProcessIdleSentinelState=0} as the LAST step, AFTER the whole tail (covers->shaft->bearing->
            // unclamp->ejector->robot, whose final WAIT is robot=Home). The ring stamps state_table[DisassemblyId]
            // with every CMD's state (src_id = this process's id), so after the robot tail that slot holds the
            // robot's last cmd (2), never 0 -- so Feed's Control.xml readiness gate WAIT(Disassembly=0) could
            // never re-arm (the "runs one cycle then Feed stops" hang) AND had no robot-home guarantee. Emitting
            // idle=0 here makes that gate BOTH satisfiable and true only once the robot has dropped the part and
            // returned Home. Symmetric with Assembly's assembly_idle (no new FB; a sentinel no actuator matches).
            if (MapperConfig.CycleReadyActive)
                // CycleReady ready: republish "robot clear" (7) as the LAST step, AFTER the robot tail (whose
                // final WAIT is robot=Home). This is what re-arms Feed each cycle -> the feeder can only start
                // once the robot has dropped the part and returned Home. Reliable dedicated CrossComm transport,
                // not the ring-transported disassembly_idle sentinel (which raced against the robot's held cmd).
                b.AddCmd("cycle_ready", MapperConfig.CycleReadyReadyState);
            else if (MapperConfig.EnableCyclicRestart)
                b.AddCmd("disassembly_idle", MapperConfig.ProcessIdleSentinelState);

            int end = b.Count;
            // CycleReady loops back to row 1 (skip the row-0 boot "robot clear", which fires only at cold start).
            b.AddEnd(MapperConfig.CycleReadyActive ? 1
                     : MapperConfig.EnableCyclicRestart ? 0 : end);

            arrays.Warnings.Add($"[Recipe] Disassembly emitted {b.Count} rows: handshake -> covers off " +
                "-> shaft out -> bearing out" +
                (MapperConfig.EnableRobotTaskTail ? " -> ejector -> robot" : "") + " -> END.");
        }
    }
}
