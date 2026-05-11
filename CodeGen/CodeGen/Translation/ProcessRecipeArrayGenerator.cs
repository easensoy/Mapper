using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    /// <summary>
    /// Phase 2 (revised again): emits six parallel arrays consumed by
    /// ProcessRuntime_Generic_v1's ECC.
    ///
    /// Encoding: 1 = CMD, 2 = WAIT, 9 = END.
    ///
    /// CRITICAL SCOPING INVARIANT (this revision):
    /// The component id space (Wait1Id values) MUST be derived from the SAME
    /// sensors + actuators that SystemLayoutInjector emits as FB instances on
    /// the canvas. Conditions in Control.xml that reference an out-of-scope
    /// component (Checker / Transfer / Assembly_Station / etc. when Button 2's
    /// scope filter strips them) are SKIPPED entirely — they cannot be satisfied
    /// at runtime because no FB on the stateRprtCmd ring publishes their state.
    ///
    /// Skipped conditions are accumulated in <see cref="RecipeArrays.SkippedConditions"/>
    /// and surfaced upstream so the .syslay top comment can list every reference
    /// that was dropped (so the file self-documents what's missing).
    ///
    /// Process_id (the FB instance's process_id parameter) is NEVER in the
    /// component map — it's not a ring participant. An assertion at the end of
    /// generation throws InvalidOperationException if any Wait1Id value happens
    /// to equal process_id, catching future regressions where a stray registry
    /// id collides with the process id.
    /// </summary>
    public sealed class RecipeArrays
    {
        public List<int> StepType       { get; } = new();
        public List<string> CmdTargetName { get; } = new();
        public List<int> CmdStateArr    { get; } = new();
        public List<int> Wait1Id        { get; } = new();
        public List<int> Wait1State     { get; } = new();
        public List<int> NextStep       { get; } = new();

        /// <summary>ComponentID → local id (sensors first, actuators next).
        /// Process is NOT in this map. Out-of-scope components are NOT in this map.</summary>
        public Dictionary<string, int> ComponentRegistry { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-condition warnings emitted during generation —
        /// every reference to an out-of-scope ComponentID lands here as
        /// "state Name X references out-of-scope component ComponentID Y (type Z)".
        /// Surfaced into the .syslay top comment by SystemLayoutInjector.</summary>
        public List<string> SkippedConditions { get; } = new();

        /// <summary>Generic generator warnings (lookup misses on StateID etc.).</summary>
        public List<string> Warnings { get; } = new();

        public int PusherId { get; set; }
        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        // Motion-in-progress verbs per the Phase-2 spec. "checking" included
        // pragmatically because PartChecking represents Checker descending in
        // the SMC rig.
        private static readonly string[] MotionVerbs = new[]
        {
            "advancing", "rising", "returning", "descending",
            "towork", "tohome", "gotowork", "gotohome",
            "checking",
        };

        /// <summary>
        /// Build the scoped component-id map from the sensors and actuators that
        /// SystemLayoutInjector emits as FB instances. Sensors first (ids 0..N-1),
        /// actuators next (ids N..N+M-1). Process is NOT in the map — the recipe
        /// never waits on its own process_id.
        /// </summary>
        public static Dictionary<string, int> BuildScopedComponentMap(
            IReadOnlyList<VueOneComponent> allowedSensors,
            IReadOnlyList<VueOneComponent> allowedActuators)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int next = 0;
            foreach (var s in allowedSensors)
            {
                if (string.IsNullOrEmpty(s.ComponentID)) continue;
                map[s.ComponentID.Trim()] = next++;
            }
            foreach (var a in allowedActuators)
            {
                if (string.IsNullOrEmpty(a.ComponentID)) continue;
                map[a.ComponentID.Trim()] = next++;
            }
            return map;
        }

        // Legacy delegation — kept so any caller that still passes StationContents works,
        // and so older test code links cleanly. Sensors-then-actuators is the same scheme.
        public static StationComponentMap BuildComponentMap(StationContents contents)
            => ProcessRecipeStGenerator.BuildComponentMap(contents);

        public static RecipeArrays Generate(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents,
            int processId = 10)
        {
            var arrays = new RecipeArrays();
            var states = process.States.OrderBy(s => s.StateNumber).ToList();

            // 1. Build scoped registry from the in-scope sensors + actuators ONLY.
            var scopedRegistry = BuildScopedComponentMap(stationContents.Sensors, stationContents.Actuators);
            foreach (var kv in scopedRegistry) arrays.ComponentRegistry[kv.Key] = kv.Value;

            // Diagnostic PusherId — not part of the recipe semantics, useful for tests.
            arrays.PusherId =
                arrays.ComponentRegistry.FirstOrDefault(kv =>
                    LookupComponent(kv.Key, allComponents) is { } c &&
                    (NameEquals(c.Name, "Feeder") || NameEquals(c.Name, "Pusher"))).Value;

            // 2. Pass-1 classify: for each source state, decide if rows are emit/skip.
            var classifications = new List<StateClassification>(states.Count);
            foreach (var state in states)
                classifications.Add(ClassifyState(state, allComponents, arrays, scopedRegistry));

            // 3. Pass-1 row layout. Skipped states contribute RowCount=0; the
            //    StateID-to-firstRowIndex map is built ONLY over surviving states.
            //    The appended final-END row sits at finalEndIndex (after the last
            //    surviving row). NextStep destinations that reference a SKIPPED
            //    state's StateID fall forward to the next surviving row via
            //    stateIdToFallForwardRow (built next).
            var stateIdToFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rowIndex = 0;
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (classifications[i].RowCount > 0 && !string.IsNullOrEmpty(s.StateID))
                    stateIdToFirstRow[s.StateID] = rowIndex;
                rowIndex += classifications[i].RowCount;
            }
            int finalEndIndex = rowIndex;

            // Fall-forward map: for any state (surviving or not), the row index
            // where execution should land if a NextStep aimed at it. For surviving
            // states this is their own firstRowIndex. For skipped states it's the
            // next surviving state's firstRowIndex (or finalEndIndex if none).
            var stateIdToFallForwardRow = BuildFallForwardMap(states, classifications,
                stateIdToFirstRow, finalEndIndex);

            // 4. Pass-2 emit. NextStep on each row resolves against the fall-forward map.
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var c = classifications[i];
                if (c.RowCount == 0) continue;   // skipped — no row at all

                int nextRowOnTransition = ResolveDestRow(
                    state, i, classifications, stateIdToFallForwardRow, finalEndIndex);

                switch (c.Kind)
                {
                    case ClassKind.MotionPair:
                        arrays.StepType.Add(1);
                        arrays.CmdTargetName.Add(c.CmdTargetName);
                        arrays.CmdStateArr.Add(c.CmdState);
                        arrays.Wait1Id.Add(0);
                        arrays.Wait1State.Add(0);
                        arrays.NextStep.Add(arrays.StepType.Count);   // → our own WAIT row

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
                        // Genuine end-of-process state from Control.xml (no transition).
                        // Becomes part of the recipe but the appended final END below is
                        // what the engine actually halts on. (We could fold this into the
                        // appended END but kept distinct for fixture-fidelity.)
                        arrays.StepType.Add(9);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(0);
                        arrays.Wait1State.Add(0);
                        arrays.NextStep.Add(0);
                        break;
                }
            }

            // Degenerate case — every source state was skipped. Emit a single
            // StepType=9 at index 0 so the engine has a recipe to halt on (rather
            // than empty arrays which would crash bounds checks).
            if (arrays.StepType.Count == 0)
            {
                arrays.Warnings.Add(
                    "Every source state was skipped or unsatisfiable — collapsing the recipe " +
                    "to a single END row at index 0. Engine will halt immediately.");
                arrays.StepType.Add(9);
                arrays.CmdTargetName.Add(string.Empty);
                arrays.CmdStateArr.Add(0);
                arrays.Wait1Id.Add(0);
                arrays.Wait1State.Add(0);
                arrays.NextStep.Add(0);
                ValidateProcessIdInvariant(arrays, processId);
                return arrays;
            }

            // 5. Append the single final END row at the highest index.
            //    StepType=9 must appear ONLY here in the array.
            arrays.StepType.Add(9);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(0);   // engine never reads NextStep after StepType=9

            ValidateProcessIdInvariant(arrays, processId);
            ValidateSingleEndMarker(arrays);
            return arrays;
        }

        private static void ValidateProcessIdInvariant(RecipeArrays arrays, int processId)
        {
            for (int i = 0; i < arrays.Wait1Id.Count; i++)
            {
                if (arrays.Wait1Id[i] == processId)
                    throw new InvalidOperationException(
                        $"Recipe generator emitted Wait1Id[{i}]={processId} which equals " +
                        $"the Process FB's process_id ({processId}). Process is not a ring " +
                        "participant and cannot publish its own wait state. Likely cause: " +
                        "a stray ComponentID in Control.xml conditions landed on the process_id " +
                        "value via the registry. Inspect ComponentRegistry / SkippedConditions.");
            }
        }

        private static void ValidateSingleEndMarker(RecipeArrays arrays)
        {
            // StepType=9 must appear EXACTLY once and ONLY at the final row.
            // Anywhere else and the runtime ECC halts at that index instead of
            // executing subsequent rows (the bug this whole revision fixes).
            int n = arrays.StepType.Count;
            if (n == 0)
                throw new InvalidOperationException("Recipe generator produced an empty StepType array.");

            for (int i = 0; i < n - 1; i++)
            {
                if (arrays.StepType[i] == 9)
                    throw new InvalidOperationException(
                        $"Recipe generator emitted StepType[{i}]=9 (END) before the final row. " +
                        $"Array length={n}; END must appear only at index {n - 1}. Likely cause: " +
                        "an out-of-scope-skip path is still emitting placeholder END rows.");
            }
            if (arrays.StepType[n - 1] != 9)
                throw new InvalidOperationException(
                    $"Recipe generator did not append a final END row — StepType[{n - 1}]=" +
                    $"{arrays.StepType[n - 1]}, expected 9.");
        }

        private static Dictionary<string, int> BuildFallForwardMap(
            List<VueOneState> states,
            List<StateClassification> classifications,
            Dictionary<string, int> stateIdToFirstRow,
            int finalEndIndex)
        {
            var fallForward = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (string.IsNullOrEmpty(s.StateID)) continue;
                if (classifications[i].RowCount > 0)
                {
                    fallForward[s.StateID] = stateIdToFirstRow[s.StateID];
                    continue;
                }
                // Skipped — walk forward looking for the next surviving state.
                int target = finalEndIndex;
                for (int j = i + 1; j < states.Count; j++)
                {
                    var s2 = states[j];
                    if (classifications[j].RowCount > 0 && !string.IsNullOrEmpty(s2.StateID))
                    {
                        target = stateIdToFirstRow[s2.StateID];
                        break;
                    }
                }
                fallForward[s.StateID] = target;
            }
            return fallForward;
        }

        // ----------------------------------------------------------------------
        // Pass-1 classification
        // ----------------------------------------------------------------------

        private enum ClassKind
        {
            MotionPair,             // RowCount=2 (CMD then WAIT)
            SettledWait,            // RowCount=1 (single WAIT)
            End,                    // RowCount=1 (genuine end-of-process state from Control.xml)
            Skipped,                // RowCount=0 — every condition out of scope; row dropped entirely
        }

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
            IReadOnlyList<VueOneComponent> allComponents, RecipeArrays arrays,
            Dictionary<string, int> scopedRegistry)
        {
            // Initialisation is structurally special in VueOne — it asserts the
            // expected world AT BOOT (typically every actuator at ReturnedHome),
            // not a step in the work cycle. Even when its conditions reference
            // in-scope components (e.g. live SMC VC_1's first cond is
            // Feeder/ReturnedHome which IS in scope), the resulting WAIT row is
            // either a tautology (actuator already at its initial state) or a
            // boot-time precondition the runtime engine does nothing with anyway.
            // Drop it from the recipe entirely so the recipe starts with the
            // first real work step.
            if (IsInitialisationState(state))
            {
                arrays.SkippedConditions.Add(
                    $"state '{state.Name}': dropped — Initialisation asserts boot " +
                    "conditions, not a work-cycle step.");
                return new StateClassification { Kind = ClassKind.Skipped, RowCount = 0 };
            }

            var trans = state.Transitions.FirstOrDefault();
            var allConds = trans?.Conditions ?? new List<VueOneCondition>();

            // No transition → END row.
            if (trans == null)
                return new StateClassification { Kind = ClassKind.End, RowCount = 1 };

            // Find the FIRST condition whose ComponentID is in the scoped registry.
            // Conditions on out-of-scope components are recorded in SkippedConditions
            // and effectively dropped from the recipe.
            VueOneCondition? cond = null;
            foreach (var c in allConds)
            {
                if (string.IsNullOrEmpty(c.ComponentID)) continue;
                if (scopedRegistry.ContainsKey(c.ComponentID.Trim()))
                {
                    cond = c;
                    break;
                }
                // Out-of-scope reference → record for the syslay top comment.
                var target = LookupComponent(c.ComponentID, allComponents);
                arrays.SkippedConditions.Add(
                    $"state '{state.Name}' references out-of-scope component " +
                    $"ComponentID={c.ComponentID} " +
                    $"(name={(target?.Name ?? "?")}, type={(target?.Type ?? "?")})");
            }

            if (cond == null)
            {
                // Every condition on this transition was out of scope. Drop the row
                // ENTIRELY (RowCount=0). The fall-forward map below ensures any
                // NextStep that pointed at this state lands on the next surviving
                // state's first row instead.
                //
                // Important: emitting a placeholder StepType=9 here would halt the
                // engine on the first tick (StepType=9 means END to the runtime ECC),
                // which is the bug this whole revision fixes. Skipped means SKIPPED.
                if (allConds.Any())
                {
                    arrays.SkippedConditions.Add(
                        $"state '{state.Name}': all transition conditions out of scope — " +
                        "row dropped from recipe; downstream NextStep pointers will skip past it.");
                    return new StateClassification { Kind = ClassKind.Skipped, RowCount = 0 };
                }
                // Genuine no-transition state. Truly the end of this Process — emit
                // a real END row.
                return new StateClassification { Kind = ClassKind.End, RowCount = 1 };
            }

            var inScopeTarget = LookupComponent(cond.ComponentID, allComponents);
            if (inScopeTarget == null)
            {
                arrays.Warnings.Add(
                    $"State '{state.Name}': in-scope ComponentID '{cond.ComponentID}' could not " +
                    "be resolved to a VueOneComponent in allComponents. Emitting WAIT id=0.");
                return new StateClassification { Kind = ClassKind.SettledWait, RowCount = 1 };
            }

            int waitId    = scopedRegistry[cond.ComponentID.Trim()];
            int waitState = ResolveStateNumber(cond, inScopeTarget, arrays);

            // Bug-1 dispatch (kept from previous Phase 2): on source-state name, not target type.
            if (StateNameSuggestsMotion(state.Name))
            {
                // CmdState is the TRANSIENT state number the actuator's own ECC
                // recognises as a motion command (Five_State_Actuator_CAT triggers
                // AtHomeInit→ToWork on state_val=1 and AtWork→ToHome on state_val=3).
                // We DERIVE this number from the actuator's own State elements in
                // Control.xml — looking up the State whose Name matches the motion
                // direction in the source-state Name (e.g. "FeederAdvancing" -> the
                // actuator's "Advancing" state). This stays generic across CAT
                // templates (Seven_State_Actuator_CAT, future custom CATs).
                int cmdState = ResolveTransientCmdState(state.Name, inScopeTarget, waitState, arrays);

                return new StateClassification
                {
                    Kind = ClassKind.MotionPair,
                    RowCount = 2,
                    CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
                    CmdState = cmdState,
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
            int srcIndex,
            List<StateClassification> classifications,
            Dictionary<string, int> stateIdToFallForwardRow,
            int finalEndIndex)
        {
            var trans = state.Transitions.FirstOrDefault();
            // Primary path: trans.DestinationStateID resolves via the fall-forward map
            // — if that destination state was skipped, the map already points us at
            // the next surviving row (or the final END if none).
            if (trans != null &&
                !string.IsNullOrEmpty(trans.DestinationStateID) &&
                stateIdToFallForwardRow.TryGetValue(trans.DestinationStateID, out var dst))
            {
                return dst;
            }
            // Fallback when DestinationStateID is empty or points outside this Process:
            // walk forward through declaration-order siblings to find the next surviving
            // row, or return finalEndIndex if there isn't one.
            for (int j = srcIndex + 1; j < classifications.Count; j++)
            {
                if (classifications[j].RowCount > 0)
                {
                    int idx = 0;
                    for (int i = 0; i < j; i++) idx += classifications[i].RowCount;
                    return idx;
                }
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

        private static bool NameEquals(string? a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True if the state is the VueOne Initialisation boot-assertion state.
        /// Detected via InitialState=true OR Name matching "Initialisation"/
        /// "Initialization" (case-insensitive). Used to drop the state from the
        /// recipe regardless of whether its first condition resolves in scope —
        /// Initialisation is a boot precondition, not a work-cycle step.
        /// </summary>
        private static bool IsInitialisationState(VueOneState s)
        {
            if (s.InitialState) return true;
            var n = (s.Name ?? string.Empty).Trim();
            return n.Equals("Initialisation", StringComparison.OrdinalIgnoreCase) ||
                   n.Equals("Initialization", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolve the transient (motion command) state number for a CMD row by
        /// looking it up on the actuator's OWN State elements — not by arithmetic
        /// on the wait state.
        ///
        /// Algorithm:
        ///   1. Extract the motion-direction token from the source state name
        ///      ("FeederAdvancing" -> "Advancing"; "TransferReturning" -> "Returning").
        ///   2. Find the actuator State whose Name equals (or contains) that token.
        ///   3. Return its State_Number.
        ///
        /// Falls back to (settledWaitState - 1) with a warning if no match is found —
        /// this matches Five_State_Actuator's settled-1=transient convention but flags
        /// it explicitly so non-Five_State CATs get visibility into the heuristic.
        ///
        /// Examples on Five_State_Actuator (per Feed_Station_Fixture):
        ///   FeederAdvancing  -> Feeder.Advancing  (State_Number=1)
        ///   FeederReturning  -> Feeder.Returning  (State_Number=3)
        /// </summary>
        private static int ResolveTransientCmdState(string? sourceStateName,
            VueOneComponent actuator, int settledWaitState, RecipeArrays arrays)
        {
            string sourceLower = (sourceStateName ?? string.Empty).Trim().ToLowerInvariant();
            if (sourceLower.Length > 0)
            {
                // Try direct verb match: actuator State whose Name appears as a
                // substring of the source state Name (e.g. "Advancing").
                foreach (var s in actuator.States)
                {
                    var sn = (s.Name ?? string.Empty).Trim();
                    if (sn.Length == 0) continue;
                    if (sourceLower.Contains(sn.ToLowerInvariant(), StringComparison.Ordinal))
                        return s.StateNumber;
                }
                // Try motion-verb mapping: source contains a motion verb -> map to
                // an actuator State by canonical synonyms.
                foreach (var (verb, synonyms) in MotionVerbToStateNames)
                {
                    if (!sourceLower.Contains(verb, StringComparison.Ordinal)) continue;
                    foreach (var syn in synonyms)
                    {
                        var match = actuator.States.FirstOrDefault(s =>
                            string.Equals((s.Name ?? string.Empty).Trim(), syn,
                                StringComparison.OrdinalIgnoreCase));
                        if (match != null) return match.StateNumber;
                    }
                }
            }

            // Fallback — Five_State convention. Surface as a warning so non-Five_State
            // CATs get visibility into the inference.
            int fallback = System.Math.Max(settledWaitState - 1, 0);
            arrays.Warnings.Add(
                $"State '{sourceStateName}': could not match a transient State name on " +
                $"actuator '{actuator.Name}'; falling back to (settledWaitState-1)={fallback}. " +
                "If this actuator's CAT does not follow the Five_State convention " +
                "(transient = settled - 1), add explicit synonyms to MotionVerbToStateNames.");
            return fallback;
        }

        // Maps source-state-name motion verbs to candidate actuator State Names.
        // Used as a soft fallback when direct substring match doesn't find an
        // actuator State. Extend as new CAT templates are added.
        private static readonly (string verb, string[] synonyms)[] MotionVerbToStateNames =
        {
            ("advancing",  new[] { "Advancing", "ToWork",   "Extending", "GoToWork" }),
            ("returning",  new[] { "Returning", "ToHome",   "Retracting","GoToHome" }),
            ("rising",     new[] { "Rising",    "GoUp" }),
            ("descending", new[] { "Descending","Lowering", "GoDown" }),
            ("checking",   new[] { "Lowering",  "Descending","Checking" }),
            ("towork",     new[] { "ToWork",    "Advancing" }),
            ("tohome",     new[] { "ToHome",    "Returning" }),
            ("gotowork",   new[] { "GoToWork",  "ToWork", "Advancing" }),
            ("gotohome",   new[] { "GoToHome",  "ToHome", "Returning" }),
        };
    }
}
