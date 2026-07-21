using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    public static class TemplateMap
    {
        // VacuumGripperNames is empty until Vacuum_Gripper_CAT is in the Template Library;
        // gripper instances otherwise fall through to Five_State_Actuator_CAT.
        public static readonly HashSet<string> VacuumGripperNames =
            new(StringComparer.OrdinalIgnoreCase) { };

        public static string CatTypeOf(VueOneComponent component, bool isBranchedSeven = false)
        {
            if (component is null) return "Five_State_Actuator_CAT";
            // Only the real UR3e task arm routes to Robot_Task_CAT; Type="Robot" grippers must not.
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

        // VueOne spells the top-cover sensor inconsistently across twin revisions: the original component name
        // carries a typo ("TopCoverSenosr") and corrected models use "TopCoverSensor" (the VcID was always the
        // corrected spelling). Match EITHER everywhere, because this component is matched BY NAME in the sensor
        // allow-list, the registry, the state_table id pin and the cover ring -- so a twin rename would silently
        // drop the sensor from all of them and take the whole cover interlock with it.
        public static readonly string[] TopCoverSensorNames = { "TopCoverSenosr", "TopCoverSensor" };

        public static bool IsTopCoverSensor(string? name)
        {
            var n = (name ?? string.Empty).Trim();
            foreach (var w in TopCoverSensorNames)
                if (n.Equals(w, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // TRUE only for the real UR3e task arm; Type="Robot" grippers (*Gripper*/*Grasp*) are excluded.
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

        // CAT types whose .fbt declares NO stationAdptr port — stitching one into a station chain
        // dangles stationAdptr_in/out against non-existent ports and EAE rejects the resource.
        // Single source of truth read by both the syslay and sysres wiring sites.
        public static readonly System.Collections.Generic.IReadOnlySet<string> NoStationAdapterCatTypes =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            { "Seven_State_Actuator_CAT", "Robot_Task_CAT" };

        public static bool LacksStationAdapter(string? catType) =>
            catType != null && NoStationAdapterCatTypes.Contains(catType);

        // The ordered M262 cross-device segment spliced onto the M580 ring at the Disassembly seam
        // when the cross-PLC discharge is active; empty when off (ring closes locally).
        public static List<string> M262CrossRingSegment(bool discharge) =>
            discharge ? new List<string>(RigCatalog.Current.CrossRingSegment) : new List<string>();

        // Actuator CAT routing. Must agree with the 6 sites in Docs/INVARIANTS.md I-4 for the same
        // actuator instance (gate: MapperConfig.StubSevenStateActuatorsAsFiveState).
        public static string ResolveActuatorCatType(
            string componentName, int stateCount, bool isBranchedSeven)
        {
            if (!string.IsNullOrEmpty(componentName) &&
                VacuumGripperNames.Contains(componentName))
                return "Vacuum_Gripper_CAT";

            if (!MapperConfig.StubSevenStateActuatorsAsFiveState
                && (stateCount == 7 || isBranchedSeven))
                // Centre-home swivel: state_val 1=Work1, 3=Work2, 5=Home; core publishes 2/4/6.
                return "Seven_State_Actuator_Centre_Home_CAT";

            if (stateCount == 4)
                return "Five_State_Actuator_No_Sensors_CAT";

            return "Five_State_Actuator_CAT";
        }

        // True when a component has a state with BOTH a PARALLEL and an ALTERNATIVE outgoing
        // transition (Bearing_PnP's 13-state shape) — routes to the Seven-state CAT when the stub is off.
        public static bool IsBranchedSevenState(VueOneComponent component)
        {
            if (component is null || component.States is null) return false;
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
