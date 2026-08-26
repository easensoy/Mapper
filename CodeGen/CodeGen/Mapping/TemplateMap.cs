using System;
using System.Collections.Generic;
using CodeGen.Configuration;
using CodeGen.Models;

namespace CodeGen.Mapping
{
    public static class TemplateMap
    {
        // The ring's claim test is CASE-SENSITIVE, so recipe CmdTargetName and the instance's actuator_name
        // must both come from here; drift leaves the command circling unclaimed and the engine parked, silently.
        public static string RingKey(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

        // A cover-sensor spelling the profile omits drops the sensor from id pin, ring splice and interlock.
        // VueOne types the task arm and the jaws alike as "Robot", so the profile names the arm instance.
        // The declared roles, frozen: a per-call read of the declaration would let two components in
        // one run be classified against different versions of it.
        static readonly Configuration.SemanticRoles Roles = RigCatalog.Current.Roles;

        public static bool IsRobotTaskArm(VueOneComponent component) =>
            component != null && Roles.Is(Roles.TaskArm, component.Name);

        // Threading a CAT with no stationAdptr port dangles it and EAE rejects the whole resource.
        public static bool LacksStationAdapter(string? catType) =>
            catType != null && TemplateManifest.Find(catType) is { StationAdapter: false };

        // One constant so CAT deploy, parameters and I/O binding cannot spell the swivel CAT differently.
        public const string SevenStateCentreHomeCat = "Seven_State_Actuator_Centre_Home_CAT";

        // The one component -> FB Type decision; every consumer resolves through here (INVARIANTS.md I-4).
        public static string ResolveActuatorCatType(VueOneComponent actuator)
        {
            if (actuator == null)
                throw new System.ArgumentNullException(nameof(actuator),
                    "[CAT] no component to resolve a type for; a default here would silently pick a command vocabulary.");
            // The task arm runs a handshake rather than a stop sequence, so its CAT is chosen by the
            // role the profile assigns, and the MANIFEST says which template serves that role.
            if (IsRobotTaskArm(actuator))
                return TemplateManifest.ForInfraRole("taskArm").Name;
            return ResolveActuatorCatType(
                actuator.Name ?? string.Empty,
                actuator.States?.Count ?? 0,
                IsBranchedSevenState(actuator));
        }

        // The twin's state graph picks the CAT via the manifest. An unclaimed shape fails here rather than
        // defaulting to five-state, which would command a swivel as if it had one work stop.
        public static string ResolveActuatorCatType(
            string componentName, int stateCount, bool isBranchedSeven) =>
            TemplateManifest.ForGraph(stateCount, isBranchedSeven)?.Name
            ?? throw new System.InvalidOperationException(
                $"[CAT] '{componentName}' has a {stateCount}-state" +
                (isBranchedSeven ? " branched" : string.Empty) +
                " graph, which no CAT protocol serves. Give it a shape an existing CAT supports, or add a " +
                "CAT whose protocol declares that shape; the Mapper will not guess a command vocabulary.");

        // True when one state has both a PARALLEL and an ALTERNATIVE outgoing transition.
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
