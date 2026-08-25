# Jyotsna's newest HMI (20260730) — investigation and Mapper adoption design

**Date:** 2026-07-30 · **READ-ONLY investigation. Nothing modified, generated, built or deployed.**
`AGENTS.md`, `CLAUDE.md` scope clamps observed. Worktree untouched — no source file edited, all
existing untracked HMI-generator work preserved.

Labels: **VERIFIED** (read from the artefact, path:line given) · **INFERRED** · **UNKNOWN**.

---

## 1. Executive conclusion

Jyotsna's new solution is a **hand-composed 7-canvas HMI over a Mapper-generated control project**.
It contributes three genuinely valuable *ideas* — a Setup faceplate with mode-gated enablement, an
Automatic/Manual process faceplate pair, and an Area mode/cycle panel. It contributes **no reusable
screens**: every top-level Designer hardcodes opaque FB IDs for one specific model.

**Its interaction layer is substantially broken.** Of the eight Area command buttons, **one works
as labelled**. Three cycle buttons are dead. Fault Reset on the actuator faceplate is dead
end-to-end. Clicking anywhere on the Manual faceplate fires a real step-execute. And its screens
reach **15 of 25** drawable plant components, while Mapper already reaches **25 of 25**.

**Recommendation: adopt the presentation ideas, regenerate everything, adopt none of its commands
in Phase 1.** The single highest-value HMI-only change is not new commands at all — it is replacing
Mapper's type-based command stripper with a destination-based classifier, which restores the
read-only diagnostic navigation the current generator destroys.

---

## 2. Projects and exact revisions inspected

| Artefact | Path | Size / date |
|---|---|---|
| **New ground truth** | `C:\Users\alper\OneDrive\Masaüstü\WMG\Jyotsna\DemonstratorWithHMI_20260730-134636206.sln.zip` | 9,487,288 B · 2026-07-30 13:59 |
| extracted (read-only) | `…\scratchpad\gt3` | 966 files, `DemonstratorWithHMI.sln` |
| Previous GT (context only) | `…\DemonstratorWithHMI_23_07_26.sln.zip` | 5,668,402 B · 2026-07-26 |
| Mapper | `C:\VueOneMapper` | dirty worktree, untouched |
| Live generated | `C:\Demonstrator\Demonstrator` | syslay + HMI both **2026-07-30 17:59** |

**Live model identity (VERIFIED):** 35 FBs, **`Clamp` present**, `Bearing_PnP` =
`Seven_State_Actuator_Centre_Home_CAT`. This is the **clamp + seven-state** variant — i.e. a **fresh
clamp baseline now exists**, closing the gap flagged in the previous report.

---

## 3. What Jyotsna's new HMI actually is

**VERIFIED.** A native EAE/NxtControl canvas HMI. Symbols bind to instances through `TagName`
holding the syslay FB ID. **OPC UA is present but entirely disabled**: across all `.opcua.xml`,
`Enabled="true"` occurs **0** times and `Enabled="false"` **71** times. OPC UA is metadata, not the
data path. → **Codex lead 1 CONFIRMED.**

Seven top-level canvases, all hand-composed: `MainScreen`, `ActuatorsScreen`, `ManualScreen`,
`ManualScreen2`, `SetupScreen`, `SetupScreen2`, `StartCanvas_2`. → **lead 2 CONFIRMED.**

Only **three** CAT faceplate folders ship: `Area_CAT`, `Five_State_Actuator_CAT`,
`Process1_Generic`. There is no Station, Seven-State, Robot or Sensor faceplate at all.

---

## 4–5. Inventories

| Metric | Jyotsna 20260730 | Mapper live |
|---|---|---|
| syslay FBs | 43 | 35 |
| **drawable FBs** (CAT declares `_HMI.fbt`) | **25** | **25** |
| top-level canvases with placements | 6 (+shell) | 11 (+shell) |
| **placements** | **28** | **37** |
| **unique TagName bindings** | **15** | **25** |
| **uncovered drawable FBs** | **10** | **0** |
| CAT faceplate folders | 3 | 7 |
| command-bearing symbols | `Area_sDefault`, `5S_sSetup`, `5S_sFault`, `Proc_sManual`, `Proc_sAutomatic` | none (read-only) |

Drawable set is identical in both: Area 1, Station 2, Process 3, Five-State 12, Seven-State 1,
Robot 1, Sensor 5.

---

## 6. Coverage comparison — exact

