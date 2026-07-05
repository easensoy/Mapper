using System;
using CodeGen.Configuration;
using CodeGen.Mapping;   // TemplateMap.IsBranchedSevenState
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    // CAT-command vocabulary for recipe generation: decides commandability and command state_val for
    // Five_State actuators, Seven_State (Centre_Home) swivels, and mechanical grippers. Must mirror
    // SystemLayoutInjector.ResolveActuatorFBType's commandability rules.
    public static class RecipeCommandVocabulary
    {
        // True if a target can be commanded with the Five_State work/home pair
        // (toWork=1 settles AtWork=2, toHome=3 settles AtHomeInit=0). Sensors/Processes and
        // 7-state / branched (Seven_State) components are not Five_State-commandable.
        public static bool IsFiveStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            // Stub on: Seven_State actuators emit as Five_State_Actuator_CAT, so they ARE
            // Five_State-commandable (work/home instead of Pick/Place/Home).
            if (!MapperConfig.StubSevenStateActuatorsAsFiveState
                && (t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t))) return false;
            return true;
        }

        // True for targets that drive a Seven_State_Actuator_CAT ECC (Bearing_PnP and any 13-state
        // branched swivel), so the classifier emits Pick/Place/Home CMDs instead of a settled-WAIT row.
        public static bool IsSevenStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            // Stub on: nothing is Seven_State-commandable (Bearing_PnP runs as Five_State), so the
            // recipe must not emit Pick/Place/Home a Five_State ECC can't honour.
            if (MapperConfig.StubSevenStateActuatorsAsFiveState) return false;
            return t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t);
        }

        // Maps a Sequence_Condition Name (e.g. "Bearing_PnP/AtPick") to the Centre-Home swivel command
        // state_val; -1 when no keyword matches. Branched-13-state caveat: the Disassembly AtPick2/AtPlace2
        // names route to the same primary Work1/Work2 slots (both legs share the slots).
        public static int MapSevenStateCommandFromConditionName(string? conditionName)
        {
            if (string.IsNullOrEmpty(conditionName)) return -1;
            // Strip optional "Component/" prefix.
            int slash = conditionName.LastIndexOf('/');
            string stateName = slash >= 0 ? conditionName.Substring(slash + 1) : conditionName;
            string lower = stateName.Trim().ToLowerInvariant();
            // Order matters: "atplace" contains "place", "atpick" contains "pick".
            // Place check before pick keeps "atplace2" / "place2" routing to Place.
            // Seven_State_Actuator_Centre_Home_CAT command vocabulary (state_val):
            //   1 = Work1 (Pick), 3 = Work2 (Place), 5 = Home (centre).
            // The core then settles publishing current_state_to_process = cmd+1
            // (AtWork1=2 / AtWork2=4 / AtHome=6) — see the WAIT row's Wait1State
            // (= sevenStateCmd + 1) in ClassifyState. The "2"-suffixed Disassembly
            // names (AtPick2 / AtPlace2) are the SAME physical Work1/Work2 slots.
            if (lower.Contains("place")) return 3;
            if (lower.Contains("pick"))  return 1;
            if (lower.Contains("home") || lower.Contains("returned")) return 5;
            return -1;
        }

        // A mechanical gripper (name contains "gripper"/"grasp"): deploys as Five_State but its WAIT
        // condition doesn't encode grip-vs-release (same "ReturnedHome" both ways), so the command comes
        // from the Assembly step name (MapGripperCommandFromStepName).
        public static bool IsGripperTarget(VueOneComponent t)
        {
            if (t == null) return false;
            var n = (t.Name ?? string.Empty).ToLowerInvariant();
            return n.Contains("gripper") || n.Contains("grasp");
        }

        // Maps an Assembly step name to the gripper command: 1=CLOSE/grip (settles AtWork=2), 3=OPEN/release
        // (settles AtHomeInit=0), -1=unknown. "open" checked FIRST so "BearingPnPOpenGripper" routes OPEN
        // while "Gripping_Part" routes CLOSE — the gripper holds the bearing at pick, releases at place.
        public static int MapGripperCommandFromStepName(string? stepName)
        {
            var n = (stepName ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0) return -1;
            if (n.Contains("open") || n.Contains("release") || n.Contains("unclamp")) return 3;
            if (n.Contains("grip") || n.Contains("grasp") || n.Contains("clos") ||
                n.Contains("hold") || n.Contains("pick")) return 1;
            return -1;
        }
    }
}
