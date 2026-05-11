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

            // 3. Pass-1 row layout: build StateID -> first-row-index map AFTER skips.
            //    Each state contributes RowCount rows in order; the appended final END
            //    sits at the index immediately after the last source state's rows.
            var stateIdToFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rowIndex = 0;
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (!string.IsNullOrEmpty(s.StateID))
                    stateIdToFirstRow[s.StateID] = rowIndex;
                rowIndex += classifications[i].RowCount;
            }
            int finalEndIndex = rowIndex;

            // 4. Pass-2 emit. NextStep on each row resolves against stateIdToFirstRow.
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var c = classifications[i];

                int nextRowOnTransition = ResolveDestRow(
                    state, classifications, i, stateIdToFirstRow, finalEndIndex);

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
                    case ClassKind.SkippedAllOutOfScope:
                    default:
                        // For the all-out-of-scope case we emit an END row so the
                        // engine doesn't run off the end of the arrays at runtime.
                        arrays.StepType.Add(9);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(0);
                        arrays.Wait1State.Add(0);
                        arrays.NextStep.Add(0);
                        break;
                }
            }

            // 5. Always append a final END row whose NextStep loops back to row 0.
            arrays.StepType.Add(9);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(0);

            // 6. Defensive assertion: process_id must NEVER appear in Wait1Id.
            //    Process is not on the ring; any equality is a scoping bug.
            for (int i = 0; i < arrays.Wait1Id.Count; i++)
            {
                if (arrays.Wait1Id[i] == processId)
                {
                    throw new InvalidOperationException(
                        $"Recipe generator emitted Wait1Id[{i}]={processId} which equals " +
                        $"the Process FB's process_id ({processId}). Process is not a ring " +
                        "participant and cannot publish its own wait state. Likely cause: " +
                        "a stray ComponentID in Control.xml conditions landed on the process_id " +
                        "value via the registry. Inspect ComponentRegistry / SkippedConditions.");
                }
            }

            return arrays;
        }

        // ----------------------------------------------------------------------
        // Pass-1 classification
        // ----------------------------------------------------------------------

        private enum ClassKind
        {
            MotionPair,
            SettledWait,
            End,
            SkippedAllOutOfScope,   // emits an END row to keep the engine from running off
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
                // Every condition on this transition was out of scope (or there were no
                // conditions). Emit an END row so the engine has somewhere to land —
                // running off the end of the arrays would deadlock the runtime ECC.
                if (allConds.Any())
                {
                    arrays.SkippedConditions.Add(
                        $"state '{state.Name}': all transition conditions out of scope — " +
                        "emitting END row in their place.");
                    return new StateClassification { Kind = ClassKind.SkippedAllOutOfScope, RowCount = 1 };
                }
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
                return new StateClassification
                {
                    Kind = ClassKind.MotionPair,
                    RowCount = 2,
                    CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
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
            var trans = state.Transitions.FirstOrDefault();
            if (trans != null &&
                !string.IsNullOrEmpty(trans.DestinationStateID) &&
                stateIdToFirstRow.TryGetValue(trans.DestinationStateID, out var dst))
            {
                return dst;
            }
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
    }
}
