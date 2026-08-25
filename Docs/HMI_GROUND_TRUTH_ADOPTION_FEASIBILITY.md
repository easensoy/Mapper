# Adopting `DemonstratorWithHMI_23_07_26` as HMI ground truth — feasibility

**Date:** 2026-07-26 · **Investigation only — nothing implemented, nothing modified.**
**Question:** can we reproduce Jyotsna's HMI design and its Auto + Setup mode capability,
data-driven from the Mapper, **without touching the existing logic** (MQTT, FBs, wiring, IO)?

Labels: **VERIFIED** (read from the artefact) · **INFERRED** · **UNVERIFIED**.

---

## 1. Direct answer

**Partly — and the split is clean and, I think, better news than you expect.**

| Capability | HMI-only? | Why |
|---|---|---|
| Her screen layout and look | **YES** | her screens are systematic symbol families — the shape the generator already produces |
| **Setup mode (per-actuator jog)** | **YES** | already fully wired in our control logic; only the HMI half is missing |
| **Mode selection** (Auto / Manual / Setup / Initial Position) | **YES** | complete chain HMI → Station → CaS → every actuator already exists |
| **Initial Position (mode 9 → all home)** | **YES** | the actuator core arc already exists |
| Fault reset | **YES** | wired |
| **Auto mode that actually starts and stops the recipe** | **NO** | our process engine ignores Mode and CycleType; hers does not |
| **Manual step-through** (Execute Step / Next Step) | **NO** | needs her engine state plus 11 new ports on the process HMI FB |

**The single blocking difference is the process engine ECC — not the Mapper, and not data.**

---

## 2. The decisive difference: her engine is not our engine

**VERIFIED.** Both are called `ProcessRuntime_Generic_v1.fbt`, but the ECCs differ fundamentally.

**Hers** — every step-advancing transition is gated on Mode and CycleType, and there is an extra
`MANUAL_COMPLETE` state:

```
IDLE1 → ISSUE_CMD      [((Mode=1 AND CycleType=1) OR (Mode=2 AND MREQ)) AND CurrentStepType=1]
IDLE1 → WAIT_STEP      [((Mode=1 AND CycleType=1) OR (Mode=2 AND MREQ)) AND CurrentStepType=2]
IDLE1 → END            [((Mode=1 AND CycleType=1) OR (Mode=2 AND MREQ)) AND CurrentStepType=9]
ISSUE_CMD → ADVANCE    [Mode=1 AND CycleType=1]
ISSUE_CMD → MANUAL_COMPLETE [Mode=2]
WAIT_STEP → ADVANCE    [WaitSatisfied AND Mode=1]
WAIT_STEP → MANUAL_COMPLETE [WaitSatisfied AND Mode=2]
MANUAL_COMPLETE → ADVANCE   [NSREQ AND Mode=2]
ADVANCE → IDLE1        [CurrentStepType <> 9]
ADVANCE → END          [CurrentStepType = 9]
END → END              [1]
```

**Ours** — no state reads Mode or CycleType, and `END → ADVANCE [1]` loops unconditionally:

```
IDLE1 → ISSUE_CMD [CurrentStepType = 1]      ISSUE_CMD → ADVANCE [1]
IDLE1 → WAIT_STEP [CurrentStepType = 2]      WAIT_STEP → IDLE1 [WaitSatisfied]
IDLE1 → END       [CurrentStepType = 9]      END → ADVANCE [1]
```

**Consequences:** in her solution STOP genuinely stops (`IDLE1` cannot leave without `CycleType=1`)
and Manual advances one step per operator press. In ours neither is possible, whatever the HMI does.

---

## 3. What our existing logic already supports — no change required

This is the part worth acting on. **VERIFIED** by tracing the live `C:\Demonstrator` artefacts.

**The mode command path is complete, end to end:**

```
HMI Station tile  MCNF / Mode
  → StationHMIAdptrIN.MCNF → Core.SMREQ                 (Station.fbt)
  → StationHMIAdptrIN.Mode → Core.StationModeCmd
  → Core.StationMode → StationAdaptrOUT.ModeCMD          (into the CaS chain)
  → actuator CAT  stationAdptr_in.ModeCMD → ActuatorCore.mode
```

**The actuator core already honours every mode** (`FiveStateActuator.fbt`):

```
ToWork: (pst_event AND mode=1 AND state_val=1)
     OR (pst_event AND mode=2 AND state_val=1)
     OR (setup_event AND mode=3 AND toWorkSetup)      AND toWorkInterlock = FALSE
ToHome: … OR mode = 9 OR (setup_event AND mode=3 AND toHomeSetup)
```

**And the Setup jog path is already wired inside the CAT:**

```
IThis.cmd_event → ActuatorCore.setup_event
IThis.toWork    → ActuatorCore.toWorkSetup
IThis.toHome    → ActuatorCore.toHomeSetup
```

So **Setup mode, mode selection and Initial-Position homing are available today with zero FB,
wiring, IO or MQTT change.** The only thing missing is the HMI half — which is exactly the half I
removed when making the HMI read-only. `Five_State_Actuator_CAT_sSetup` still ships in
`Template Library\HMI\Faceplates`.

---

## 4. What genuinely needs control-logic change

**VERIFIED** — her `Process1_Generic_sAutomatic` / `sManual` contracts require **14 inputs and 2
event outputs**. Our `Process1_Generic_HMI.fbt` declares 6 inputs and **no** event outputs.

