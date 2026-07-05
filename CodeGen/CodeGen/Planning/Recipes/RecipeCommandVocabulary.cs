using System;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    // Commandability + command state_val per CAT; mirrors SystemLayoutInjector.ResolveActuatorFBType.
    public static class RecipeCommandVocabulary
    {
        // Five_State-commandable (toWork=1->AtWork=2, toHome=3->AtHomeInit=0); Sensors/Processes and 7-state/branched are not (unless the stub flips them to Five_State).
        public static bool IsFiveStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            if (!MapperConfig.StubSevenStateActuatorsAsFiveState
                && (t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t))) return false;
            return true;
        }

        // Drives a Seven_State_Actuator_CAT ECC (7-state / branched swivel) -> Pick/Place/Home CMDs; false when the stub runs it as Five_State.
        public static bool IsSevenStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            if (MapperConfig.StubSevenStateActuatorsAsFiveState) return false;
            return t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t);
        }

        // Condition Name (e.g. "Bearing_PnP/AtPick") -> Centre-Home state_val; -1 if no match. Disassembly AtPick2/AtPlace2 share the Work1/Work2 slots.
        public static int MapSevenStateCommandFromConditionName(string? conditionName)
        {
            if (string.IsNullOrEmpty(conditionName)) return -1;
            int slash = conditionName.LastIndexOf('/');
            string stateName = slash >= 0 ? conditionName.Substring(slash + 1) : conditionName;
            string lower = stateName.Trim().ToLowerInvariant();
            // Order matters (place before pick): state_val 1=Work1(Pick), 3=Work2(Place), 5=Home; settle = cmd+1 (see ClassifyState).
            if (lower.Contains("place")) return 3;
            if (lower.Contains("pick"))  return 1;
            if (lower.Contains("home") || lower.Contains("returned")) return 5;
            return -1;
        }

        // Gripper (name contains gripper/grasp): direction comes from the Assembly step name, not the WAIT condition (same ReturnedHome both ways).
        public static bool IsGripperTarget(VueOneComponent t)
        {
            if (t == null) return false;
            var n = (t.Name ?? string.Empty).ToLowerInvariant();
            return n.Contains("gripper") || n.Contains("grasp");
        }

        // Assembly step name -> gripper command: 1=CLOSE/grip(AtWork=2), 3=OPEN/release(AtHomeInit=0), -1=unknown. "open" checked FIRST — R-12.
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
