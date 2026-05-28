using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

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

            // 2. Pass-1 classify: for each source state, decide if rows are emit/skip.
            var classifications = new List<StateClassification>(states.Count);
            foreach (var state in states)
                classifications.Add(ClassifyState(state, allComponents, arrays, scopedRegistry, commandFromCondition));

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
            Dictionary<string, int> scopedRegistry, bool commandFromCondition)
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
                    int condCmdState = (waitState == 1 || waitState == 2) ? 1 : 3;
                    int condSettledWait = condCmdState == 1 ? 2 : 0;
                    if (condSettledWait != waitState)
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
                // Seven_State (Bearing_PnP) — the SE Seven_State_Actuator_CAT
                // ECC accepts state_val as a target slot and stably publishes
                // current_state_to_pocess matching the slot once settled:
                //   state_val=1  -> ToPick   -> AtPick    publishes 1
                //   state_val=2  -> ToPlace  -> AtPlace   publishes 2
                //   state_val=0  -> ToHome   -> AtHome    publishes 0
                // Control.xml's State_Number for the wait target (e.g.
                // Bearing_PnP/AtPick State_Number=2) does NOT line up with the
                // CAT's published value (AtPick publishes 1), so we cannot use
                // waitState directly. Instead we read the WAIT condition's
                // Name suffix ("Bearing_PnP/AtPick" -> "AtPick") and pattern
                // match Pick/Place/Home to the CAT's settle value.
                if (IsSevenStateCommandable(inScopeTarget))
                {
                    int sevenStateCmd = MapSevenStateCommandFromConditionName(cond.Name);
                    if (sevenStateCmd >= 0)
                    {
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': Seven_State CMD on '{inScopeTarget.Name}' " +
                            $"-> state_val={sevenStateCmd} (CAT settles publishing {sevenStateCmd}); " +
                            $"derived from condition Name '{cond.Name}'.");
                        return new StateClassification
                        {
                            Kind = ClassKind.MotionPair,
                            RowCount = 2,
                            CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
                            CmdState = sevenStateCmd,
                            WaitId = waitId,
                            WaitState = sevenStateCmd,
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
            // Place check before pick keeps "atplace2" / "place2" routing to 2.
            if (lower.Contains("place")) return 2;
            if (lower.Contains("pick"))  return 1;
            if (lower.Contains("home") || lower.Contains("returned")) return 0;
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
