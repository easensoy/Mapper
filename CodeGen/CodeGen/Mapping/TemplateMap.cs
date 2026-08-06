using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    public static class TemplateMap
    {
        // The key a component answers to on the stateRprtCmd ring. `updateComponentState.BREQ` claims a command
        // with the ST test `component_state_in.dest_name = name`, which is CASE-SENSITIVE, so the recipe's
        // CmdTargetName and the instance's own actuator_name parameter must be produced HERE, by one function:
        // if the two spellings ever drift the command circles the ring unclaimed, the actuator never moves and
        // nothing reports an error -- the engine simply parks on the following WAIT forever.
        public static string RingKey(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

        // VacuumGripperNames is empty until Vacuum_Gripper_CAT is in the Template Library;
        // gripper instances otherwise fall through to Five_State_Actuator_CAT.
        public static readonly HashSet<string> VacuumGripperNames =
            new(StringComparer.OrdinalIgnoreCase) { };


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

        // The centre-home swivel CAT. Named once so the sites that must agree on the selected vocabulary
        // (CAT deploy, parameters, I/O binding) compare against one constant rather than a repeated literal.
        public const string SevenStateCentreHomeCat = "Seven_State_Actuator_Centre_Home_CAT";

        // The twin's shape test for the centre-home swivel. One owner: the recipe's command vocabulary and
        // the CAT routing below must agree on what "seven-shape" means, and they used to spell it out
        // separately.
        public static bool IsSevenShape(VueOneComponent component) =>
            component != null &&
            ((component.States?.Count ?? 0) == 7 || IsBranchedSevenState(component));

        // THE component -> emitted FB Type decision. Every consumer (deploy, parameters, wiring, I/O
        // binding) resolves through here so the sites INVARIANTS.md I-4 requires to agree cannot drift.
        public static string ResolveActuatorCatType(VueOneComponent actuator)
        {
            if (actuator == null) return "Five_State_Actuator_CAT";
            // Only the real UR3e (IsRobotTaskArm) -> Robot_Task_CAT; Type="Robot" grippers stay Five_State/Vacuum.
            if (MapperConfig.EnableRobotTaskTail && IsRobotTaskArm(actuator))
                return "Robot_Task_CAT";
            return ResolveActuatorCatType(
                actuator.Name ?? string.Empty,
                actuator.States?.Count ?? 0,
                IsBranchedSevenState(actuator));
        }

        // Actuator CAT routing: the twin's own state graph decides the CAT, so a model change needs
        // no code change. Every consumer (deploy, parameters, I/O binding) must resolve the same way.
        public static string ResolveActuatorCatType(
            string componentName, int stateCount, bool isBranchedSeven)
        {
            if (!string.IsNullOrEmpty(componentName) &&
                VacuumGripperNames.Contains(componentName))
                return "Vacuum_Gripper_CAT";

            if (stateCount == 7 || isBranchedSeven)
                // Centre-home swivel: state_val 1=Work1, 3=Work2, 5=Home; core publishes 2/4/6.
                // A swivel the twin models with only Work1 + Work2 and no centre stop falls through to the
                // five-state CAT below, whose Home/Work vocabulary then carries Work1/Work2 respectively.
                return SevenStateCentreHomeCat;

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