**Jyotsna: 15/25 covered. The 10 uncovered (VERIFIED):**
`Station1_HMI`, `Station2_HMI` (Station_CAT) · `Bearing_PnP` (Seven_State_Centre_Home) ·
`Ejector` (Five_State) · `Robot` (Robot_Task) · `PartInHopper`, `PartAtAssembly`, `BearingSensor`,
`ShaftSensor`, `TopCoverSenosr` (Sensor_Bool). → **lead 3 CONFIRMED exactly**, including the
predicted omission classes.

**`CoverPNP_Vr` (`2C5F84BA3C43A064`) appears only on `ActuatorsScreen`** — on neither Setup screen.
→ **lead 3b CONFIRMED.** One cover cannot be jogged at all.

**Mapper: 25/25 covered, 0 uncovered**, 37 placements over 11 process-grouped, paginated screens.
→ **lead 4 CONFIRMED.**

Repeats are legitimate in both (a component appears on each screen that owns it): Jyotsna repeats
13 components across Actuators/Setup and Main/Manual; Mapper repeats 11 across owning process
screens.

---

## 7. CAT-by-CAT contract comparison (condensed)

| CAT | Jyotsna symbols | Commands emitted | Mapper template | Mapper equivalent |
|---|---|---|---|---|
| `Area_CAT` | `sDefault` | `MCNF`(1,2,3,9), `CTCNF`(0,1,2,3), `FRCNF` | yes | read-only |
| `Station_CAT` | **none** | — | **yes** | read-only, **placed** — Mapper ahead |
| `Process1_Generic` | `sAutomatic`, `sManual` | `MREQO`, `NSREQO` | `sDefault` only | read-only |
| `Five_State_Actuator_CAT` | `sDefault`, `sSetup`, `sFault`, `sInterlock` | `cmd_event`(toWork/toHome), `Reset_Fault` | all four | read-only |
| `Seven_State_Centre_Home` | **none** | — | **yes** | read-only |
| `Robot_Task_CAT` | **none** | — | **yes** | read-only |
| `Sensor_Bool_CAT` | **none** | — | **yes** | read-only |
| `Five_State_No_Sensors`, `Vacuum_Gripper` | none | — | not instantiated in this model | n/a |

**Mapper has a superset of faceplate templates.** Jyotsna's advantage is symbol *variety* within
three CATs (`sAutomatic`/`sManual`/`sSetup`), not breadth.

---

## 8. End-to-end command trace — classification

**Area mode/cycle values (VERIFIED, `HMI\Area_CAT\Area_CAT_sDefault.cnv.cs`):**
`:40 MCNF(1)` Auto · `:54 MCNF(2)` Manual · `:70 MCNF(3)` Setup · `:86 MCNF(9)` Home ·
`:104 CTCNF(0)` Stop · `:111 CTCNF(1)` Run Continuous · `:118 CTCNF(2)` Stop At End ·
`:125 CTCNF(3)` Single Run. → **lead 5 CONFIRMED exactly.**

**The engine tests exactly one CycleType value.** `ProcessRuntime_Generic_v1.fbt` — grep for
`CycleType = n` yields **only `CycleType = 1`**.

| Control | Verdict | Evidence |
|---|---|---|
| Auto (Mode 1) | **proven functional** — opens the `IDLE1` gate with CycleType 1 | ECC `IDLE1→ISSUE_CMD` |
| Manual (Mode 2) | **partially implemented / unsafe** — see lead 7/8 below | ECC + `:148` |
| Setup (Mode 3) | **proven functional** for actuators on a CaS chain | core arc `setup_event AND mode=3` |
| Home (Mode 9) | **unsafe/unproven** — `ToWork→ToHome [mode = 9]` carries **no interlock term** | `FiveStateActuator.fbt` |
| **Run Continuous (1)** | **misleading label** — runs **one** recipe; `END→END` is terminal | ECC `END→END [1]` |
| **Stop (0)** | **partially implemented** — pauses at the next step boundary; **strands the engine if pressed during `ISSUE_CMD`** (no exit satisfiable) | ECC `ISSUE_CMD` exits |
| **Stop At End (2)** | **dead output** — no ECC term tests CycleType 2 | grep: only `CycleType = 1` |
| **Single Run (3)** | **dead output** — same | grep: only `CycleType = 1` |
| Area Fault Reset | **partially implemented** — reaches Station faults on M262/M580; **BX1 has no CaS chain** | prior verification |
| 5-State toWork / toHome (Setup) | **proven functional**, interlock term present | core arcs |
| **5-State Reset Fault** | **DEAD END-TO-END** — see lead 9 | below |
| Process Manual Execute | **unsafe** — see lead 8 | below |
| Process Manual Next | proven functional in Mode 2 | ECC `MANUAL_COMPLETE→ADVANCE` |

