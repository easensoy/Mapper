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

        // Both twin spellings of the cover sensor, from the profile. Matched by name in the id pin, the
        // ring splice and the cover interlock, so a spelling the profile does not list drops the sensor
        // from all three at once.
        public static IReadOnlyList<string> TopCoverSensorNames =>
            RigCatalog.Current.Roles.TopCoverSensor;

        public static bool IsTopCoverSensor(string? name) => RigCatalog.Current.Roles.IsTopCover(name);

        // The task arm: one command runs a whole taught program, so it is not a five-state jaw even though
        // VueOne types both as "Robot". The twin cannot express that, so the profile names the instance.
        public static bool IsRobotTaskArm(VueOneComponent component) =>
            component != null && RigCatalog.Current.Roles.Is(RigCatalog.Current.Roles.TaskArm, component.Name);

        // Whether a CAT can be stitched into the station chain at all. Declared on the template, not
        // listed here: threading one that has no stationAdptr port dangles it and EAE rejects the resource.
        public static bool LacksStationAdapter(string? catType) =>
            catType != null && TemplateManifest.Find(catType) is { StationAdapter: false };

        // The ordered M262 cross-device segment spliced onto the M580 ring at the Disassembly seam
        // when the cross-PLC discharge is active; empty when off (ring closes locally).
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
            if (IsRobotTaskArm(actuator))
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
