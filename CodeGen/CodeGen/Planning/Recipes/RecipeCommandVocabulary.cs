using System;
using CodeGen.Configuration;
using CodeGen.Mapping;   // TemplateMap.IsBranchedSevenState (canonical branched-swivel predicate)
using CodeGen.Models;

namespace CodeGen.Translation.Process.Recipes
{
    /// <summary>
    /// Pure CAT-command vocabulary for recipe generation: given a Control.xml
    /// component (or a Sequence_Condition / Assembly-step name), decide
    /// commandability and the command state_val for Five_State actuators,
    /// Seven_State (Centre_Home Pick/Place/Home) swivels, and mechanical
    /// grippers. Every method is a pure function of its arguments — the only
    /// external input is the static
    /// <see cref="MapperConfig.StubSevenStateActuatorsAsFiveState"/> flag; there
    /// is no I/O, no shared state, and no <c>RecipeArrays</c> contact.
    ///
    /// Mirrors <c>SystemLayoutInjector.ResolveActuatorFBType</c>'s commandability
    /// rules; branched-swivel detection is shared via
    /// <c>TemplateMap.IsBranchedSevenState</c>.
    /// </summary>
    public static class RecipeCommandVocabulary
    {
        /// <summary>
        /// True if a condition target can be commanded with the Five_State
        /// work/home pair (toWork=1 settles AtWork=2, toHome=3 settles
        /// AtHomeInit=0). Mirrors <c>SystemLayoutInjector.ResolveActuatorFBType</c>:
        /// sensors and Processes are never commandable; a 7-state or PARALLEL+
        /// ALTERNATIVE-branched component is Seven_State (Bearing_PnP) and is NOT
        /// Five_State-commandable; everything else (5-state cylinders + mechanical
        /// grippers, whether VueOne Type is "Actuator" or "Robot") is. Used by the
        /// condition-driven Station-2 classifier so we only emit work/home commands
        /// for targets whose ECC actually understands them.
        /// </summary>
        public static bool IsFiveStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            // Interim stub: when on, Seven_State actuators ARE Five_State-commandable
            // (they emit as Five_State_Actuator_CAT — see MapperConfig flag), so the
            // recipe drives them with work/home instead of Pick/Place/Home.
            if (!MapperConfig.StubSevenStateActuatorsAsFiveState
                && (t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t))) return false;
            return true;
        }

        /// <summary>
        /// Complementary to <see cref="IsFiveStateCommandable"/>: returns true for
        /// targets that drive a Seven_State_Actuator_CAT ECC (Bearing_PnP and any
        /// 13-state branched-swivel actuator routed through the seven-state runtime).
        /// Used by the condition-driven classifier to emit Pick/Place/Home CMDs on
        /// these targets instead of falling back to a settled-WAIT-only row.
        /// </summary>
        public static bool IsSevenStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            // Interim stub: when on, nothing is Seven_State-commandable — Bearing_PnP
            // runs as Five_State (see MapperConfig flag), so the recipe must NOT emit
            // Pick/Place/Home state_val commands a Five_State ECC can't honour.
            if (MapperConfig.StubSevenStateActuatorsAsFiveState) return false;
            return t.States.Count == 7 || TemplateMap.IsBranchedSevenState(t);
        }

        /// <summary>
        /// Maps a Sequence_Condition Name (e.g. "Bearing_PnP/AtPick") to the
        /// Seven_State_Actuator_CAT command state_val. The SE Seven_State ECC
        /// publishes current_state_to_pocess matching state_val once settled:
        ///   AtPick (and Picking)   -> 1
        ///   AtPlace / Place        -> 2
        ///   AtHome / ReturnedHome  -> 0
        /// Returns -1 when no keyword matches so the caller can decide whether to
        /// emit a settled-WAIT fallback or extend this table.
        ///
        /// <para>TODO (Seven_State data-driven Phase 1, see
        /// Docs/SevenStateActuator_DataDriven_Gap.md): once the CAT carries
        /// TargetPickState / TargetPlaceState / TargetHomeState parameters
        /// matching the Control.xml State_Number on each actuator, this
        /// keyword shim is redundant — caller can use the resolved waitState
        /// directly the way the Five_State path does. Keep this method until
        /// the parameter surface is widened; delete it the same commit that
        /// stops the recipe generator from special-casing Seven_State.</para>
        ///
        /// <para>TODO (Phase 2, branched 13-state Bearing_PnP): the
        /// disassembly-side states AtPick2 / AtPlace2 / Athome2 currently
        /// fall through to the same Pick / Place / Home keywords and route
        /// to the primary leg's state_val. That is silently wrong — both
        /// legs share the same target slots. Fix when Disassembly testing
        /// starts (see Phase 2 of the design doc — either add Pick2/Place2
        /// state slots to the ECC + a BranchSelector parameter, or split
        /// the branched actuator into two parallel CAT instances).</para>
        /// </summary>
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

        /// <summary>
        /// True when the condition target is a mechanical gripper (VueOne
        /// Type="Robot" whose name contains "gripper"/"grasp"). Grippers deploy as
        /// Five_State_Actuator_CAT but, unlike clamps/cylinders, their Control.xml
        /// WAIT condition does not encode grip-vs-release direction (it is the same
        /// "ReturnedHome" for both), so the command is taken from the Assembly STEP
        /// name instead (see <see cref="MapGripperCommandFromStepName"/>).
        /// </summary>
        public static bool IsGripperTarget(VueOneComponent t)
        {
            if (t == null) return false;
            var n = (t.Name ?? string.Empty).ToLowerInvariant();
            return n.Contains("gripper") || n.Contains("grasp");
        }

        /// <summary>
        /// Maps an Assembly STEP name to the Five_State gripper command:
        ///   1 = toWork  (CLOSE / grip  — settles AtWork=2),
        ///   3 = toHome  (OPEN  / release — settles AtHomeInit=0),
        ///  -1 = unknown (caller falls back to the condition-derived command).
        /// "open"/"release"/"unclamp" -> OPEN; otherwise a grip/grasp/close/hold/
        /// pick keyword -> CLOSE. Open is checked FIRST so "BearingPnPOpenGripper"
        /// (which also contains "gripper") routes to OPEN, while "Gripping_Part"
        /// routes to CLOSE. This is what sequences the bearing pick-and-place
        /// correctly: gripper CLOSES at the pick/grip step to hold the bearing,
        /// OPENS at the place/release step to let it go.
        /// </summary>
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