### Lead 7 — CONFIRMED, and it conflates two semantics
`Process1_Generic.fbt:148` — `stationAdptr_in.MCTRL → ProcessEngine.MREQ`.
`MCTRL` is the CaS **mode-change** event. `MREQ` is the ECC's **manual Execute Step** request
(`IDLE1→ISSUE_CMD [… OR ((Mode = 2) AND MREQ)]`). **INFERRED consequence:** any mode-change
broadcast while Mode = 2 satisfies the manual-execute condition and dispatches a recipe step.
Selecting Manual would itself execute one step.

### Lead 8 — CONFIRMED, and it is the most serious finding
`Process1_Generic_sManual.cnv.Designer.cs`:
```
:253  this.chkManualExecuteStep.Click += … ChkManualExecuteStepClick
:270  this.chkManualNextStep.Click   += … ChkManualNextStepClick
:414  this.Click                     += … ChkManualExecuteStepClick     ← the whole canvas
```
and `…_sManual.cnv.cs:29  FireEvent_MREQO(true);`

**Clicking anywhere on the Manual faceplate emits a manual Execute Step**, which in Mode 2 issues a
real actuator command. The `:414` binding is in addition to the intended checkbox at `:253` —
**INFERRED** to be an accidental designer binding. **safety-relevant.**

### Lead 9 — CONFIRMED dead
`Five_State_Actuator_CAT_sFault.cnv.cs:127` calls `FireEvent_Reset_Fault(...)`.
The event is declared in the `sDefault` and `sSetup` contracts (`…cnv.xml:14`).
**`Reset_Fault` occurs 0 times in `IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt`** —
no `IThis.Reset_Fault` connection, no reference of any kind. The operator presses Reset Fault, sees
no error, and nothing happens. **Dead output + misleading label.**

---

## 9–11. Lifecycle findings (static, no rig operation)

**Auto, first cycle — PROVEN.** Mode 1 + CycleType 1 opens `IDLE1`; steps dispatch on `StepType`.
**Auto, second cycle — PROVEN IMPOSSIBLE.** `ADVANCE→END [CurrentStepType = 9]` then the terminal
`END→END [1]`; `ProcessEngine.INIT` has a single driver from the boundary `INIT`, so only a
controller re-INIT restarts it. "Run **Continuous**" is a misnomer.

**Stop while waiting — works as a pause.** `WAIT_STEP→ADVANCE [WaitSatisfied AND (Mode=1)]` does not
test CycleType, so the wait completes, then `IDLE1` holds. **Stop while issuing a command —
strands.** `ISSUE_CMD` exits require `(Mode=1 AND CycleType=1)` or `(Mode=2)`; with CycleType 0 and
Mode 1 neither is satisfiable. **Continuation** by restoring CycleType 1 works from `IDLE1`, and is
impossible from a stranded `ISSUE_CMD`.

**Mode change mid-cycle strands.** No state admits Mode 3; selecting Setup during a cycle blocks
the process permanently.

**Setup toHome/toWork — proven**, interlock-gated, for actuators reachable by mode 3.
**Interlock-blocked Setup — UNKNOWN to the operator:** there is no rejection or acknowledgement
channel anywhere in either project. A blocked jog is silently ignored.

**Reset All / actuator initialisation — does not exist** as a defined operation in either project.

---

## 12. Defects, dead paths and misleading controls

| # | Defect | Severity |
|---|---|---|
| J-1 | Whole-canvas `Click` on the Manual faceplate fires Execute Step (`:414`) | **safety-relevant** |
| J-2 | `MCTRL → MREQ` conflates mode change with manual execute (`:148`) | safety-relevant |
| J-3 | `Reset_Fault` dead end-to-end (0 refs in the CAT) | dead output |
| J-4 | Stop At End (2) and Single Run (3) dead — only `CycleType = 1` is tested | misleading label ×2 |
| J-5 | "Run Continuous" runs exactly one recipe (`END→END`) | misleading label |
| J-6 | Stop during `ISSUE_CMD` strands the engine | defect |
| J-7 | No state admits Mode 3 — Setup mid-cycle strands the process | defect |
| J-8 | `mode = 9` arc bypasses the interlock (present in Mapper too) | safety-relevant |
| J-9 | `CoverPNP_Vr` has no Setup widget | gap |
| J-10 | 10 of 25 drawable components have no HMI representation | gap |
| J-11 | No acknowledgement, rejection or timeout anywhere | gap |
| **M-1** | **Mapper's stripper classifies by UI type, not destination** | **defect (ours)** |

