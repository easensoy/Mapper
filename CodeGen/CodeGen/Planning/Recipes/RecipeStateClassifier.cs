using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;
using static CodeGen.Translation.Process.Recipes.RecipeCommandVocabulary;
using static CodeGen.Translation.Process.Recipes.RecipeComponentLookup;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;

namespace CodeGen.Translation.Process.Recipes
{
    // Pass-1 classification + Pass-2 row navigation: turns a Process's ordered VueOneStates into
    // per-state CMD/WAIT classifications the Generate emit loop serialises. Owns the classifier nested
    // types (ClassKind/StateClassification/RecipeRow) and the motion-verb tables.
    internal static class RecipeStateClassifier
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

        internal enum ClassKind
        {
            MotionPair,             // RowCount=2 (CMD then WAIT)
            SettledWait,            // RowCount=1 (single WAIT)
            End,                    // RowCount=0 (terminal state — emits NO in-loop END; routes to the single appended final END)
            Skipped,                // RowCount=0 — every condition out of scope; row dropped entirely
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
                // MergeFeedRing: an Initialisation transition gated on ANOTHER process at a specific
                // state (Feed_Station.Initialisation -> Assembly_Station/Initialisation) is a genuine
                // cross-station readiness/back-pressure gate, NOT a boot tautology -- preserve it as a
                // WAIT so Feed holds until the upstream is idle before pushing a part.
                var readiness = TryCrossProcessReadinessGate(state, allComponents, arrays);
                if (readiness != null) return readiness;
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