Missing inputs: `ModeCMD`, `CurrentStep`, `CurrentStepType`, `WaitSatisfied`, `ManualStepReady`,
`ManualStepComplete`, `ProcessComplete`, `ProcessName`, `OperatorInstruction`.
Missing outputs: `MREQO` → `ManualExecuteStep`, `NSREQO` → `ManualNextStep`.

Her engine also emits five outputs ours lacks: `ManualStepReady`, `ManualStepComplete`,
`ProcessComplete`, `OperatorInstruction`, plus the ECC state above.

Adopting them means changing `ProcessRuntime_Generic_v1.fbt`, `Process1_Generic_HMI.fbt` and
`Process1_Generic.fbt` — i.e. **FBs and wiring**. There is no HMI-side substitute.

---

## 5. The data-driven question — the part that is nearly free

This is the key finding, and it reframes the work.

**VERIFIED — her `RecipeStep` datatype is identical to ours:**

```
StepType : INT | CmdTargetName : STRING[150] | CmdStateArr : INT
Wait1Id : INT  | Wait1State : INT            | NextStep : INT
```

**VERIFIED — `OperatorInstruction` is not per-step recipe text.** Her engine generates it as
mode-phase literals:

```
'Select Manual mode and load the process'   'Manual controls inactive'
'Ready - press Execute Step'                'Waiting for equipment feedback'
'Process complete'                          'Waiting'
```

So the PPTX slide-9 idea of a `StepText` column **does not exist even in the ground truth**. She
solved it by telling the operator what to do *in the current mode*, which needs no recipe data.

**Therefore the Mapper needs to pass no new sequencing data at all.** The recipe it already
generates is exactly what her engine consumes — same struct, same fields, same ordering. The
actuator name her HMI shows comes from the engine's existing `cmd_target_name` output.

**One small wiring gap** (**VERIFIED**): in our `Process1_Generic.fbt`, `ANString.REQ` is driven by
`ProcessEngine.SCNF` but **`ANString.IN1` has no source** — it should be
`ProcessEngine.cmd_target_name`. That single missing wire is why our process tile cannot name the
actuator it is commanding. It is one connection inside a CAT, not new data.

---

## 6. Her screens are exactly the shape the generator already produces

**VERIFIED** — placements per screen:

| Screen | Contents |
|---|---|
| MainScreen | 1 × `Area_CAT.sDefault`, 3 × `Five_State.sDefault`, 3 × `Process1_Generic.sAutomatic` |
| ActuatorsScreen | 8 × `Five_State.sDefault` |
| SetupScreen (+ page 2) | 6 × `Five_State.sSetup` |
| ManualScreen (+ page 2) | 2 × `Process1_Generic.sManual` |

Every screen is a homogeneous, tag-bound family — hand-placed, but systematically. That is
precisely what `HmiPlanner` already emits (it produced a `SetupScreen` family from `sSetup` before
I gated it off, and it paginates the same way). **INFERRED: reproducing her layout is a
symbol-selection and screen-naming change in the planner, not new machinery.**

---

## 7. Options, with honest cost and risk

**Option A — Setup mode only, strictly HMI-only.** Restore the `sSetup` family and a Station/Area
mode selector behind a config flag. Gives working per-actuator jog, mode selection and
Initial-Position homing. **No FB, wiring, IO, MQTT or topology change.** Delivers slide 7 of the
deck in full.
⚠ It makes the HMI command-capable again, and the rig is flagged unsafe (clamp damage, swivel
collision risk). Setup jog moves real actuators. Interlocks still apply — they are operational, not
a safety function.

**Option B — adopt her three process templates as ground truth.** Replace
`ProcessRuntime_Generic_v1.fbt`, `Process1_Generic.fbt` and `Process1_Generic_HMI.fbt` in
`Template Library`, then generate her Automatic + Manual screens. This is precedented: on
2026-07-19 three Template Library zips were replaced with rig-proven `.fbt` files for the CycleReady
handoff. **The Mapper's recipe generation needs no change** (§5).
⚠ This *is* touching FBs. It changes how every process executes, so it needs the full
byte-identical gate plus a rig run. It also makes STOP genuinely stop — which is the point.

**Option C — do nothing to the engine; display only.** Keep the monitoring HMI, add her visual
layout. Honest, but Auto/Setup remain absent.

---

## 8. Recommendation

**Option A first, gated off by default.** It is genuinely HMI-only, it is the half of your request
that costs nothing in control risk, and it can be verified without a rig run.

**Then Option B as a separate, explicitly-approved change** — because it is the only way to get real
Auto/Stop and Manual, and because adopting her engine would also fix the defect I reported
separately: our STOP is inert precisely because our ECC lacks her Mode/CycleType gate.

**What I'd need from you before either:**
1. Confirmation that a Template Library process-FB swap counts as "touching logic" in your rule, or
   is the intended path (it is FBs, but it is template adoption, not hand-editing the rig).
2. Whether Setup jog may be enabled at all while the clamp and swivel hazards stand.

---

## 9. Not established

- **UNVERIFIED:** that her engine is drop-in compatible with our generated recipes and ring. The
  `RecipeStep` struct matches, but nothing was built or run.
- **UNVERIFIED:** whether her `Process1_Generic.fbt` internal wiring differs beyond the new ports.
- **UNVERIFIED:** anything about runtime behaviour — no build, no deploy, no rig.
- Her `sAutomatic`/`sManual` faceplate graphics were not compared in detail to ours.

**Nothing in this document makes the machine safe to operate.** Option A deliberately re-enables a
command path to real actuators; Option B changes how every recipe executes. Both need their own
safety judgement, and the protective stop remains the certified hardware safety system.
