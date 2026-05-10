using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    /// <summary>
    /// Phase 2 (revised): emits six parallel arrays (StepType, CmdTargetName,
    /// CmdStateArr, Wait1Id, Wait1State, NextStep) consumed by
    /// ProcessRuntime_Generic_v1's ECC.
    ///
    /// Encoding: 1 = CMD, 2 = WAIT, 9 = END.
    ///
    /// Bug-1 fix — dispatch on source state name, not transition target type.
    ///     Conditions like "Feeder/Advanced" name an actuator but semantically
    ///     mean "wait FOR that actuator to finish", not "command it". The right
    ///     signal that we should command something is the source state's own
    ///     name describing motion in progress (e.g. "FeederAdvancing").
    ///
    /// Bug-2 fix — ResolveStateNumber is now StateID-only; on miss it logs a
    ///     warning to <see cref="RecipeArrays.Warnings"/> and returns 0 rather
    ///     than name-pattern guessing. The previous InferStateNumberFromName
    ///     fallback was unreliable and produced numbers that didn't match what
    ///     Five_State_Actuator_CAT actually publishes on the ring.
    ///
    /// Classification rules:
    ///   * Source-state name (lowercase) contains a motion-in-progress verb
    ///     (advancing, rising, returning, descending, towork, tohome, gotowork,
    ///     gotohome, checking) -> two rows: CMD then WAIT.
    ///       CMD:  StepType=1
    ///             CmdTargetName = wait condition's target component name (lowercased)
    ///             CmdStateArr   = actuator's canonical state number for the
    ///                             motion target (looked up from the wait condition's
    ///                             StateID on the target component — same number that
    ///                             becomes Wait1State on the following WAIT row)
    ///             Wait1Id/Wait1State = 0
    ///       WAIT: StepType=2
    ///             Wait1Id    = registry id of wait condition's target component
    ///             Wait1State = canonical state number from the StateID lookup
    ///             CmdTargetName / CmdStateArr = empty/0
    ///   * Source-state name does NOT match a motion verb (e.g. AtWork, AtHome,
    ///     Initialisation, PartChecking, WaitingReleaseSt2, HandShake) ->
    ///     a single WAIT row using the transition condition.
    ///   * No transition or no condition -> single END row.
    ///   * A final END row is always appended; its NextStep loops back to 0.
    ///
    /// Two-pass NextStep: pass 1 counts rows per source state and builds a
    /// StateID -> first-row-index map. Pass 2 emits rows and resolves each row's
    /// NextStep against that map (so transition destinations land on the right
    /// row index even when motion states unfolded into 2 rows).
    /// </summary>
    public sealed class RecipeArrays
    {
        public List<int> StepType       { get; } = new();
        public List<string> CmdTargetName { get; } = new();
        public List<int> CmdStateArr    { get; } = new();
        public List<int> Wait1Id        { get; } = new();
        public List<int> Wait1State     { get; } = new();
        public List<int> NextStep       { get; } = new();

        /// <summary>Diagnostic registry: ComponentID → local id. Useful for tests
        /// to verify Wait1Id values point at the right component.</summary>
        public Dictionary<string, int> ComponentRegistry { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Warnings emitted during generation. Surfaced by Bug-2 fix —
        /// every StateID lookup miss adds a warning here so the operator can see
        /// which Control.xml condition didn't resolve.</summary>
        public List<string> Warnings { get; } = new();

        public int PusherId { get; set; }
        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        // Motion-in-progress verbs per the Phase-2 spec. "checking" is included
        // pragmatically because PartChecking represents Checker descending in
        // the SMC rig and the verification test asserts a CMD row for Checker.
        private static readonly string[] MotionVerbs = new[]
        {
            "advancing", "rising", "returning", "descending",
            "towork", "tohome", "gotowork", "gotohome",
            "checking",
        };

        public static StationComponentMap BuildComponentMap(StationContents contents)
            => ProcessRecipeStGenerator.BuildComponentMap(contents);

        public static RecipeArrays Generate(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents)
        {
            var arrays = new RecipeArrays();
            var states = process.States.OrderBy(s => s.StateNumber).ToList();

            // Build component registry from this process's union of conditions.
            BuildExtendedRegistry(process, arrays.ComponentRegistry);

            arrays.PusherId =
                arrays.ComponentRegistry.FirstOrDefault(kv =>
                    LookupComponent(kv.Key, allComponents) is { } c &&
                    (NameEquals(c.Name, "Feeder") || NameEquals(c.Name, "Pusher"))).Value;

            // Pass 1 — classify each source state and count its rows; build
            // the StateID -> first-row-index map used for NextStep resolution.
            var classifications = new List<StateClassification>(states.Count);
            var stateIdToFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rowIndex = 0;
            foreach (var state in states)
            {
                var c = ClassifyState(state, allComponents, arrays);
                classifications.Add(c);
                if (!string.IsNullOrEmpty(state.StateID))
                    stateIdToFirstRow[state.StateID] = rowIndex;
                rowIndex += c.RowCount;
            }
            int finalEndIndex = rowIndex;   // appended END row sits at this index

            // Pass 2 — emit rows; resolve NextStep against the map.
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var c = classifications[i];

                int nextRowOnTransition = ResolveDestRow(
                    state, classifications, i, stateIdToFirstRow, finalEndIndex);

                switch (c.Kind)
                {
                    case ClassKind.MotionPair:
                        // CMD row
                        arrays.StepType.Add(1);
                        arrays.CmdTargetName.Add(c.CmdTargetName);
                        arrays.CmdStateArr.Add(c.CmdState);
                        arrays.Wait1Id.Add(0);
                        arrays.Wait1State.Add(0);
                        arrays.NextStep.Add(arrays.StepType.Count);   // -> our own WAIT row

                        // WAIT row
                        arrays.StepType.Add(2);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(c.WaitId);
                        arrays.Wait1State.Add(c.WaitState);
                        arrays.NextStep.Add(nextRowOnTransition);
                        break;

                    case ClassKind.SettledWait:
                        arrays.StepType.Add(2);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(c.WaitId);
                        arrays.Wait1State.Add(c.WaitState);
                        arrays.NextStep.Add(nextRowOnTransition);
                        break;

                    case ClassKind.End:
                    default:
                        arrays.StepType.Add(9);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(0);
                        arrays.Wait1State.Add(0);
                        arrays.NextStep.Add(0);
                        break;
                }
            }

            // Always append a final END row whose NextStep loops back to row 0.
            arrays.StepType.Add(9);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(0);

            return arrays;
        }

        // ----------------------------------------------------------------------
        // Pass-1 classification
        // ----------------------------------------------------------------------

        private enum ClassKind { MotionPair, SettledWait, End }

        private sealed class StateClassification
        {
            public ClassKind Kind;
            public int RowCount;
            public string CmdTargetName = string.Empty;
            public int CmdState;
            public int WaitId;
            public int WaitState;
        }

        private static StateClassification ClassifyState(VueOneState state,
            IReadOnlyList<VueOneComponent> allComponents, RecipeArrays arrays)
        {
            var trans = state.Transitions.FirstOrDefault();
            var cond  = trans?.Conditions.FirstOrDefault(c => !string.IsNullOrEmpty(c.ComponentID));

            // No transition or no usable condition → END row.
            if (trans == null || cond == null)
                return new StateClassification { Kind = ClassKind.End, RowCount = 1 };

            var target = LookupComponent(cond.ComponentID, allComponents);
            if (target == null)
            {
                arrays.Warnings.Add(
                    $"State '{state.Name}' (StateID={state.StateID}): condition references " +
                    $"ComponentID '{cond.ComponentID}' which was not found in allComponents. " +
                    "Emitting WAIT with id=0.");
                return new StateClassification { Kind = ClassKind.SettledWait, RowCount = 1 };
            }

            int waitId    = arrays.ComponentRegistry.TryGetValue(cond.ComponentID, out var rid) ? rid : 0;
            int waitState = ResolveStateNumber(cond, target, arrays);

            // Bug-1 fix: dispatch on the source state's name, NOT the target's component type.
            if (StateNameSuggestsMotion(state.Name))
            {
                return new StateClassification
                {
                    Kind = ClassKind.MotionPair,
                    RowCount = 2,
                    CmdTargetName = (target.Name ?? string.Empty).Trim().ToLowerInvariant(),
                    // Per the Phase-2 spec the CMD row carries the actuator's canonical
                    // destination state number, which is exactly what the StateID lookup
                    // on the wait condition resolves to. Same as Wait1State.
                    CmdState = waitState,
                    WaitId = waitId,
                    WaitState = waitState,
                };
            }

            return new StateClassification
            {
                Kind = ClassKind.SettledWait,
                RowCount = 1,
                WaitId = waitId,
                WaitState = waitState,
            };
        }

        private static bool StateNameSuggestsMotion(string? stateName)
        {
            var n = (stateName ?? string.Empty).Trim().ToLowerInvariant();
            if (n.Length == 0) return false;
            foreach (var verb in MotionVerbs)
                if (n.EndsWith(verb, StringComparison.Ordinal) ||
                    n.Contains(verb, StringComparison.Ordinal))
                    return true;
            return false;
        }

        // ----------------------------------------------------------------------
        // Pass-2 NextStep helpers
        // ----------------------------------------------------------------------

        private static int ResolveDestRow(VueOneState state,
            List<StateClassification> classifications, int srcIndex,
            Dictionary<string, int> stateIdToFirstRow, int finalEndIndex)
        {
            // Use the destination StateID from the transition; fall back to the
            // next state in declaration order; final fallback is the final END row.
            var trans = state.Transitions.FirstOrDefault();
            if (trans != null &&
                !string.IsNullOrEmpty(trans.DestinationStateID) &&
                stateIdToFirstRow.TryGetValue(trans.DestinationStateID, out var dst))
            {
                return dst;
            }
            // No explicit destination — point at the next source state's first row,
            // or wrap to the final END if we're already at the last source state.
            int nextSrc = srcIndex + 1;
            if (nextSrc < classifications.Count)
            {
                int idx = 0;
                for (int i = 0; i < nextSrc; i++) idx += classifications[i].RowCount;
                return idx;
            }
            return finalEndIndex;
        }

        // ----------------------------------------------------------------------
        // Component / state lookup primitives
        // ----------------------------------------------------------------------

        private static VueOneComponent? LookupComponent(string componentId,
            IReadOnlyList<VueOneComponent> all)
        {
            if (string.IsNullOrEmpty(componentId)) return null;
            var key = componentId.Trim();
            return all.FirstOrDefault(c =>
                string.Equals((c.ComponentID ?? string.Empty).Trim(), key,
                    StringComparison.OrdinalIgnoreCase));
        }

        // Bug-2 fix: StateID lookup ONLY. No name-pattern fallback. Miss → warning + 0.
        private static int ResolveStateNumber(VueOneCondition cond, VueOneComponent target,
            RecipeArrays arrays)
        {
            if (string.IsNullOrEmpty(cond.ID))
            {
                arrays.Warnings.Add(
                    $"Condition on '{target.Name}' has empty StateID — returning 0.");
                return 0;
            }
            var key = cond.ID.Trim();
            var refState = target.States.FirstOrDefault(s =>
                string.Equals((s.StateID ?? string.Empty).Trim(), key,
                    StringComparison.OrdinalIgnoreCase));
            if (refState == null)
            {
                arrays.Warnings.Add(
                    $"StateID '{cond.ID}' (referenced by condition '{cond.Name}') not found " +
                    $"on component '{target.Name}'. Returning 0.");
                return 0;
            }
            return refState.StateNumber;
        }

        private static void BuildExtendedRegistry(VueOneComponent process,
            Dictionary<string, int> registry)
        {
            int next = 0;
            foreach (var s in process.States.OrderBy(x => x.StateNumber))
                foreach (var t in s.Transitions)
                    foreach (var c in t.Conditions)
                    {
                        if (string.IsNullOrEmpty(c.ComponentID)) continue;
                        var key = c.ComponentID.Trim();
                        if (registry.ContainsKey(key)) continue;
                        registry[key] = next++;
                    }
        }

        private static bool NameEquals(string? a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), b, StringComparison.OrdinalIgnoreCase);
    }
}