            // Find every condition whose ComponentID is in the scoped registry.
            // Conditions on out-of-scope components are recorded in SkippedConditions
            // and effectively dropped from the recipe. Older code stopped at the
            // first in-scope condition; that loses conjunctive joins such as
            // CoverPnp_Gripper/AtReleasePos AND CoverPNP_Hr/ReturnedHome, causing
            // auto-retract to guess the missing home command at the wrong point.
            var inScopeConds = new List<VueOneCondition>();
            // MergeFeedRing: the id of a dropped cross-PROCESS-state condition, resolved to a
            // WAIT on that process's sentinel slot. Stays -1 (no sentinel) unless the flag is on
            // and a dropped condition targets a Process with a known process_id.
            int crossProcessWaitId = -1;
            foreach (var c in allConds)
            {
                if (string.IsNullOrEmpty(c.ComponentID)) continue;
                if (scopedRegistry.ContainsKey(c.ComponentID.Trim()))
                {
                    inScopeConds.Add(c);
                    continue;
                }
                // Out-of-scope reference → record for the syslay top comment.
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
                // In-scope actuator motion gated only by a cross-process wait: keep the command
                // (derived from the state name) and settle on the actuator's own state.
                if (!commandFromCondition && allConds.Any() && StateNameSuggestsMotion(state.Name))
                {
                    var mover = ResolveInScopeMotionActuator(state.Name, allComponents, scopedRegistry);
                    if (mover != null)
                    {
                        int moverCmd = ResolveTransientCmdState(state.Name, mover, 2, arrays);
                        int moverWaitState = moverCmd == 1 ? 2 : moverCmd == 3 ? 0 : 2;
                        // MergeFeedRing: the state's only leave-condition is a cross-process gate
                        // (Feed's TransferAdvancing -> Disassembly/bearing_pnp_home_pos). Keep the
                        // motion (CMD mover -> WAIT mover) AND append a WAIT on the referenced
                        // process's sentinel so Feed holds until Disassembly reports it. Off (or no
                        // process condition) -> the plain MotionPair below (byte-identical).
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
                    // MergeFeedRing: a non-initial state gated only by a cross-process readiness
                    // condition keeps its WAIT (the Initialisation gate itself is handled above).
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
                // CmdState is the TRANSIENT state number the actuator's own ECC
                // recognises as a motion command (Five_State_Actuator_CAT triggers
                // AtHomeInit→ToWork on state_val=1 and AtWork→ToHome on state_val=3).
                // We DERIVE this number from the actuator's own State elements in
                // Control.xml — looking up the State whose Name matches the motion
                // direction in the source-state Name (e.g. "FeederAdvancing" -> the
                // actuator's "Advancing" state). This stays generic across CAT
                // templates (Seven_State_Actuator_CAT, future custom CATs).
                int cmdState = ResolveTransientCmdState(state.Name, inScopeTarget, waitState, arrays);

                // RUNTIME-SETTLED WAIT STATE: the engine compares Wait1State against the Five_State ECC's
                // published current_state_to_process (AtHomeInit=0/ToWork=1/AtWork=2/ToHome=3/AtHomeEnd=4),
                // NOT the raw Control.xml State_Number. The actuator only stably HOLDS AtWork=2 (after
                // ToWork) or AtHomeInit=0 (after ToHome); AtHomeEnd=4 is transient (overwritten 4->0 in
                // one run-to-stable tick, missed by check_wait), so map by command direction.
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
                    // GRIPPER GRIP/RELEASE DIRECTION from the Assembly STEP name, not the WAIT condition:
                    // a gripper's WAIT condition is the same (ReturnedHome, State_Number 0) for both grip
                    // and release, so the generic rule would open it twice. Grip/close step -> toWork
                    // (cmd 1, AtWork=2, closes the OutputToWork valve); release/open -> toHome (cmd 3,
                    // AtHomeInit=0). Non-gripper Five_State targets keep the condition-derived command.
                    // R-12 (coil direction unverified on the rig): if the gripper grips/releases the
                    // OPPOSITE way, swap Bearing_Gripper_Q / the sensor pair in M580SymbolBinder, NOT here.
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
                // Seven_State Centre-Home core: state_val is a target slot, settle publishes cmd+1
                // (1/Pick->AtWork1(2), 3/Place->AtWork2(4), 5/Home->AtHome(6)->AtHomeInit(0)). The
                // Control.xml State_Number doesn't line up, so pattern-match the WAIT condition Name
                // suffix (Pick/Place/Home) to the CMD slot + its settle value.
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
                            // Centre-Home settle value: pick 1->AtWork1(2), place 3->AtWork2(4),
                            // home 5->AtHomeInit(0) NOT AtHome(6) — 6 is transient (overwritten 6->0 in
                            // one run-to-stable tick, missed by check_wait), the Seven_State analogue of 4->0.
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

            // SETTLED WAIT runtime encoding (same 4->0 rule as MotionPair): remap an actuator settled
            // wait on the home-finished family (State_Number 4) to the resting AtHomeInit 0 — 4 is
            // transient and a wait reached after the return sees 0. Sensors untouched (publish the raw number).
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

        // Control.xml-driven merge trigger: true iff an M262 (Feed) process has a MOTION state whose
        // leave-condition targets a Process carrying a mergeable sentinel (no-clamp Transfer-hold). The
        // clamp model (Feed never waits on Disassembly) returns false -> Feed ring decoupled ->
        // byte-identical. Read by both the recipe classifier and the ring wiring.
        public static bool FeedRingMergeNeeded(IReadOnlyList<VueOneComponent> allComponents)
        {
            foreach (var proc in allComponents)
            {
                if (!string.Equals(proc.Type, "Process", StringComparison.OrdinalIgnoreCase)) continue;
                if (CodeGen.Translation.HcfSymbolIndex.NameBasedPlcGuess(proc.Name)
                    != CodeGen.Translation.PlcAssignment.M262) continue;
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

        // MergeFeedRing: if this state's leave-condition is a cross-process gate on ANOTHER process at
        // a specific state (a readiness/back-pressure gate), return a pure WAIT on that process's
        // state_table slot; else null. Feed_Station.Initialisation -> Assembly_Station/Initialisation
        // => WAIT(AssemblyProcessId, 0). Off (clamp model) -> null -> the state is handled as before.
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
                    // "Process at its Initialisation" = idle. Its state_table slot carries CMD states,
                    // not the design-time State_Number, so wait on the dedicated idle-sentinel value the
                    // process publishes at Initialisation (shared constant), NOT ResolveStateNumber.
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

        // Map a process NAME to its state_table process_id (config-backed). Resolves a cross-process
        // readiness gate (Feed_Station.Initialisation -> Assembly_Station/Initialisation) to a WAIT slot.
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

        // Resolve the Control.xml condition on componentName that gates the transition INTO destStateName
        // to a (state_table id, runtime state) WAIT (the twin owns the timing). The transient
        // home-finished State 4 remaps to the stable AtHomeInit 0 for a Five_State actuator. False if no
        // such gated condition exists.
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

        // Resolve the Control.xml gate on a process's INITIAL transition (Initial_State's leave-condition)
        // to a (state_table id, runtime state) WAIT — the twin's own material gate, used under
        // MergeFeedRing in place of the injected PartAtAssembly gate. False if the initial state has no
        // in-scope non-Process gated transition (caller keeps the injected HandoffPlanner gate).
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

        // Like TryGetInitialConditionGate but returns the gate component's HOME and ADVANCED settled
        // state numbers so the caller can reconstruct the twin's rising EDGE (WAIT(home) -> WAIT(advanced)).
        // The initial transition is gated on Transfer/Advancing (a *transition*); MergeFeedRing HOLDS the
        // Transfer advanced through Assembly+Disassembly, so a single WAIT(Advanced) is a held level that
        // would let Assembly re-fire while Disassembly is on the shared swivel — the edge makes Assembly
        // re-arm only after the Transfer has fully cycled. home/advanced read from the gate's OWN States.
        public static bool TryGetInitialConditionEdgeGate(VueOneComponent process,
            RecipeArrays arrays, IReadOnlyList<VueOneComponent> allComponents,
            out int waitId, out int homeState, out int advancedState)
        {
            waitId = -1; homeState = 0; advancedState = 2;
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
                    homeState = ResolveStateByNameFamily(target, HomeStateNames, 0);
                    advancedState = ResolveStateByNameFamily(target, AdvancedStateNames, 2);
                    return true;
                }
            return false;
        }

        // Runtime home / advanced settled-state name families for the Transfer-edge reconstruction.
        private static readonly string[] HomeStateNames = { "ReturnedHome", "AtHomeInit", "Home" };
        private static readonly string[] AdvancedStateNames = { "Advanced", "AtWork", "AtWork1" };

        private static int ResolveStateByNameFamily(VueOneComponent comp, string[] names, int fallback)
        {
            foreach (var want in names)
                foreach (var s in comp.States)
                    if (string.Equals((s.Name ?? string.Empty).Trim(), want, StringComparison.OrdinalIgnoreCase))
                        return s.StateNumber;
            return fallback;
        }

        // The state_table process_id a cross-process WAIT condition resolves to. Only processes that
        // publish a mergeable sentinel are mapped; null for any other name (caller falls back to the drop).
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

        // Longest in-scope component whose name prefixes the state name (TransferAdvancing -> Transfer);
        // lets a motion command survive when its only transition wait is an out-of-scope cross-process target.
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

                // Robot / UR3e is a TASK component (Robot_Task_CAT): one StartTask runs the whole
                // pick/place/home program and completion is the real DI10 Task_Complete bit, not a
                // per-state walk. Emit the proven task handshake ONCE (same sequence as the hardcoded
                // DisassemblyRecipe recipes.yml robot block) and fold the other robot states into it,
                // so the data-driven path never generic-walks the robot (wrong id / Partplace=4 it
                // never reports).
                if (CodeGen.Mapping.TemplateMap.IsRobotTaskArm(target))
                {
                    if (!arrays.RobotTaskEmitted)
                    {
                        arrays.RobotTaskEmitted = true;
                        string robotName = (target.Name ?? string.Empty).Trim().ToLowerInvariant();
                        int robotId = CodeGen.Configuration.MapperConfig.RobotActuatorId;
                        AddCmdWaitRows(result.Rows, robotName, 1, robotId, 2); // StartTask -> WAIT done
                        AddCmdWaitRows(result.Rows, robotName, 2, robotId, 0); // reset     -> WAIT ready
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

        // ----------------------------------------------------------------------
        // Pass-2 NextStep helpers
        // ----------------------------------------------------------------------

        internal static int ResolveDestRow(VueOneState state,
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

        // Resolve the transient (motion command) state number for a CMD row from the actuator's OWN State
        // elements (motion token in the source-state name -> matching actuator State's State_Number), NOT
        // by arithmetic on the wait state. Falls back to (settledWaitState-1) with a warning (Five_State
        // convention) if no match — extend MotionVerbToStateNames for non-Five_State CATs.
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
