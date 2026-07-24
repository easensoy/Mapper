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
            // holding transport (Control.xml Assembly Initialisation -> Transfer/Advancing), emitted below as
            // a fresh rising edge, THEN the PartAtAssembly material-presence safety gate.
            // PartAtAssembly material-presence gate (the physical part-present confirmation). Resolved via
            // HandoffPlanner.AssemblyStart -- WaitId>=0 is the rig synth sensor id (PartAtAssembly, id 3 from
            // Config/smc-rig.yml synthSensors; the twin does not model it), WaitId<0 falls back to a real ring
            // component by name. One resolver, reused by both models so the id lives in exactly one place.
            void EmitMaterialGate()
            {
                var mg = HandoffPlanner.AssemblyStart;
                int id = mg.WaitId >= 0 ? mg.WaitId
                    : (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, mg.SignalComponent, out var mgId) ? mgId : -1);
                if (id < 0) return;
                // Cyclic re-arm: reconstruct the PartAtAssembly RISING EDGE so a held level cannot re-fire Assembly
                // on the part it just processed -- wait CLEAR (the previous part has been removed by Disassembly's
                // robot) BEFORE waiting for the new part present.
                if (MapperConfig.EnableCyclicRestart)
                    b.AddWait(id, 0);
                b.AddWait(id, mg.WaitState);   // material: the new part is present
                // Cyclic: reset the Assembly->Disassembly handshake sentinel to idle each cycle (AFTER the part is
                // present, so it is race-free) -- gives Disassembly's WAIT(Assembly=idle) a fresh edge to catch
                // before WAIT(Assembly=done). Idempotent with the MergeFeedRing idle at row 0.
                if (MapperConfig.EnableCyclicRestart)
                    b.AddCmd("assembly_idle", MapperConfig.ProcessIdleSentinelState);
            }

            // Part-presence interlock (Config/smc-rig.yml sensorInterlocks): before a pick block, WAIT for
            // the gate sensor to report "present" so the pnp actuator cannot start on an absent part.
            // Bearing/shaft resolve their id from the ring map (they already ride the Assembly ring); the
            // cover sensor uses its COMPUTED slot (TopCoverSensorId -- the highest free Assembly-ring slot,
            // off the positional map, model-independent). Skipped silently if the flag is off or the sensor
            // is absent -- so flag-off is byte-identical and models without a given sensor still generate.
            void EmitSensorGate(string block)
            {
                if (!MapperConfig.EnableSensorPresenceInterlock) return;
                var si = CodeGen.Configuration.RigCatalog.Current.SensorInterlocks
                    .FirstOrDefault(s => string.Equals(s.Block, block, StringComparison.OrdinalIgnoreCase));
                if (si == null) return;
                int id;
                if (CodeGen.Mapping.TemplateMap.IsTopCoverSensor(si.Sensor))
                {
                    if (!MapperConfig.CoverInterlockActive) return;   // cover gate rides its computed ring slot
                    id = MapperConfig.TopCoverSensorId;
                }
                else if (!ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, si.Sensor, out id))
                    return;                                           // sensor not in this model -> no gate
                b.AddWait(id, TwinPresentState(allComponents, si.Sensor) ?? si.PresentState);
            }

            // Resolve "part present" from the TWIN's own sensor state table rather than a hardcoded polarity.
            // Control.xml declares each sensor's states (e.g. Off=0 / On=1) and Sensor_Bool publishes
            // Status:=1 for Input=1 / Status:=0 for Input=0, so the twin's State_Number IS the runtime state
            // this gate compares against. A configured constant silently inverts on any model that declares
            // its sensor the other way round -- and an inverted gate is worse than none here, because
            // state_table initialises to 0: WAIT(sensor==0) is VACUOUSLY satisfied at boot, so the interlock
            // passes instantly and the pick proceeds with nothing under it. Falls back to the configured
            // value only when the twin does not describe the sensor.
            static int? TwinPresentState(IReadOnlyList<VueOneComponent> all, string sensorName)
            {
                bool wantCover = CodeGen.Mapping.TemplateMap.IsTopCoverSensor(sensorName);
                var sensor = all.FirstOrDefault(c =>
                    string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(c.Name, sensorName, StringComparison.OrdinalIgnoreCase) ||
                     (wantCover && CodeGen.Mapping.TemplateMap.IsTopCoverSensor(c.Name))));
                if (sensor == null || sensor.States.Count == 0) return null;
                // The asserted ("something is there") state, by the twin's own naming.
                var on = sensor.States.FirstOrDefault(s => IsAssertedStateName(s.Name));
                return on?.StateNumber;
            }

            static bool IsAssertedStateName(string? name)
            {
                var n = (name ?? string.Empty).Trim();
                return n.Equals("On", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("True", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Present", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Detected", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Occupied", StringComparison.OrdinalIgnoreCase);
            }

            if (!MapperConfig.MergeFeedRing)
                EmitMaterialGate();  // clamp: PartAtAssembly BEFORE the clamp close+wait

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

            // Material-presence SAFETY gate (no-clamp _vc only): after the transport has freshly advanced and
            // settled, hold Bearing_PnP until the physical part is CONFIRMED present at the assembly position.
            // Deliberately AFTER the Transfer edge -- the edge's advance-start (Transfer/Advancing) is a
            // transient that must be caught first (a PartAtAssembly-first order would miss it), and a settled
            // transport does not by itself prove a part is present (it could settle empty). Same PartAtAssembly
            // material sensor the clamp model gates on (via the shared EmitMaterialGate above). No Bearing_PnP
            // motion before this.
            if (MapperConfig.MergeFeedRing)
                EmitMaterialGate();  // no-clamp: PartAtAssembly AFTER the fresh Transfer edge, before bearing_pnp

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

            EmitSensorGate("bearing");   // WAIT BearingSensor present, before the pick
            RecipeStepEmitter.Emit(b, def.Block("bearing"), arrays, allComponents);

            if (hasShaft)
            {
                EmitSensorGate("shaft");  // WAIT ShaftSensor present, before the first shaft move
                RecipeStepEmitter.Emit(b, def.Block("shaft"), arrays, allComponents);
            }

            if (ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_vr", out _) &&
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_hr", out _) &&
                ProcessRecipeArrayGenerator.TryGetComponentId(arrays, allComponents, "coverpnp_gripper", out _))
            {
                EmitSensorGate("coverPlace");  // WAIT TopCoverSenosr present, before the cover pick
                RecipeStepEmitter.Emit(b, def.Block("coverPlace"), arrays, allComponents);
            }

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
