using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    /// <summary>
    /// Template lens — answers "which CAT (Composite FB Type) does this Control.xml
    /// component instantiate?". Consolidates the actuator-routing logic previously
    /// inside <c>SystemLayoutInjector.ResolveActuatorFBType</c> plus the implicit
    /// Sensor/Process routings spread through the same file.
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
                return "Seven_State_Actuator_CAT";

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
        /// Source-of-truth definition lives in <c>SystemLayoutInjector.IsBranchedSevenState</c>;
        /// callers that don't want to import SystemLayoutInjector can pass the
        /// precomputed bool to <see cref="CatTypeOf"/>.
        /// </summary>
        public static bool IsBranchedSevenState(VueOneComponent component)
        {
            if (component is null || component.States is null) return false;
            // A resting state with BOTH a PARALLEL outgoing transition AND an
            // ALTERNATIVE outgoing transition (Bearing_PnP's 13-state shape).
            // Matches SystemLayoutInjector.IsBranchedSevenState byte-for-byte.
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
