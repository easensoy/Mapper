# SMC Rig (Expo, with Clamp) — HMI Research & Design Report

**Date:** 2026-07-08 · **Status:** RESEARCH + DESIGN. No implementation. No project/control files modified.
**Author trigger:** Robert raised HMI + further interlocks. This report inspects the *actual* reference project (now on disk), audits the CAT/interlock reality, researches modern web/industrial HMI practice, and recommends a concrete direction with honest feasibility.

**Scope guard:** PLC control logic, recipes, interlocks, HCF, CATs, MQTT publishers, EAE runtime, and `C:\Demonstrator` are **frozen surfaces** here. This report is analysis + design only; nothing below changes generated behaviour. Where a recommendation *would* touch a frozen surface, it is called out explicitly as requiring an approved, gate-verified task.

> This supersedes and corrects `Docs/WEB_HMI_GENERATION_DESIGN.md` (2026-07-03) on three points, because the reference EAE project — absent when that doc was written — is now present at `C:\Schenider EAE Projects\SMC_Rig_Expo_withClamp`:
> 1. There **is** a hand-composed overview screen (`MainScreen.cnv`) + a start screen, and the Area/Station faceplates **do** carry Mode/Cycle/Fault-Reset command buttons — the EAE HMI is richer than that doc credited.
> 2. There are **no** `fault_active`/`fault_code` tags on the actuator faceplates in this reference (that doc's table overstated the per-component fault surface). Faults surface only as a single binary `LL_Fault` rollup.
> 3. Mode/CycleType are **commandable and displayable but not enforced** in the generated control logic (the CATs contain no mode-gating).

---

## 1. Executive Summary

**What exists.** The reference `SMC_Rig_Expo_withClamp` project ships a working, EAE-native (NxtControl 24.1) HMI: one bespoke overview canvas (`MainScreen`, 1010×600) that hosts an Area faceplate (Auto/Manual/Setup/InitialPosition + Stop/Continuous/StopAtEnd/Single/Dry + Fault-Reset buttons), four read-only Process/step faceplates, and ~10 read-only actuator faceplates (state text + home/work LEDs). It runs on two SoftdPAC HMI panels (`HMIB1X_1` @192.168.1.209, `HMIP6_1` @192.168.1.2). It is a **component-status display with area/station mode buttons** — good for commissioning, not a supervisory/diagnostic HMI.

**What's weak or missing (honest).**
- **No navigation / no drill-down:** a single fixed canvas. No component-detail, alarm, recovery, diagnostics, I/O, or cycle-time screens.
- **No real alarm system:** only the two framework default alarm classes (`Alarm`, `Warning`); zero rig-specific alarms. Faults appear only as one binary `LL_Fault` LED — no code, cause, timestamp, or history.
- **No fault/recovery workflow, and almost nothing generating faults:** the actuator CATs expose **no** `fault_active`/`fault_code`, and only the Seven-State swivel has a timeout (750/500 ms). Every Five-State actuator (grippers, covers, shafts, feeder, checker, transfer, **clamp**) has **no watchdog** — a stuck sensor hangs the recipe **indefinitely and silently**. The `Fault_Reset` button has essentially no fault-state to clear.
- **Mode is not enforced:** Mode/CycleType are wired through the Area/Station adapters as pass-through data highways but no CAT gates commands on them. Selecting "Manual" does not actually restrict anything at the actuator level.
- **Interlocking is thin:** only Control.xml-derived crossing rules (Feeder↔Checker/Transfer; Bearing_PnP Work1↔Work2 vs Shaft_Hr/Cover/Transfer). The **clamp has no interlock rule** — it is guarded only by recipe ordering + a part-present gate. No clamp-locked permissive, no grasp-detect, no robot-zone gate, no bidirectional station handshake.
- **The overview screen is hand-authored, not generated,** and is already drifting from the model (its instances are named `Pusher`/`Rejector`/`Gripper`/`swivel`/`CoverPnpVr`, with no `Clamp` faceplate placed, despite this being the "withClamp" project). Every layout/model change is manual re-work — exactly the maintenance trap to avoid.
- **AVEVA OMI layer is an empty stub** (`AvevaOMI.omiproj` with no content).

**The spine of this report:** *an HMI can only be as good as the data the control logic exposes.* Today the control logic exposes component **states** richly (already on MQTT) but exposes **faults, timeouts, interlock-active status, and enforced modes** barely or not at all. So a genuinely diagnostic, recovery-capable, mode-aware HMI is **not purely an HMI project** — the honest parts of it require additive (gate-verified) CAT/model work, or bridge-side derivation, first.

**Recommendation (three horizons).**
- **Immediate (demo, days, zero control risk):** a **generated, read-only web HMI** driven by an `hmi-manifest.json` the Mapper emits from the model, rendering an ISA-101-style overview + station + component tiles from the existing `smc/#` MQTT state stream (via the already-designed StateSync UNS bridge over MQTT-WebSockets). Interlocks are **explained** from the static RuleTable, never enforced by the browser. No EAE, PLC, or pipeline change; the byte-identical gate stays green.
- **Medium (robust, weeks):** add the honest data the HMI needs — Five-State timeout-to-fault + `fault_code`/interlock-active flags in the CATs (gate-verified), a derived alarm list, station mode/cycle *display*, a live "current step" (bridge inference or a gated additive publish), and a diagnostics/connectivity screen. Keep the EAE panel HMI as the on-machine view; the web HMI is the supervisory shadow.
- **Long (product-quality):** a **.NET OPC UA command gateway** (Blazor + the OPC UA .NET stack) so the web HMI can *command* mode/cycle/fault-reset and (permissive-gated) manual jog — **through an audited server-side choke point, never browser-to-PLC** — plus role-based access, an ISA-18.2-style alarm manager, and a historian/cycle-time view.

---

## 2. Current HMI Findings (Task 1)

### 2.1 How the project was identified

Two trees are relevant and consistent:

| Tree | Path | Role |
|---|---|---|
| **Reference EAE project** | `C:\Schenider EAE Projects\SMC_Rig_Expo_withClamp` | The canonical hand-built EAE demo project (added working directory). This is the "active current SMC rig with clamp" — a full EAE solution (`.sln`, `HMI/`, `IEC61499/`, `Topology/`, `HwConfiguration/`, `AvevaOMI/`). |
| **Generated output** | `C:\Demonstrator\Demonstrator` (written by the Mapper) + `MapperUI\...\bin\...\Output` / `_gated*` snapshots | What VueOneMapper generates from `Control.xml`. Faceplate *types* match the reference; the composed overview does not (see §2.6). |

The live clamp twin the Mapper consumes is `MapperUI\MapperUI\bin\Debug\net10.0-windows\Input\Control.xml` (per `CLAUDE.md`).

### 2.2 Folder map (reference `HMI\`)

```
HMI\
  HMI.csproj                     # EAE HMI project: ProjectType=HMI, NxtVersion 24.1.0.0,
                                 #   netstandard2.0, theme DefaultLight, HMILibraries=HMIBaseSymbols,
                                 #   16 SE.* / AVEVA libs referenced
  MainScreen.cnv.{cs,Designer.cs,resx}     # THE overview canvas (1010x600) — hand-composed
  StartCanvas_3.cnv.{cs,Designer.cs,resx}  # start screen
  CanvasesResolutionList.xml, GraphicsList.xml
  Area_CAT\                      # area faceplate (mode/cycle/fault-reset buttons + LEDs)
  Station_CAT\                   # per-station faceplate (mode/cycle/fault-reset + name labels)
  Process1_CAT\ .. Process4_CAT\ # step faceplates (read-only step text + actuator/state)
  Five_State_Actuator_CAT\       # read-only: state + atHome/atWork LEDs + counter
  Five_State_Actuator_No_Sensors_CAT\
  Five_State_Double_Acting_Cylinder_CAT\
  Seven_State_Actuator_CAT\
  Robot_Task_CAT\ , Vacuum_Gripper_CAT\    # read-only state
  Start_Btn\                     # a Btn_val BOOL output
  TM3BC_Ethe_*\                  # two device panels — EMPTY data contracts (<Mapping></Mapping>)
  Alarms\ AlarmClasses.xml (224 B), SystemAlarmClasses.xml (1037 B)  # framework defaults only
  Colors\ (ProjectColors.xml hand-authored), Theme\, Configurations\ (TagValueEditor empty;
    HistoryTrend/Journalling stubs), Languages\neutral\Dictionary.Resources.xml (EMPTY)
AvevaOMI\ AvevaOMI.omiproj       # STUB — omiproj only, no OMI content
Topology\                        # HMIB1X_1 @192.168.1.209, HMIP6_1 @192.168.1.2 (SoftdPAC panels)
```

### 2.3 The overview screen (`MainScreen.cnv.Designer.cs`)

A single 1010×600 canvas hosts: `Area_HMI` (5,5); `Process1..4` faceplates; and actuator faceplates `Pusher` (32,360), `Checker` (24,488), `Shaft_Hr` (360,320), `Shaft_Vr` (360,456), `CoverPnpVr` (688,320), `CoverPnPHr` (777,485), with free-text labels ("Feeder", "Checker", "Shaft PnP Horizontal", …). **No navigation to any second screen.** `StartCanvas_3` is a start/landing canvas.

### 2.4 The faceplate data contracts (the reusable gold)

From the authoritative tag spec `Docs/SMC Rig Expo demo exposed OPCUA tags.docx` (A. Evans, 13/03/2026), cross-checked against each reference `.cnv.xml`:

| Faceplate | Read (display) | Write (command) |
|---|---|---|
| **Area_CAT** (`Area_HMI`) | `System_Mode` INT, `System_Cycle_Type` INT, `LL_Fault_Status` BOOL, `Area_Name` STR | **`Mode`** INT, **`CycleType`** INT, **`Fault_Reset`** BOOL (buttons: Auto/Manual/Setup/InitialPosition/Stop/Continuous/StopAtEnd/Single/Dry + Reset) |
| **Station_CAT** (`Checking_Station_HMI`, `AssemblyDisassemblyStation_HMI`) | `StationName`, `ParentName`, `LocalMode` INT, `LocalCycleType` INT, `LL_Fault` BOOL, `LL_Mode` INT, `LL_CycleType` INT | **`Mode`**, **`CycleType`**, **`FaultReset`** |
| **Process1..4_CAT** | `ThisStepText`, `NextStepText`, `Previous_Step_Text` STR, `ActuatorName` STR, `StateValue` INT | Process1 only: `ManualFeedCmd` BOOL; P2–P4 read-only |
| **Five_State_Actuator_CAT** (Feeder, Checker, Transfer, Gripper, Shaft_Hr/Vr, Shaft_Gripper, CoverPnpVr) | `current_state_to_plc` BOOL, `current_state_to_process` INT (0 home-init,1 to-work,2 at-work,3 to-home,4 home-finished), `atHome`, `atWork`, `state_change_counter` | **none (read-only)** |
| **Five_State_No_Sensors_CAT** (Rejector) | `current_state_to_plc`, `current_state_to_process` (0–4) | none |
| **Five_State_Double_Acting_CAT** (CoverPnPHr) | `current_state1_to_plc`, `current_state2_to_plc`, `current_state_to_process` (0–4) | none |
| **Seven_State_Actuator_CAT** (swivel) | `current_state1_to_plc` (to-pick), `current_state2_to_plc` (to-place), `current_state_to_process` (0 home,1 pick,2 place) *(exposed tag abbreviates the runtime 0–6 centre-home space)* | none |
| **Robot_Task_CAT** (Robot_Pick_And_Place1) | `current_state_to_process` (0 init,1 running,2 complete), `PulseActive` BOOL | none |
| **Vacuum_Gripper_CAT** (CoverGripper) | `current_state_to_process` (0 off,1 on), `current_state_to_plc` | none |

**Canonical enums (authoritative, from the A. Evans spec):**
- **System_Mode:** `0` No Mode · `1` Automatic · `2` Manual (step-through) · `3` Setup (actuators independent of sequence) · `9` Initial Position (return home) · `100` Undetermined (area only, stations disagree).
- **System_Cycle_Type:** `0` Stop · `1` Continuous · `2` Stop at end of cycle · `3` Single · `4` Dry (run without parts; part-sensors ignored) · `100` Undetermined.

### 2.5 What the current HMI DOES

- Displays live component state (state text + home/work LEDs) for ~10 actuators, plus 4 process-step readouts (this/next/prev step text, actuator name, state value).
- Presents area-level and station-level **Mode** and **CycleType** selection buttons and a **Fault-Reset** button (the operator command surface).
- Runs on two physical panels over Ethernet (native EAE WebHMI/SoftdPAC runtime).

### 2.6 What the current HMI does NOT do

| Missing | Consequence |
|---|---|
| Navigation / drill-down | One canvas; can't isolate a component, view alarms, or run a recovery flow. |
| Real alarm system | Only default `Alarm`/`Warning` classes; **zero** rig alarms. Faults = one binary LED; no code/cause/time/history. |
| Fault codes & recovery workflow | CATs expose no `fault_active`/`fault_code`; `Fault_Reset` has almost no state to clear (§3). |
| Manual jog from HMI | All actuator faceplates read-only (only `Start_Btn`, and `Process1.ManualFeedCmd`). No permissive-gated manual control. |
| Enforced modes | Mode/Cycle are display + pass-through; the control logic doesn't act on them (§3.5). |
| Diagnostics / connectivity | `TM3BC` device panels have empty contracts; no MQTT/UNS/PLC-health view. |
| Trend / cycle-time | `HistoryTrend` config is a stub; no cycle-time, bottleneck, or last-N-cycles view. |
| i18n | Language dictionary empty (English hard-coded). |
| MES/supervisory | AVEVA OMI is an empty stub; no production/OEE/remote view. |

### 2.7 Generated vs hand-authored

| Artefact | Origin | Note |
|---|---|---|
| Faceplate **types** (`*_CAT` `.cnv.cs`/`.Designer.cs`/`.cnv.xml`) | Template Library, copied by the Mapper (`TemplateLibraryDeployer.cs:81-144`) | Reusable per-CAT symbols; **do not hand-edit** (regenerated). |
| HMI **instances** (`Area_HMI`, `Station1/2_HMI`, per-component `*_HMI`) + **adapters** (`AreaHMIAdptr`, `StationHMIAdptr`) | Generated by the Mapper (`SystemLayoutInjector.cs:951-980, 2328-2332`; `ResourceWireEmitter.cs:68-74`) | This is the control-side HMI plumbing. |
| **`MainScreen.cnv` overview composition** (which faceplate sits where) | **Hand-authored in EAE Designer** — the Mapper does **not** generate an overview | The drift source: names/components on it (`Pusher`/`Rejector`/`swivel`, no `Clamp`) don't match the current clamp model. |
| `ProjectColors.xml`, `AlarmClasses.xml`, `HMI.csproj` skeleton | Hand-authored / framework | Safe to extend; alarms currently framework-default only. |
| Theme, `SystemAlarmClasses.xml` | Framework (Schneider) | Do not touch. |

### 2.8 Do-not-touch-without-care

- `*.cnv.Designer.cs`, `*.event.cs`, `*.cnv.xml` — **generated**; edit only via EAE Designer, never by hand (regeneration overwrites).
- Anything under the Mapper's frozen surfaces (CATs, recipes, interlocks, MQTT publishers, HCF) — changes must go through the byte-identical `_gate` + a rig check (`CLAUDE.md`).
- `HMI.csproj` item groups + `Topology\` device definitions — EAE-managed; wrong edits break Build/Deploy.
- The reference `MainScreen` — hand-maintained; changing the model without re-laying it out leaves the overview stale (already the case).

---

## 3. Current CAT / Interlock Findings (Task 2)

**Bottom line:** the rig has **operational sequencing** (recipe order + state-wait barriers + a handful of Control.xml crossing interlocks + one swivel timer). It does **not** have resilient interlocking, timeout/fault handling on most actuators, fault codes, a recovery workflow, or enforced modes. This is adequate for a well-tuned rig with reliable pneumatics; it is fragile to sensor failure, weak actuation, manual interference, and clamp failure.

> **Safety disclaimer (explicit):** nothing below is a machine-safety function and this report makes **no safety-compliance claim**. Everything here is *operational* interlocking/sequencing in soft logic. Real machine safety (E-stop, guard interlocks, light curtains, robot safety-rated stop, two-hand controls) is a hardware/safety-PLC concern **outside** this HMI/soft-logic layer and must be validated by the appropriate safety process — see §7 and §12.

### 3.1 Interlock inventory (how it works today)

Interlocks originate from Control.xml `<Interlock_Condition>` elements on component states and are translated by the generator into a per-actuator `RuleTable` of `(FromState, ToState, SourceID, BlockedState)` — "block target's From→To transition while source holds BlockedState":

- Extraction: `CodeGen\CodeGen\Planning\Interlocks\InterlockPlanner.cs:13-147`
- Emission: `CodeGen\CodeGen\Planning\Interlocks\InterlockEmitter.cs:16-111` (+ `WithReverseCrossings()` :116-133 for the swivel)
- Evaluator FB: `...\Demonstrator\IEC61499\CommonInterlockEvaluator.fbt:175-213`
- Config: `Config\interlock.yaml` (`ruleArraySize: 10`)

Observed rules for the clamp model:
- **Feeder/Advancing** blocked while `Checker` NOT down OR `Transfer` NOT advanced (Control.xml Feeder `<Interlock_Condition>`).
- **Bearing_PnP** (Seven-State centre-home swivel) has bidirectional Work1↔Work2 crossing rules vs `Shaft_Hr`, `CoverPNP_Hr`, `Transfer` (filtered to the centre-home 0..6 range + reverse crossings auto-added).
- **CoverPNP_Vr / covers:** RuleCount **0** (no cross-actuator blocking).
- **Clamp:** RuleCount **0** — the clamp has **no** `<Interlock_Condition>`. It is commanded unconditionally once the recipe gate is satisfied.

### 3.2 Clamp sequence & permissives

`Config\recipes.yml` + `Planning\Recipes\AssemblyRecipe.cs`:

```
WAIT(PartAtAssembly = 1)      # material gate — part must be present (AssemblyRecipe.cs:66-67)
CMD(clamp = 1); WAIT(clamp = 2)   # close + confirm closed (recipes.yml clampClose)
... bearing_pnp pick/place, shaft, covers ...
CMD(clamp = 3); WAIT(clamp = 0)   # open + confirm open at end (recipes.yml clampOpen)
```

- **Part-present IS a permissive** before clamping (good).
- **Clamp-locked is NOT an interlock** — nothing blocks `bearing_pnp` from moving to Pick unless the clamp reports closed; it is *recipe-order* only. If the clamp fails to reach state 2, the recipe stalls at the WAIT, but no interlock rule inhibits the swivel/robot/ejector.
- **No independent clamp sensor:** state 2 = "reported closed" from valve/position logic, not a jaw-contact proximity switch. The clamp can report closed on empty air or when mechanically jammed part-way.

### 3.3 Timeout / fault handling

- **Only** the Seven-State swivel has a watchdog: `Seven_State_Actuator_CAT.fbt:61-70` uses an `E_DELAY` with `SELECT` timeout `T#750ms` (AtWork1) / `T#500ms` (AtWork2); `SevenStateActuator2.fbt` drives AtPlace→timerStart→ToHome→timerEnd→home.
- **Five-State actuators have NO timeout** — `Five_State_Actuator.fbt:53-102` is a pure sensor-driven state machine (`ToWork→AtWork` on `atwork=TRUE`, etc.). If a sensor sticks, it **stalls forever**. This affects grippers, covers, shafts, feeder, checker, transfer, **and the clamp**.
- **No `fault_active`/`fault_code`** in any actuator CAT. There is a `LL_Fault` rollup at station/area, but no CAT logic *sets* it from a timeout/stall — so in practice faults are near-invisible. (This corrects the prior WEB_HMI doc, which listed `fault_active`/`fault_code` on Five-State.)

### 3.4 Reset / recovery

- **No fault-reset path in the CATs.** Recovery is only re-`INIT` of the state machine (`Five_State_Actuator.fbt:97`). The HMI `Fault_Reset` button has essentially no CAT fault-state to clear.
- **No faulted-actuator inhibit.** A stalled actuator hangs the recipe at its WAIT; other motion is blocked by the *recipe being stuck*, not by an interlock. (Exception: if the stuck state happens to match a `BlockedState` of a downstream rule, that rule blocks — incidental, not designed.)

### 3.5 Mode arbitration

- **None in the CATs.** No Mode `VarDeclaration`, no ECC transitions gated on mode. Five-State/Seven-State CATs accept commands unconditionally; a manual jog `pst_event` is processed identically to a recipe command. Mode/Cycle are carried on the Station/Area adapters as pass-through highways (Mapper wires them but the actuator cores ignore them — Agent-verified in `Area_CAT`/`Station_CAT` + `Five_State_Actuator_CAT` connections).
- Consequence: **Manual/Setup does not actually restrict anything at the actuator layer.** Mode ownership/arbitration is an *unimplemented* concept today.

### 3.6 Gap list (operational — NOT safety)

| Gap | Type | Impact |
|---|---|---|
| Clamp-locked permissive (block swivel/robot/ejector until clamp=closed) | interlock | swivel can move on an unclamped/failed part |
| Part-lost re-confirmation after clamp | interlock | clamp/assembly can run on empty air if part drops post-gate |
| Grasp/vacuum-confirm before lift (grippers, CoverGripper) | interlock | weak grip reports "closed" without holding |
| Robot-zone / robot-ready / robot-complete gate before ejector/transfer | interlock | ejector can fire while robot arm is in the volume |
| Ejector-safe (part centred) gate | interlock | eject on mis-positioned part |
| No-conflicting-motion (Bearing_Gripper vs Shaft_Gripper) | interlock | overlapping grip commands can jam |
| Bidirectional station handshake (Disassembly waits for Assembly, not just the reverse) | interlock | Disassembly cover-removal can start mid-Assembly |
| Timeout-to-fault on Five-State actuators | fault | stuck sensor hangs recipe forever, silently |
| `fault_code` + interlock-active flag exposed per component | diagnostics | HMI/operator can't see *why* it's stuck |
| Fault state + reset preconditions ("safely at home before re-cycle") | recovery | no guided/safe recovery |
| Enforced Auto-only / Manual-only / Setup gating | mode | manual commands indistinguishable from auto |
| Configurable timeouts (currently hardcoded 750/500 ms in the CAT) | tuning | can't tune per rig without recompiling the CAT |

**What's genuinely fine today:** the Control.xml-derived crossing interlocks (Feeder, Bearing_PnP), the part-present gate before clamp, the swivel timeout, and recipe-order sequencing. The design should keep these and *add around* them, not replace them.

---

## 4. External HMI Research (Task 3)

### 4.1 ISA-101 — high-performance HMI (the primary design frame)

ISA-101 is about **situational awareness**, not aesthetics: a grey, low-contrast *normal* state so that **colour = abnormal** jumps out; visual prominence proportional to consequence; and a **4-level display hierarchy** — L1 process overview → L2 area/unit overview → L3 detailed control (the working screen for one unit) → L4 maintenance/diagnostics (restricted). Design elements that carry information at a glance: **moving analog indicators** (value shown as position within a range, with dotted normal-range limits), **sparklines** (tiny per-datum trend), and colour reserved for intervention-needed states. This maps almost 1:1 to the rig: L1 = whole-rig overview, L2 = feed/assembly/disassembly station, L3 = component detail, L4 = diagnostics/engineering.

### 4.2 ISA-18.2 — alarm management (what "faults" should become)

The rig's single binary `LL_Fault` is the anti-pattern. ISA-18.2 gives the target shape: an **alarm lifecycle** (philosophy → rationalisation → design → operation → monitoring), **rationalisation** (every alarm is *actionable* and carries cause/consequence/corrective-action), alarm **states** (active/ack/cleared/shelved), **priority**, and **shelving** (temporarily silence a nuisance alarm; it auto-returns). For the rig this means: derive a small, rationalised alarm list (e.g. "Bearing_PnP move timeout — check swivel air/sensor"), each with a suggested recovery — not a wall of raw bits.

### 4.3 Web-based industrial HMI stacks (what's realistic to build)

- **FUXA** (open-source, MIT): browser SCADA/HMI with native MQTT/OPC-UA/Modbus, runs on Docker — a fast way to stand up a real web HMI without writing a framework. Good for a serious demo; weaker for a *generated* HMI (you draw screens in its editor).
- **Ignition Perspective** (Inductive Automation): pure-web, UNS-native, OPC-UA built in; the "grown-up" answer, but a licensed platform — overkill/cost for an expo rig, relevant if this becomes a product.
- **Node-RED / FlowFuse Dashboard 2.0:** build a browser HMI from MQTT flows in hours; strong for the immediate demo, with the honest caveat that "commands flow back through MQTT" — acceptable for a shadow, **not** for real actuator control (see §4.5).
- **Plain static HTML + MQTT.js:** zero build, reuses the broker over WebSockets. Best fit for a *generated, read-only, manifest-driven* HMI (our recommendation) — nothing to license, nothing to maintain per rig.

### 4.4 UNS / MQTT / OPC UA & automotive/andon patterns

- **Unified Namespace (UNS)** over MQTT is the modern industrial-data pattern: an event-driven single source of truth, retained "last known state" for instant snapshots, edge OPC-UA for device I/O + MQTT for the namespace, Perspective/dashboards on top (Inductive Automation, HiveMQ). The rig already has the seed of this (`smc/#` + the `uns/v1/...` StateSync design).
- **Sparkplug B** adds birth/death certificates + state management for *fleets*. For a single-site, single-broker, ~20-topic rig it's over-engineering; the StateSync bridge's `epoch` + retained snapshot already give the two Sparkplug features we'd use (rebirth + last-known-state). Revisit only if a plant-wide UNS is mandated.
- **Automotive digital-factory / andon** (Ford Advanced Manufacturing Center; COPA-DATA zenon andon; general Industry-4.0 andon): the borrowable ideas are the **andon board** (line state at a glance, active incidents, downtime-by-category, response/escalation, OEE) and **role-based operator/line-leader/maintenance views**. Ford's AMC posture — a *test-bed* that proves tech before rollout — is exactly the framing for this expo rig: build the pattern small, prove it, then scale.

### 4.5 What to borrow vs skip (for THIS rig)

| Borrow now (small expo rig) | Borrow later (serious demo/product) | Skip / hype for this rig |
|---|---|---|
| ISA-101 grey-normal / colour-abnormal + L1–L4 hierarchy | ISA-18.2 alarm manager (ack/shelve/priority/history) | Sparkplug B (fleet feature; single site) |
| Moving-analog & sparkline elements for cycle-time/timers | Ignition Perspective / a licensed platform | Full MES/ERP/OEE integration (AVEVA OMI) until there's a line |
| Retained-UNS read model (instant snapshot) | Role-based access (operator/engineer/maintenance) | AR/VR, "digital twin AI", cloud analytics buzz |
| An andon-style incident strip on the overview | OPC-UA command gateway for real control | Browser-directly-controls-PLC (never — §4.6) |
| FUXA / static-HTML+MQTT.js for a quick real HMI | Historian + cycle-time analytics | Heavy SPA framework (React/Node) for a generated shadow |

### 4.6 Command safety (non-negotiable principle from the research)

Industry practice is explicit: **do not command a PLC directly from a browser over MQTT.** MQTT-back-from-dashboard is fine for a *shadow/monitor*; for control you expose only specific points through a **gateway** (OPC-UA with message-level security/certs, or a REST/WS bridge that whitelists points) so there is a single audited, authenticated, server-side-gated choke point. This directly shapes our architecture: the web HMI is **read-only until** a .NET OPC-UA gateway exists, and even then the browser writes to the *gateway*, never to the controller.

*(Sources: §14.)*

---

## 5. Recommended HMI Concept (Task 4)

**One line:** *Generate a model (a manifest), render it generically, read state from the UNS, explain interlocks from the RuleTable, align modes to the model's own enum, and put every command behind an audited gateway — never hand-author per-rig screens, never write to the PLC from the browser.*

### 5.1 Architecture

```
 Control.xml ─► VueOneMapper ─► (control artefacts, FROZEN)
      │                     └─► NEW HmiManifestEmitter ─► hmi-manifest.json ─┐ (standalone, not in the
      │                                                                       │  control pipeline → 0 gate impact)
 rig  ┴─ smc/# {state:N} ─► StateSync bridge ─► uns/v1/smc/... (retained JSON)│
                                                        │ MQTT-WebSockets     ▼
                                                        └──────────────►  Web HMI (generic renderer)
                                                                              │  READ-ONLY (v1)
   (v2) browser ─HTTPS/SignalR─► .NET OPC UA gateway ─► EAE OPC UA server (Mode/Cycle/FaultReset, gated jog)
```

- **Read path:** the existing `smc/#` publishers → StateSync UNS bridge (`Docs/STATESYNC_UNS_BRIDGE_DESIGN.md`) → retained `uns/v1/...` topics → browser over MQTT-WebSockets. Retained topics = instant full snapshot on load. No PLC/Mapper change.
- **Model path:** a new `HmiManifestEmitter` (mirrors the StateSync sync-map generator — runs on demand, **not** in the pipeline) writes `hmi-manifest.json` from data the Mapper already computes: `ComponentRegistry` (name/station/PLC/CAT), Control.xml state tables, `InterlockPlan`/RuleTable, recipe rows, the mode/cycle enum. One generic web app renders any rig from it.
- **Command path (deferred to v2):** a .NET OPC-UA gateway exposes only the faceplate command tags (`Mode`/`CycleType`/`FaultReset`, and permissive-gated `toWork`/`toHome`). Server-side mode/interlock preconditions + audit log. **Browser never writes OPC-UA directly.**

### 5.2 Screen hierarchy (ISA-101 L1–L4)

- **L1 Overview** — whole rig, mode, active process/step, alarm strip, connectivity. (Overview screen, §7.)
- **L2 Station** — Feed / Assembly / Disassembly / Robot-Clamp, one at a time. (Station screen.)
- **L3 Component detail** — one actuator: state, permissives/interlocks, timers, (gated) commands; plus Sequence/STD, Manual/Jog, Alarm/Recovery. 
- **L4 Diagnostics/Engineering** — I/O, MQTT/UNS/VueOne/VC/PLC connectivity, cycle-time, CAT/topic/command log. (Restricted.)

### 5.3 Operator workflow

Land on **Overview** → mode + any alarm visible at a glance (grey = normal, colour = attention). Normal run: watch the active station/step advance. On a stall/alarm: the alarm strip flags it → click into **Alarm/Recovery** → see cause + suggested action + reset eligibility → (in Setup/Manual, gated) jog the offending actuator home on the **Manual/Jog** screen → reset → resume. Engineers use **Diagnostics/Engineering** for topic/state/command-log and connectivity.

### 5.4 Mode model

Use the model's **own enum** (§2.4) as the single source of truth shared by PLC, EAE panel HMI, and web HMI — don't invent HMI-only modes. v1 **displays** `LL_Mode`/`LocalMode`; v2 **commands** via the gateway. Robert's requested modes map onto it (full table in §6), with the honest caveat that **mode is not enforced in the control logic today** — real mode ownership/arbitration is a medium-term CAT change (§8).

### 5.5 Alarm/fault model

Move from one binary LED to a small **rationalised** alarm list (ISA-18.2): each alarm has priority, state (active/ack/shelved/cleared), cause, and a suggested recovery. v1 **derives** alarms from the UNS stream + manifest (e.g. "state unchanged past expected move time" = a derived timeout alarm; "interlock rule active" = an explained block) with **no PLC change**. The robust version surfaces real `fault_code`/timeout events once the CATs emit them (§8).

### 5.6 Manual control model

v1: **none** (read-only) — safest, honest. v2: manual/jog **only** in Setup/Manual mode, each command showing its **live permissive state** (green = allowed, greyed + reason = blocked), routed through the gateway with server-side interlock re-checks. The browser never bypasses an interlock; it *shows* it.

### 5.7 State-visibility model

Every component shows: current state (name + number), sensor LEDs (home/work), and — where it matters — a moving-analog "time-in-state vs expected" bar (surfaces a slow/stuck move before it's a hard fault). Retained UNS = correct posture even for a late-joining browser.

### 5.8 Data strategy (UNS/MQTT/OPC-UA)

- **Read:** MQTT `smc/#` → UNS `uns/v1/...` (retained JSON) → browser over WS. (OPC-UA is *not* needed for reading — the state is already on MQTT.)
- **Write (v2):** OPC-UA to the faceplate command tags via the gateway (message-level security). MQTT is **read/shadow only**; no command path on MQTT.
- **Infra gap (one line):** Mosquitto is TCP-only (`listener 1883`); browsers need `listener 9001` + `protocol websockets` (additive, doesn't touch the PLCs' 1883).

### 5.9 Role/access model

- **Operator:** Overview, Station, Alarm/Recovery; ack/shelve alarms; in v2, mode/cycle + reset.
- **Engineer:** + Component detail, Sequence/STD, Diagnostics, Manual/Jog (gated), command log.
- **Maintenance:** + I/O screen, timeout/parameter view, forced-state (v2, gated + audited).
- Enforced at the **gateway** (v2), not the browser (a read-only v1 needs only view-level roles).

### 5.10 Generated vs hand-designed (the DRY rule)

| Generate from VueOne/CATs | Hand-design once (rig-agnostic) |
|---|---|
| `hmi-manifest.json`: components, stations, PLC, CAT type, state tables, interlock rows (+ pre-rendered "explain" sentences), recipe rows, command-tag ids, mode/cycle enum | The generic **renderer** (overview/station/component/alarm/diagnostics templates) — built once, drives any rig |
| Faceplate *types* (already: Template Library) | The ISA-101 style/theme, colour standard, layout rules |
| Live topic list (already: StateSync sync-map) | The gateway (v2) command policy + audit |

**No per-rig screen authoring, ever** — a new/edited twin changes only the manifest; the renderer just re-renders. This is exactly what fixes the "hand-authored `MainScreen` drifts from the model" problem in §2.7.

---

## 6. Modes Of Operation (Task 4 detail)

Mapping Robert's requested modes to the model enum, with honest notes. **Today none of these are *enforced* by the control logic (§3.5); this table is the target once mode-gating exists (§8). v1 HMI only *displays* the current mode.**

| Requested mode | Model value | Who can enter | Allowed | Blocked | Permissives | HMI shows | Transition |
|---|---|---|---|---|---|---|---|
| **Off / Not Ready** | `0` No Mode | anyone (default) | none (view only) | all motion | — | "Not ready", why (no power/not homed/comms down) | auto on boot / comms loss / E-stop |
| **Auto** | `1` Automatic | operator | Start cycle, Stop, select CycleType | manual jog | all stations ready, no active alarm, all at home, part-present as required | mode, active process/step, cycle progress, next step | operator selects; blocked if not-ready or faulted |
| **Manual (step)** | `2` Manual | operator/engineer | advance ONE step at a time, Stop | continuous auto run | per-step permissives; no active alarm | current step + "advance" affordance | operator selects; only from a safe/idle posture |
| **Step / Cycle-Step** | `2` + `CycleType 3` Single | operator | run one full cycle then stop | continuous | as Auto | "single cycle" indication | select CycleType=Single in Auto/Manual |
| **Setup (jog)** | `3` Setup | engineer/maintenance | jog individual actuators (gated), home | sequence/auto run | actuator's live interlock permissive must be clear; other conflicting motion idle | per-actuator jog with permissive lamps | explicit entry; auto-exit to Off on timeout |
| **Maintenance** | `3` Setup (+ `9` Initial Position) | maintenance (role) | jog, return-to-home, view/adjust timeouts, forced state (v2, audited) | auto run; production commands | as Setup + role check | I/O, timers, forced-state banner | role-gated entry; audited |
| **Simulation / OLP Follow** | *not in enum — HMI/system state* | engineer | none on the rig | ALL rig commands | rig disconnected or shadow-only source selected | banner "SHADOW — following sim, not the rig"; source = VueOne/VC | HMI-side source switch; never commands the rig |
| **Faulted** | *derived (from alarms)* | auto-entered | acknowledge, go to Recovery | run/jog until cleared | — | active fault(s), cause, affected component | auto on timeout/interlock-latched/comms loss |
| **Recovery / Reset** | `9` Initial Position + `FaultReset` | operator (simple) / maintenance (complex) | Fault-Reset, guided return-to-home | resume Auto until preconditions met | fault cleared at source, all at home, no conflicting motion | reset eligibility (green when preconditions met), step-by-step recovery | from Faulted; on success → Off/Not-Ready → Auto |

Key honesty points:
- **Simulation/OLP-Follow** and **Faulted/Recovery** are *not* System_Mode values — the first is an HMI *data-source* state (shadow vs rig), the latter two are *derived* states over alarms + `9 Initial Position`. Don't force them into the INT enum; model them as HMI states layered on it.
- **Dry cycle** (`CycleType 4`) is a distinct, useful demo mode already in the model: run the sequence with part-sensors ignored — ideal for a safe expo run with no parts.
- Enforcing any of "blocked/allowed" above is a **CAT change** (§8), not an HMI change.

---

## 7. Proposed Screens (Task 5)

Each screen notes **data source** and whether the data **exists today** (E), needs **bridge derivation** (D), or needs a **CAT/model change** (C).

| # | Screen | Purpose & key content | Data source / status |
|---|---|---|---|
| 1 | **Overview (L1)** | Whole-rig layout; mode; active process + current step; **andon-style alarm strip**; connectivity (broker/PLCs/bridge). Grey-normal, colour-abnormal. | states **E** (UNS); step **D** (infer) or **C** (publish); alarms **D**; conn **E** (`_bridge/status`) |
| 2 | **Station (L2)** | One of Feed / Assembly / Disassembly / Robot-Clamp: that station's components, station mode/cycle, station fault, handshake status. | states **E**; station mode **E** (`LocalMode`); handshake **D** |
| 3 | **Component detail (L3)** | One actuator: state (name+num), sensors, **active interlocks (explained)**, time-in-state vs expected, (v2 gated) commands. | state/sensors **E**; interlock-active **D** (derive from RuleTable+state) or **C** (flag); timer **C** (expected-time) |
| 4 | **Manual / Jog (L3)** | Per-actuator jog with **live permissive lamps** (green allowed / greyed+reason blocked). Setup/Manual only. | permissive **D** (derive) / **C** (enforce); command **C**+gateway (v2) |
| 5 | **Sequence / STD (L3)** | Current process's step list, current step highlighted, the **waiting condition** ("WAIT clamp=2"), prev/next. | recipe rows **E** (manifest); live step **D**/**C**; wait cond **E** (manifest) + **D** (live) |
| 6 | **Alarm / Recovery (L3)** | Alarm list (priority, state, cause), **suggested recovery**, **reset eligibility** (green when preconditions met), ack/shelve. | **D** (derive v1) → **C** (real `fault_code`/timeout) |
| 7 | **I/O / Signals (L4)** | Sensors + actuators, **raw vs normalized** state, per-PLC (M262/M580/BX1). | states **E**; raw DI/DO **C** (not on MQTT today — needs a publish or OPC-UA read) |
| 8 | **Diagnostics (L4)** | MQTT/UNS health, VueOne socket, VC connection, PLC/rig connectivity, last-seen per component, BX1-silent detector. | **E**/**D** (`_bridge/status`, StateSync `lastSeen`) |
| 9 | **Cycle / Time (L4)** | Cycle time, per-station time, bottleneck, last-N cycles (moving-analog + sparkline). | **D** (bridge computes from state stream + `cycleId`) |
| 10 | **Engineering / Debug (L4)** | CAT state map, UNS topic map, state map, command log (v2). | manifest **E**; command log **C** (v2 gateway) |

**Note:** the strongest, safest, quickest subset is **1, 2, 3, 5, 8** — all achievable read-only from UNS + manifest with **no** control change. Screens **4, 6, 7, 10** are where the honest CAT/gateway work lives.

---

## 8. Required CAT / Interlock Changes (Task 8 detail)

These are the changes the HMI's diagnostic/recovery/mode features *depend on*. **All touch frozen surfaces → each is its own approved task, byte-identical `_gate` verified, rig-checked. None is an HMI-only change.** Ordered by value/effort.

1. **Five-State timeout-to-fault** — add an `E_DELAY` watchdog to `Five_State_Actuator_CAT` (mirror the Seven-State pattern): a move that doesn't confirm within a (configurable) time → set a fault. *Biggest single resilience win; kills the "silent forever stall".* (Gate + rig.)
2. **Expose `fault_code` + `interlock_active`/`active_rule`** per actuator so the HMI can show *why*. Without this the HMI can only *derive* faults heuristically.
3. **Clamp-locked permissive** — an interlock rule (or recipe gate) blocking swivel/robot/ejector until `clamp = closed` (and ideally an independent jaw sensor). Today it's recipe-order only.
4. **Grasp/vacuum confirm before lift** — for grippers + `CoverGripper`, gate the lift on a grasp/vacuum-OK signal (needs the signal to exist).
5. **Robot-zone / ready / complete gate** — interlock ejector/transfer against the robot's actual busy/clear state, not just a recipe WAIT.
6. **Bidirectional station handshake** — Disassembly should also wait for Assembly idle (today only Assembly waits for Disassembly).
7. **Enforced mode-gating** — make CATs honour Mode (Auto-only vs Manual/Setup) so manual jog is actually distinct from an auto command. This is the biggest conceptual change (mode ownership/arbitration).
8. **Configurable timeouts** — move the hardcoded 750/500 ms (and new Five-State timeouts) into config so they're rig-tunable without recompiling CATs.
9. **A live "current step" publish** (optional) — a small additive publish from the process engine so the HMI shows the true step (vs bridge inference).

**Deliberately NOT recommended:** re-implementing interlocks in the HMI/browser; the HMI *explains* interlocks, the PLC *enforces* them.

---

## 9. Data / Connectivity Architecture (Task 4 detail)

| Layer | Mechanism | Status |
|---|---|---|
| PLC → broker | Embedded `MqttStateFormatter`/`MqttPub` in 4 CATs → `smc/<component>` `{state:N}`, QoS1, retain=FALSE, one connection per PLC (`SMC`), broker `mqtt://192.168.1.50:1883` | **exists, frozen** |
| Normalisation | StateSync bridge (`Tools/statesync`, Python + paho): parse, seq/epoch/cycleId, dedup, **retained** `uns/v1/smc/<station>/<component>/state` JSON, `_bridge/status`+LWT, replay to VueOne socket | **designed, not built** (`Docs/STATESYNC_UNS_BRIDGE_DESIGN.md`) |
| Browser read | MQTT.js over **WebSockets** to `uns/v1/#` (retained → instant snapshot) | needs `listener 9001`/`protocol websockets` (additive) |
| Model | `hmi-manifest.json` from the Mapper (components/stations/interlocks/recipes/enums/command-tag ids) | **to build** (standalone emitter, 0 gate impact) |
| Browser write (v2) | HTTPS/SignalR → **.NET OPC-UA gateway** → EAE OPC-UA server (command tags only; ship `Enabled=true`+profiled) | **v2** — the only path that ever writes toward the PLC |

**Hard rules:** (1) no `smc/#` publish or PLC write from the bridge/browser; (2) the browser writes to the *gateway*, never the controller; (3) MQTT is read/shadow only; commands are OPC-UA-through-gateway; (4) the raw `smc/#` retain flag stays FALSE (frozen) — only `uns/...` is retained.

**Followers (already designed):** the same UNS feeds Visual Components 5.0 (native MQTT, JSON only, explicit topic list) and VueOne STD (localhost socket `VcComponentArg`+`EOM`) as one-way shadows. The web HMI is a *third* read consumer of the same UNS — no new pipeline.

---

## 10. Implementation Options (Task 6)

| Option | Pros | Cons | Effort | Risk | Maintainability | Demo value | Production realism |
|---|---|---|---|---|---|---|---|
| **A. EAE/IEC-61499 generated HMI (as-is)** | On-machine, native, already deployed, integrated with control | Single canvas, no alarms/diag/recovery, EAE-locked, not portable/remote | — (exists) | low | low (hand-laid overview drifts) | med | med (real panel) |
| **B. Extend the EAE HMI** (add screens/alarms in EAE Designer) | Reuses runtime + faceplates; on-machine | Hand-authored per rig (drift), NxtControl/C# canvas skill, no remote/browser | med | med | **low** (drift trap) | med | med |
| **C. Separate web HMI — generated + static HTML/MQTT.js** *(recommended v1)* | Model-driven (no per-rig screens), read-only-safe, remote, reuses UNS, zero build/licence, gate stays green | Read-only until gateway; needs WS listener; new (small) codebase | **low–med** | **low** | **high** (renderer built once) | **high** | med→high (via gateway) |
| **D. Hybrid — generated CAT HMI on panel + web supervisory** *(target)* | Best of both: native on-machine + remote supervisory; each does what it's good at | Two HMIs to keep coherent (both read the same tags — mitigated) | med | low | high | high | **high** |
| **E. VC / VueOne debug view** | Already-designed shadow; great for visualising motion | Not an operator HMI; sim/OLP-follow only; no commands | low (per StateSync) | low | med | high (visual) | low (not an HMI) |
| **F. MQTT/UNS browser dashboard + WS bridge (FUXA / Node-RED / Ignition)** | Fast real web HMI, multi-protocol, andon/trend widgets out-of-box | Screens drawn in-tool (not generated → drift), MQTT-command temptation, licence (Ignition) | low (FUXA/NodeRED) / med (Ignition) | med (command-via-MQTT if misused) | med | high | med (FUXA) → high (Ignition) |

**Recommended path:**
- **Immediate demo:** **C** — `HmiManifestEmitter` (Feed slice first) + one generic static HTML/JS renderer reading UNS over WS; ISA-101 overview + station + component tiles + explained-interlock panel; **read-only**. Optionally stand up **FUXA/Node-RED (F)** in parallel for a richer andon look if a polished visual is wanted fast (accept it's hand-drawn, not generated).
- **Medium-term robust:** **D** — keep the EAE panel HMI on-machine; grow the web HMI to full read-only supervisory (all stations, sequence/STD, diagnostics, cycle-time). In parallel land the medium CAT changes (§8.1–8.2, 8.6) so alarms/timeouts are *real*, not just derived.
- **Long-term product-quality:** **D + gateway** — add the .NET OPC-UA command gateway (Blazor + OPC-UA .NET) for mode/cycle/reset + gated jog, role-based access, an ISA-18.2 alarm manager, and a historian/cycle-time view.

---

## 11. Recommended Roadmap

1. **PoC (days):** `HmiManifestEmitter` (Feed slice) + `hmi.html`+`hmi.js` reading UNS; `listener 9001` on Mosquitto; StateSync bridge Feed slice. Read-only overview of feeder/checker/transfer/PartInHopper + an explained-interlock panel. Acceptance: retained snapshot on load; interlock rule lights with its sentence; `_gate` byte-identical; broker log shows browser only *subscribes*.
2. **Full read-only HMI (weeks):** extend manifest + renderer to Assembly/Disassembly/covers/ejector/robot/clamp; add Sequence/STD (recipe rows + live step via inference), Station screens, Diagnostics/connectivity, Cycle/Time. Add the andon alarm strip (derived alarms).
3. **Real alarms & resilience (parallel, gate-verified):** Five-State timeout-to-fault + `fault_code`/interlock-active exposure (§8.1–8.2); clamp-locked permissive (§8.3); HMI switches from *derived* to *real* faults.
4. **Command gateway (v2):** Blazor + OPC-UA .NET; enable/profile the command tags in EAE; wire Mode/Cycle/FaultReset + Setup/Manual jog with server-side permissive re-checks + audit log; role-based access.
5. **Later:** ISA-18.2 alarm manager (ack/shelve/priority/history), historian/trend, mode enforcement in CATs (§8.7), multi-client/remote hardening.

---

## 12. Open Questions

1. **Demo target surface:** is the expo HMI meant to run on the existing `HMIP6`/`HMIB1X` panels (native EAE) or on a laptop/tablet browser? (Drives whether we grow the EAE HMI or the web HMI first.)
2. **Read-only vs command:** does Robert want the web HMI to *command* the rig (mode/reset/jog), or only monitor + explain? (Command ⇒ the v2 gateway is mandatory; monitoring ⇒ v1 ships now.)
3. **Scope for CAT changes:** are §8's timeout/fault/interlock additions in scope? They're the honest path to real diagnostics/recovery but each touches a frozen surface (gate + rig). If out of scope, the HMI stays *derived/heuristic* — acceptable for a demo, not for production.
4. **Which model — clamp or no-clamp (`_vc`)?** The expo demo has run both; the reference `MainScreen` names don't include a Clamp faceplate. Confirm the canonical demo model so the manifest/overview match.
5. **VC licence tier** (Professional/Premium?) — gates the VC MQTT follower (already flagged in StateSync).
6. **Safety ownership:** none of this soft logic is a safety function. Who owns/validates the *actual* machine safety (E-stop, guarding, robot safe-stop)? The HMI must never imply safety it doesn't provide. The bench rig is currently flagged unsafe (damaged clamp / swivel collision) — **no rig actuation from any new HMI until safety clearance.**
7. **Should the Mapper own the overview composition?** Today `MainScreen` is hand-laid-out and drifting. Generating it (from the manifest layout) removes the drift but is new generator work — worth it if the EAE HMI is kept as the primary.
8. **Live "current step":** infer at the bridge (zero PLC change, approximate) or add a gated additive publish (accurate, touches the engine)? (§2.6 of the StateSync doc.)

---

## 13. Appendix: File / Code References

**Reference EAE HMI (`C:\Schenider EAE Projects\SMC_Rig_Expo_withClamp\`)**
- `HMI\HMI.csproj` — HMI project (NxtVersion 24.1, netstandard2.0, DefaultLight, HMIBaseSymbols, 16 SE.* libs).
- `HMI\MainScreen.cnv.Designer.cs` — the 1010×600 overview; Area_HMI + Process1..4 + Pusher/Checker/Shaft_Hr/Shaft_Vr/CoverPnpVr/CoverPnPHr placements.
- `HMI\StartCanvas_3.cnv.*` — start canvas.
- `HMI\Area_CAT\Area_CAT_sDefault.cnv.xml` + `.Designer.cs:46-62` — Mode/Cycle/Fault-Reset buttons + LEDs.
- `HMI\Station_CAT\Station_CAT_sDefault.cnv.xml` + `.Designer.cs:59-82` — station mode/cycle/fault-reset.
- `HMI\Process1_CAT\...cnv.xml` — step text + `ManualFeedCmd` (P1 only).
- `HMI\Five_State_Actuator_CAT\...cnv.xml` — read-only state/sensors/counter.
- `HMI\Alarms\AlarmClasses.xml` (224 B), `SystemAlarmClasses.xml` (1037 B) — framework defaults only.
- `HMI\Configurations\` (TagValueEditor empty; HistoryTrend/Journalling stubs); `HMI\Languages\neutral\Dictionary.Resources.xml` (empty).
- `AvevaOMI\AvevaOMI.omiproj` — stub, no content.
- `Topology\` — `HMIB1X_1` @192.168.1.209, `HMIP6_1` @192.168.1.2 (SoftdPAC).
- `IEC61499\Seven_State_Actuator_CAT\Seven_State_Actuator_CAT.fbt:61-70` — E_DELAY timeout 750/500 ms.
- `IEC61499\...\SevenStateActuator2.fbt:98-162` — swivel ECC + timer.
- `IEC61499\Five_State_Actuator_CAT\...Five_State_Actuator.fbt:53-102` — sensor-driven ECC, **no timeout**.

**VueOneMapper generator (`C:\VueOneMapper\`)**
- `CodeGen\CodeGen\Planning\SystemLayoutInjector.cs:951-980, 2328-2332` — Area_HMI/Station1_HMI/Station2_HMI instances + wiring.
- `CodeGen\CodeGen\Artefacts\Resource\ResourceWireEmitter.cs:68-74` — AreaHMIAdptr/StationHMIAdptr wiring.
- `CodeGen\CodeGen\Artefacts\Templates\HmiTemplatePatcher.cs:69-103` — HMI frame patch (visual only).
- `CodeGen\CodeGen\Artefacts\Templates\TemplateLibraryDeployer.cs:81-144` — deploy CATs/adapters (UniversalHmiCats = Area_CAT, Station_CAT).
- `CodeGen\CodeGen\Planning\Interlocks\InterlockPlanner.cs:13-147`, `InterlockEmitter.cs:16-133` — RuleTable extraction/emission (+ reverse crossings).
- `Config\interlock.yaml` (ruleArraySize 10); `Config\recipes.yml` (clampClose/clampOpen); `Config\smc-rig.yml` (PartAtAssembly DI08/id 3).
- `CodeGen\CodeGen\Planning\Recipes\AssemblyRecipe.cs:66-130` — material gate → clamp → bearing → clamp-open.
- `MapperUI\MapperUI\bin\Debug\net10.0-windows\Input\Control.xml` — the live clamp twin.
- `...\_gated1\Demonstrator\IEC61499\CommonInterlockEvaluator.fbt:175-213` — rule evaluation.
- `Docs\SMC Rig Expo demo exposed OPCUA tags.docx` (A. Evans) — canonical tag surface + Mode/Cycle enums.
- `Docs\STATESYNC_UNS_BRIDGE_DESIGN.md`, `Docs\WEB_HMI_GENERATION_DESIGN.md` — prior state-pipeline + web-HMI designs (this report corrects §2 of the latter).
- `MQTT\mosquitto.conf` — broker (add `listener 9001`/`protocol websockets` for the browser).

## 14. Appendix: Web Sources

**ISA-101 / High-Performance HMI**
- ISA-101 standard overview — https://www.isa.org/standards-and-publications/isa-standards/isa-101-standards
- "Unpacking ISA-101: Beyond the Misunderstood Grayscale" (display levels + grayscale nuance) — https://malisko.com/isa-101/
- ISA-101 practitioner guide — https://plcprogramming.io/blog/hmi-design-best-practices-complete-guide
- High-Performance HMI techniques (moving analog, sparkline, radar) — Inductive Automation — https://www.docs.inductiveautomation.com/docs/8.3/ignition-modules/vision/common-tasks-in-vision/high-performance-hmitechniques
- Rockwell Process HMI Style Guide — https://literature.rockwellautomation.com/idc/groups/literature/documents/wp/proces-wp023_-en-p.pdf
- ABB 800xA High Performance Graphics — https://new.abb.com/control-systems/system-800xa/800xa-dcs/operator-interfaces-hmi/high-performance-graphics

**ISA-18.2 / Alarm Management**
- ISA-18 series — https://www.isa.org/standards-and-publications/isa-standards/isa-18-series-of-standards
- ANSI/ISA-18.2 overview (rationalisation, shelving, states) — https://blog.ansi.org/ansi/ansi-isa-18-2-alarm-systems-process-industries/
- exida alarm-management resources — https://www.exida.com/Alarm-Management/Resources

**Web-based industrial HMI / SCADA**
- FUXA (open-source web SCADA/HMI, MQTT/OPC-UA/Modbus) — https://github.com/frangoteam/FUXA
- Ignition SCADA / Perspective — https://inductiveautomation.com/scada-software/
- FlowFuse — "Building a Web HMI for Factory Equipment Control" — https://flowfuse.com/blog/2025/11/building-hmi-for-equipment-control/
- Modern web design principles for SCADA — https://www.cse-icon.com/modern-web-design-principles-scada/

**UNS / MQTT / Sparkplug / OPC UA**
- Ignition Unified Namespace — https://inductiveautomation.com/solutions/unified-namespace
- HiveMQ — Implementing UNS with MQTT Sparkplug — https://www.hivemq.com/blog/implementing-unified-namespace-uns-mqtt-sparkplug/
- MQTT vs OPC UA for IIoT — https://www.machinecdn.com/blog/mqtt-vs-opcua-industrial-iot/
- FlowFuse — reading/writing PLC data via OPC UA — https://flowfuse.com/blog/2025/07/reading-and-writing-plc-data-using-opc-ua/

**Automotive / digital factory / andon**
- Ford Advanced Manufacturing Center — https://connectedmanufacturing.wbresearch.com/blog/ford-motor-company-advanced-manufacturing-center-strategy
- COPA-DATA zenon andon boards (automotive) — https://www.copadata.com/en/industries/automotive/automotive-solutions/andon-boards/
- Digital andon systems (real-time alerts, escalation, OEE) — https://oxmaint.com/industries/manufacturing-plant/andon-system-manufacturing-real-time-alert-software

**EcoStruxure Automation Expert / IEC 61499**
- EcoStruxure Automation Expert (product) — https://www.se.com/us/en/product-range/23643079-ecostruxure-automation-expert/
- EAE / IEC 61499 community forum — https://community.se.com/t5/EcoStruxure-Automation-Expert-IEC/bd-p/ecostruxure-automation-expert-forum

---

*Prepared read-only. No CAT, recipe, interlock, HCF, MQTT, EAE, or `C:\Demonstrator` artefact was modified. Every §8 change and the v2 gateway are explicitly out of scope until requested and would each be an approved, gate-verified, rig-checked task.*
