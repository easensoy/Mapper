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
    // Six parallel recipe arrays for ProcessRuntime_Generic_v1's ECC. StepType 1=CMD/2=WAIT/9=END; Wait1Id from in-scope sensors+actuators (out-of-scope conditions skipped).
    public sealed class RecipeArrays
    {
        public List<int> StepType       { get; } = new();
        public List<string> CmdTargetName { get; } = new();
        public List<int> CmdStateArr    { get; } = new();
        public List<int> Wait1Id        { get; } = new();
        public List<int> Wait1State     { get; } = new();
        public List<int> NextStep       { get; } = new();

        // ComponentID -> local id (sensors first, actuators next). Process is NOT in this map.
        public Dictionary<string, int> ComponentRegistry { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> SkippedConditions { get; } = new();

        public List<string> Warnings { get; } = new();

        public bool RobotTaskEmitted { get; set; }

        public List<string> TransitionTable { get; } = new();

        public string OrderingSummary { get; set; } = string.Empty;

        public int PusherId { get; set; }
        public int Count => StepType.Count;
    }

    public static class ProcessRecipeArrayGenerator
    {
        public static int RecipeArraySize => GenerationConfig.Current.RecipeArraySize;

        // Sensors first (ids 0..N-1), actuators next (ids N..N+M-1). Process is NOT in the map.
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

        public static RecipeArrays Generate(VueOneComponent process,
            StationContents stationContents, IReadOnlyList<VueOneComponent> allComponents,
            int processId = 10, bool commandFromCondition = false)
        {
            var arrays = new RecipeArrays();
            // Step order follows the transition chain, not State_Number (incrementally-authored models leave State_Number=0).
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

            var scopedRegistry = BuildScopedComponentMap(stationContents.Sensors, stationContents.Actuators);
            foreach (var kv in scopedRegistry) arrays.ComponentRegistry[kv.Key] = kv.Value;

            arrays.PusherId =
                arrays.ComponentRegistry.FirstOrDefault(kv =>
                    LookupComponent(kv.Key, allComponents) is { } c &&
                    (NameEquals(c.Name, "Feeder") || NameEquals(c.Name, "Pusher"))).Value;

            if (MapperConfig.UnparkDisassembly && !MapperConfig.DataDrivenRecipes &&
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

            if (!MapperConfig.DataDrivenRecipes &&
                ShouldParkOnIntraPlcProcessHandoff(process, states, allComponents))
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

            // TEST ISOLATION: restrict this process's recipe to RecipeTestActuatorAllowlist (others parked); null = no restriction.
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

            var classifications = new List<StateClassification>(states.Count);
            foreach (var state in states)
                classifications.Add(ClassifyState(state, allComponents, arrays, scopedRegistry, commandFromCondition, testActuatorAllowlist));

            // Skipped states contribute RowCount=0; NextStep to a skipped state falls forward via stateIdToFallForwardRow.
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

            var stateIdToFallForwardRow = BuildFallForwardMap(states, classifications,
                stateIdToFirstRow, finalEndIndex);

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
                        arrays.NextStep.Add(arrays.StepType.Count);

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
                        break;
                }
            }

            // Every source state skipped: emit a single END (empty arrays would crash bounds checks).
            if (arrays.StepType.Count == 0)
            {
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

            // AUTO-RETRACT (safety, AutoRetractProcesses-scoped): advanced-but-never-retracted actuator gets a retract+wait-athome pair after its atwork WAIT (stranded-atwork = collision).
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
                    if (arrays.StepType[i] != StepType.Cmd) continue;
                    var tgt = (arrays.CmdTargetName[i] ?? string.Empty).Trim();
                    if (tgt.Length == 0) continue;
                    if (arrays.CmdStateArr[i] == 1)
                    {
                        if (advanced.Add(tgt)) advancedOrder.Add(tgt);
                    }
                    else if (arrays.CmdStateArr[i] == 3)
                    {
                        retracted.Add(tgt);
                    }
                }

                var stranded = advancedOrder.Where(a => !retracted.Contains(a)).ToList();

                if (stranded.Count > 0)
                {
                    // Inserting 2 rows at p rebases every NextStep value >= p by +2.
                    foreach (var act in stranded)
                    {
                        actuatorNameToId.TryGetValue(act, out var actId);

                        int advCmdIdx = -1;
                        for (int i = 0; i < arrays.StepType.Count; i++)
                            if (arrays.StepType[i] == StepType.Cmd &&
                                arrays.CmdStateArr[i] == 1 &&
                                string.Equals(
                                    (arrays.CmdTargetName[i] ?? string.Empty).Trim(),
                                    act, StringComparison.OrdinalIgnoreCase))
                                advCmdIdx = i;   // keep last

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
                            atworkWaitIdx = arrays.StepType.Count - 1;
                            arrays.Warnings.Add(
                                $"[Recipe] auto-retract for '{act}': atwork WAIT not " +
                                "locatable via advance-CMD NextStep; appended retract " +
                                "at end (safe; ordering not serialised for this input).");
                        }

                        int insertAt = atworkWaitIdx + 1;
                        int origTarget = arrays.NextStep[atworkWaitIdx];   // pre-shift

                        for (int i = 0; i < arrays.NextStep.Count; i++)
                            if (arrays.NextStep[i] >= insertAt)
                                arrays.NextStep[i] += 2;
                        if (origTarget >= insertAt) origTarget += 2;

                        arrays.StepType.Insert(insertAt, 1);
                        arrays.CmdTargetName.Insert(insertAt, act);
                        arrays.CmdStateArr.Insert(insertAt, 3);
                        arrays.Wait1Id.Insert(insertAt, 0);
                        arrays.Wait1State.Insert(insertAt, 0);
                        arrays.NextStep.Insert(insertAt, insertAt + 1);

                        // WAIT athome targets stable AtHomeInit=0, not transient AtHomeEnd=4 (waiting on 4 parks the engine forever).
                        arrays.StepType.Insert(insertAt + 1, 2);
                        arrays.CmdTargetName.Insert(insertAt + 1, string.Empty);
                        arrays.CmdStateArr.Insert(insertAt + 1, 0);
                        arrays.Wait1Id.Insert(insertAt + 1, actId);
                        arrays.Wait1State.Insert(insertAt + 1, 0);
                        arrays.NextStep.Insert(insertAt + 1, origTarget);

                        arrays.NextStep[atworkWaitIdx] = insertAt;
                    }

                    arrays.Warnings.Add(
                        "[Recipe] auto-retract serialised (each forgotten-retract " +
                        "actuator returns home before the recipe proceeds — no " +
                        "subsequent actuator advances while it is atwork) for: " +
                        string.Join(", ", stranded) + ".");
                }
            }

            // Single final END (StepType=9 only here).
            arrays.StepType.Add(StepType.End);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(0);

            // HOME-FIRST preamble (EnableSevenStateHomePreamble, default off): prepend "CMD Home -> WAIT home" per commanded actuator for a known all-home start.
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
                    // Home ONLY the Seven_State swivel (boots at a work position); a Five_State home CMD would be a no-op the engine waits on forever.
                    if (!IsSevenStateCommandable(comp)) continue;
                    if (!scopedRegistry.TryGetValue((comp.ComponentID ?? string.Empty).Trim(), out var id))
                        continue;
                    homeOrder.Add((tgt, id, 5));   // Home = state_val 5, settles AtHomeInit=0
                }

                if (homeOrder.Count > 0)
                {
                    int rowsPerHome = MapperConfig.SimulatorRecipeMode ? 2 : 3;
                    int shift = rowsPerHome * homeOrder.Count;
                    // Rebase every NextStep by shift EXCEPT the END row's own (must stay 0).
                    for (int i = 0; i < arrays.NextStep.Count; i++)
                        if (arrays.StepType[i] != StepType.End)
                            arrays.NextStep[i] += shift;

                    // Rig waits on the physical AtHome pulse (6) first so a blank state_table can't false-pass; sim boots settled -> 0.
                    int sevenHomePreambleProofWait = MapperConfig.SimulatorRecipeMode ? 0 : 6;

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
                        arrays.Wait1State.Insert(pos, sevenHomePreambleProofWait);
                        arrays.NextStep.Insert(pos, pos + 1);
                        pos++;

                        if (!MapperConfig.SimulatorRecipeMode)
                        {
                            arrays.StepType.Insert(pos, 2);
                            arrays.CmdTargetName.Insert(pos, string.Empty);
                            arrays.CmdStateArr.Insert(pos, 0);
                            arrays.Wait1Id.Insert(pos, id);
                            // Rig-only second WAIT on settled AtHomeInit=0 after the AtHome pulse.
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

            // DataDrivenRecipes: on = generic walk + injected handoffs; off = hardcoded AssemblyRecipe overwrites the walk.
            if (!MapperConfig.DataDrivenRecipes)
                AssemblyRecipe.Apply(process, arrays, allComponents);
            else
            {
                DataDrivenHandoffInjector.InjectAssembly(process, arrays, allComponents);
                DataDrivenHandoffInjector.InjectDisassembly(process, arrays, allComponents);
            }

            // CONTINUOUS-LINE re-arm SAFETY is NOT a Feed-local barrier. The proven-safe gate is Feed's own
            // Control.xml readiness transition "Feed leaves only when Disassembly is at Initialisation" ->
            // WAIT(Disassembly=ProcessIdleSentinelState=0) (emitted by RecipeStateClassifier.TryCrossProcessReadinessGate,
            // present in the rig-proven Ground Truth). That gate holds Feed until Disassembly publishes its idle
            // sentinel, and Disassembly now publishes that sentinel ONLY after WAIT(robot=Home) at its END
            // (DisassemblyRecipe). So Feed cannot pass the readiness gate -- and therefore cannot reach the
            // feeder CMD -- until the robot has completed its drop and returned Home in the CURRENT cycle. A held
            // level (a 2nd part in the hopper mid-cycle) is powerless: the readiness gate is downstream of the
            // hopper wait in Feed's loop, so the feeder still waits for the fresh Disassembly-idle-after-robot-home.
            // A Feed-local WAIT(robot=Home) was intentionally NOT used: robot=Home is a stale level (the robot is
            // Home for almost the whole cycle), and the level engine never re-checks a WAIT once passed
            // (ProcessRuntime ECC: WAIT_STEP -> ADVANCE, never back), so it could be satisfied long before the
            // robot's final drop -- exactly the weak gate this fix removes.

            // END NextStep: EnableCyclicRestart -> 0 (loop to the trigger gate); else RecipeRunOnce -> self-loop (park). After the preamble shift so endIdx is final.
            if (arrays.StepType.Count > 0)
            {
                int endIdx = arrays.StepType.Count - 1;
                if (arrays.StepType[endIdx] == StepType.End)
                {
                    if (MapperConfig.EnableCyclicRestart)
                        arrays.NextStep[endIdx] = 0;
                    else if (MapperConfig.RecipeRunOnce)
                        arrays.NextStep[endIdx] = endIdx;
                }
            }

            ValidateProcessIdInvariant(arrays, processId);
            ValidateSingleEndMarker(arrays);

            // Refuse an over-long recipe: EAE silently truncates the array literal (ArraySize=RecipeArraySize) -> engine stalls on StepType=0.
            if (arrays.StepType.Count > RecipeArraySize)
                throw new InvalidOperationException(
                    $"[Recipe] Recipe length {arrays.StepType.Count} exceeds template " +
                    $"ArraySize {RecipeArraySize}. Raise ProcessRecipeArrayGenerator" +
                    ".RecipeArraySize — it drives both Process1_Generic.fbt and " +
                    "ProcessRuntime_Generic_v1.fbt via PatchProcess1RecipeArraySize.");

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

            // Every CMD flows through its OWN wait (Place CMD waits AtWork2 before the gripper releases — no CMD jumps its WAIT).
            arrays.NextStep[placeCmd] = placeWait;
            arrays.NextStep[placeWait] = releaseCmd;
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

        private static string BuildOrderingNarrative(RecipeArrays arrays,
            IReadOnlyList<VueOneComponent> allComponents)
        {
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
            // StepType=9 exactly once and only at the final row (else the ECC halts early).
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

        // True when the initial state gates on another process on the SAME PLC (unsatisfiable intra-PLC handoff -> park, avoiding a shared-actuator collision); cross-PLC gates return false.
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
                if (string.Equals((target.ComponentID ?? string.Empty).Trim(),
                                  (process.ComponentID ?? string.Empty).Trim(),
                                  StringComparison.OrdinalIgnoreCase)) continue;   // never park on a self-reference
                if (SysresFbMirror.BucketFor(target.Name ?? string.Empty) == myPlc)
                    return true;   // same-PLC upstream -> park
            }
            return false;
        }

    }
}
