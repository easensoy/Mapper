using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;
using static CodeGen.Translation.Process.Recipes.RecipeCommandVocabulary;
using static CodeGen.Translation.Process.Recipes.RecipeComponentLookup;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;

namespace CodeGen.Translation.Process.Recipes
{
    internal static class RecipeStateClassifier
    {
        private static readonly string[] MotionVerbs = new[]
        {
            "advancing", "rising", "returning", "descending",
            "towork", "tohome", "gotowork", "gotohome",
            "checking",
        };

        internal static Dictionary<string, int> BuildFallForwardMap(
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
                // Terminal state routes to the single final END; out-of-scope skip falls forward below.
                if (classifications[i].Kind == ClassKind.End)
                {
                    fallForward[s.StateID] = finalEndIndex;
                    continue;
                }
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

        internal enum ClassKind
        {
            MotionPair,             // RowCount=2 (CMD then WAIT)
            SettledWait,            // RowCount=1 (single WAIT)
            End,                    // RowCount=0 (terminal — routes to the single final END)
            Skipped,                // RowCount=0 (all conditions out of scope — row dropped)
        }

        internal sealed class StateClassification
        {
            public ClassKind Kind;
            public int RowCount;
            public string CmdTargetName = string.Empty;
            public int CmdState;
            public int WaitId;
            public int WaitState;
            public List<RecipeRow> Rows { get; } = new();
        }

        internal sealed class RecipeRow
        {
            public int StepType;
            public string CmdTargetName = string.Empty;
            public int CmdState;
            public int WaitId;
            public int WaitState;
        }

        internal static StateClassification ClassifyState(VueOneState state,
            IReadOnlyList<VueOneComponent> allComponents, RecipeArrays arrays,
            Dictionary<string, int> scopedRegistry, bool commandFromCondition,
            IReadOnlyCollection<string>? testActuatorAllowlist = null)
        {
            // Initialisation asserts the boot world, not a work step — drop it (its WAIT is a tautology).
            if (IsInitialisationState(state))
            {
                // MergeFeedRing: an Initialisation gated on another process is a cross-station readiness gate, not a tautology — keep as WAIT.
                var readiness = TryCrossProcessReadinessGate(state, allComponents, arrays);
                if (readiness != null) return readiness;
                arrays.SkippedConditions.Add(
                    $"state '{state.Name}': dropped — Initialisation asserts boot " +
                    "conditions, not a work-cycle step.");
                return new StateClassification { Kind = ClassKind.Skipped, RowCount = 0 };
            }

            var trans = state.Transitions.FirstOrDefault();
            var allConds = trans?.Conditions ?? new List<VueOneCondition>();

            // No transition = terminal; predecessors route to the final END via the fall-forward map.
            if (trans == null)
                return new StateClassification { Kind = ClassKind.End, RowCount = 0 };

            // Collect every in-scope condition (all conjuncts, not just the first) — out-of-scope ones are recorded and dropped.
            var inScopeConds = new List<VueOneCondition>();
            // MergeFeedRing: id of a dropped cross-process condition -> WAIT on that process's sentinel slot; -1 = none.
            int crossProcessWaitId = -1;
            foreach (var c in allConds)
            {
                if (string.IsNullOrEmpty(c.ComponentID)) continue;
                if (scopedRegistry.ContainsKey(c.ComponentID.Trim()))
                {
                    inScopeConds.Add(c);
                    continue;
                }
                var target = LookupComponent(c.ComponentID, allComponents);
                arrays.SkippedConditions.Add(
                    $"state '{state.Name}' references out-of-scope component " +
                    $"ComponentID={c.ComponentID} " +
                    $"(name={(target?.Name ?? "?")}, type={(target?.Type ?? "?")})");
                if (CodeGen.Configuration.MapperConfig.MergeFeedRing && crossProcessWaitId < 0 &&
                    target != null &&
                    string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                    ProcessSentinelId(target.Name) is int pid)
                    crossProcessWaitId = pid;
            }

            var cond = inScopeConds.FirstOrDefault();
            if (cond == null)
            {
                // In-scope motion gated only by a cross-process wait: keep the CMD (from the state name), settle on the actuator's own state.
                if (!commandFromCondition && allConds.Any() && StateNameSuggestsMotion(state.Name))
                {
                    var mover = ResolveInScopeMotionActuator(state.Name, allComponents, scopedRegistry);
                    if (mover != null)
                    {
                        int moverCmd = ResolveTransientCmdState(state.Name, mover, 2, arrays);
                        int moverWaitState = moverCmd == 1 ? 2 : moverCmd == 3 ? 0 : 2;
                        // MergeFeedRing: keep the motion AND append a WAIT on the referenced process's sentinel (Feed holds for Disassembly).
                        if (crossProcessWaitId >= 0)
                        {
                            var rows = new StateClassification { Kind = ClassKind.MotionPair };
                            AddCmdWaitRows(rows.Rows,
                                (mover.Name ?? string.Empty).Trim().ToLowerInvariant(),
                                moverCmd, scopedRegistry[mover.ComponentID.Trim()], moverWaitState);
                            rows.Rows.Add(new RecipeRow
                            {
                                StepType = 2,
                                WaitId = crossProcessWaitId,
                                WaitState = CodeGen.Configuration.MapperConfig.MergeFeedRingBearingHomeState,
                            });
                            rows.RowCount = rows.Rows.Count;
                            arrays.Warnings.Add(
                                $"[Recipe] MergeFeedRing: '{state.Name}' keeps its motion and now WAITs on " +
                                $"the cross-process sentinel (id {crossProcessWaitId}, state " +
                                $"{CodeGen.Configuration.MapperConfig.MergeFeedRingBearingHomeState}) before proceeding.");
                            return rows;
                        }
                        return new StateClassification
                        {
                            Kind = ClassKind.MotionPair,
                            RowCount = 2,
                            CmdTargetName = (mover.Name ?? string.Empty).Trim().ToLowerInvariant(),
                            CmdState = moverCmd,
                            WaitId = scopedRegistry[mover.ComponentID.Trim()],
                            WaitState = moverWaitState,
                        };
                    }
                }
                if (allConds.Any())
                {
                    // MergeFeedRing: a non-initial state gated only by a cross-process readiness condition keeps its WAIT.
                    var readiness = TryCrossProcessReadinessGate(state, allComponents, arrays);
                    if (readiness != null) return readiness;
                    arrays.SkippedConditions.Add(
                        $"state '{state.Name}': all transition conditions out of scope — row dropped.");
                    return new StateClassification { Kind = ClassKind.Skipped, RowCount = 0 };
                }
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

            // TEST ISOLATION: drop any non-Process target not on RecipeTestActuatorAllowlist (Process parks via the intra-PLC guard).
            if (!commandFromCondition &&
                testActuatorAllowlist != null
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

            if (commandFromCondition)
            {
                return ClassifyConditionDrivenState(
                    state, inScopeConds, allComponents, arrays, scopedRegistry,
                    testActuatorAllowlist);
            }

            // Dispatch on the source-state name, not the target type.
            if (StateNameSuggestsMotion(state.Name))
            {
                // Transient CMD state derived from the actuator's own State whose Name matches the motion direction.
                int cmdState = ResolveTransientCmdState(state.Name, inScopeTarget, waitState, arrays);

                // Runtime WAIT != Control.xml State_Number: actuator stably holds AtWork=2 / AtHomeInit=0; AtHomeEnd=4 is transient (missed) -> remap 4->0.
                int settledWait =
                    cmdState == 1 ? 2 :
                    cmdState == 3 ? 0 :
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

            // Condition-driven CMD (commandFromCondition): drive C toward the WAIT condition's state, then settle (toWork/1->AtWork=2, toHome/3->AtHomeInit=0).
            if (commandFromCondition &&
                !string.Equals(inScopeTarget.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
            {
                if (IsFiveStateCommandable(inScopeTarget))
                {
                    // Gripper direction from the Assembly STEP name (open checked first), NOT the WAIT condition (same ReturnedHome both ways) — R-12.
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
                // Seven_State Centre-Home settle: pick 1->AtWork1(2), place 3->AtWork2(4), home 5->AtHome(6)->AtHomeInit(0); 6 transient -> use 0. Match condition Name suffix (Pick/Place/Home).
                if (IsSevenStateCommandable(inScopeTarget))
                {
                    int sevenStateCmd = MapSevenStateCommandFromConditionName(cond.Name);
                    if (sevenStateCmd >= 0)
                    {
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': Seven_State CMD on '{inScopeTarget.Name}' " +
                            $"-> state_val={sevenStateCmd} (Centre-Home core settles publishing {(sevenStateCmd == 5 ? 0 : sevenStateCmd + 1)}); " +
                            $"derived from condition Name '{cond.Name}'.");
                        return new StateClassification
                        {
                            Kind = ClassKind.MotionPair,
                            RowCount = 2,
                            CmdTargetName = (inScopeTarget.Name ?? string.Empty).Trim().ToLowerInvariant(),
                            CmdState = sevenStateCmd,
                            WaitId = waitId,
                            WaitState = sevenStateCmd == 5
                                ? 0
                                : sevenStateCmd + 1,
                        };
                    }
                    arrays.Warnings.Add(
                        $"[Recipe] '{state.Name}': Seven_State condition Name '{cond.Name}' did not " +
                        "match Pick/Place/Home; falling back to settled WAIT. Extend " +
                        "MapSevenStateCommandFromConditionName if a new state name keyword is needed.");
                }
                else
                {
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

            // SETTLED WAIT: same 4->0 actuator remap (AtHomeEnd=4 transient -> AtHomeInit=0); sensors untouched (raw number).
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

        // MergeFeedRing trigger: true iff an M262 Feed process has a motion state whose leave-condition targets a Process with a mergeable sentinel.
        public static bool FeedRingMergeNeeded(IReadOnlyList<VueOneComponent> allComponents)
        {
            foreach (var proc in allComponents)
            {
                if (!string.Equals(proc.Type, "Process", StringComparison.OrdinalIgnoreCase)) continue;
                // Feed process runs on M262 or RevPi (byte-identical for M262 — nothing guesses RevPi there).
                if (CodeGen.Translation.HcfSymbolIndex.NameBasedPlcGuess(proc.Name)
                    is not (CodeGen.Translation.PlcAssignment.M262 or CodeGen.Translation.PlcAssignment.RevPi)) continue;
                foreach (var st in proc.States)
                {
                    if (!StateNameSuggestsMotion(st.Name)) continue;
                    foreach (var tr in st.Transitions)
                        foreach (var c in tr.Conditions)
                        {
                            if (string.IsNullOrEmpty(c.ComponentID)) continue;
                            var target = LookupComponent(c.ComponentID, allComponents);
                            if (target != null &&
                                string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                                ProcessSentinelId(target.Name) is int)
                                return true;
                        }
                }
            }
            return false;
        }

        // MergeFeedRing: if this state's leave-condition is a cross-process gate, return a pure WAIT on that process's state_table slot; else null.
        private static StateClassification? TryCrossProcessReadinessGate(
            VueOneState state, IReadOnlyList<VueOneComponent> allComponents, RecipeArrays arrays)
        {
            if (!CodeGen.Configuration.MapperConfig.MergeFeedRing) return null;
            var tr = state.Transitions.FirstOrDefault();
            foreach (var c in tr?.Conditions ?? Enumerable.Empty<VueOneCondition>())
            {
                var tgt = LookupComponent(c.ComponentID, allComponents);
                if (tgt != null &&
                    string.Equals(tgt.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                    ProcessIdOf(tgt.Name) is int rpid)
                {
                    // Process-at-Initialisation = idle: its state_table slot carries CMD states, so wait on the idle sentinel, not ResolveStateNumber.
                    var refState = tgt.States.FirstOrDefault(s =>
                        string.Equals((s.StateID ?? string.Empty).Trim(),
                            (c.ID ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
                    int rstate = refState?.InitialState == true
                        ? CodeGen.Configuration.MapperConfig.ProcessIdleSentinelState
                        : ResolveStateNumber(c, tgt, arrays);
                    arrays.Warnings.Add(
                        $"[Recipe] MergeFeedRing readiness gate: '{state.Name}' WAITs on " +
                        $"{tgt.Name} state {rstate} (id {rpid}) before proceeding.");
                    var rg = new StateClassification { Kind = ClassKind.MotionPair };
                    rg.Rows.Add(new RecipeRow { StepType = 2, WaitId = rpid, WaitState = rstate });
                    rg.RowCount = rg.Rows.Count;
                    return rg;
                }
            }
            return null;
        }

        // Process NAME -> its state_table process_id (config-backed).
        private static int? ProcessIdOf(string? name)
        {
            var n = (name ?? string.Empty).Trim();
            if (string.Equals(n, "Assembly_Station", StringComparison.OrdinalIgnoreCase))
                return CodeGen.Configuration.MapperConfig.AssemblyProcessId;
            if (string.Equals(n, "Disassembly", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "Disassembly_Station", StringComparison.OrdinalIgnoreCase))
                return CodeGen.Configuration.MapperConfig.DisassemblyProcessId;
            if (string.Equals(n, "Feed_Station", StringComparison.OrdinalIgnoreCase))
                return CodeGen.Configuration.MapperConfig.FeedStationProcessId;
            return null;
        }

        // Resolve the condition gating the transition INTO destStateName to a (id, runtime state) WAIT; Five_State actuator State 4 remaps 4->0. False if none.
        public static bool TryGetTransitionGate(VueOneComponent process, string destStateName,
            string componentName, RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents,
            out int waitId, out int waitState)
        {
            waitId = -1; waitState = 0;
            var dest = process.States.FirstOrDefault(s =>
                string.Equals((s.Name ?? string.Empty).Trim(), destStateName,
                    StringComparison.OrdinalIgnoreCase));
            if (dest == null) return false;
            var destId = (dest.StateID ?? string.Empty).Trim();
            foreach (var st in process.States)
                foreach (var tr in st.Transitions)
                {
                    if (!string.Equals((tr.DestinationStateID ?? string.Empty).Trim(), destId,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var c in tr.Conditions)
                    {
                        var tgt = LookupComponent(c.ComponentID, allComponents);
                        if (tgt == null ||
                            !string.Equals((tgt.Name ?? string.Empty).Trim(), componentName,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (!arrays.ComponentRegistry.TryGetValue((c.ComponentID ?? string.Empty).Trim(),
                                out var id)) continue;
                        int raw = ResolveStateNumber(c, tgt, arrays);
                        waitId = id;
                        waitState = (raw == 4 &&
                            string.Equals(tgt.Type, "Actuator", StringComparison.OrdinalIgnoreCase) &&
                            !CodeGen.Mapping.TemplateMap.IsBranchedSevenState(tgt)) ? 0 : raw;
                        return true;
                    }
                }
            return false;
        }

        // Resolve the gate on a process's INITIAL transition to a (id, runtime state) WAIT (MergeFeedRing material gate). False if no in-scope non-Process gate.
        public static bool TryGetInitialConditionGate(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents,
            out int waitId, out int waitState)
        {
            waitId = -1; waitState = 0;
            var initial = process.States.FirstOrDefault(s => s.InitialState);
            if (initial == null) return false;
            foreach (var tr in initial.Transitions)
                foreach (var cond in tr.Conditions)
                {
                    if (string.IsNullOrEmpty(cond.ComponentID)) continue;
                    if (!arrays.ComponentRegistry.TryGetValue(cond.ComponentID.Trim(), out var id)) continue;
                    var target = LookupComponent(cond.ComponentID, allComponents);
                    if (target == null ||
                        string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase)) continue;
                    waitId = id;
                    waitState = ResolveStateNumber(cond, target, arrays);
                    return true;
                }
            return false;
        }

        // MergeFeedRing Transfer-hold: resolve the twin's Assembly-start gate on the holding transport as a
        // fresh RISING EDGE. The gate's transport is the Initialisation condition's component (Control.xml,
        // e.g. Transfer). Both states are read from that actuator's OWN Control.xml states (NOT the
        // condition's literal state, which varies between twin revisions -- Advancing vs Advanced):
        //   settledState   = its settled/holding position (Advanced/AtWork) -- HELD all cycle, so a bare
        //                    level wait on it is stale-prone.
        //   advancingState = the transient it passes through to REACH that position (Advancing/ToWork) --
        //                    only present while it is freshly moving, so it can never be a stale held level.
        // The caller emits WAIT(advancingState) -> WAIT(settledState): the fresh advance-start guarantees the
        // following settled wait is a FRESH landing, so Bearing_PnP cannot pick on a stale held Advanced.
        public static bool TryGetInitialConditionEdgeGate(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents,
            out int waitId, out int advancingState, out int settledState)
        {
            waitId = -1; advancingState = 1; settledState = 2;
            var initial = process.States.FirstOrDefault(s => s.InitialState);
            if (initial == null) return false;
            foreach (var tr in initial.Transitions)
                foreach (var cond in tr.Conditions)
                {
                    if (string.IsNullOrEmpty(cond.ComponentID)) continue;
                    if (!arrays.ComponentRegistry.TryGetValue(cond.ComponentID.Trim(), out var id)) continue;
                    var target = LookupComponent(cond.ComponentID, allComponents);
                    if (target == null ||
                        string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase)) continue;
                    waitId = id;
                    settledState = ResolveStateByNameFamily(target, AdvancedStateNames, 2);
                    advancingState = ResolveStateByNameFamily(target, AdvancingStateNames,
                        settledState > 0 ? settledState - 1 : settledState);
                    return true;
                }
            return false;
        }

        private static readonly string[] HomeStateNames = { "ReturnedHome", "AtHomeInit", "Home" };
        private static readonly string[] AdvancedStateNames = { "Advanced", "AtWork", "AtWork1" };
        // The transient a transport passes through to reach its Advanced/AtWork position (ToWork move).
        private static readonly string[] AdvancingStateNames = { "Advancing", "ToWork", "Extending" };

        private static int ResolveStateByNameFamily(VueOneComponent comp, string[] names, int fallback)
        {
            foreach (var want in names)
                foreach (var s in comp.States)
                    if (string.Equals((s.Name ?? string.Empty).Trim(), want, StringComparison.OrdinalIgnoreCase))
                        return s.StateNumber;
            return fallback;
        }

        // state_table process_id for a cross-process WAIT; only sentinel-publishing processes map, else null.
        private static int? ProcessSentinelId(string? processName)
        {
            var n = (processName ?? string.Empty).Trim();
            if (string.Equals(n, "Disassembly", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "Disassembly_Station", StringComparison.OrdinalIgnoreCase))
                return CodeGen.Configuration.MapperConfig.DisassemblyProcessId;
            return null;
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

        // Longest in-scope component whose name prefixes the state name (TransferAdvancing -> Transfer).
        private static VueOneComponent? ResolveInScopeMotionActuator(string? stateName,
            IReadOnlyList<VueOneComponent> allComponents, Dictionary<string, int> scopedRegistry)
        {
            var n = (stateName ?? string.Empty).Trim().ToLowerInvariant();
            if (n.Length == 0) return null;
            VueOneComponent? best = null;
            foreach (var c in allComponents)
            {
                var cn = (c.Name ?? string.Empty).Trim();
                if (cn.Length == 0 || string.IsNullOrEmpty(c.ComponentID)) continue;
                if (!scopedRegistry.ContainsKey(c.ComponentID.Trim())) continue;
                if (!n.StartsWith(cn.ToLowerInvariant(), StringComparison.Ordinal)) continue;
                if (best == null || cn.Length > (best.Name ?? string.Empty).Trim().Length)
                    best = c;
            }
            return best;
        }

        private static StateClassification ClassifyConditionDrivenState(
            VueOneState state,
            IReadOnlyList<VueOneCondition> inScopeConds,
            IReadOnlyList<VueOneComponent> allComponents,
            RecipeArrays arrays,
            Dictionary<string, int> scopedRegistry,
            IReadOnlyCollection<string>? testActuatorAllowlist)
        {
            var result = new StateClassification { Kind = ClassKind.MotionPair };

            foreach (var cond in inScopeConds)
            {
                var target = LookupComponent(cond.ComponentID, allComponents);
                if (target == null)
                {
                    arrays.Warnings.Add(
                        $"State '{state.Name}': in-scope ComponentID '{cond.ComponentID}' could not " +
                        "be resolved to a VueOneComponent in allComponents; condition skipped.");
                    continue;
                }

                // Robot_Task_CAT: one StartTask runs the whole program (DI10 completion), so emit the task handshake ONCE and fold the other robot states in — never generic-walk the robot.
                if (CodeGen.Mapping.TemplateMap.IsRobotTaskArm(target))
                {
                    if (!arrays.RobotTaskEmitted)
                    {
                        arrays.RobotTaskEmitted = true;
                        string robotName = (target.Name ?? string.Empty).Trim().ToLowerInvariant();
                        int robotId = CodeGen.Configuration.MapperConfig.RobotActuatorId;
                        AddCmdWaitRows(result.Rows, robotName, 1, robotId, 2);
                        AddCmdWaitRows(result.Rows, robotName, 2, robotId, 0);
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': robot '{target.Name}' emitted as the Robot_Task " +
                            $"handshake (cmd1 start -> WAIT id{robotId}=2 done -> cmd2 reset -> WAIT id{robotId}=0 " +
                            "ready); generic robot walk suppressed (Robot_Task_CAT, real DI10 completion).");
                    }
                    else
                    {
                        arrays.SkippedConditions.Add(
                            $"state '{state.Name}': robot '{target.Name}' folded into the single Robot_Task " +
                            "handshake; this robot state contributes no generic row.");
                    }
                    continue;
                }

                if (testActuatorAllowlist != null
                    && !string.Equals(target.Type, "Process", StringComparison.OrdinalIgnoreCase)
                    && !testActuatorAllowlist.Contains((target.Name ?? string.Empty).Trim().ToLowerInvariant()))
                {
                    arrays.SkippedConditions.Add(
                        $"state '{state.Name}': condition on actuator '{target.Name}' PARKED by " +
                        "RecipeTestActuatorAllowlist; row segment removed.");
                    continue;
                }

                int waitId = scopedRegistry[cond.ComponentID.Trim()];
                int waitState = ResolveStateNumber(cond, target, arrays);

                if (!string.Equals(target.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsFiveStateCommandable(target))
                    {
                        int gripperCmd = IsGripperTarget(target)
                            ? MapGripperCommandFromStepName(state.Name)
                            : -1;
                        int cmdState = gripperCmd >= 0
                            ? gripperCmd
                            : (waitState == 1 || waitState == 2) ? 1 : 3;
                        int settledWait = cmdState == 1 ? 2 : 0;

                        if (gripperCmd >= 0)
                            arrays.Warnings.Add(
                                $"[Recipe] '{state.Name}': gripper '{target.Name}' command derived " +
                                $"from the STEP name -> {(cmdState == 1 ? "CLOSE/grip" : "OPEN/release")} " +
                                $"(cmd {cmdState}, wait {settledWait}). Condition '{cond.Name}' supplies " +
                                "the target component/state; the step name supplies gripper direction.");
                        else if (settledWait != waitState)
                            arrays.Warnings.Add(
                                $"[Recipe] '{state.Name}': condition-driven CMD on '{target.Name}' " +
                                $"-> {(cmdState == 1 ? "toWork" : "toHome")} (cmd {cmdState}); WAIT " +
                                $"remapped Control.xml State_Number {waitState} -> runtime ECC state {settledWait}.");

                        AddCmdWaitRows(result.Rows,
                            (target.Name ?? string.Empty).Trim().ToLowerInvariant(),
                            cmdState, waitId, settledWait);
                        continue;
                    }

                    if (IsSevenStateCommandable(target))
                    {
                        int sevenStateCmd = MapSevenStateCommandFromConditionName(cond.Name);
                        if (sevenStateCmd >= 0)
                        {
                            int sevenWait = sevenStateCmd == 5
                                ? 0
                                : sevenStateCmd + 1;
                            arrays.Warnings.Add(
                                $"[Recipe] '{state.Name}': Seven_State CMD on '{target.Name}' " +
                                $"-> state_val={sevenStateCmd}, wait {sevenWait}; derived from " +
                                $"condition Name '{cond.Name}'.");
                            AddCmdWaitRows(result.Rows,
                                (target.Name ?? string.Empty).Trim().ToLowerInvariant(),
                                sevenStateCmd, waitId, sevenWait);
                            continue;
                        }

                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': Seven_State condition Name '{cond.Name}' did not " +
                            "match Pick/Place/Home; emitted a settled WAIT segment.");
                    }
                    else
                    {
                        arrays.Warnings.Add(
                            $"[Recipe] '{state.Name}': condition target '{target.Name}' (type " +
                            $"{target.Type}, {target.States.Count} states) is not commandable; " +
                            "emitted a settled WAIT segment.");
                    }
                }

                result.Rows.Add(new RecipeRow
                {
                    StepType = 2,
                    CmdTargetName = string.Empty,
                    CmdState = 0,
                    WaitId = waitId,
                    WaitState = RemapSettledWaitState(state, target, waitState, arrays),
                });
            }

            result.RowCount = result.Rows.Count;
            if (result.RowCount == 0)
            {
                arrays.SkippedConditions.Add(
                    $"state '{state.Name}': every in-scope condition was filtered out; row dropped.");
                result.Kind = ClassKind.Skipped;
            }
            return result;
        }

        private static void AddCmdWaitRows(List<RecipeRow> rows,
            string targetName, int cmdState, int waitId, int waitState)
        {
            rows.Add(new RecipeRow
            {
                StepType = 1,
                CmdTargetName = targetName,
                CmdState = cmdState,
                WaitId = 0,
                WaitState = 0,
            });
            rows.Add(new RecipeRow
            {
                StepType = 2,
                CmdTargetName = string.Empty,
                CmdState = 0,
                WaitId = waitId,
                WaitState = waitState,
            });
        }

        private static int RemapSettledWaitState(VueOneState state,
            VueOneComponent target, int waitState, RecipeArrays arrays)
        {
            if (string.Equals(target.Type, "Actuator", StringComparison.OrdinalIgnoreCase) &&
                waitState == 4)
            {
                arrays.Warnings.Add(
                    $"[Recipe] '{state.Name}': SETTLED WAIT on '{target.Name}' remapped " +
                    "Control.xml State_Number 4 -> runtime AtHomeInit 0.");
                return 0;
            }
            return waitState;
        }

        internal static int ResolveDestRow(VueOneState state,
            int srcIndex,
            List<StateClassification> classifications,
            Dictionary<string, int> stateIdToFallForwardRow,
            int finalEndIndex)
        {
            var trans = state.Transitions.FirstOrDefault();
            // DestinationStateID via the fall-forward map (a skipped dest already points at the next surviving row / final END).
            if (trans != null &&
                !string.IsNullOrEmpty(trans.DestinationStateID) &&
                stateIdToFallForwardRow.TryGetValue(trans.DestinationStateID, out var dst))
            {
                return dst;
            }
            // Empty/out-of-Process dest: walk declaration-order siblings for the next surviving row, else finalEndIndex.
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

        // Transient CMD state from the actuator's OWN State whose Name matches the source-state motion token; falls back to (settledWaitState-1) (Five_State convention).
        private static int ResolveTransientCmdState(string? sourceStateName,
            VueOneComponent actuator, int settledWaitState, RecipeArrays arrays)
        {
            string sourceLower = (sourceStateName ?? string.Empty).Trim().ToLowerInvariant();
            if (sourceLower.Length > 0)
            {
                // Direct: actuator State whose Name is a substring of the source state Name.
                foreach (var s in actuator.States)
                {
                    var sn = (s.Name ?? string.Empty).Trim();
                    if (sn.Length == 0) continue;
                    if (sourceLower.Contains(sn.ToLowerInvariant(), StringComparison.Ordinal))
                        return s.StateNumber;
                }
                // Else map a motion verb to an actuator State by canonical synonyms.
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

            int fallback = System.Math.Max(settledWaitState - 1, 0);
            arrays.Warnings.Add(
                $"State '{sourceStateName}': could not match a transient State name on " +
                $"actuator '{actuator.Name}'; falling back to (settledWaitState-1)={fallback}. " +
                "If this actuator's CAT does not follow the Five_State convention " +
                "(transient = settled - 1), add explicit synonyms to MotionVerbToStateNames.");
            return fallback;
        }

        // Motion-verb -> candidate actuator State Names (soft fallback when direct substring match fails).
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
