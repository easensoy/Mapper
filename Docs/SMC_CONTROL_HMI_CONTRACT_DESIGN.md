# SMC rig — next Mapper-generated HMI/control contract

**Date:** 2026-07-27 · **Investigation and design only. Nothing was modified, generated or deployed.**
Labels: **VERIFIED** (read from the artefact) · **INFERRED** · **UNVERIFIED**.

Trees referenced, because the prompt's labels are inverted against reality (**VERIFIED**):

| Tag | Path | What it actually is |
|---|---|---|
| DEMO-1 | `C:\Demonstrator\Demonstrator` | **no-clamp**, five-state `Bearing_PnP`, 34 FBs, regenerated 07-27 15:27 |
| DEMO-2 | `C:\Demonstrator 2\Demonstrator` | **with Clamp**, seven-state centre-home, 35 FBs, stale (07-07) |
| GT-MANUAL | `DemonstratorWithHMI_23_07_26.sln.zip` | Mapper clamp project + Jyotsna's manual overlay |
| GT-ECC | `SMC_Rig_Expo_withClamp_RevPi…`, `SMC_Rig_Expo_20260112…` | hand-authored Schneider ECC, **no recipe engine** |

There is currently **no fresh clamp build**. Every clamp conclusion below comes from DEMO-2 or GT.

---

## 1. Executive verdict

**The rig has no operating-mode system today.** Not a weak one — none. Five independent breaks, each
verified:

1. The recipe engine's ECC never reads Mode or CycleType.
2. `Process1_Generic` never even wires Mode/CycleType to the engine.
3. No mode is broadcast at power-up; `Station_Core.INIT` sets `StationMode := 0` and emits no MCNF.
4. The BX1 covers sit on **no CaS chain at all** — mode and fault-reset cannot reach them.
5. `Area.AreaAdptrOUT` reaches `Station1` only; `Station2` (M580) has no Area source.

**Every process begins executing its recipe on INIT with no operator action.** There is no run
gate, no armed state, no enable.

**I must correct myself.** Last turn I told you the mode chain was "complete, end to end… → every
actuator" and that Setup mode was therefore achievable with zero control change. That is true for
M262 and M580 actuators and **false for the three BX1 cover actuators**, which are unreachable.
Setup mode is *partially* achievable HMI-only; the covers need a wiring change.

**The good news is the handshake.** Jyotsna's "missing handshake / two extra steps" is already
implemented by the Mapper, derived and universal. No recipe change is needed there.

---

## 2. Current Auto behaviour

**VERIFIED** — `ProcessRuntime_Generic_v1.fbt:97-124`, 11 ECC transitions:

```
START→INIT [INIT]            INIT→IDLE1 [1]
IDLE1→ISSUE_CMD [CurrentStepType=1]   IDLE1→WAIT_STEP [=2]   IDLE1→END [=9]
ISSUE_CMD→ADVANCE [1]        WAIT_STEP→IDLE1 [WaitSatisfied]
WAIT_STEP→WAIT_HOLD [NOT WaitSatisfied]   WAIT_HOLD→WAIT_STEP [state_change]
ADVANCE→IDLE1 [1]            END→ADVANCE [1]
```

Not one condition references `Mode`, `CycleType`, `MREQ` or `CTREQ`. `INIT→IDLE1` is unconditional.

**VERIFIED** — `Process1_Generic.fbt:261,289`: `stationAdptr_in.ModeCMD → stationAdptr_out.ModeCMD`
and the same for `CycleTypeCMD`. Both are **pass-through only**; there is no connection whose
destination is `ProcessEngine.Mode` or `ProcessEngine.CycleType`. **So even a Mode-gated ECC would
read the unconnected InputVar default.** This matters enormously for §7 — swapping in Jyotsna's
engine alone would silently do nothing.

**VERIFIED** — `Station_Core.fbt:199-200`: the INIT algorithm sets `StationMode := 0`, and MCNF is
emitted only from the AUTO/MANUAL/SETUP/HOME states. No mode is published at boot. **INFERRED**:
each actuator core therefore holds `mode` at its declared `InitialValue`, i.e. Automatic.

**Answers to your questions:**
- *What starts each process?* The INIT chain, nothing else.
- *Do all processes begin immediately on INIT?* Yes.
- *What prevents commands before Auto/Run?* **Nothing.**
- *Is behaviour deterministic across M262/M580/BX1?* No — see §3.

