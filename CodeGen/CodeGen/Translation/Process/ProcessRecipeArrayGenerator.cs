using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Devices.Core;   // SysresFbMirror.BucketFor — PLC bucket for the intra-PLC process-handoff park guard

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
        // Recipe-array capacity — SINGLE SOURCE OF TRUTH. Process1_Generic.fbt
        // and ProcessRuntime_Generic_v1.fbt declare the six recipe arrays
        // (StepType/CmdTargetName/CmdStateArr/Wait1Id/Wait1State/NextStep) at
        // this ArraySize; TemplateLibraryDeployer.PatchProcess1RecipeArraySize
        // forces the deployed .fbt ArraySize to exactly this value, and the
        // length guard in Generate() refuses any recipe longer than this — so
        // the declared capacity and the guard can never drift apart. The runtime
        // ECC indexes the arrays via NextStep navigation (StepType[CurrentStep]),
        // not a fixed FOR loop, so a larger bound needs no ST change.
        //
        // Raised 20 -> 50 for the full-system simulator (~21+ rows), then 50 -> 100
        // for the Station-2 condition-driven recipes: Assembly_Station expands to 54
        // rows once each "command actuator X to state Y" condition emits a CMD+WAIT
        // pair (plus serialised auto-retract), and Disassembly to 43. 50 truncated
        // Assembly mid-cycle. SAFE for the byte-identical hardware slice —
        // FormatIntArray emits ONLY the rows actually present (e.g. [1, 2, 9]), never
        // padded to ArraySize, so no instance .syslay changes; only the shared
        // TYPE's declared array bound grows (and that .fbt is deploy-patched via
        // PatchProcess1RecipeArraySize on both paths). ~26 bytes/row, so the
        // 50->100 bump adds <1.5 KB to the two shared .fbt TYPEs, nothing per-instance.
        public const int RecipeArraySize = 100;

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

            // 1. Build scoped registry from the in-scope sensors + actuators ONLY.
            var scopedRegistry = BuildScopedComponentMap(stationContents.Sensors, stationContents.Actuators);
            foreach (var kv in scopedRegistry) arrays.ComponentRegistry[kv.Key] = kv.Value;

            // Diagnostic PusherId — not part of the recipe semantics, useful for tests.
            arrays.PusherId =
                arrays.ComponentRegistry.FirstOrDefault(kv =>
                    LookupComponent(kv.Key, allComponents) is { } c &&
                    (NameEquals(c.Name, "Feeder") || NameEquals(c.Name, "Pusher"))).Value;

            // PARK GUARD (2026-05-29): a process whose INITIAL state gates on
            // ANOTHER process on the SAME PLC is an intra-PLC hand-off (e.g.
            // Disassembly's Initialize waits for Assembly_Station/HandShaking,
            // both on the M580). That hand-off is not wired at runtime yet:
            // ProcessStateBusHandler never publishes a process's live state into
            // state_table (state_sts is unconnected -> always 0) and the wait-
            // registry deliberately excludes process ids, so the gate can never
            // be satisfied. The old IsInitialisationState path DROPPED the gate,
            // which let the process FREE-RUN from its first motion and collide
            // with the still-running upstream process over the shared actuators
            // (bearing_pnp / shaft / clamp on the M580). Until cross-process
            // coordination is built, PARK such a process: emit a single END row so
            // it holds at step 0 and issues NO actuator commands. Feed_Station and
            // Assembly_Station gate on CROSS-PLC processes (different bucket) and
            // are NOT parked -- they free-run to start their own cycle (unchanged,
            // so the M262 Feed recipe stays byte-identical).
            if (ShouldParkOnIntraPlcProcessHandoff(process, states, allComponents))
            {
                arrays.Warnings.Add(
                    $"[Recipe] Process '{process.Name}' PARKED: its initial state gates on a " +
                    "same-PLC process (intra-PLC hand-off) the runtime cannot yet signal -- no " +
                    "cross-process state publish. Emitting a single END row so it holds at step 0 " +
                    "and issues no commands; prevents a free-run collision with the upstream " +
                    "process over the shared actuators. Lift when cross-process coordination is built.");
                arrays.StepType.Add(9);
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
            if (MapperConfig.RecipeTestActuatorAllowlist != null &&
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
                arrays.Warnings.Add(
                    "Every source state was skipped or unsatisfiable — collapsing the recipe " +
                    "to a single END row at index 0. Engine will halt immediately.");
                arrays.StepType.Add(9);
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
                    if (arrays.StepType[i] != 1) continue;             // CMD rows only
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
                            if (arrays.StepType[i] == 1 &&
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
                                arrays.StepType[w] == 2)
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
            arrays.StepType.Add(9);
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
            //    AtHomeInit=0" pair: Seven swivel uses state_val=5, Five_State uses
            //    toHome=3; both settle publishing current_state_to_process=0. This
            //    drives a REAL ECC transition (AtWork* -> ToHome -> AtHome ->
            //    AtHomeInit) the running engine observes, so the cycle then starts
            //    from a known all-home state and every later command is a real move.
            //    Safe no-op when an actuator already boots home: the home command has
            //    no arc out of AtHomeInit and WAIT AtHomeInit=0 is satisfied on entry
            //    (state_table defaults to 0 and a homed actuator publishes 0). Only
            //    fires for actuators the recipe actually commands, so Feed_Station's
            //    recipe is unchanged when it commands none via this path.
            {
                var homeOrder = new List<(string name, int id, int homeCmd)>();
                var seenActuator = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < arrays.StepType.Count; i++)
                {
                    if (arrays.StepType[i] != 1) continue;
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
                    int shift = 2 * homeOrder.Count;
                    // Every existing row moves down by `shift`, so every NextStep
                    // pointer is rebased -- EXCEPT the final END row's own NextStep,
                    // which must stay 0. The END ECState runs EndSequence
                    // (CurrentStep := Recipe[CurrentStep].NextStep) on its END->END
                    // self-loop; rebasing END's NextStep to `shift` made it walk
                    // CurrentStep off into the cycle rows instead of pinning at the
                    // recipe start. Pointers TO the END row (other rows' NextStep)
                    // are still rebased correctly because the END row itself moved +shift.
                    for (int i = 0; i < arrays.NextStep.Count; i++)
                        if (arrays.StepType[i] != 9)
                            arrays.NextStep[i] += shift;

                    // Insert the home CMD+WAIT pairs at the front, in order. Each row
                    // chains to the next (NextStep = its own index + 1); the last
                    // WAIT's NextStep lands on `shift`, i.e. the original first row.
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
                        // Home-preamble WAIT target — differs RIG vs SIM (see
                        // MapperConfig.SimulatorRecipeMode). SIM: the swivel boots at
                        // AtHomeInit(0) and a home command is a no-op, so wait for 0.
                        // RIG: the swivel boots parked at a work position and the engine's
                        // blank state_table reads 0 — WAIT-for-0 false-matches and the
                        // homing is SKIPPED (swivel goes atWork1->atWork2, atHome never
                        // TRUE). Wait for AtHome=6 instead (== atHome sensor TRUE): the
                        // blank 0 can't match it, so the engine truly waits for the swivel
                        // to physically reach home before the Pick step. The Centre-Home
                        // ECC reports current_state=6 the moment the atHome sensor closes
                        // (a distinct pst_out event the engine catches), then settles to 0.
                        arrays.Wait1State.Insert(pos, MapperConfig.SimulatorRecipeMode ? 0 : 6);
                        arrays.NextStep.Insert(pos, pos + 1);
                        pos++;
                    }

                    arrays.Warnings.Add(
                        $"[Recipe] HOME-FIRST preamble prepended for {homeOrder.Count} actuator(s): " +
                        string.Join(", ", homeOrder.Select(h => $"{h.name}(cmd {h.homeCmd})")) +
                        " -- each CMD Home -> WAIT AtHomeInit=0 before the cycle. Guarantees a known " +
                        "all-home start regardless of where each actuator was physically parked at " +
                        "power-up; without it an actuator booting parked at work stalls the recipe.");
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
            foreach (var kv in arrays.ComponentRegistry)
            {
                var comp = allComponents.FirstOrDefault(c =>
                    string.Equals((c.ComponentID ?? string.Empty).Trim(), kv.Key,
                        StringComparison.OrdinalIgnoreCase));
                idToName[kv.Value] =
                    (comp?.Name ?? $"id{kv.Value}").Trim();
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
                        var verb = arrays.CmdStateArr[i] switch
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
                        var phase = arrays.Wait1State[i] switch
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
                // Terminal (End) state — route straight to the single appended
                // final END so execution HALTS here, preserving the original
                // in-loop-END "halt at terminal" semantics now that END is
                // centralised at finalEndIndex. (Distinct from an out-of-scope
                // skip, which falls forward to the next surviving row below.)
                if (classifications[i].Kind == ClassKind.End)
                {
                    fallForward[s.StateID] = finalEndIndex;
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
            End,                    // RowCount=0 (terminal state — emits NO in-loop END; routes to the single appended final END)
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
            Dictionary<string, int> scopedRegistry, bool commandFromCondition,
            IReadOnlyCollection<string>? testActuatorAllowlist = null)
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

            // No transition → terminal state. RowCount=0: emit NO in-loop END
            // row. Centralised-END design (ValidateSingleEndMarker) allows
            // StepType=9 ONLY at the single appended final row; a terminal's
            // predecessors are routed to that final END via the fall-forward map
            // (see BuildFallForwardMap), preserving "halt at terminal" without a
            // mid-array END.
            if (trans == null)
                return new StateClassification { Kind = ClassKind.End, RowCount = 0 };

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
                // Genuine no-transition state. Truly the end of this Process.
                // RowCount=0: no in-loop END — predecessors route to the single
                // appended final END via the fall-forward map (centralised-END
                // invariant; see the trans==null branch above).
                return new StateClassification { Kind = ClassKind.End, RowCount = 0 };
            }

            var inScopeTarget = LookupComponent(cond.ComponentID, allComponents);
            if (inScopeTarget == null)
            {
                arrays.Warnings.Add(
                    $"State '{state.Name}': in-scope ComponentID '{cond.ComponentID}' could not " +
                    "be resolved to a VueOneComponent in allComponents. Emitting WAIT id=0.");
                return new StateClassification { Kind = ClassKind.SettledWait, RowCount = 1 };
            }

            // TEST ISOLATION (MapperConfig.RecipeTestActuatorAllowlist): when a
            // restricted allowlist is active for this process, any state whose
            // command/wait target is NOT on the allowlist is dropped (that target
            // is PARKED -- never commanded). Lets one mechanism (bearing_pnp +
            // bearing_gripper) run while shaft_pnp / clamp / cover stay still.
            // NOTE: grippers carry Control.xml Type="Robot" (NOT "Actuator"); an
            // earlier "Actuator only" check let shaft_gripper / coverpnp_gripper
            // slip through, so we now drop ANY non-Process target not on the
            // allowlist. Process targets are never parked here (the intra-PLC park
            // guard handles those).
            if (testActuatorAllowlist != null
                && !string.Equals(inScopeTarget.Type, "Process", StringComparison.OrdinalIgnoreCase)
                && !testActuatorAllowlist.Contains((inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant()))
            {
                arrays.SkippedConditions.Add(
                    $"state '{state.Name}': dropped -- actuator '{inScopeTarget.Name}' PARKED by " +
                    "RecipeTestActuatorAllowlist (bearing-only isolation test); CMD/WAIT removed, " +
                    "NextStep pointers skip past it.");
                return new StateClassification { Kind = ClassKind.Skipped, RowCount = 0 };
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

                // RUNTIME-SETTLED WAIT STATE (root-cause fix for the recipe
                // stalling mid-cycle). The engine compares Wait1State against
                // what the actuator PUBLISHES on the stateRptCmd ring, which is
                // the Five_State ECC's current_state_to_process encoding
                // (AtHomeInit=0, ToWork=1, AtWork=2, ToHome=3, AtHomeEnd=4) —
                // NOT the raw Control.xml <State_Number>. After a to-work
                // command the actuator STABLY HOLDS AtWork=2. After a to-home
                // command it transitions ToHome -> AtHomeEnd (publishes 4
                // momentarily) -> AtHomeInit (publishes 0, where it stably
                // rests) because the AtHome -> AtHomeInit ECC arc is
                // data-conditioned (atwork=FALSE AND athome=TRUE) and fires
                // in the same run-to-stable tick as the AtHome entry. The
                // engine's check_wait re-samples state_table on each
                // state_change, and by the time it evaluates a Wait1State=4,
                // state_table has already been overwritten 4 -> 0. So athome
                // waits MUST target the stably-held AtHomeInit=0, not the
                // transient AtHomeEnd=4. Emitting the Control.xml StateNumber
                // only matched by accident for components numbering their
                // advanced state 2; Checker's RisingFinished is 4, unreachable
                // from a single ToWork command. Map by command direction so
                // every Five_State actuator wait targets the value the
                // actuator stably holds: AtWork=2 after ToWork, AtHomeInit=0
                // after ToHome.
                int settledWait =
                    cmdState == 1 ? 2 :          // ToWork -> actuator stably holds AtWork
                    cmdState == 3 ? 0 :          // ToHome -> actuator settles at AtHomeInit
                    waitState;                   // non-Five_State CAT: keep the Control.xml number
                if (settledWait != waitState)
                    arrays.Warnings.Add(
                        $"[Recipe] '{state.Name}': WAIT on '{inScopeTarget.Name}' remapped " +
                        $"Control.xml State_Number {waitState} -> runtime ECC state {settledWait} " +
                        $"(cmd {cmdState}). The actuator stably holds AtWork=2 or AtHomeInit=0; " +
                        "AtHomeEnd=4 is transient (overwritten 4 -> 0 in the same run-to-stable " +
                        "tick) and the engine misses it, so athome waits must target 0.");

                return new StateClassification
                {
                    Kind = ClassKind.MotionPair,
                    RowCount = 2,
                    CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
                    CmdState = cmdState,
                    WaitId = waitId,
                    WaitState = settledWait,
                };
            }

            // CONDITION-DRIVEN COMMAND (Station-2 opt-in; commandFromCondition).
            // The source-state name did NOT encode a motion verb, but Control.xml
            // still says "leave this state when component C reaches state S". For a
            // Five_State-commandable target that IS the command: drive C toward S,
            // then wait until it settles. Five_State ECC encoding — target work
            // positions (transient ToWork=1 or settled Advanced/AtWork/Down/
            // Clamped/CloseGripper=2) ← command toWork (cmd 1), settles AtWork=2;
            // target home positions (AtHomeInit=0, transient ToHome=3, AtHomeEnd/
            // ReturnedFinished/RisingFinished=4) ← command toHome (cmd 3), settles
            // AtHomeInit=0. Feed_Station passes commandFromCondition=false and
            // never enters here, so its recipe is byte-identical.
            if (commandFromCondition &&
                !string.Equals(inScopeTarget.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
            {
                if (IsFiveStateCommandable(inScopeTarget))
                {
                    // GRIPPER GRIP/RELEASE DIRECTION (derive from the Assembly STEP
                    // name, not the WAIT condition). A mechanical gripper's
                    // Control.xml WAIT condition is the SAME for both the grip step
                    // and the release step -- "Bearing_Gripper/ReturnedHome" (the
                    // twin waits for the gripper's internal open/close cycle to
                    // settle home each time), so the condition target State_Number
                    // is 0 in BOTH cases and the generic
                    // "(waitState 1|2 -> toWork else toHome)" rule collapses both to
                    // toHome=3. The gripper would then OPEN twice and never grip the
                    // bearing -- the exact bug observed on the rig. The Assembly STEP
                    // NAME does encode intent ("Gripping_Part" vs
                    // "BearingPnPOpenGripper"), so for a gripper target we take the
                    // command from the step name: a grip/close step commands toWork
                    // (cmd 1 -> Five_State AtWork=2; M580SymbolBinder maps
                    // closed=atwork and the single OutputToWork coil energises the
                    // close valve), a release/open step commands toHome (cmd 3 ->
                    // AtHomeInit=0; open=athome). Non-gripper Five_State targets
                    // (clamps, shaft cylinders) keep the condition-derived command.
                    // NOTE (R-12, coil direction unverified on the rig): if the
                    // physical gripper grips/releases the OPPOSITE way to this
                    // (energise OutputToWork opens it), that is a binder/coil wiring
                    // inversion -- swap Bearing_Gripper_Q / the open+closed sensor
                    // pair in M580SymbolBinder, NOT the recipe, so sim stays correct.
                    int gripperCmd = IsGripperTarget(inScopeTarget)
                        ? MapGripperCommandFromStepName(state.Name)
                        : -1;
                    int condCmdState = gripperCmd >= 0
                        ? gripperCmd
                        : (waitState == 1 || waitState == 2) ? 1 : 3;
                    int condSettledWait = condCmdState == 1 ? 2 : 0;
                    if (gripperCmd >= 0)
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': gripper '{inScopeTarget.Name}' command derived " +
                            $"from the STEP name -> {(condCmdState == 1 ? "CLOSE/grip (toWork, settles AtWork=2)" : "OPEN/release (toHome, settles AtHomeInit=0)")} " +
                            $"(cmd {condCmdState}). Control.xml WAIT condition '{cond.Name}' is " +
                            "direction-agnostic (ReturnedHome) so the step name is authoritative for " +
                            "grippers -- this is what makes the gripper grip at Pick and release at Place.");
                    else if (condSettledWait != waitState)
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': condition-driven CMD on '{inScopeTarget.Name}' " +
                            $"-> {(condCmdState == 1 ? "toWork" : "toHome")} (cmd {condCmdState}); WAIT " +
                            $"remapped Control.xml State_Number {waitState} -> runtime ECC state {condSettledWait} " +
                            "(actuator stably holds AtWork=2 or AtHomeInit=0).");
                    return new StateClassification
                    {
                        Kind = ClassKind.MotionPair,
                        RowCount = 2,
                        CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
                        CmdState = condCmdState,
                        WaitId = waitId,
                        WaitState = condSettledWait,
                    };
                }
                // Seven_State (Bearing_PnP) — Seven_State_Actuator_Centre_Home_CAT.
                // The core takes state_val as a target slot and, once settled,
                // publishes current_state_to_process = the slot's AT-value (cmd+1):
                //   state_val=1 (Work1/Pick)  -> ToWork1 -> AtWork1 publishes 2
                //   state_val=3 (Work2/Place) -> ToWork2 -> AtWork2 publishes 4
                //   state_val=5 (Home/centre) -> ToHome  -> AtHome  publishes 6
                // Control.xml's State_Number for the wait target does NOT line up
                // with the core's published value, so we read the WAIT condition's
                // Name suffix ("Bearing_PnP/AtPick" -> "AtPick") and pattern-match
                // Pick/Place/Home to the CMD slot (CmdState) and its settle value
                // (WaitState = CmdState + 1).
                if (IsSevenStateCommandable(inScopeTarget))
                {
                    int sevenStateCmd = MapSevenStateCommandFromConditionName(cond.Name);
                    if (sevenStateCmd >= 0)
                    {
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': Seven_State CMD on '{inScopeTarget.Name}' " +
                            $"-> state_val={sevenStateCmd} (Centre-Home core settles publishing {sevenStateCmd + 1}); " +
                            $"derived from condition Name '{cond.Name}'.");
                        return new StateClassification
                        {
                            Kind = ClassKind.MotionPair,
                            RowCount = 2,
                            CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
                            CmdState = sevenStateCmd,
                            WaitId = waitId,
                            // Centre-Home core settle value once stable:
                            //   pick  1 -> AtWork1     publishes 2
                            //   place 3 -> AtWork2     publishes 4
                            //   home  5 -> AtHomeInit  publishes 0  (NOT AtHome=6)
                            // Home is the exception: the ECC takes AtHome -> AtHomeInit
                            // on (atWork1=FALSE AND atWork2=FALSE AND atHome=TRUE) in the
                            // same run-to-stable tick as the AtHome entry, so current_state
                            // is overwritten 6 -> 0 and the engine (which re-samples
                            // state_table on each state_change) only ever sees the stable
                            // 0. Waiting on the transient 6 misses it and parks the engine
                            // forever -- exactly the Five_State AtHomeEnd=4 -> 0 remap.
                            WaitState = sevenStateCmd == 5 ? 0 : sevenStateCmd + 1,
                        };
                    }
                    arrays.Warnings.Add(
                        $"[Recipe] '{state.Name}': Seven_State condition Name '{cond.Name}' did not " +
                        "match Pick/Place/Home; falling back to settled WAIT. Extend " +
                        "MapSevenStateCommandFromConditionName if a new state name keyword is needed.");
                }
                else
                {
                    // Robot arm or other non-commandable target — emit settled WAIT
                    // and flag for follow-up. Bearing_PnP shouldn't land here now
                    // (IsSevenStateCommandable catches it above).
                    arrays.Warnings.Add(
                        $"[Recipe] '{state.Name}': condition target '{inScopeTarget.Name}' (type " +
                        $"{inScopeTarget.Type}, {inScopeTarget.States.Count} states) is neither " +
                        "Five_State- nor Seven_State-commandable. Emitted a settled WAIT only.");
                }
                return new StateClassification
                {
                    Kind = ClassKind.SettledWait,
                    RowCount = 1,
                    WaitId = waitId,
                    WaitState = waitState,
                };
            }

            // SETTLED WAIT runtime encoding (complementary to the MotionPair
            // fix above). A settled wait on a Five_State actuator must target
            // the value the actuator STABLY HOLDS, not the transient
            // Control.xml State_Number. The actuator only stably holds
            // AtHomeInit (runtime 0) and AtWork (runtime 2). AtHomeEnd
            // (runtime 4) is the momentary publish during the ToHome ->
            // AtHomeInit transition, so any settled wait reached after the
            // return completes sees 0, not 4. Concretely, Feed_Station's
            // WaitingReleaseSt2 waits on Feeder/ReturnedFinished
            // (State_Number 4); by the time it is reached feeder has long
            // settled to AtHomeInit 0 and the ==4 wait would park forever.
            // Remap actuator settled waits on the home-finished family
            // (State_Number 4) to the resting AtHomeInit (0). Sensors are
            // untouched (Sensor_Bool_CAT publishes the Control.xml number).
            int settledStateWait = waitState;
            if (string.Equals(inScopeTarget.Type, "Actuator",
                    StringComparison.OrdinalIgnoreCase) &&
                waitState == 4)
            {
                settledStateWait = 0;
                arrays.Warnings.Add(
                    $"[Recipe] '{state.Name}': SETTLED WAIT on " +
                    $"'{inScopeTarget.Name}' remapped Control.xml State_Number 4 " +
                    "(home-finished family) -> runtime AtHomeInit 0. The actuator " +
                    "only stably holds 0 home or 2 atwork; AtHomeEnd 4 is " +
                    "transient and missed by a wait reached after the return " +
                    "has already completed.");
            }

            return new StateClassification
            {
                Kind = ClassKind.SettledWait,
                RowCount = 1,
                WaitId = waitId,
                WaitState = settledStateWait,
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

        /// <summary>
        /// Orders a Process's states in EXECUTION order by walking the transition
        /// chain from the initial state, rather than by State_Number.
        ///
        /// <para>Why: VueOne models authored incrementally leave State_Number = 0
        /// on every state added after the first pass. Ordering by State_Number then
        /// pulls all the 0-numbered states to the front, and because the runtime
        /// starts at CurrentStep 0 the recipe begins mid-cycle. Observed on
        /// Assembly_Station: the shaft-place and cover steps all carry
        /// State_Number = 0, so the recipe started with shaft_vr instead of the
        /// real first step (Clamping_Part). Walking Initial_State -&gt;
        /// transition.DestinationStateID reproduces the true sequence.</para>
        ///
        /// <para>Any state not reachable from the initial state (alternate /
        /// parallel branches such as Bearing_PnP's disassembly leg) is appended in
        /// declaration order so the classifier still sees every state and no work
        /// is silently dropped.</para>
        /// </summary>
        private static List<VueOneState> OrderStatesByTransitionChain(IList<VueOneState> states)
        {
            if (states == null || states.Count == 0) return new List<VueOneState>();

            var byId = new Dictionary<string, VueOneState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in states)
                if (!string.IsNullOrEmpty(s.StateID) && !byId.ContainsKey(s.StateID))
                    byId[s.StateID] = s;

            var start = states.FirstOrDefault(IsInitialisationState) ?? states[0];
            var ordered = new List<VueOneState>(states.Count);
            var seen = new HashSet<VueOneState>();

            VueOneState? cur = start;
            while (cur != null && seen.Add(cur))
            {
                ordered.Add(cur);
                VueOneState? next = null;
                var tr = cur.Transitions?.FirstOrDefault();
                if (tr != null && !string.IsNullOrEmpty(tr.DestinationStateID))
                    byId.TryGetValue(tr.DestinationStateID, out next);
                cur = next;
            }

            // Append unreachable branch states in declaration order so nothing is lost.
            foreach (var s in states)
                if (!seen.Contains(s))
                    ordered.Add(s);

            return ordered;
        }

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
        private static bool IsFiveStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            // Interim stub: when on, Seven_State actuators ARE Five_State-commandable
            // (they emit as Five_State_Actuator_CAT — see MapperConfig flag), so the
            // recipe drives them with work/home instead of Pick/Place/Home.
            if (!MapperConfig.StubSevenStateActuatorsAsFiveState
                && (t.States.Count == 7 || IsBranchedSevenState(t))) return false;
            return true;
        }

        /// <summary>
        /// Complementary to <see cref="IsFiveStateCommandable"/>: returns true for
        /// targets that drive a Seven_State_Actuator_CAT ECC (Bearing_PnP and any
        /// 13-state branched-swivel actuator routed through the seven-state runtime).
        /// Used by the condition-driven classifier to emit Pick/Place/Home CMDs on
        /// these targets instead of falling back to a settled-WAIT-only row.
        /// </summary>
        private static bool IsSevenStateCommandable(VueOneComponent t)
        {
            if (t == null) return false;
            if (string.Equals(t.Type, "Sensor", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(t.Type, "Process", StringComparison.OrdinalIgnoreCase)) return false;
            // Interim stub: when on, nothing is Seven_State-commandable — Bearing_PnP
            // runs as Five_State (see MapperConfig flag), so the recipe must NOT emit
            // Pick/Place/Home state_val commands a Five_State ECC can't honour.
            if (MapperConfig.StubSevenStateActuatorsAsFiveState) return false;
            return t.States.Count == 7 || IsBranchedSevenState(t);
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
        private static int MapSevenStateCommandFromConditionName(string? conditionName)
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
        private static bool IsGripperTarget(VueOneComponent t)
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
        private static int MapGripperCommandFromStepName(string? stepName)
        {
            var n = (stepName ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0) return -1;
            if (n.Contains("open") || n.Contains("release") || n.Contains("unclamp")) return 3;
            if (n.Contains("grip") || n.Contains("grasp") || n.Contains("clos") ||
                n.Contains("hold") || n.Contains("pick")) return 1;
            return -1;
        }

        /// <summary>
        /// Mirrors <c>SystemLayoutInjector.IsBranchedSevenState</c> — a resting
        /// state with at least one outgoing PARALLEL transition AND at least one
        /// outgoing ALTERNATIVE transition (Bearing_PnP's 13-state branched swivel,
        /// which runs as a Seven_State_Actuator_CAT ECC).
        /// </summary>
        private static bool IsBranchedSevenState(VueOneComponent comp)
        {
            if (comp?.States == null) return false;
            foreach (var st in comp.States)
            {
                bool hasParallel = false, hasAlternative = false;
                foreach (var tr in st.Transitions)
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
