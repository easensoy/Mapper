using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Devices.Core;
using static CodeGen.Translation.Process.Recipes.RecipeCommandVocabulary;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
using static CodeGen.Translation.Process.Recipes.RecipeComponentLookup;
using static CodeGen.Translation.Process.Recipes.RecipeStateClassifier;

namespace CodeGen.Translation.Process
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

        /// <summary>
        /// StateID-based Control.xml transition chain used to derive this recipe.
        /// Kept as generated metadata so the syslay/report can show the exact
        /// design-intent path without maintaining a separate hand-written table.
        /// </summary>
        public List<string> TransitionTable { get; } = new();

        /// <summary>
        /// Human-readable one-line rendering of the FINAL emitted recipe
        /// (every CMD/WAIT row in execution order, e.g. "feeder advance →
        /// feeder atwork → checker advance → … → END"). Surfaced verbatim
        /// into the .syslay top comment by SystemLayoutInjector so the
        /// serialised collision-safe ordering (Defect 3) is self-documenting
        /// and operator-verifiable without decoding the parallel arrays.
        /// </summary>
        public string OrderingSummary { get; set; } = string.Empty;

        public int PusherId { get; set; }
        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        public static int RecipeArraySize => GenerationConfig.Current.RecipeArraySize;

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

        /// <param name="commandFromCondition">
        /// OPT-IN Station-2 path. When <c>false</c> (the default — Feed_Station /
        /// M262) the classifier is BYTE-IDENTICAL to the proven hardware recipe:
        /// commands are inferred from the source-state NAME (FeederAdvancing →
        /// feeder advance). When <c>true</c> (Assembly_Station / Disassembly) a
        /// state whose name does NOT encode a motion verb still emits a CMD+WAIT,
        /// deriving the command from the transition CONDITION instead: "command
        /// component C to its target state S, then wait until C settles there".
        /// This is exactly the operator model — StepType 1 = command actuator X to
        /// state Y, StepType 2 = wait until Z is W. The work/home split is by the
        /// condition's target State_Number (1/2 → toWork=cmd1/settle AtWork=2;
        /// 0/3/4 → toHome=cmd3/settle AtHomeInit=0). Only applied to
        /// Five_State-commandable targets (Five_State actuators + mechanical
        /// grippers); Seven_State (Bearing_PnP) and Robot targets fall back to a
        /// settled WAIT and are surfaced in <see cref="RecipeArrays.Warnings"/>
        /// because their command vocabulary is not the Five_State work/home pair.
        /// </param>
        public static RecipeArrays Generate(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents,
            int processId = 10, bool commandFromCondition = false)
        {
            var arrays = new RecipeArrays();
            // Step order MUST follow the transition chain, not State_Number.
            // VueOne models built incrementally leave State_Number = 0 on every
            // state added after the first authoring pass (e.g. Assembly_Station's
            // shaft-place and cover steps). OrderBy(State_Number) then sorts all
            // those 0-numbered states to the front, so the runtime — which starts
            // at CurrentStep 0 — begins mid-cycle (observed: shaft_vr first
            // instead of clamp). Walking Initial_State -> transition chain
            // reproduces the true sequence Initialisation -> Clamping_Part ->
            // Bearing_PnP_Picking -> ... -> shaft -> cover. Feed_Station is
            // unaffected: its State_Numbers are already sequential (0..9), so the
            // chain walk yields the identical order it had under OrderBy.
            var states = OrderStatesByTransitionChain(process.States);
            foreach (var line in BuildTransitionTable(process.States, states))
                arrays.TransitionTable.Add(line);

            var reachable = new HashSet<VueOneState>(states);
            var unreachable = process.States.Where(s => !reachable.Contains(s)).ToList();
            if (unreachable.Count > 0)
            {
                arrays.Warnings.Add(
                    $"[Recipe] Process '{process.Name}': {unreachable.Count} state(s) are not " +
                    "reachable from the Initialisation transition chain and were not serialized " +
                    "into the recipe: " +
                    string.Join(", ", unreachable.Select(s => $"'{s.Name}'")) + ".");
            }

            // 1. Build scoped registry from the in-scope sensors + actuators ONLY.
            var scopedRegistry = BuildScopedComponentMap(stationContents.Sensors, stationContents.Actuators);
            foreach (var kv in scopedRegistry) arrays.ComponentRegistry[kv.Key] = kv.Value;

            // Diagnostic PusherId — not part of the recipe semantics, useful for tests.
            arrays.PusherId =
                arrays.ComponentRegistry.FirstOrDefault(kv =>
                    LookupComponent(kv.Key, allComponents) is { } c &&
                    (NameEquals(c.Name, "Feeder") || NameEquals(c.Name, "Pusher"))).Value;

            // Disassembly gets its reverse recipe directly. Any other process whose initial state
            // gates on a same-PLC process is PARKED (single END): cross-process state is not
            // published, so the gate can never be satisfied and free-running would collide on the
            // shared actuators. Cross-PLC gates (Feed/Assembly) are not parked.
            if (MapperConfig.UnparkDisassembly &&
                string.Equals((process.Name ?? string.Empty).Trim(), "Disassembly",
                    StringComparison.OrdinalIgnoreCase))
            {
                DisassemblyRecipe.Apply(process, arrays, allComponents);
                arrays.OrderingSummary =
                    "Disassembly (Stage 5a): handshake-wait -> covers off -> shaft out -> " +
                    "bearing out -> UNCLAMP -> END (Ejector/Robot = M262, Stage 5b)";
                ValidateProcessIdInvariant(arrays, processId);
                return arrays;
            }

            if (ShouldParkOnIntraPlcProcessHandoff(process, states, allComponents))
            {
                arrays.Warnings.Add(
                    $"[Recipe] Process '{process.Name}' PARKED: its initial state gates on a " +
                    "same-PLC process (intra-PLC hand-off) the runtime cannot yet signal -- no " +
                    "cross-process state publish. Emitting a single END row so it holds at step 0 " +
                    "and issues no commands; prevents a free-run collision with the upstream " +
                    "process over the shared actuators. Lift when cross-process coordination is built.");
                arrays.StepType.Add(StepType.End);
                arrays.CmdTargetName.Add(string.Empty);
                arrays.CmdStateArr.Add(0);
                arrays.Wait1Id.Add(0);
                arrays.Wait1State.Add(0);
                arrays.NextStep.Add(0);
                arrays.OrderingSummary =
                    $"PARKED ('{process.Name}' gates on a same-PLC process; holds at step 0, no commands)";
                ValidateProcessIdInvariant(arrays, processId);
                return arrays;
            }

            // TEST ISOLATION (MapperConfig.RecipeTestActuatorAllowlist): when an
            // allowlist is configured AND this process is the targeted one (or the
            // target name is blank = all), restrict the recipe to those actuators —
            // every other actuator's CMD/WAIT state is dropped (parked). Used to
            // exercise bearing_pnp + bearing_gripper alone while shaft_pnp / clamp /
            // cover stay still. Null when no restriction applies (Feed_Station etc.),
            // so those recipes stay byte-identical.
            HashSet<string>? testActuatorAllowlist = null;
            if (!MapperConfig.SimulatorRecipeMode &&
                MapperConfig.RecipeTestActuatorAllowlist != null &&
                MapperConfig.RecipeTestActuatorAllowlist.Length > 0 &&
                (string.IsNullOrWhiteSpace(MapperConfig.RecipeTestProcessName) ||
                 string.Equals((process.Name ?? string.Empty).Trim(),
                               MapperConfig.RecipeTestProcessName.Trim(),
                               StringComparison.OrdinalIgnoreCase)))
            {
                testActuatorAllowlist = new HashSet<string>(
                    MapperConfig.RecipeTestActuatorAllowlist
                        .Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()),
                    StringComparer.Ordinal);
                arrays.Warnings.Add(
                    $"[Recipe] TEST ISOLATION active for '{process.Name}': only actuators " +
                    $"[{string.Join(", ", testActuatorAllowlist)}] are commanded; every other " +
                    "actuator is PARKED (its CMD/WAIT steps dropped). Clear " +
                    "MapperConfig.RecipeTestActuatorAllowlist to restore the full cycle.");
            }

            // 2. Pass-1 classify: for each source state, decide if rows are emit/skip.
            var classifications = new List<StateClassification>(states.Count);
            foreach (var state in states)
                classifications.Add(ClassifyState(state, allComponents, arrays, scopedRegistry, commandFromCondition, testActuatorAllowlist));

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

                if (c.Rows.Count > 0)
                {
                    for (int r = 0; r < c.Rows.Count; r++)
                    {
                        var row = c.Rows[r];
                        arrays.StepType.Add(row.StepType);
                        arrays.CmdTargetName.Add(row.CmdTargetName);
                        arrays.CmdStateArr.Add(row.CmdState);
                        arrays.Wait1Id.Add(row.WaitId);
                        arrays.Wait1State.Add(row.WaitState);
                        arrays.NextStep.Add(r == c.Rows.Count - 1
                            ? nextRowOnTransition
                            : arrays.StepType.Count);
                    }
                    continue;
                }

                switch (c.Kind)
                {
                    case ClassKind.MotionPair:
                        arrays.StepType.Add(StepType.Cmd);
                        arrays.CmdTargetName.Add(c.CmdTargetName);
                        arrays.CmdStateArr.Add(c.CmdState);
                        arrays.Wait1Id.Add(0);
                        arrays.Wait1State.Add(0);
                        arrays.NextStep.Add(arrays.StepType.Count);   // → our own WAIT row

                        arrays.StepType.Add(StepType.Wait);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(c.WaitId);
                        arrays.Wait1State.Add(c.WaitState);
                        arrays.NextStep.Add(nextRowOnTransition);
                        break;

                    case ClassKind.SettledWait:
                        arrays.StepType.Add(StepType.Wait);
                        arrays.CmdTargetName.Add(string.Empty);
                        arrays.CmdStateArr.Add(0);
                        arrays.Wait1Id.Add(c.WaitId);
                        arrays.Wait1State.Add(c.WaitState);
                        arrays.NextStep.Add(nextRowOnTransition);
                        break;

                    case ClassKind.End:
                        // Unreachable: terminal states are now RowCount=0 (see
                        // ClassifyState) and skipped by the `c.RowCount == 0`
                        // guard above, so no in-loop END is ever emitted. The
                        // single END lives only at the appended final row;
                        // terminals route to it via the fall-forward map.
                        // (Previously this emitted StepType=9 mid-array, which
                        // ValidateSingleEndMarker rejects.)
                        break;
                }
            }

            // Degenerate case — every source state was skipped. Emit a single
            // StepType=9 at index 0 so the engine has a recipe to halt on (rather
            // than empty arrays which would crash bounds checks).
            if (arrays.StepType.Count == 0)
            {
                // Cover_Station is a SYNTHESIZED, STATELESS engine — its recipe comes from
                // ApplyCoverRuntimeRecipe (the BX1 cover pick/place cycle), NOT a state walk,
                // so the walk legitimately yields 0 steps. Fill it from the cover recipe HERE
                // instead of collapsing to an empty END — otherwise Cover_Station deploys with
                // a single StepType=9 and sits "Resting in END" commanding nothing, so the
                // cover never moves (regression observed 2026-06-09: the degenerate-case early
                // return at this line pre-empted the ApplyCoverRuntimeRecipe call near the end
                // of Generate). ApplyCoverRuntimeRecipe bails on its Name guard for every
                // non-Cover_Station process, so the genuine every-state-skipped degenerate
                // path below is unchanged for them.
                CoverRecipe.Apply(process, arrays, allComponents, stationContents);
                if (arrays.StepType.Count > 0)
                {
                    ValidateProcessIdInvariant(arrays, processId);
                    return arrays;
                }

                arrays.Warnings.Add(
                    "Every source state was skipped or unsatisfiable — collapsing the recipe " +
                    "to a single END row at index 0. Engine will halt immediately.");
                arrays.StepType.Add(StepType.End);
                arrays.CmdTargetName.Add(string.Empty);
                arrays.CmdStateArr.Add(0);
                arrays.Wait1Id.Add(0);
                arrays.Wait1State.Add(0);
                arrays.NextStep.Add(0);
                arrays.OrderingSummary =
                    "END (every source state skipped — engine halts immediately)";
                ValidateProcessIdInvariant(arrays, processId);
                return arrays;
            }

            // 4b. AUTO-RETRACT (safety + collision ordering — Defect 3).
            //     Every actuator commanded to a transient work state
            //     (CmdState=1) MUST be commanded home (CmdState=3) before the
            //     cycle ends. An early recipe advanced checker (cmd=1) but
            //     never retracted it — checker stayed atwork forever and the
            //     rig needed its air supply killed to recover. Walk the
            //     emitted CMD rows; any actuator advanced but never retracted
            //     gets an explicit retract CMD + wait-athome pair inserted
            //     IMMEDIATELY AFTER its own atwork-confirmation WAIT row (not
            //     batched at the end), so it pulses advance → atwork → retract
            //     → athome before the recipe proceeds. This serialises the
            //     recipe: no subsequent actuator advances while a forgotten-
            //     retract actuator is still atwork (the previous batch-at-end
            //     LIFO let Transfer fully cycle while Checker was atwork —
            //     a rig collision). Index discipline is handled per-insertion
            //     (NextStep values >= the insert point are rebased by +2).
            //
            // SCOPE (2026-06-04): runs ONLY for processes in
            // MapperConfig.AutoRetractProcesses (default Feed_Station). Other
            // processes — e.g. Assembly_Station — are generated VERBATIM from their
            // Control.xml state-transition chain: the bearing/shaft retract via their
            // own GoHome/Go_Up states, and the CLAMP is HELD (engaged at Clamping_Part,
            // never released in the chain, exactly as the twin sequences it). Injecting
            // a retract there would contradict the twin. Feed_Station keeps the net (its
            // twin forgets the Checker retract). Don't touch Feed_Station's mechanism.
            if (MapperConfig.AutoRetractProcesses != null &&
                MapperConfig.AutoRetractProcesses.Any(p =>
                    string.Equals((p ?? string.Empty).Trim(),
                        (process.Name ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                var actuatorNameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in stationContents.Actuators)
                    if (!string.IsNullOrEmpty(a.ComponentID) &&
                        scopedRegistry.TryGetValue(a.ComponentID.Trim(), out var aid))
                        actuatorNameToId[(a.Name ?? string.Empty).Trim().ToLowerInvariant()] = aid;

                var advancedOrder = new List<string>();
                var advanced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var retracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < arrays.StepType.Count; i++)
                {
                    if (arrays.StepType[i] != StepType.Cmd) continue;             // CMD rows only
                    var tgt = (arrays.CmdTargetName[i] ?? string.Empty).Trim();
                    if (tgt.Length == 0) continue;
                    if (arrays.CmdStateArr[i] == 1)                    // toWork
                    {
                        if (advanced.Add(tgt)) advancedOrder.Add(tgt);
                    }
                    else if (arrays.CmdStateArr[i] == 3)               // toHome
                    {
                        retracted.Add(tgt);
                    }
                }

                var stranded = advancedOrder.Where(a => !retracted.Contains(a)).ToList();

                if (stranded.Count > 0)
                {
                    // SERIALISED AUTO-RETRACT (Defect 3 — collision ordering).
                    // The old logic batched EVERY forgotten retract at the very
                    // END in LIFO order. That let a later actuator fully cycle
                    // while an earlier one stayed atwork — concretely Transfer
                    // advanced and retracted while Checker was still atwork,
                    // colliding on the rig (Checker only retracted after the
                    // whole Transfer cycle). The fix: insert each stranded
                    // actuator's "retract + wait-athome" pair IMMEDIATELY after
                    // its OWN atwork-confirmation WAIT row, so the actuator
                    // pulses advance → atwork → retract → athome before the
                    // recipe proceeds to anything else. No subsequent actuator
                    // advances while a forgotten-retract actuator is still
                    // atwork. For the Feed Station (only Checker is stranded;
                    // Feeder + Transfer retract in Control.xml) this yields the
                    // required order:
                    //   feeder advance, feeder atwork,
                    //   checker advance, checker atwork, checker retract, checker athome,
                    //   feeder retract, feeder athome,
                    //   transfer advance, transfer atwork, transfer retract, transfer athome,
                    //   END
                    //
                    // Index discipline: inserting 2 rows at position p shifts
                    // every row at index >= p down by 2, so every NextStep
                    // VALUE >= p is rebased by +2 before the physical insert.
                    // The arrays are re-scanned per stranded actuator so a
                    // prior insertion's shift is already baked in (n <= 20, so
                    // the re-scan cost is irrelevant).
                    foreach (var act in stranded)               // advance order
                    {
                        actuatorNameToId.TryGetValue(act, out var actId);

                        // This actuator's advance CMD (CmdState=1). Take the
                        // last one if Control.xml advanced it more than once;
                        // its atwork WAIT is the CMD row's NextStep target.
                        int advCmdIdx = -1;
                        for (int i = 0; i < arrays.StepType.Count; i++)
                            if (arrays.StepType[i] == StepType.Cmd &&
                                arrays.CmdStateArr[i] == 1 &&
                                string.Equals(
                                    (arrays.CmdTargetName[i] ?? string.Empty).Trim(),
                                    act, StringComparison.OrdinalIgnoreCase))
                                advCmdIdx = i;                  // keep last

                        int atworkWaitIdx = -1;
                        if (advCmdIdx >= 0)
                        {
                            int w = arrays.NextStep[advCmdIdx];
                            if (w >= 0 && w < arrays.StepType.Count &&
                                arrays.StepType[w] == StepType.Wait)
                                atworkWaitIdx = w;
                        }

                        if (atworkWaitIdx < 0)
                        {
                            // Shape not recognised — fall back to appending the
                            // retract at the end so the actuator is STILL
                            // guaranteed home (safety preserved; ordering may
                            // not be optimally serialised for this odd input).
                            atworkWaitIdx = arrays.StepType.Count - 1;
                            arrays.Warnings.Add(
                                $"[Recipe] auto-retract for '{act}': atwork WAIT not " +
                                "locatable via advance-CMD NextStep; appended retract " +
                                "at end (safe; ordering not serialised for this input).");
                        }

                        int insertAt = atworkWaitIdx + 1;
                        int origTarget = arrays.NextStep[atworkWaitIdx];   // pre-shift

                        // Rebase every forward NextStep for the 2 inserted rows.
                        for (int i = 0; i < arrays.NextStep.Count; i++)
                            if (arrays.NextStep[i] >= insertAt)
                                arrays.NextStep[i] += 2;
                        if (origTarget >= insertAt) origTarget += 2;

                        // retract CMD (toHome) → its own wait-athome
                        arrays.StepType.Insert(insertAt, 1);
                        arrays.CmdTargetName.Insert(insertAt, act);
                        arrays.CmdStateArr.Insert(insertAt, 3);
                        arrays.Wait1Id.Insert(insertAt, 0);
                        arrays.Wait1State.Insert(insertAt, 0);
                        arrays.NextStep.Insert(insertAt, insertAt + 1);

                        // wait athome-resting (AtHomeInit=0, the value the
                        // actuator stably holds). AtHomeEnd=4 is transient —
                        // the FiveStateActuator ECC takes AtHome -> AtHomeInit
                        // on a data-only guard (atwork=FALSE AND athome=TRUE)
                        // in the same run-to-stable tick as the AtHome entry,
                        // so by the time the engine re-samples state_table the
                        // 4 has already been overwritten to 0. Waiting on 4
                        // misses the transient and parks the engine forever.
                        arrays.StepType.Insert(insertAt + 1, 2);
                        arrays.CmdTargetName.Insert(insertAt + 1, string.Empty);
                        arrays.CmdStateArr.Insert(insertAt + 1, 0);
                        arrays.Wait1Id.Insert(insertAt + 1, actId);
                        arrays.Wait1State.Insert(insertAt + 1, 0);
                        arrays.NextStep.Insert(insertAt + 1, origTarget);

                        // Re-point the atwork WAIT into the new retract chain.
                        arrays.NextStep[atworkWaitIdx] = insertAt;
                    }

                    arrays.Warnings.Add(
                        "[Recipe] auto-retract serialised (each forgotten-retract " +
                        "actuator returns home before the recipe proceeds — no " +
                        "subsequent actuator advances while it is atwork) for: " +
                        string.Join(", ", stranded) + ".");
                }
            }

            // 5. Append the single final END row at the highest index.
            //    StepType=9 must appear ONLY here in the array.
            arrays.StepType.Add(StepType.End);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(0);   // engine never reads NextStep after StepType=9

            // 6. HOME-FIRST PREAMBLE for EVERY commanded actuator (safe-start).
            //    Every actuator core boots to whatever its physical sensors report:
            //    the Five_State ECC does INIT -> AtWork on atwork=TRUE, the Seven
            //    Centre-Home core does INIT -> AtWork1/ToWork2 on atWork1/atWork2 =
            //    TRUE -- neither force-homes at INIT. So if an actuator is physically
            //    parked at a WORK position at power-up (e.g. the swivel left at Pick
            //    or the gripper left CLOSED by a previous run or a manual jog) it
            //    boots parked at work, NOT home. The recipe's first command for that
            //    actuator is then a no-op (it is already at the commanded work
            //    position, and the ECC has no AtWork ->(go to that same work) arc),
            //    so the actuator never produces a fresh transition; and because the
            //    Process engine inits LAST in the resource INIT chain it already
            //    missed the actuator's boot-time publish, so the matching "WAIT
            //    actuator AtWork" never re-triggers and the recipe STALLS (observed
            //    on the rig: swivel parked at AtWork1 and gripper parked closed ->
            //    "nothing triggered"). Prepend, for each distinct actuator the recipe
            //    commands (in first-appearance order), a "command Home -> wait
            //    home proof at the front. Seven swivel uses state_val=5, and the proof
            //    WAIT targets the settled AtHomeInit=0 (sim AND rig). 0 is reachable only
            //    through AtHome in the core, so it proves the arm homed, and -- key -- it
            //    is satisfied whether the swivel was already home or just homed. (A prior
            //    rig variant waited on the transient AtHome=6 first; that stalled for
            //    ever when the swivel booted already settled at 0 -- the clamp/cycle then
            //    never started.)
            //    This drives a REAL ECC transition (AtWork* -> ToHome -> AtHome ->
            //    AtHomeInit) the running engine observes, so the cycle then starts
            //    from a known all-home state and every later command is a real move.
            //    Safe no-op when an actuator already boots home: the home command has
            //    no arc out of AtHomeInit and WAIT AtHomeInit=0 is satisfied on entry
            //    (state_table defaults to 0 and a homed actuator publishes 0). Only
            //    fires for actuators the recipe actually commands, so Feed_Station's
            //    recipe is unchanged when it commands none via this path.
            //
            // 2026-06-04 DISABLED BY DEFAULT (follow the twin). CurrentStep=0 stall
            // traced here: with the swivel parked at atWork1 holding the bearing, the
            // hardcoded "CMD Home -> WAIT AtHomeInit=0" hangs at step 0 and the cycle
            // never starts. The twin has no home-first step — the bearing Pick command
            // handles whatever position the swivel boots in (Pick at atWork1 is a
            // satisfied no-op; the engine reads state 2, the WAIT AtPick=2 clears, then
            // Place carries it to atWork2). Flip EnableSevenStateHomePreamble to restore.
            if (MapperConfig.EnableSevenStateHomePreamble)
            {
                var homeOrder = new List<(string name, int id, int homeCmd)>();
                var seenActuator = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < arrays.StepType.Count; i++)
                {
                    if (arrays.StepType[i] != StepType.Cmd) continue;
                    var tgt = (arrays.CmdTargetName[i] ?? string.Empty).Trim();
                    if (tgt.Length == 0 || !seenActuator.Add(tgt)) continue;
                    var comp = allComponents.FirstOrDefault(c =>
                        string.Equals((c.Name ?? string.Empty).Trim(), tgt,
                            StringComparison.OrdinalIgnoreCase));
                    if (comp == null) continue;
                    // Home ONLY the Seven_State swivel. It is the actuator that boots
                    // parked at a work position (atWork1/atWork2) and so needs to be
                    // driven home first. Five_State actuators (cylinders, mechanical
                    // grippers) boot to home via their athome sensor and their first
                    // recipe command is already a real move, so they need no home
                    // preamble -- and crucially, with the anti-race engine guard a
                    // "go home" command to an actuator that ALREADY boots home is a
                    // no-op (no transition, no fresh report) that the engine would
                    // then wait on forever. So we must not prepend a home step for
                    // them.
                    if (!IsSevenStateCommandable(comp)) continue;
                    if (!scopedRegistry.TryGetValue((comp.ComponentID ?? string.Empty).Trim(), out var id))
                        continue;
                    homeOrder.Add((tgt, id, 5));   // Seven_State Home = state_val 5, settles AtHomeInit=0
                }

                if (homeOrder.Count > 0)
                {
                    int rowsPerHome = MapperConfig.SimulatorRecipeMode ? 2 : 3;
                    int shift = rowsPerHome * homeOrder.Count;
                    // Every existing row moves down by `shift`, so every NextStep
                    // pointer is rebased -- EXCEPT the final END row's own NextStep,
                    // which must stay 0. The END ECState runs EndSequence
                    // (CurrentStep := Recipe[CurrentStep].NextStep) on its END->END
                    // self-loop; rebasing END's NextStep to `shift` made it walk
                    // CurrentStep off into the cycle rows instead of pinning at the
                    // recipe start. Pointers TO the END row (other rows' NextStep)
                    // are still rebased correctly because the END row itself moved +shift.
                    for (int i = 0; i < arrays.NextStep.Count; i++)
                        if (arrays.StepType[i] != StepType.End)
                            arrays.NextStep[i] += shift;

                    // Runtime must first see the physical AtHome pulse (6). Waiting
                    // directly for settled 0 can false-pass on the state_table's blank
                    // startup/default value when the swivel is physically parked at
                    // Work1, causing the recipe to skip homing and jump into Pick/Place.
                    // Simulator stays at 0 because its synthetic swivel can boot already
                    // settled with no physical AtHome pulse to observe.
                    int sevenHomePreambleProofWait = MapperConfig.SimulatorRecipeMode ? 0 : 6;

                    // Insert the home CMD+WAIT rows at the front, in order. Each row
                    // chains to the next (NextStep = its own index + 1); the last
                    // WAIT's NextStep lands on `shift`, i.e. the original first row.
                    // Hardware adds a second WAIT for AtHomeInit=0 after the AtHome=6
                    // proof so startup homing cannot false-pass on the transient 6.
                    int pos = 0;
                    foreach (var (name, id, homeCmd) in homeOrder)
                    {
                        arrays.StepType.Insert(pos, 1);
                        arrays.CmdTargetName.Insert(pos, name);
                        arrays.CmdStateArr.Insert(pos, homeCmd);
                        arrays.Wait1Id.Insert(pos, 0);
                        arrays.Wait1State.Insert(pos, 0);
                        arrays.NextStep.Insert(pos, pos + 1);
                        pos++;

                        arrays.StepType.Insert(pos, 2);
                        arrays.CmdTargetName.Insert(pos, string.Empty);
                        arrays.CmdStateArr.Insert(pos, 0);
                        arrays.Wait1Id.Insert(pos, id);
                        // Runtime proof waits on AtHome=6 first so startup state_table=0
                        // cannot skip the physical home move from Work1/Work2.
                        arrays.Wait1State.Insert(pos, sevenHomePreambleProofWait);
                        arrays.NextStep.Insert(pos, pos + 1);
                        pos++;

                        if (!MapperConfig.SimulatorRecipeMode)
                        {
                            arrays.StepType.Insert(pos, 2);
                            arrays.CmdTargetName.Insert(pos, string.Empty);
                            arrays.CmdStateArr.Insert(pos, 0);
                            arrays.Wait1Id.Insert(pos, id);
                            // Rig-only second confirmation of the settled rest state
                            // (AtHomeInit=0, coils cleared) after the physical AtHome
                            // pulse was observed.
                            arrays.Wait1State.Insert(pos, 0);
                            arrays.NextStep.Insert(pos, pos + 1);
                            pos++;
                        }
                    }

                    arrays.Warnings.Add(
                        $"[Recipe] HOME-FIRST preamble prepended for {homeOrder.Count} actuator(s): " +
                        string.Join(", ", homeOrder.Select(h => $"{h.name}(cmd {h.homeCmd})")) +
                        $" -- each CMD Home -> WAIT AtHomeInit=0 before the cycle. Guarantees a known " +
                        "all-home start regardless of where each actuator was physically parked at " +
                        "power-up; without it an actuator booting parked at work stalls the recipe.");
                }
            }

            AssemblyRecipe.Apply(process, arrays, allComponents);
            CoverRecipe.Apply(process, arrays, allComponents, stationContents);

            // RUN-ONCE: park on the END row after one cycle instead of looping.
            // (2026-06-03 — MapperConfig.RecipeRunOnce, default ON.) The END
            // ECState runs EndSequence (CurrentStep := Recipe[CurrentStep].NextStep),
            // so the END row's NextStep decides what happens when the recipe
            // finishes. It was 0 -> the engine jumps to step 0; with the home-first
            // preamble now at step 0 that RE-RUNS Home->Pick->Place->Home forever
            // (the observed swivel atWork1<->atWork2 bounce). Point the END row at
            // ITSELF so CurrentStep stays on the END row: the engine parks, issues
            // no further commands, and the swivel holds its last (home) position --
            // a clean single Home->Pick->Place->Home. Done HERE (after the preamble
            // prepend has shifted every index) so endIdx is the FINAL END index.
            // Flip RecipeRunOnce off to restore continuous looping.
            // Cover_Station runs a CONTINUOUS cover cycle (its END loops to step 0), so it
            // is exempted from the run-once self-park that the other engines use.
            bool isCoverStation = string.Equals((process.Name ?? string.Empty).Trim(),
                "Cover_Station", StringComparison.OrdinalIgnoreCase);
            if (!isCoverStation && arrays.StepType.Count > 0)
            {
                int endIdx = arrays.StepType.Count - 1;
                if (arrays.StepType[endIdx] == StepType.End)
                {
                    if (MapperConfig.EnableCyclicRestart)
                        // CYCLIC: END jumps back to step 0 (this recipe's trigger gate). The engine's
                        // EndSequence runs CurrentStep := Recipe[END].NextStep, so 0 = restart; the
                        // leftoverSuspect logic holds row-0's WAIT until a FRESH trigger, so the line
                        // self-sequences (Feed->Assembly->Disassembly->robot drop->Feed...). Overrides
                        // the run-once self-park. Safe: EnableSevenStateHomePreamble is off, so step 0
                        // is a trigger WAIT, never a Home command (no atWork1<->atWork2 bounce).
                        arrays.NextStep[endIdx] = 0;
                    else if (MapperConfig.RecipeRunOnce)
                        arrays.NextStep[endIdx] = endIdx;   // self-loop = park (run once)
                }
            }

            ValidateProcessIdInvariant(arrays, processId);
            ValidateSingleEndMarker(arrays);

            // Recipe-length guard. The Process1_Generic.fbt / ProcessRuntime_
            // Generic_v1.fbt recipe-array InputVars are ArraySize=RecipeArraySize.
            // EAE silently truncates an over-long array literal, so ProcessEngine
            // would receive a partial recipe and stall on StepType=0 (Unknown
            // step). Refuse to emit rather than ship a truncated recipe.
            if (arrays.StepType.Count > RecipeArraySize)
                throw new InvalidOperationException(
                    $"[Recipe] Recipe length {arrays.StepType.Count} exceeds template " +
                    $"ArraySize {RecipeArraySize}. Raise ProcessRecipeArrayGenerator" +
                    ".RecipeArraySize — it drives both Process1_Generic.fbt and " +
                    "ProcessRuntime_Generic_v1.fbt via PatchProcess1RecipeArraySize.");

            // Render the FINAL serialised recipe as a one-line narrative for
            // the .syslay top comment (Defect 3: operator-verifiable ordering).
            arrays.OrderingSummary = BuildOrderingNarrative(arrays, allComponents);

            return arrays;
        }

        internal static void ApplyAssemblyBearingReleaseSequence(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents)
        {
            if (!string.Equals((process.Name ?? string.Empty).Trim(), "Assembly_Station",
                    StringComparison.OrdinalIgnoreCase))
                return;

            if (!TryGetComponentId(arrays, allComponents, "bearing_pnp", out var pnpId) ||
                !TryGetComponentId(arrays, allComponents, "bearing_gripper", out var gripperId))
                return;

            int pickCmd = FindCmd(arrays, "bearing_pnp", 1, 0);
            if (pickCmd < 0) return;
            int pickWait = FindWaitAfter(arrays, pickCmd, pnpId, 2);
            if (pickWait < 0) return;
            int gripCmd = FindCmd(arrays, "bearing_gripper", 1, pickWait + 1);
            if (gripCmd < 0) return;
            int gripWait = FindWaitAfter(arrays, gripCmd, gripperId, 2);
            if (gripWait < 0) return;
            int placeCmd = FindCmd(arrays, "bearing_pnp", 3, gripWait + 1);
            if (placeCmd < 0) return;
            int placeWait = FindWaitAfter(arrays, placeCmd, pnpId, 4);
            if (placeWait < 0) return;
            int releaseCmd = FindCmd(arrays, "bearing_gripper", 3, placeWait + 1);
            if (releaseCmd < 0) return;
            int releaseWait = FindWaitAfter(arrays, releaseCmd, gripperId, 0);
            if (releaseWait < 0) return;
            int homeCmd = FindCmd(arrays, "bearing_pnp", 5, releaseWait + 1);
            if (homeCmd < 0) return;
            int homeWait = FindWaitAfter(arrays, homeCmd, pnpId, 0);
            if (homeWait < 0) return;

            int afterHome = arrays.NextStep[homeWait];

            // Every CMD flows through its OWN wait -- the Place CMD waits for AtWork2
            // (placeWait) before the gripper releases, proving the swivel is physically
            // at place. (Earlier this bypassed placeWait as a workaround for an
            // unreliable AtWork2 publish; that skipped the place confirmation and is
            // removed per the operator: no CMD may jump over its own WAIT.)
            arrays.NextStep[placeCmd] = placeWait;   // CMD -> its OWN wait (no skip)
            arrays.NextStep[placeWait] = releaseCmd; // AtPlace settled -> release
            arrays.NextStep[releaseCmd] = releaseWait;
            arrays.NextStep[releaseWait] = homeCmd;
            arrays.NextStep[homeCmd] = homeWait;
            arrays.NextStep[homeWait] = afterHome;

            arrays.Warnings.Add(
                "[Recipe] Assembly bearing release sequence: Bearing_PnP Place CMD -> WAIT AtPlace -> " +
                "Bearing_Gripper release -> WAIT gripper home -> Bearing_PnP Home. Every CMD has its " +
                "own WAIT; the place confirmation is proven before the gripper releases.");
        }

        internal static bool TryGetComponentId(RecipeArrays arrays,
            IReadOnlyList<VueOneComponent> allComponents, string componentName, out int id)
        {
            foreach (var kv in arrays.ComponentRegistry)
            {
                var comp = LookupComponent(kv.Key, allComponents);
                if (comp != null &&
                    string.Equals((comp.Name ?? string.Empty).Trim(), componentName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    id = kv.Value;
                    return true;
                }
            }

            id = -1;
            return false;
        }

        private static int FindCmd(RecipeArrays arrays, string target, int cmdState, int start)
        {
            for (int i = Math.Max(0, start); i < arrays.StepType.Count; i++)
            {
                if (arrays.StepType[i] == StepType.Cmd &&
                    arrays.CmdStateArr[i] == cmdState &&
                    string.Equals((arrays.CmdTargetName[i] ?? string.Empty).Trim(), target,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int FindWaitAfter(RecipeArrays arrays, int cmdRow, int waitId, int waitState)
        {
            int i = cmdRow >= 0 && cmdRow < arrays.NextStep.Count ? arrays.NextStep[cmdRow] : -1;
            if (i >= 0 && i < arrays.StepType.Count &&
                arrays.StepType[i] == StepType.Wait &&
                arrays.Wait1Id[i] == waitId &&
                arrays.Wait1State[i] == waitState)
                return i;

            for (i = cmdRow + 1; i < arrays.StepType.Count; i++)
            {
                if (arrays.StepType[i] == StepType.Wait &&
                    arrays.Wait1Id[i] == waitId &&
                    arrays.Wait1State[i] == waitState)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Walk the FINAL emitted recipe in row order and render it as one
        /// human-readable line, e.g. "WAIT PartInHopper on → feeder advance →
        /// feeder atwork → checker advance → checker atwork → checker retract
        /// → checker athome → feeder retract → feeder athome → transfer
        /// advance → transfer atwork → transfer retract → transfer athome →
        /// END". CMD rows render as "{target} advance|retract"; WAIT rows
        /// resolve Wait1Id back through the scoped registry to the component
        /// name and map Wait1State (2=atwork, 4=athome, 1=on) to a phrase.
        /// This makes the collision-safe serialisation self-documenting in the
        /// syslay without decoding six parallel arrays by hand.
        /// </summary>
        private static string BuildOrderingNarrative(RecipeArrays arrays,
            IReadOnlyList<VueOneComponent> allComponents)
        {
            // id → component display name (sensors first, actuators next —
            // exactly the recipe Wait1Id scheme in arrays.ComponentRegistry).
            var idToName = new Dictionary<int, string>();
            var idToComponent = new Dictionary<int, VueOneComponent>();
            foreach (var kv in arrays.ComponentRegistry)
            {
                var comp = allComponents.FirstOrDefault(c =>
                    string.Equals((c.ComponentID ?? string.Empty).Trim(), kv.Key,
                        StringComparison.OrdinalIgnoreCase));
                idToName[kv.Value] =
                    (comp?.Name ?? $"id{kv.Value}").Trim();
                if (comp != null) idToComponent[kv.Value] = comp;
            }

            var parts = new List<string>(arrays.StepType.Count);
            for (int i = 0; i < arrays.StepType.Count; i++)
            {
                switch (arrays.StepType[i])
                {
                    case 9:
                        parts.Add("END");
                        break;
                    case 1:
                        var tgt = (arrays.CmdTargetName[i] ?? string.Empty).Trim();
                        if (tgt.Length == 0) tgt = "?";
                        var cmdComp = allComponents.FirstOrDefault(c =>
                            string.Equals((c.Name ?? string.Empty).Trim(), tgt,
                                StringComparison.OrdinalIgnoreCase));
                        var verb = IsSevenStateCommandable(cmdComp!)
                            ? arrays.CmdStateArr[i] switch
                            {
                                1 => "pick/work1",
                                3 => "place/work2",
                                5 => "home",
                                _ => $"cmd{arrays.CmdStateArr[i]}",
                            }
                            : arrays.CmdStateArr[i] switch
                        {
                            1 => "advance",
                            3 => "retract",
                            _ => $"cmd{arrays.CmdStateArr[i]}",
                        };
                        parts.Add($"{tgt} {verb}");
                        break;
                    case 2:
                        int wid = arrays.Wait1Id[i];
                        var nm = idToName.TryGetValue(wid, out var n) ? n : $"id{wid}";
                        idToComponent.TryGetValue(wid, out var waitComp);
                        var phase = IsSevenStateCommandable(waitComp!)
                            ? arrays.Wait1State[i] switch
                            {
                                0 => "home-init",
                                2 => "atWork1/pick",
                                4 => "atWork2/place",
                                6 => "atHome",
                                _ => $"state{arrays.Wait1State[i]}",
                            }
                            : arrays.Wait1State[i] switch
                        {
                            2 => "atwork",
                            4 => "athome",
                            1 => "on",
                            0 => "settled",
                            _ => $"state{arrays.Wait1State[i]}",
                        };
                        parts.Add($"WAIT {nm} {phase}");
                        break;
                    default:
                        parts.Add($"step{arrays.StepType[i]}");
                        break;
                }
            }
            return string.Join(" → ", parts);
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
                if (arrays.StepType[i] == StepType.End)
                    throw new InvalidOperationException(
                        $"Recipe generator emitted StepType[{i}]=9 (END) before the final row. " +
                        $"Array length={n}; END must appear only at index {n - 1}. Likely cause: " +
                        "an out-of-scope-skip path is still emitting placeholder END rows.");
            }
            if (arrays.StepType[n - 1] != StepType.End)
                throw new InvalidOperationException(
                    $"Recipe generator did not append a final END row — StepType[{n - 1}]=" +
                    $"{arrays.StepType[n - 1]}, expected 9.");
        }

        // The recipe STATE CLASSIFIER moved to Recipes/RecipeStateClassifier.cs (2026-06-18,
        // behaviour-preserving verbatim move): ClassKind/StateClassification/RecipeRow +
        // ClassifyState, ClassifyConditionDrivenState, AddCmdWaitRows, RemapSettledWaitState,
        // StateNameSuggestsMotion, ResolveStateNumber, ResolveTransientCmdState,
        // BuildFallForwardMap, ResolveDestRow (+ the MotionVerbs/MotionVerbToStateNames tables).
        // Generate calls + its StateClassification/ClassKind references resolve via
        // `using static CodeGen.Translation.Process.Recipes.RecipeStateClassifier`.

        /// <summary>
        /// True when <paramref name="process"/>'s initial state has a transition
        /// gate that references ANOTHER process on the SAME PLC (an intra-PLC
        /// hand-off, e.g. Disassembly's Initialize -> Assembly_Station/HandShaking,
        /// both M580). Such a process must wait for the upstream process to finish,
        /// but the runtime has no cross-process state publish yet (see PARK GUARD in
        /// Generate), so the wait is unsatisfiable. We park it (single END) rather
        /// than drop the gate (which free-runs into a collision over shared
        /// actuators). Cross-PLC process gates (Feed &lt;-&gt; Assembly, different
        /// bucket) return false -- those are boot coordination and the process
        /// should free-run to start its own cycle.
        /// </summary>
        private static bool ShouldParkOnIntraPlcProcessHandoff(
            VueOneComponent process, List<VueOneState> orderedStates,
            IReadOnlyList<VueOneComponent> allComponents)
        {
            var initial = orderedStates.FirstOrDefault();
            var trans = initial?.Transitions?.FirstOrDefault();
            if (trans?.Conditions == null) return false;
            var myPlc = SysresFbMirror.BucketFor(process.Name ?? string.Empty);
            foreach (var cond in trans.Conditions)
            {
                if (string.IsNullOrEmpty(cond.ComponentID)) continue;
                var target = LookupComponent(cond.ComponentID, allComponents);
                if (target == null) continue;
                if (!string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase)) continue;
                // Never park on a self-reference (a process gating on its own state).
                if (string.Equals((target.ComponentID ?? string.Empty).Trim(),
                                  (process.ComponentID ?? string.Empty).Trim(),
                                  StringComparison.OrdinalIgnoreCase)) continue;
                if (SysresFbMirror.BucketFor(target.Name ?? string.Empty) == myPlc)
                    return true;   // same-PLC upstream process -> park until coordination exists
            }
            return false;
        }

        // OrderStatesByTransitionChain, BuildTransitionTable, and IsInitialisationState
        // moved to Recipes/TransitionChainParser.cs (2026-06-18, behaviour-preserving —
        // pure functions over VueOneState). Call-sites resolve via
        // `using static CodeGen.Translation.Process.Recipes.TransitionChainParser`.

        // Recipe command/state mapping vocabulary (IsFiveStateCommandable,
        // IsSevenStateCommandable, MapSevenStateCommandFromConditionName,
        // IsGripperTarget, MapGripperCommandFromStepName) moved VERBATIM to
        // RecipeCommandVocabulary (2026-06-18, behaviour-preserving). Call-sites
        // resolve via `using static CodeGen.Translation.Process.Recipes.RecipeCommandVocabulary`.

    }
}
