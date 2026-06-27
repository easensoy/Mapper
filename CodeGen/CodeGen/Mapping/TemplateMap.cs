using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    /// <summary>
    /// Template lens — answers "which CAT (Composite FB Type) does this Control.xml
    /// component instantiate?".
    ///
    /// The actuator branch is the gated one (Bearing_PnP toggles between Five_State
    /// stub and the real Seven_State CAT via
    /// <see cref="MapperConfig.StubSevenStateActuatorsAsFiveState"/>); the Sensor
    /// and Process branches are static.
    /// </summary>
    public static class TemplateMap
    {
        /// <summary>
        /// Names that EAE deploys as <c>Vacuum_Gripper_CAT</c> (suction-cup grippers
        /// with a single coil and no athome/atwork sensor pair). Currently empty —
        /// <c>Vacuum_Gripper_CAT</c> is not yet in the Template Library, so all
        /// gripper instances fall through to the universal <c>Five_State_Actuator_CAT</c>.
        /// Kept as a hook so a future deploy of the vacuum CAT only changes this set.
        /// </summary>
        public static readonly HashSet<string> VacuumGripperNames =
            new(StringComparer.OrdinalIgnoreCase) { };

        /// <summary>
        /// Resolves the CAT (FB Type) for any Control.xml component. Routes by
        /// component type:
        /// <list type="bullet">
        ///   <item><c>Process</c> → <c>Process1_Generic</c></item>
        ///   <item><c>Sensor</c>  → <c>Sensor_Bool_CAT</c></item>
        ///   <item><c>Actuator</c>, <c>Robot</c>, anything else → <see cref="ResolveActuatorCatType"/></item>
        /// </list>
        /// </summary>
        public static string CatTypeOf(VueOneComponent component, bool isBranchedSeven = false)
        {
            if (component is null) return "Five_State_Actuator_CAT";
            // STAGE 5b foundation: route ONLY the real UR3e task arm to the task-handshake
            // Robot_Task_CAT. Type="Robot" is NOT enough — the Bearing/Shaft/CoverPnp grippers
            // are also Type="Robot" (5-state) and MUST keep their Five_State/Vacuum mapping; the
            // narrow IsRobotTaskArm predicate excludes them. Default-off → byte-identical.
            if (MapperConfig.EnableRobotTaskTail && IsRobotTaskArm(component))
                return "Robot_Task_CAT";
            return component.Type switch
            {
                "Process" => "Process1_Generic",
                "Sensor"  => "Sensor_Bool_CAT",
                _ /* Actuator, Robot, fallback */ => ResolveActuatorCatType(
                    component.Name,
                    component.States?.Count ?? 0,
                    isBranchedSeven),
            };
        }

        /// <summary>
        /// TRUE only for the real UR3e task arm — the single Type="Robot" component that is an
        /// actual robot, NOT a robot-category gripper. In this twin <c>Bearing_Gripper</c>,
        /// <c>Shaft_Gripper</c> and <c>CoverPnp_Gripper</c> are ALSO <c>Type="Robot"</c> (5-state
        /// mechanical/vacuum grippers) and MUST keep their Five_State/Vacuum CAT. The arm is
        /// identified by exact Name "Robot", its known ComponentID, or <c>VcID="UR3e"</c>; anything
        /// named *Gripper*/*Grasp* is excluded first. Shared by every call site so the
        /// narrowing is defined in exactly one place.
        /// </summary>
        public static bool IsRobotTaskArm(VueOneComponent component)
        {
            if (component is null) return false;
            var name = component.Name ?? string.Empty;
            if (name.IndexOf("Gripper", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Grasp", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return string.Equals(name, "Robot", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(component.ComponentID, "C-c4ebfd68-0a5b-4512-889e-f6ab61bccecb",
                                 System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(component.VcID, "UR3e", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// CAT types whose .fbt declares NO stationAdptr (CaSBus) port. They are INIT-chained and
        /// (if they carry the ring node) join the stateRprtCmd report ring, but stay OFF the
        /// station/mode/fault chain. SINGLE source of truth for every wiring site — the syslay
        /// (<c>SystemLayoutInjector.BuildFeedStationWiring</c> + <c>BuildStation2Wiring</c>) and the
        /// sysres (<c>ResourceWireEmitter.NoStationAdapterTypes</c>) all read this set. Stitching one
        /// of these into a station chain dangles <c>stationAdptr_in/out</c> against ports that don't
        /// exist → EAE rejects the resource (the Stage 5b "nothing triggers" bug). Seven_State (the
        /// old, non-centre-home swivel) has the ring node but no stationAdptr; Robot_Task_CAT (UR3e)
        /// the same.
        /// </summary>
        public static readonly System.Collections.Generic.IReadOnlySet<string> NoStationAdapterCatTypes =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            { "Seven_State_Actuator_CAT", "Robot_Task_CAT" };

        /// <summary>True if <paramref name="catType"/> has no stationAdptr port (off the CaSBus chain).</summary>
        public static bool LacksStationAdapter(string? catType) =>
            catType != null && NoStationAdapterCatTypes.Contains(catType);

        /// <summary>
        /// The ordered M262 nodes spliced into the M580 Assembly/Disassembly stateRprtCmd ring as ONE
        /// cross-PLC segment (Disassembly.out -> seg[0] -> ... -> seg[^1] -> M580 head) via two
        /// cross-device adapter hops EAE bridges (the rig-proven M580&lt;-&gt;BX1 cover-ring mechanism).
        /// The ordered M262 cross-device segment spliced onto the M580 ring at the Disassembly seam
        /// when the cross-PLC discharge is active (HandoffPlanner.DischargeActive). Two cross-device
        /// adapter hops EAE bridges (Disassembly-&gt;seg[0], seg[^1]-&gt;M580 head) carry the Ejector/Robot
        /// COMMANDS out to M262 and PartAtAssembly's REPORT back into M580 — WITHOUT stretching the
        /// M580 bearing/shaft/clamp actuator ring. Order: Ejector, Robot (rig-proven
        /// Disassembly-&gt;Ejector-&gt;Robot), then PartAtAssembly (its report tacks on before the return
        /// hop). Empty when discharge is off (the ring closes locally Disassembly-&gt;head). Single
        /// source for BuildStation2Wiring (the two cross-hops), BuildFeedStationWiring +
        /// ResourceWireEmitter (intra-M262 chain + Feed-ring exclusion).
        /// </summary>
        public static List<string> M262CrossRingSegment(bool discharge) =>
            discharge ? new List<string>(RigCatalog.Current.CrossRingSegment) : new List<string>();

        /// <summary>
        /// Actuator CAT routing, factored out so callers that already know
        /// <c>isBranchedSeven</c> can avoid recomputing it:
        /// <list type="number">
        ///   <item>Vacuum gripper name → <c>Vacuum_Gripper_CAT</c>.</item>
        ///   <item>Stub OFF + (7-state OR branched) → <c>Seven_State_Actuator_CAT</c>.</item>
        ///   <item>4-state shape → <c>Five_State_Actuator_No_Sensors_CAT</c>.</item>
        ///   <item>Everything else → <c>Five_State_Actuator_CAT</c>.</item>
        /// </list>
        /// <para>
        /// Mirrors the gate at <see cref="MapperConfig.StubSevenStateActuatorsAsFiveState"/>
        /// — see <c>Docs/INVARIANTS.md</c> I-4 for the 6 sites that must agree on
        /// the same answer for the same actuator instance.
        /// </para>
        /// </summary>
        public static string ResolveActuatorCatType(
            string componentName, int stateCount, bool isBranchedSeven)
        {
            if (!string.IsNullOrEmpty(componentName) &&
                VacuumGripperNames.Contains(componentName))
                return "Vacuum_Gripper_CAT";

            if (!MapperConfig.StubSevenStateActuatorsAsFiveState
                && (stateCount == 7 || isBranchedSeven))
                // Bearing_PnP instantiates the centre-home swivel CAT (3-position:
                // Work1=Pick / Work2=Place / centre Home).
                // It wires like Five_State (has stationAdptr + stateRprtCmd, uses
                // updateComponentState, takes mode from the station ring) — unlike
                // the old Seven_State_Actuator_CAT which had no stationAdptr. Command
                // vocabulary: state_val 1=Work1, 3=Work2, 5=Home; the core settles
                // publishing current_state_to_process 2/4/6.
                return "Seven_State_Actuator_Centre_Home_CAT";

            if (stateCount == 4)
                return "Five_State_Actuator_No_Sensors_CAT";

            return "Five_State_Actuator_CAT";
        }

        /// <summary>
        /// True when a component is a "13-state branched" actuator (one PARALLEL
        /// state and one ALTERNATIVE state — Bearing_PnP's shape). This is the
        /// shape that routes to <c>Seven_State_Actuator_CAT</c> when the stub flag
        /// is off, even though the raw state count is 13 rather than 7.
        ///
        /// This is the single canonical definition:
        /// <c>SystemInjector.ResolveActuatorFBType</c> and
        /// <c>RecipeCommandVocabulary</c> both call it; callers may also pass the
        /// precomputed bool to <see cref="CatTypeOf"/>.
        /// </summary>
        public static bool IsBranchedSevenState(VueOneComponent component)
        {
            if (component is null || component.States is null) return false;
            // A resting state with BOTH a PARALLEL outgoing transition AND an
            // ALTERNATIVE outgoing transition (Bearing_PnP's 13-state shape).
            // The per-state Transitions null-guard below is defensive only —
            // VueOneState.Transitions is always a non-null list (= new()).
            foreach (var state in component.States)
            {
                bool hasParallel = false, hasAlternative = false;
                if (state.Transitions is null) continue;
                foreach (var tr in state.Transitions)
                {
                    if (string.Equals(tr.TransitionType, "PARALLEL", StringComparison.OrdinalIgnoreCase))
                        hasParallel = true;
                    else if (string.Equals(tr.TransitionType, "ALTERNATIVE", StringComparison.OrdinalIgnoreCase))
                        hasAlternative = true;
                }
                if (hasParallel && hasAlternative) return true;
            }
            return false;
        }
    }
}