---

## 3. Why repeated cycles work today

**VERIFIED** — two separate mechanisms, routinely conflated:

1. *(2026-08-25: `MapperConfig.EnableCyclicRestart` is deleted — it was always true, so the engine's END→ADVANCE loop-back is unconditional. The point below still holds of that loop-back.)* It is consumed **only** by
   `ProcessRuntimeTemplatePatcher.PatchProcessRuntimeEccDeadEnd`, which writes `END→ADVANCE` (true)
   or `END→END` (false).
2. The **actual** loop-back is a compile-time back-edge: `ProcessCompiler.Serialize` sets the last
   row of each state to `NextStep = DestRow(stateId)`, which follows the twin's transition chain
   and resolves to row 0 where the chain closes on the Initialisation state.

So the recipe index is reset by **generated data**, not by the engine. The engine merely follows
`NextStep`.

### The handshake that makes it safe — and the race that remains

**VERIFIED** — `ProcessCompiler.EmitHandoff` (`ProcessCompiler.cs:567-609`):

```csharp
var entry = EntryState(peer);   int done = refState.StateNumber;
bool armHere = armed.Add(peer.Name?.Trim() ?? string.Empty);
if (armHere) {
    if (entry != null && entry.StateNumber != done)
        rows.Add(Row.Wait(peerId, entry.StateNumber, state.StateID));   // ARM
    else if (done == 0) throw Fail(...);                                // refuses ambiguous slot 0
}
rows.Add(Row.Wait(peerId, done, state.StateID));                        // COMPLETE
```

Emitted rows, DEMO-1 `Disassembly` (process_id 18), syslay line 99:

```
[ 0] WAIT Assembly[17] == 1   next=1     ARM  (Assembly entry phase)
[ 1] WAIT Assembly[17] == 7   next=2     COMPLETE
[58] WAIT Feed[10]     == 0   next=59    ARM
[59] WAIT Feed[10]     == 1   next=0     COMPLETE, then loop to row 0
```

**⚠ Latent race — INFERRED, not yet disproved.** `check_wait` is a *level* compare
(`state_table[Wait1Id].state = Wait1State`) re-evaluated on `state_change`. The arming wait is only
satisfied if the consumer is **sitting on that step while the producer's slot still holds the entry
value**. A consumer that arrives late misses the transient and must wait a full further cycle — and
where the producer is itself waiting on that consumer, that is a mutual wait. This is exactly the
failure recorded for `_vc` on 2026-07-26 (one clean cycle, then stop).

**Verdict on your four options: "Mapper already implements the same handshake differently."** Do
not add Jyotsna's two steps. Do prove the arming transient cannot be missed (§15).

---

## 4-5. Jyotsna's handshake, and the comparison

**VERIFIED** — her two steps enforce the same invariant the Mapper derives: a producer must be seen
*entering* a cycle before its *completion* counts. Hers is hand-added at one boundary; the Mapper's
is derived at every boundary from the twin's transition chain, and it **fails generation** rather
than emitting a completion wait on `State_Number 0` that a never-written slot could satisfy.

Sequence, DEMO-1 (**VERIFIED** from the recipes):

| # | Event | Mapper |
|---|---|---|
| 1 | final Feed action | Feed advances Transfer, then holds |
| 2 | Transfer handoff | ownership derived per-actuator; Feed advances, Disassembly returns |
| 3 | Assembly completion | announces phase 7 |
| 4 | Disassembly arming | `WAIT Assembly==1` then `WAIT Assembly==7` |
| 5 | Disassembly completion | announces phases 6 / 11 |
| 6 | Feed rearming | `WAIT Feed==0` then `WAIT Feed==1` (from Disassembly), `NextStep=0` |
| 7 | next-cycle first command | row 0 of each recipe |

**GT-ECC contains no recipe engine at all** — it is a hand-authored ECC machine. It is evidence
about *mode semantics*, not about recipe handshaking, and must not be treated as authoritative for
the latter.

---

## 6. Cross-process and interlock audit

**VERIFIED** — `CommonInterlockEvaluator` consumes `RuleTable : ARRAY OF InterlockRule`
(`FromState, ToState, SourceID, BlockedState`) plus `Target`, and raises per-direction interlock
flags consumed by the core's transition conditions.

**Three structural findings dominate:**

1. **`ToWork → ToHome [mode = 9]` carries no interlock term at all** (`FiveStateActuator.fbt:110`).
   Compare `:107`, which ends `AND toHomeInterlock = FALSE`. **A mode-9 command can reverse an
   actuator mid-travel with interlocks bypassed.** Directly fatal to any naive "Home All". **VERIFIED,
   safety-relevant.**
2. **mode 1 and mode 2 are identical at every actuator** — both accept `pst_event AND state_val`.
   "Manual" today is Auto with a different number.
3. **The seven-state centre-home core's mode arcs carry no event qualifier** — pure level tests
   re-evaluated on any arriving event (the documented 07-25(k) asymmetry). **race-risk.**

**Reachability gaps (VERIFIED):**
- BX1 covers: **zero** `stationAdptr` connections in the BX1 sysres; no Station or Area FB on BX1.
  No mode, no fault reset, no Setup.
- `Station2.AreaAdptrIN` has no source anywhere; only `Area.AreaAdptrOUT → Station1.AreaAdptrIN`
  exists. Area-level commands reach M262 only.
- `Robot_Task_CAT` has no mode, no reset, no CaS adapter.

**Cross-controller staleness:** a rule can only block if the source component's state has *reached*
this evaluator's `state_table`. A lost or late cross-controller report leaves a stale slot and the
rule silently permits the movement. **INFERRED, race-risk** — untested.

⚠ RuleTable interlocks are **operational**. They are not a certified safety function and must never
be described as making motion safe.

---

## 7. Auto lifecycle design

**Required semantics** (all currently absent):

- **`Mode = Auto` must not start motion.** Selecting Auto only permits a subsequent Run.
- **Run** starts/resumes when prerequisites hold. Prerequisites are model-derived: no active fault,
  every owned actuator at a known state, no other process mid-command on a shared actuator.
- **Stop, three distinct behaviours, never conflated:**
  - *Command inhibition* — issue no new commands; in-flight movement completes.
  - *Pause* — hold at the current step, retain index and pending wait, resume from there.
  - *Stop at end of cycle* — run to the recipe's cycle boundary, then hold before row 0.
- **Scope:** Feed, Assembly and Disassembly stop **together**, at the Station level, because they
  share actuators. A per-process stop would leave a shared actuator owned by a running peer.
- **Retention:** recipe index and pending wait are retained on Pause and Stop-at-end; cleared only
  by an explicit Process Reset.
- **Restart/comms loss:** on controller restart every recipe index returns to 0. Because there is
  no state reconciliation today, a restart mid-cycle leaves software and plant disagreeing —
  Recovery (§10) is what must close that.

**Minimum control-side change to make Auto real (three items, all in `Template Library`):**

1. `ProcessRuntime_Generic_v1.fbt` — gate every step-advancing transition on Mode and CycleType.
2. `Process1_Generic.fbt` — **wire `stationAdptr_in.ModeCMD`/`CycleTypeCMD` to the engine.** Without
   this, item 1 is inert.
3. `Process1_Generic_HMI.fbt` — expose mode/cycle status and the Run/Stop command channel.

**This is the smallest change that makes Stop mean anything.**

---

## 8. Setup design

**Achievable HMI-only for M262 and M580 actuators; not for BX1 covers.**

**VERIFIED already present:** `IThis.cmd_event → ActuatorCore.setup_event`,
`IThis.toWork/toHome → toWorkSetup/toHomeSetup`, and the core arc
`(setup_event AND mode = 3 AND toWorkSetup) AND toWorkInterlock = FALSE`. Interlocks are honoured on
the Setup arcs (unlike mode 9).

**Required semantics:**
- Commands only via the actuator CAT's `IThis`; never a physical output.
- Accepted only when that actuator's `mode = 3`. Mode is per-Station, so Setup is a Station-scoped
  privilege, not a global one.
- Auto processes on the same Station must be inhibited first — with today's engine they cannot be,
  which is why Setup and Auto cannot currently coexist safely.
- RuleTable interlocks remain active (they do).
- Acknowledgement, timeout, fault and release: **none exist today.** A jog command has no ack path
  at all (§11).

**Blocking gap:** the three BX1 covers cannot enter Setup. Either add a Station FB on BX1 and wire
the CaS chain, or accept that Setup excludes the covers and say so on the screen.

---

## 9. Manual-mode design

**Smallest generic contract** (from GT-MANUAL, reduced):

| Direction | Port | Necessary because |
|---|---|---|
| HMI→PLC | `ManualExecuteStep` (`MREQO`) | executes the current step |
| HMI→PLC | `ManualNextStep` (`NSREQO`) | advances after completion |
| PLC→HMI | `ManualStepReady` | enables Execute |
| PLC→HMI | `ManualStepComplete` | enables Next |
| PLC→HMI | `CurrentStep`, `CurrentStepType` | shows position and command-vs-wait |
| PLC→HMI | `OperatorInstruction` | tells the operator what to do |
| PLC→HMI | `ProcessComplete` | end of recipe |
| PLC→HMI | `ModeCMD` | enables the manual controls at all |

`ProcessName` is redundant — the Mapper already knows it and can render it statically.
`WaitSatisfied` is diagnostic, not required for the handshake.

**Manual must execute the generated recipe.** The HMI supplies only *permission to advance*; step
content stays in `Recipe`. Command steps complete when the commanded actuator reports its target;
wait steps complete when the wait is satisfied — the same `check_wait`, gated on an operator event
instead of running free.

**Engine ECC changes required (identified, not implemented):** an extra `MANUAL_COMPLETE` state and
Mode/`MREQ`/`NSREQ` terms on the dispatch and advance transitions, per GT-MANUAL.

`OperatorInstruction` is **not** per-step recipe text: in GT-MANUAL the engine emits mode-phase
literals (`'Ready - press Execute Step'`, `'Waiting for equipment feedback'`). **So no recipe
datatype change is needed for Manual** — see §12.

---

## 10. Reset / Initialise / Home / Recovery

**These are five different operations and must never share one button.**

| Operation | Meaning | Exists today? |
|---|---|---|
| **Fault Reset** | clears accepted faults only, no motion | **partly** — `FRCNF → Faults.Local_Fault_Reset` on M262/M580; **not** on BX1 |
| **Process Reset** | recipe index and lifecycle state to a defined start | **no** |
| **Initialise** | re-establish software state, re-publish current feedback, no motion | **no group mechanism** |
| **Home All** | controlled motion to generated home states | mode 9 exists — **and is not interlock-safe** |
| **Recovery** | reconcile software state with actual sensors after interruption | **no** |

**Recommendation — and it is a firm one: "Reset All" must not include motion.** Ship *Fault Reset*,
*Process Reset* and *Initialise* as non-motion actions, and *Home All* as a separate, confirmed,
prerequisite-gated action.

**Home All cannot be built on mode 9 as it stands**, because `ToWork→ToHome [mode = 9]` bypasses
the interlock. Homing must be expressed as ordinary per-actuator Home commands through `IThis`,
which do honour interlocks, sequenced by the Mapper.

**Ordering — Clamp first or last?** **UNVERIFIED, and I will not guess.** DEMO-2 is three weeks
stale and DEMO-1 has no Clamp, so no current RuleTable evidence exists for clamp-vs-transfer and
clamp-vs-bearing interactions. This needs a fresh clamp generation before any homing order is
proposed. Deriving the order from the RuleTable graph (home an actuator only once nothing that
blocks it is still at a blocking state) is the right generic approach.

---

## 11. Proposed `IThis` contracts

Separate concerns; today they are mixed and there is **no acknowledgement channel anywhere** —
every command is fire-and-forget.

| Group | Area | Station | Process | 5-state | 7-state | Sensor | Robot |
|---|---|---|---|---|---|---|---|
| Runtime status | mode, cycle, fault | +local/LL mode, cycle, fault | step, type, texts | state, sensors, coils, fault | + work1/2 | state | task state |
| **Commands** | mode | mode, cycle, **run/stop** | **execute/next step** | **home/work, setup** | **home/work1/work2** | — | — |
| **Command ack** | **new** | **new** | **new** | **new** | **new** | — | — |
| Capability | — | — | manual-capable | home/work | home/work1/work2 | — | — |
| Fault | status | status + **reset** | — | active, code | active, code | — | — |
| Interlock | — | — | — | **blocked reason: rule, source, blocked state** | same | — | — |
| Identity | area name | station name | process name | display name | display name | name | name |

**Capability must be generated**, not assumed: the Mapper knows from the twin whether a component
has Work1/Work2 or a single Work, and whether a process is manual-capable. The lowercase ring/MQTT
key stays separate from the operator display name — the key is protocol (`updateComponentState`
compares it case-sensitively) and must never be shown.

---

## 12. Recipe presentation contract

**Recommendation: an HMI-only presentation manifest. Do not extend `RecipeStep`.**

**VERIFIED** — GT-MANUAL's `RecipeStep` is identical to ours (`StepType, CmdTargetName, CmdStateArr,
Wait1Id, Wait1State, NextStep`), and its `OperatorInstruction` is engine-generated mode text, not
per-step data. So runtime manual execution does **not** require extra recipe fields.

Everything the operator needs — station, index, type, target display name, target state number and
generated state name, wait source display name, expected state, instruction, advance capability —
is derivable at HMI generation time from the same model that produced the recipe. Generate it
beside the HMI; leave the controller datatype alone.

---

## 13. HMI screen and capability model

Screens: Plant Overview · Feed · Assembly · Disassembly · Auto lifecycle · Setup · Manual stepping ·
Faults/Interlocks · Reset/Recovery. All membership derived from recipe ownership (proven working).

Security: monitoring open; Auto commands role-controlled; Setup requires Setup mode **and**
authorisation; Reset/Home All require confirmation and prerequisite checks. **Disabled must mean
disabled by contract** — a control the operator cannot use must not exist as a live handler, which
is exactly the discipline the current read-only generator already enforces and should be retained
as the default posture.

---

## 14. Implementation phases, smallest risk first

| Phase | Content | Touches control logic? |
|---|---|---|
| 0 | Fresh clamp generation, so clamp interlocks can be audited at all | no |
| 1 | HMI presentation manifest + capability model + display names | no |
| 2 | Setup mode for M262/M580 actuators, config-gated off | **no** |
| 3 | Wire BX1 CaS chain so covers can receive mode/fault reset | wiring |
| 4 | Wire Mode/CycleType into the engine + gate the ECC → real Auto/Run/Stop | engine |
| 5 | Command acknowledgement channel on `IThis` | FB interfaces |
| 6 | Process Reset, Initialise, Recovery (non-motion) | FB |
| 7 | Home All as sequenced per-actuator Home via `IThis` | generation |
| 8 | Manual mode | engine ECC |

---

## 15. Verification and acceptance

All four variants (clamp × {5-state, 7-state}, no-clamp × {5-state, 7-state}), in temp outputs, no rig.

First cycle; ten consecutive cycles; Feed-before-Disassembly and the reverse; **already-true wait
condition**; **delayed cross-controller state**; **lost/duplicate state event**; Stop during command;
Stop during wait; stop-at-end; resume; Reset with all home; Reset with one at work; Setup rejected by
interlock; Home All partial failure; controller restart mid-cycle.

**The single most important test: prove the arming transient (§3) cannot be missed** by a late
consumer. Until that is proven, repeated cycling is empirical, not guaranteed.

---

## 16. Risks and unanswered questions

1. **The arming-transient race** — INFERRED, unproven either way.
2. **No fresh clamp build** — clamp interlock ordering, and therefore Home All, cannot be designed yet.
3. **BX1 covers unreachable** for mode and fault reset.
4. **mode 9 bypasses interlocks** on one arc.
5. **No command acknowledgement exists anywhere** — every proposed command needs one inventing.
6. **Cross-controller staleness** can silently defeat an interlock rule — untested.
7. Two workflow agents ran without the safety classifier; every load-bearing claim above was
   re-verified by me directly.

---

## 17. Closing distinction

These are eight different things and this report keeps them apart deliberately:

**operational Stop** (inhibit commands) · **pause/resume** · **stop at end of cycle** ·
**fault reset** (clears faults, no motion) · **process reset** (recipe state) ·
**software initialisation** (re-publish, no motion) · **physical homing** (motion) ·
**certified safety stop** (hardware, outside all of this).

Nothing here makes the machine safe to operate. Auto, Setup, Reset and RuleTable interlocks are
operational functions. The protective stop remains the certified hardware safety system, and the
rig's documented clamp damage and swivel collision risk are unaffected by any of it.