### M-1 — the Mapper defect worth fixing first
`HmiCommandStripper.IsCommandControl` (`CodeGen\CodeGen\Hmi\Templates\HmiCommandStripper.cs`):
```
:139  if (shortType.StartsWith("ChangeCanvasButton")) return true;   // strips navigation
:140  if (shortType.EndsWith("Button")) return true;                 // strips ALL buttons
:145  return Regex.IsMatch(designer, $@"this\.{name}\.IsOnlyInput\s*=\s*false");
```
This cannot distinguish *"opens a local diagnostic faceplate"* from *"commands the plant"*. It is
why `sFault` and `sInterlock` remain registered in the generated `.cfg` yet are **unreachable** —
their launcher buttons were stripped. → **lead 10 CONFIRMED.**

---

## 13. Adoption matrix

| Asset | Verdict | Reason |
|---|---|---|
| HMI shell / project structure | **Adopt unchanged** | already shared; model-independent |
| Themes, colours, image store, languages | **Adopt unchanged** | pure resources |
| Alarms / configuration files | **Adopt unchanged** | model-independent |
| `Area_CAT_sDefault` | **Adapt** | keep layout; regenerate as read-only status until commands are proven |
| `Five_State sDefault` | **Adapt** | good status tile; strip only proven-unsafe controls |
| `Five_State sSetup` | **Defer** | control path proven, but needs mode-3 reach + rejection feedback |
| `Five_State sFault` / `sInterlock` | **Adopt unchanged** | read-only diagnostics — and **must stop being stripped** |
| `Process1_Generic_sAutomatic` | **Adapt** | excellent status content; drop command outputs |
| `Process1_Generic_sManual` | **Reject as-is** | J-1 whole-canvas execute; redesign if Manual is ever approved |
| **Top-level screens** (Main/Actuators/Setup/Manual) | **Reject — regenerate** | hardcoded FB IDs, 15/25 coverage |
| Runtime topology / configuration | **Regenerate** | `HmiRuntimeEmitter` already owns this and it deploys |
| OPC UA metadata | **Reject** | 71× disabled; not the data path |

**Explicitly not adopted:** any `MainScreen`, `SetupScreen`, `ManualScreen` or their Designer files.

---

## 14. Proposed pure-automatic architecture

Mapper's existing pipeline is already the right shape and needs extension, not replacement:

```
finished syslay → HMI plant model → CAT capability discovery → process/component grouping
→ screen family planning → pagination → symbol placement → TagName binding
→ project/runtime emission → validation
```

**Data sources (all already available, VERIFIED in the current generator):**

| Datum | Source |
|---|---|
| instance name / display name | syslay `<FB Name>` (humanised) |
| CAT type, TagName | syslay `<FB Type>`, `<FB ID>` |
| process ownership, shared ownership | process `Recipe` parameter — `CmdTargetName`, `Wait1Id` |
| actuator state names / numbers | `Target*` parameters + the twin's state table |
| interlocks | `RuleTable` parameter, decoded against `actuator_id` |
| device/controller allocation | which `.sysres` hosts the FB |
| command capability | CAT `_HMI.fbt` + `.cnv.xml` contract |
| MQTT/UNS status | existing `Telemetry` FBs (status only) |
| recipe step text | **derive at HMI-generation time** — do not extend `RecipeStep` |

---

## 15. CAT capability catalog

Automatic discovery covers most of it: `<CAT>_HMI.fbt` gives ports and directions; `.cnv.xml` gives
the per-symbol contract; the Designer gives `SymbolSize`. **What cannot be discovered is semantics** —
that a `cmd_event` moves an actuator, that mode 3 is its prerequisite, that a `DrawnButton` opens a
diagnostic rather than commanding. A **small once-per-CAT descriptor** is therefore unavoidable:

```
catType · symbolPurpose(overview|automatic|manual|setup|fault|diagnostic) · dimensions
requiredInputs · outputCommands · allowedModes · localNavControls · plantCommandControls
confirmationFields · commandPrerequisites
```

One row per CAT type — **never per component instance**. **Command safety must never be inferred
from an event name.**

---

## 16. Read-only versus command-capable generation

Replace the destructive type-based strip with a **destination-based classifier** and **two generated
symbol variants**:

