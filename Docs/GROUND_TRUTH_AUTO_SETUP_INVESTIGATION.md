# Ground truth `DemonstratorWithHMI_23_07_26` — why Auto and Setup work, and what Mapper lacks

**Date:** 2026-07-27 · **Investigation and design only. Nothing modified, generated, built or deployed.**
Labels: **VERIFIED** (read from the artefact) · **INFERRED** · **UNVERIFIED**.

---

## 1. Exact ground-truth location and contents

**VERIFIED.** The solution exists on this machine **only as an archive**:

```
C:\Users\alper\OneDrive\Masaüstü\WMG\Jyotsna\DemonstratorWithHMI_23_07_26.sln.zip
5,668,402 bytes · 2026-07-26 09:51
```

No extracted copy exists anywhere else (searched `…\WMG` to depth 3, `C:\`, `C:\EAE Projects`).
For inspection it was extracted read-only to
`…\scratchpad\gt2` (868 files). **The archive itself was not modified.**

Solution file inside: `DemonstratorWithHMI.sln`. Projects (**VERIFIED**, from the `.sln`):

| Project | Path |
|---|---|
| IEC61499 | `IEC61499\IEC61499.dfbproj` |
| HMI | `HMI\HMI.csproj` |
| AssetLinkData | `AssetLinkData\AssetLinkData.assetLinkDataproj` |
| TopologyManager | `Topology\TopologyManager.topologyproj` |
| ATVHMIPlugin | `ATVHMIPlugin\ATVHMIPlugin.atvdisplayproj` |
| AvevaOMI | `AvevaOMI\AvevaOMI.omiproj` |
| HwConfiguration | `HwConfiguration\HwConfiguration.hwconfigproj` |

HMI canvases (**VERIFIED**): `MainScreen`, `ActuatorsScreen`, `SetupScreen`, `SetupScreen2`,
`ManualScreen`, `ManualScreen2`, `StartCanvas_2`.
Faceplates: `Area_CAT_sDefault`; `Five_State_Actuator_CAT_{sDefault,sFault,sInterlock,sSetup}`;
`Process1_Generic_{sAutomatic,sManual}`.

**Note on identity — INFERRED:** this is a *Mapper-generated* clamp project with Jyotsna's
Auto/Manual/Setup overlay on top, not a hand-built solution. Its `RecipeStep` datatype and recipe
structure are the Mapper's. That matters: her work is an **overlay on our architecture**, which is
why adopting it is tractable.

---

## 2. Why Auto works — the causal chain, proven

Two things are true in the ground truth and **false** in Mapper output. Both are necessary.

### 2.1 The engine's ECC is gated on Mode and CycleType

**VERIFIED** — `gt2\IEC61499\ProcessRuntime_Generic_v1.fbt`, 8 states (`:94-113`), 14 transitions
(`:116-151`):

```
:116  START → INIT            [INIT]
:119  INIT  → IDLE1           [1]
:122  IDLE1 → ISSUE_CMD       [((Mode=1 AND CycleType=1) OR (Mode=2 AND MREQ)) AND CurrentStepType=1]
:125  IDLE1 → WAIT_STEP       [((Mode=1 AND CycleType=1) OR (Mode=2 AND MREQ)) AND CurrentStepType=2]
:128  IDLE1 → END             [((Mode=1 AND CycleType=1) OR (Mode=2 AND MREQ)) AND CurrentStepType=9]
:131  ISSUE_CMD → ADVANCE     [(Mode=1) AND (CycleType=1)]
:134  ISSUE_CMD → MANUAL_COMPLETE [Mode=2]
:137  WAIT_STEP → ADVANCE     [WaitSatisfied AND (Mode=1)]
:140  WAIT_STEP → MANUAL_COMPLETE [WaitSatisfied AND (Mode=2)]
:143  WAIT_STEP → WAIT_STEP   [state_change]
:144  MANUAL_COMPLETE → ADVANCE [NSREQ AND (Mode=2)]
:147  ADVANCE → IDLE1         [CurrentStepType <> 9]
:150  END → END               [1]
:151  ADVANCE → END           [CurrentStepType = 9]
```

### 2.2 `Process1_Generic` actually delivers Mode and CycleType to the engine

**VERIFIED** — `gt2\IEC61499\Process1_Generic\Process1_Generic.fbt`:

```
:312  stationAdptr_in.ModeCMD      → ProcessEngine.Mode        (data)
:313  stationAdptr_in.CycleTypeCMD → ProcessEngine.CycleType    (data)
:148  stationAdptr_in.MCTRL        → ProcessEngine.MREQ         (event — samples Mode)
:112  stationAdptr_in.CTCTRL       → ProcessEngine.CTREQ        (event — samples CycleType)
:177  IThis.MREQO                  → ProcessEngine.MREQ         (HMI Execute Step)
:213  IThis.NSREQO                 → ProcessEngine.NSREQ        (HMI Next Step)
```

**The Mapper's equivalent file has ZERO connections whose destination begins `ProcessEngine.`**
other than INIT, Recipe, state_table and state_change — Mode and CycleType are pass-through only
(`C:\Demonstrator\Demonstrator\IEC61499\Process1_Generic\Process1_Generic.fbt:261,289`).

**Causal chain, stated plainly:**
`Auto button → MCNF(Mode=1)` and `Run button → CTCNF(CycleType=1)` → Station_Core adopts them →
`StationAdaptrOUT.ModeCMD/CycleTypeCMD` → `Process1_Generic:312,313` → `ProcessEngine.Mode/CycleType`
→ the `IDLE1` gate at `:122` opens → the recipe dispatches.

In Mapper output the chain breaks at the **second-to-last** link, and the last link would not
matter anyway because the ECC never tests the value.

---

## 3. One-cycle semantics — proven, not assumed

**VERIFIED.** `END` has exactly one outgoing transition: `:150 END → END [1]`. It is a **terminal
self-loop**. Once `:151 ADVANCE → END [CurrentStepType = 9]` fires, the engine can never leave.

**VERIFIED.** `ProcessEngine.INIT` has exactly one driver:
`Process1_Generic.fbt:107 ProcessHandler.INITO → ProcessEngine.INIT`, itself driven only by the
boundary `:106 INIT → ProcessHandler.INIT`. **There is no reset, re-init or restart event reaching
the engine from the HMI, the Station or anywhere else.**

**Therefore:**

- *How does it start one Auto cycle?* Mode=1 **and** CycleType=1 together open the `IDLE1` gate.
- *What stops it after that cycle?* `ADVANCE → END` on `CurrentStepType = 9`, then `END → END`.
- *Can it start a second cycle?* **No.** Not by Start, not by Reset — only by re-INIT, i.e. a
  controller restart or redeploy. This is not a tuning matter; it is structural.

That is exactly the reported behaviour: **Auto works for one cycle.**

---

## 4. Two defects in the ground-truth ECC — do not copy it verbatim

**VERIFIED by exhaustive enumeration of the exits from each state.**

**Defect GT-1 — Stop during a command strands the engine.** `ISSUE_CMD` has two exits: `:131`
(`Mode=1 AND CycleType=1`) and `:134` (`Mode=2`). If the operator sets CycleType=0 while Mode=1 and
the engine is in `ISSUE_CMD`, **neither exit is satisfiable** and the engine is stuck there
permanently. Stop is safe only at a step boundary.

**Defect GT-2 — switching to Setup mid-cycle strands the engine.** No transition from `IDLE1`,
`ISSUE_CMD`, `WAIT_STEP` or `MANUAL_COMPLETE` admits `Mode = 3`. Selecting Setup while a cycle is
in progress leaves the process permanently blocked, requiring a re-INIT.

**Consequence for the design:** the ground truth proves the *shape* of the gate is right, but its
state machine is not complete. A Mapper implementation must add explicit exits for
stop-during-command and mode-change-mid-cycle. **This is the main reason not to adopt her ECC
unchanged.**

---

## 5. What "Stop" actually means in the ground truth

**VERIFIED from the transitions:** setting `CycleType = 0` makes the `IDLE1` gate false, so the
engine holds at `IDLE1` **after the current step completes**. That is *command inhibition at a step
boundary* — a genuine, if coarse, operational stop.

It is **not** a pause with resume-in-place (there is no resume-from-mid-step concept), **not** a
stop-at-end-of-cycle (END is terminal anyway), and **emphatically not** a safety stop.

---

## 6. Why Setup works

**VERIFIED already in our own architecture** (the ground truth uses the same CAT):

```
IThis.cmd_event → ActuatorCore.setup_event
IThis.toWork    → ActuatorCore.toWorkSetup
IThis.toHome    → ActuatorCore.toHomeSetup
```

and the core arc
`(setup_event AND mode = 3 AND toWorkSetup) AND toWorkInterlock = FALSE`
(`FiveStateActuator.fbt:95`; the Home counterpart at `:107`).

So Setup works because (a) the faceplate `sSetup` declares `cmd_event` in its contract, (b) the CAT
routes it to the core, and (c) the core admits it when `mode = 3` — **with the interlock term
present on the Setup arcs.**

**Mapper has (b) and (c) already.** What it lacks is (a) — I stripped the command contract when
making the HMI monitoring-only — and a way to put a Station into mode 3.

⚠ **But mode 3 cannot reach every actuator in Mapper output.** The BX1 resource has **no Station or
Area FB and zero `stationAdptr` connections**, so the three cover actuators can receive neither
`ModeCMD` nor fault reset. Setup would work for M262/M580 actuators and **not** for the covers.
Fixing that is a wiring change, not an HMI change.

---

## 7. The handshake question

**VERIFIED.** Mapper's `ProcessCompiler.EmitHandoff`
(`C:\VueOneMapper\CodeGen\CodeGen\Planning\Recipes\ProcessCompiler.cs:567-609`) emits an **arming
wait on the producer's entry phase followed by a completion wait**, for every cross-process
boundary, derived from the twin. Observed in the generated recipe (`…000000.syslay:99`,
`Disassembly`):

```
[ 0] WAIT Assembly[17]==1  ARM        [58] WAIT Feed[10]==0  ARM
[ 1] WAIT Assembly[17]==7  COMPLETE   [59] WAIT Feed[10]==1  COMPLETE → NextStep=0
```

It also **fails generation** rather than emit a completion wait on `State_Number 0` that a
never-written slot could satisfy (`:602-606`).

**Both implementations enforce the same invariant** — a producer must be seen entering a cycle
before its completion counts. Hers is hand-added at one boundary; Mapper's is derived at all of
them. **No recipe change is warranted.**

**Residual risk — INFERRED, unproven.** The arming wait is a level compare re-evaluated on
`state_change`; a consumer that reaches the arming step *after* the producer has left its entry
phase misses the transient. Mapper's cyclic recipes make this reachable; the ground truth's
one-cycle design makes it largely moot there. This is the top verification item.

---

## 8. Capability matrix

| Capability | Ground truth | Mapper |
|---|---|---|
| Auto mode request | works (MCNF, Mode=1) | **PRESENT BUT UNCONNECTED** — chain stops at the adapter |
| Accepted Auto mode reported | Station_Core → status | **DISPLAY ONLY** |
| Cycle Start | works (CTCNF, CycleType=1) | **MISSING** |
| Idle/armed state before Start | `IDLE1` gate | **MISSING** — runs on INIT |
| One-cycle execution | works | **SEMANTICALLY DIFFERENT** — runs unconditionally |
| Repeated cycles | **impossible** (END terminal) | works (derived back-edge) |
| Cycle completion reported | `ProcessComplete` | **MISSING** |
| Stop | step-boundary inhibition (**with GT-1 strand defect**) | **MISSING** |
| Stop at end of cycle | n/a (END terminal) | **MISSING** |
| Pause / Resume | **MISSING** in both | **MISSING** |
| Setup mode request | works | **PRESENT BUT UNUSED** (contract stripped) |
| Individual Setup motion | works, interlocked | **PRESENT BUT UNUSED**; **BX1 UNCONNECTED** |
| Fault reset | present M262/M580 | present M262/M580; **BX1 MISSING** |
| Process reset | **MISSING** in both | **MISSING** |
| Home All | mode 9 exists | mode 9 exists — **UNSAFE**, one arc bypasses interlocks |
| Command acknowledgement | **MISSING** in both | **MISSING** |
| Rejection / timeout | **MISSING** in both | **MISSING** |
| Area → Station1 | yes | yes |
| Area → Station2 | **UNVERIFIED** | **MISSING** — no `Station2.AreaAdptrIN` source |
| BX1 / cover distribution | **UNVERIFIED** | **MISSING** — no CaS chain on BX1 |
| Interlock enforcement (Setup arcs) | yes | yes |
| Interlock enforcement (mode 9 arc) | **no** | **no** — `FiveStateActuator.fbt:110` has no interlock term |

---

## 9. The safe HMI boundary

**A — achievable by Mapper/HMI generation alone, no control change:**
capability discovery and generated screens; display names; accurate bindings to signals that
already exist; recipe-step labels and operator text generated at HMI-generation time; interlock
explanation from the RuleTable; disabling unavailable controls **by contract**; presenting
requested-vs-accepted mode where the signal exists.

**B — control-contract changes needing separate approval:**
wiring `ModeCMD`/`CycleTypeCMD` into the engine; gating the ECC; Cycle Start and an armed state;
stop-at-cycle-end; Pause/Resume; **BX1 CaS connectivity**; `Station2.AreaAdptrIN`; acknowledgement
and rejection events; process reset; safe initialisation; sequenced Home All that does not use the
interlock-bypassing mode-9 arc.

**C — prohibited:** direct output writes; bypassing CAT interlocks; persistent motion writes;
treating an HMI write as proof of acceptance; calling Setup "Manual"; presenting Stop as an
emergency stop; using mode 9 as it currently stands.

---

## 10. Smallest change for equivalent one-cycle Auto

**Corrected after the parallel traces — it is four edits, not three.** Two Template Library files:

1. **`Process1_Generic.fbt`** — add the two data connections
   `stationAdptr_in.ModeCMD → ProcessEngine.Mode` and
   `stationAdptr_in.CycleTypeCMD → ProcessEngine.CycleType` (GT `:312,313`).
   The matching **event** connections (`MCTRL→MREQ`, `CTCTRL→CTREQ`) already exist in Mapper output.
2. **`ProcessRuntime_Generic_v1.fbt` — change `Mode` and `CycleType` `InitialValue` from `1` to `0`.**
   **VERIFIED:** Mapper declares both `InitialValue="1"`, GT declares both `InitialValue="0"`.
   **Without this the gate is defeated** — a never-commanded engine still reads Auto+Run and
   free-runs. This edit is what makes "held" the safe default.
3. **`ProcessRuntime_Generic_v1.fbt`** — AND the three `IDLE1` exits and `ISSUE_CMD→ADVANCE` with the
   mode/cycle guard, **plus exits for GT-1 and GT-2** (stop-during-command, mode-change-mid-cycle).
4. **One-cycle park** — either `END→END` in the ECC, or emit the recipe END row with `NextStep`
   pointing at itself. **GT does both** (`ECC :150` *and* self-pointing END rows in its syslay).

`Process1_Generic_HMI.fbt` is additionally needed to *command* it from the HMI, but is not required
for the control semantics.

Mapper's recipe generation needs **no change**: the `RecipeStep` struct is identical and the
handshake is already correct.

### What needs no work at all — VERIFIED byte-identity

`Station.fbt`, `Station_Core.fbt`, `CaSAdptr.adp`, `Station_CAT.fbt` and `Area_CAT.fbt` are
**byte-identical** between the ground truth and Mapper output. The entire Area→Station→CaS-chain
mode, cycle-type and fault-reset distribution layer is already correct and proven.
(`Area.fbt` differs by 6 lines; `Five_State_Actuator_CAT.fbt` differs substantially — GT 735 vs
Mapper 625 lines.)

**The actuator core's Setup arcs are identical in both** — `AtHomeInit→ToWork` and `AtWork→ToHome`
carry the same `(setup_event AND mode = 3 AND toXSetup) … AND toXInterlock = FALSE` disjunct. Mapper
adds two extra `INIT→ToWork/ToHome` arcs that GT lacks. **So the Setup control path is genuinely
equivalent, and my §6 conclusion stands.**

**The mode-9 no-interlock arc `ToWork→ToHome [mode = 9]` exists in BOTH** — inherited from the shared
template, not introduced by Mapper.

⚠ Adopting the gate changes existing behaviour: today's recipes run on INIT and cycle repeatedly.
After gating they will not run until Auto+Run is commanded. That is the intent, but it is a
behavioural change on a working rig and needs the byte-identical gate plus a rig run.

---

## 11. Phased plan (not implemented)

| Phase | Content | Layer | Rollback |
|---|---|---|---|
| 0 | Fresh clamp baseline; confirm which build is deployed | generation | n/a |
| 1 | Read-only telemetry + capability manifest + recipe labels | HMI/Mapper | delete generated HMI |
| 2 | Setup for M262/M580, config-gated off by default | HMI/Mapper | flag |
| 3 | BX1 CaS connectivity so covers can receive mode/reset | **wiring** | revert wiring |
| 4 | Wire Mode/CycleType + gate ECC (with GT-1/GT-2 fixed) | **FB** | Template Library revert |
| 5 | Command acknowledgement channel | **FB** | revert |
| 6 | Separate Fault Reset / Process Reset / Initialise / Home All | **FB + generation** | revert |
| 7 | Repeated-cycle and handshake race testing | verification | n/a |

---

## 12. Direct answers

- **How does the ground truth start one Auto cycle?** Mode=1 and CycleType=1 together satisfy the
  `IDLE1` gate (`:122`).
- **What stops it after that cycle?** `ADVANCE → END [CurrentStepType=9]` (`:151`) then the terminal
  `END → END [1]` (`:150`).
- **Can it start a second cycle?** **No** — `ProcessEngine.INIT` has a single driver from the
  boundary INIT (`:106-107`); only a controller re-INIT restarts it.
- **How does Setup work?** `IThis.cmd_event → ActuatorCore.setup_event` with the core arc
  `setup_event AND mode = 3 AND toWorkSetup`, interlock term intact.
- **How are commands acknowledged?** **They are not.** Neither project has any acknowledgement,
  rejection or timeout channel. Feedback is state and sensor values only.
- **What do its Reset/Init controls do?** Fault reset reaches Station faults on M262/M580. There is
  no process reset and no engine re-init. **Detail pending the parallel trace.**
- **Can the current Mapper HMI start one controlled Auto cycle?** **No** — no command contract, and
  the engine would ignore it.
- **Can it safely stop or continue?** **No.** Stop has no meaning today.
- **Can it operate Setup?** Not as generated. The control-side path exists for M262/M580; the HMI
  contract was removed and BX1 is unreachable.
- **What is missing?** Execution gating, Cycle Start, an armed state, Stop semantics,
  acknowledgement, process reset, safe Home All, BX1 command connectivity.
- **What can HMI generation alone add?** Everything in boundary **A** above.
- **Smallest approved control change?** The three files in §10.

---

## 13. Deployment verdict

**Do not deploy anything from this investigation.** Nothing was modified, generated, built or
deployed. The current Mapper HMI remains monitoring-only, which is the correct posture until the
control contract exists.

Nothing here makes the machine safe. Auto, Setup, Reset and RuleTable interlocks are operational
functions. The protective stop remains the certified hardware safety system, and the rig's
documented clamp damage and swivel collision risk are unaffected.

---

## 14. Refinements from the parallel traces — all independently verified

**14.1 The ground truth's Setup is itself only partially reachable.** Its `SetupScreen` places ten
`sSetup` widgets, but only **Feeder, Checker and Transfer** can actually receive mode 3. The other
seven (Clamp, Bearing_Gripper, Shaft_Hr, Shaft_Vr, Shaft_Gripper, CoverPNP_Hr, CoverPnp_Gripper) sit
on components that cannot: Station2 has no Area link and no Station faceplate, and BX1 has no CaS
chain at all. **INFERRED, safety-relevant.** So "Setup works" is true for the Feed station and
overstated for the rest — the ground truth is not a complete Setup implementation.

**14.2 Mapper is *ahead* of the ground truth on Station reach.** Mapper already **places** Station
faceplates bound to `Station1_HMI` (`A1D4FECE65B60B9D`) and `Station2_HMI` (`6B8F46ACF1FE46AF`) on
its PlantOverview screens, and `Station_CAT.fbt` → `Station.fbt` (`MCNF→Core.SMREQ`,
`Mode→Core.StationModeCmd`) is byte-identical to GT and fully wired. **Giving those faceplates
buttons would close the exact Station2 gap the ground truth never closed** — without any Area link.
That is an HMI-generation change, not a control change.

**14.3 Homing is mostly de-energisation, not a driven stroke. VERIFIED and safety-relevant.**
`OutputToHome` appears in **0** `.hcf` channel bindings in the ground truth and **1** in Mapper
output, against **11** and **9** for `OutputToWork`. Almost no actuator has a physical home coil:
"To Home" releases the work coil and the actuator returns by spring or air. **Any Home All design
that assumes a commanded home stroke is wrong for most of this rig**, and a de-energised return
cannot be interlock-gated the way a driven stroke can.

**14.4 The ground truth confirms the enable pattern worth copying.** Its `sSetup` buttons are
disabled at construction and enabled only when the **accepted** `ModeCMD` reads 3 — a permissive
confirmation that Setup was accepted upstream, rather than an optimistic local enable. That is the
right generic pattern for Mapper, and it needs no control change.

---

## 15. Sections awaiting further widget-level detail

The following will be extended with widget-level detail: the Setup control-by-control trace
(§6), the Reset/Initialise/Home inventory (§12), the chronological event-by-event Auto table, and
the per-actuator mode-distribution table for the ground truth. The conclusions above do not depend
on them — each is proven from the artefacts cited.