| Class | Example | Read-only build |
|---|---|---|
| local canvas navigation | `ChangeCanvasButton` to a generated canvas | **keep** |
| open diagnostic faceplate | button opening `sFault` / `sInterlock` | **keep** |
| read-only inspection | interlock/fault display | keep |
| plant mode command | `MCNF` | omit |
| plant cycle command | `CTCNF` | omit |
| actuator movement | `cmd_event` | omit |
| process manual command | `MREQO`/`NSREQO` | omit |
| fault reset | `FRCNF` / `Reset_Fault` | omit (and `Reset_Fault` is dead anyway) |

The test is **where the control's event lands**, proven from the `.cnv.xml` contract and the CAT
`.fbt` connection — not the control's C# type. Generate a `sMonitor`-style variant beside the
original rather than regex-rewriting it in place.

---

## 17. Smallest safe HMI-only improvement

Ordered by value, all HMI-only, none touching control logic:

1. **Fix M-1** — destination-based classification; restores `sFault`/`sInterlock` reachability that
   the generator currently destroys. Highest value, lowest risk.
2. **Adopt the `sAutomatic` status content** as a read-only process tile (step text, current step,
   process name) — Mapper already binds only `ThisStepText`.
3. **Generate state *names* not numbers**, from the twin's state table.
4. **Generate interlock explanations** from `RuleTable`, decoded to component names.
5. **Show controller/device allocation** per component.
6. **Adopt the shell, theme and resources** unchanged.
7. **Keep 25/25 coverage** — Mapper's real advantage over the reference.

---

## 18. Deferred — requires controller changes (out of HMI scope)

Auto/Run/Stop gating (engine ECC + `Process1_Generic` wiring + `InitialValue` 1→0) · Manual mode ·
Setup mode reach for BX1 covers (no CaS chain) · `Station2.AreaAdptrIN` source · any command
acknowledgement/rejection/timeout · `Reset_Fault` sink · a Home All that does not use the
interlock-bypassing mode-9 arc.

---

## 19. Validation and acceptance

Add/remove/rename a component → no HMI edit. Clamp and no-clamp use one generator. Generated
coverage set **equals** the drawable syslay set. No stale screens after regeneration. **No
model-specific FB ID in any template.** No unsupported output event survives in read-only mode.
**Local diagnostic and navigation controls remain reachable** (the M-1 regression test). Every
command-capable control has a proven controller sink. Generated project builds in EAE. Non-HMI
artefacts byte-identical. Deterministic regeneration. State/step labels model-derived. RuleTable
explanations generated. No direct physical output from the HMI.

---

## 20–21. Evidence index and proposed file list

**Evidence:** `Area_CAT_sDefault.cnv.cs:40,54,70,86,104,111,118,125` · `Process1_Generic.fbt:148` ·
`Process1_Generic_sManual.cnv.Designer.cs:253,270,414` · `…_sManual.cnv.cs:29` ·
`Five_State_Actuator_CAT_sFault.cnv.cs:127` · `Five_State_Actuator_CAT.fbt` (0 × `Reset_Fault`) ·
`ProcessRuntime_Generic_v1.fbt` ECC (14 transitions; only `CycleType = 1`) ·
`HmiCommandStripper.cs:139,140,145` · `MapperConfig.cs:144` · `HmiGenerator.cs:35-37`.

**Files a Phase-1 implementation would touch (no code written):**
`Hmi/Templates/HmiCommandStripper.cs` (replace classifier) · `Hmi/Model/HmiModel.cs` (capability
descriptor) · new `Hmi/Templates/HmiCatCapabilities.cs` · `Hmi/Planning/HmiPlanner.cs` (state names,
interlock text, allocation) · `Hmi/Emission/HmiCanvasEmitter.cs` · `Hmi/Validation/HmiPlanValidator.cs`
(diagnostic-reachability test) · `Template Library/HMI/Faceplates/**` (adapted symbols).
**No CAT, FB, recipe, interlock, topology, wiring, I/O, MQTT, HCF, EIPScanner or Control.xml change.**

---

## Closing

Nothing here makes the machine safe. The interlocks discussed are operational, not certified safety
functions; the protective stop remains the certified hardware safety system; and the rig's
documented clamp damage and swivel collision risk are unaffected.

**Do not treat Jyotsna's HMI as a validated command interface.** One of its eight cycle/mode
buttons behaves as labelled, one of its faceplates fires a plant command on any click, and its
fault-reset button is wired to nothing.
