# Mapper-generated EAE HMI — definitive capability report

**Date:** 2026-07-26 · **Scope:** investigation only. Nothing was modified, regenerated, deployed,
committed, or copied. Every claim is labelled **VERIFIED** (read directly from the artefact),
**INFERRED** (reasoned from what was read), or **UNVERIFIED** (could not be established).

---

## 1. Executive verdict — seven separate verdicts, deliberately not collapsed

| # | Verdict | Status |
|---|---|---|
| 1 | **Generator source is correct** | **INFERRED** — behaves correctly on four trees; no formal test suite exists |
| 2 | **The correct DLL is loaded** | **VERIFIED as of 19:33** — all five copies identical; but this is fragile, see §3 |
| 3 | **Generated HMI compiles** | **VERIFIED** — `Finished building HMI, success=True` (19:53:33) |
| 4 | **HMI deploys** | **VERIFIED** — `Files downloaded (269 ms)` / `SendCommand command is success` (19:53:36) |
| 5 | **Runtime values display** | **UNVERIFIED** — bindings resolve statically; no live panel observation |
| 6 | **HMI is command-free** | **VERIFIED** for the live generated tree — zero command surfaces, §8 |
| 7 | **Machine is safe to operate** | **NOT ESTABLISHED, and not establishable here** — §6, §11 |

**The one thing not to conclude:** verdicts 3, 4 and 6 do not add up to verdict 7. A successful
build and deploy proves the HMI is well-formed and reached the panel. It proves nothing about the
plant. The HMI can no longer *ask* the controller to do anything — that is all.

---

## 2. Architecture and generation flow

**VERIFIED** — single entry point:

```
SystemLayoutInjector.GenerateFeedStationSyslayToPath
  └─ (after EnsureOpcuaXmlBesideArtefact) → CodeGen.Hmi.HmiGenerator.Emit(syslayPath, config)
```

Both MapperUI's Generate button and the hidden runner call that method, so both paths get the HMI.
`HmiGenerator.Emit` then runs (`CodeGen/CodeGen/Hmi/HmiGenerator.cs`):

| Step | Method | Effect |
|---|---|---|
| 1 | guard `config.HmiReadOnly` | **throws** if false — §10 |
| 2 | `HmiTemplateLibrary.Load` | reads `Template Library\HMI\Faceplates` (7 CAT folders) |
| 3 | `ResetGeneratedCanvases` | deletes root `*.cnv.*`, `bin`, `obj`, and its own template folders |
| 4 | `CopyDirectory` | Shell + each faceplate folder → `Demonstrator\HMI\` |
| 5 | **`HmiCommandStripper.StripAll`** | removes command capability from the **deployed copies** |
| 6 | `HmiTemplateLibrary.LoadFrom(hmiDir)` | re-reads what was actually written |
| 7 | `HmiPlanner.Plan` | syslay → screens (process-grouped) |
| 8 | `HmiCanvasEmitter.Emit` | `.cnv.cs` / `.cnv.Designer.cs` / `.cnv.resx` per canvas |
| 9 | `HmiProjectEmitter` | `CanvasesResolutionList.xml` + `HMI.csproj` (rebuilt from disk) |
| 10 | `HmiCatCfgEmitter.Emit` | `<CAT>.cfg` from the **stripped** templates |
| 11 | `HmiRuntimeEmitter.Emit` | HMI_NET logical device + topology + registrations |
| 12 | `HmiPlanValidator.Validate` | structural + read-only checks; **fatal ones throw** |

**Binding mechanism (VERIFIED, unchanged):** a placed symbol binds to its instance solely through
`TagName`, which is the syslay `<FB ID>`. No OPC UA, no MQTT, no new control wiring.

### One entry point — and one path that silently produces no HMI

**VERIFIED.** `HmiGenerator.Emit` has exactly **one** call site repo-wide:
`SystemLayoutInjector.cs:1362`. It runs only when a non-null `MapperConfig` reaches the 5-arg
overload, because `Emit` returns immediately on a null config (`HmiGenerator.cs:23`).

| Caller | Overload | HMI generated? |
|---|---|---|
| `MainForm.cs:989` → `GenerateStation1TestSyslay` → `SystemLayoutInjector.cs:2178` | 5-arg, passes `config` | **yes** |
| `MainForm.cs:1398` → `GenerateFeedStationSyslayToPath(…, bindings, out report)` | 4-arg → forwards `config: null` (`:799`) | **no — silently** |

**Defect D10:** the Feed-station generate button writes a syslay and no HMI, with no warning. The
resulting Demonstrator keeps whatever HMI was there before, which may have been generated from a
different model.

---

## 3. DLL / runtime provenance — the most fragile part of the system

**VERIFIED**, measured 19:33:

| SHA-256 (16) | Timestamp | Path |
|---|---|---|
| `cac8c7ddc7a7400d` | 19:33:21 | `VueOneMapper\CodeGen\CodeGen\bin\Debug\net10.0\CodeGen.dll` |
| `d8d0ab613fd95934` | 19:33:03 | `VueOneMapper\MapperUI\MapperUI\bin\Debug\net10.0-windows\CodeGen.dll` |
| `d8d0ab613fd95934` | 19:33:04 | `V-Dev\VueOneFullVersion\...\Published_Alper\CodeGen.dll` |
| `d8d0ab613fd95934` | 19:33:04 | `V-Dev\VueOneVcVersion\...\HiddenRunner\bin\Debug\net10.0\CodeGen.dll` |
| `d8d0ab613fd95934` | 19:33:05 | `V-Dev\VueOneVcVersion\...\HiddenRunner\bin\Release\net10.0\CodeGen.dll` |
| `d8d0ab613fd95934` | 19:33:06 | `V-Dev\VueOneVcVersion\...\vueone_vc\Published_Alper\CodeGen.dll` |

**The repo build hash differs from the five deployed copies. INFERRED that this is build
non-determinism, not a content difference:** the newest HMI source is `HmiCommandStripper.cs` at
**19:26:38**, and both builds (19:32:50 → `d8d0ab61`, 19:33:21 → `cac8c7dd`) postdate it with no
intervening source edit. .NET embeds a fresh MVID per compilation, so identical source yields
different bytes. **UNVERIFIED** by decompilation.

### This has already caused two false "fixed" claims today

**VERIFIED from timestamps.** A defect was fixed in source at 19:26 and CodeGen rebuilt at 19:27,
but `MapperUI\bin\CodeGen.dll` was still **18:25** and the four V-Dev copies were still **16:28**.
A generate at 19:28 therefore reproduced the identical `CS1513`/`CS1026` errors. **Building CodeGen
changes nothing that VueOne or MapperUI executes.**

### Required sequence after ANY CodeGen change (VERIFIED as effective)

1. `dotnet build CodeGen\CodeGen\CodeGen.csproj`
2. `dotnet build MapperUI\MapperUI\MapperUI.csproj --no-incremental` (MapperUI must be closed)
3. Copy the fresh `CodeGen.dll` to **all five** paths above
4. Confirm all five hashes match
5. Relaunch MapperUI / VueOne — a running process holds the old assembly

**Gap (defect):** nothing in the toolchain enforces or checks this. There is no version stamp in
the generated output identifying which `CodeGen.dll` produced it, so a stale-DLL generation is
indistinguishable from a fresh one by inspecting the output alone.

---

## 4. Current live HMI contents — `C:\Demonstrator\Demonstrator`

**VERIFIED.** Generated **19:51**, HMI built **19:53**.

**12 canvases:** `MainScreen`, `FeedStationScreen`, `AssemblyStationScreen` 1–3,
`DisassemblyScreen` 1–3, `PlantOverviewScreen` 1–3, plus the shell `StartCanvas_2`.
`CanvasesResolutionList.xml` sets `FirstCanvas="MainScreen"`.

**Screens are process-grouped, not CAT-typed.** `FeedStationScreen` (verified verbatim):

```
Feed_Station  → Process1_Generic.sDefault
Checker, Feeder, Transfer → Five_State_Actuator_CAT.sDefault
PartInHopper  → Sensor_Bool_CAT.sDefault
roBanner.Text     = "MONITORING ONLY - HMI COMMANDS DISABLED"
screenTitle.Text  = "Feed Station"
cap_PartInHopper.Text = "Part In Hopper"      ← generated caption
MainScreen.Text   = "Main Screen"             ← ChangeCanvasButton
```

**Banner present on every screen; no `SetupScreen`; captions human-readable;
Previous/Next present on multi-page families.**

### ⚠ The live tree is NOT `_se`

**VERIFIED from the syslay:** `Bearing_PnP` has `Type="Five_State_Actuator_CAT"` (i.e. the 5-state
swivel) and **there is no `Clamp` FB**; 34 FBs total. That is the **`_sw5_noclamp`** profile, not
`_se` (which has a Clamp and a 13-state swivel → `Seven_State_Actuator_Centre_Home_CAT`).

**INFERRED:** either a different model was generated, or VueOne loaded a different model than the
one selected. `CLAUDE.md` documents a live hazard where two models share a `SystemID` and VueOne
keys the loaded model on the `Control.xml` header rather than the folder. **This should be checked
before trusting anything deployed from this tree.**

---

## 5. Capability matrix

**VERIFIED** by reading every `TagName` binding in each live `<CAT>_sDefault.cnv.Designer.cs`.
"Displayed" = a control is bound to that value. Every row is **commandable: NO**.

| Component class | Displayed | Source | Runtime path | Limitations |
|---|---|---|---|---|
| **Area** | `System_Mode`, `System_Cycle_Type`, `LL_Fault_Status`, `Area_Name` | `Area_CAT_HMI.fbt` | AreaHMIAdptr → `IThis` | mode/cycle shown as **numbers**, no name |
| **Station1/2** | `LocalMode`, `LocalCycleType`, `LL_Mode`, `LL_CycleType`, `LL_Fault`, `StationName`, `ParentName` | `Station_CAT_HMI.fbt` | StationHMIAdptr | numbers only; `StationName` fed by an FB InputVar the Mapper **does not set** → blank |
| **Feed/Assembly/Disassembly** | **`ThisStepText` only** | `Process1_Generic_HMI.fbt` | `ProcessEngine.SCNF` → TSString | `NextStepText`, `PreviousStepText`, `ActuatorName`, `StateValue` are in the contract but **not bound**. Text is an engine-phase literal (`'Command step'`, `'Wait step'`) — **not** an operator instruction |
| **Five-state actuators** | `current_state_to_process`, `atHome`, `atWork`, `toWorkPLC`, `toHomePLC`, `fault_code`, `Work1Interlock`, `HomeInterlock`, `component_name`×3 | `Five_State_Actuator_CAT_HMI.fbt` | ring → `IThis` | state is a **number**, no name; `fault_active` **not bound**; `MoveAllowed` / `ActiveRuleIndex` / `ActiveSourceID` / `ActiveBlockedState` **not bound** → no live blocked reason; `component_name` is **blank** (see §11) |
| **Bearing_PnP (seven-state)** | as above + `atWork1`, `atWork2`, `toWork1PLC`, `toWork2PLC`, `Work1Interlock`, `Work2Interlock` | `Seven_State_..._HMI.fbt` | ring → `IThis` | same limitations |
| **Sensors** | `State` only | `Sensor_Bool_CAT_HMI.fbt` | ring → `IThis` | the `name` TextBox has `TagName=""` → sensor name **not** displayed; generated canvas caption compensates |
| **Robot task** | **nothing** | — | — | `Robot_Task_CAT_sDefault.cnv.Designer.cs` is an **empty stub** — `InitializeComponent()` sets only `this.Name`. No controls, no `SymbolSize`. The tile renders blank |
| **Faults** | `fault_code` (actuators), `LL_Fault` / `LL_Fault_Status` (station/area) | | | `fault_active` unbound; `sFault` pop-up exists and is registered but is only reachable from a symbol that no longer opens it |
| **Interlocks** | configured blocked flags `Work1Interlock` / `HomeInterlock` / `Work2Interlock` | | | **live blocked reason not displayed** — rule index, source ID and blocked state are unbound |
| **Controller/device allocation** | not displayed | | | no device or PLC-allocation view is generated |
| **MQTT / telemetry status** | not displayed | | | Telemetry FBs have no HMI faceplate; connection state is visible only in EAE Watch |

**Modes:** Auto / Manual / Setup are **displayed as integers** (Station `LocalMode`, Area
`System_Mode`) and are **not selectable**. Run / Stop / Reset / jog: **absent by construction**.

---

## 6. Auto / Manual / Setup behaviour, and what STOP actually reached

### ⚠ STOP never stopped the process engine — VERIFIED

`ProcessRuntime_Generic_v1.fbt` declares `Mode : INT` and `CycleType : INT` as InputVars, and
`Process1_Generic.fbt` routes `stationAdptr_in.MCTRL → ProcessEngine.MREQ` and
`stationAdptr_in.CTCTRL → ProcessEngine.CTREQ`. **But the ECC has 11 transitions and not one reads
Mode, CycleType, MREQ or CTREQ:**

```
START→INIT [INIT]                    INIT→IDLE1 [1]
IDLE1→ISSUE_CMD [CurrentStepType=1]  IDLE1→WAIT_STEP [CurrentStepType=2]
IDLE1→END [CurrentStepType=9]        ISSUE_CMD→ADVANCE [1]
WAIT_STEP→IDLE1 [WaitSatisfied]      WAIT_STEP→WAIT_HOLD [NOT WaitSatisfied]
WAIT_HOLD→WAIT_STEP [state_change]   ADVANCE→IDLE1 [1]        END→ADVANCE [1]
```

`INIT→IDLE1` and `END→ADVANCE` are unconditional. The old `StopButtonClick` fired
`FireEvent_CTCNF(0)` (`Station_CAT_sDefault.cnv.cs:68` in the previous build) — **CycleType=0 could
not stop recipe execution.** Removing the button removes a misleading control; it does not add a
stop. Likewise AUTO=1, MANUAL=2, SETUP=3 reached `Mode`, which no transition reads.

**Setup jog would have moved the actuator.** The actuator CATs wire
`IThis.cmd_event → ActuatorCore.setup_event` and `IThis.toWork/toHome → toWorkSetup/toHomeSetup`,
and the core's launch arcs carry a `mode = 3 AND toWorkSetup` disjunct. That path is now
unreachable from the HMI because no control can raise `cmd_event`.

---

## 7. Monitoring and command paths

**Monitoring (live):** PLC → CAT `IThis` (HMI SIFB) → EAE's internal accessor service → the bound
control, addressed by `TagName` = syslay FB Id. **VERIFIED** statically for every placed symbol;
**UNVERIFIED** at runtime.

**Command (severed):** the former path was control → `.Click` handler → `FireEvent_*` →
`IThis` EventOutput → CAT → `ActuatorCore.setup_event` / Station-Area `Mode`/`CycleType`. It is cut
at the first two links and at the contract, §8.

---

## 8. Read-only enforcement evidence

**VERIFIED across the entire live `HMI\` tree:**

| Check | Result |
|---|---|
| `.cnv.xml` declaring `<EventOutputs>` or `<Outputs>` with content | **0** (grep: *No matches found*) |
| Non-navigation Button instantiations in any Designer | **0** — every Button is `ChangeCanvasButton` |
| `.Click +=` wiring anywhere | **0** (grep: *No matches found*) |
| `FireEvent` **call sites** in `*.cnv.cs` | **0** |
| `IsOnlyInput = false` | **0** |
| `sSetup` / `setup` registered in any `<CAT>.cfg` | **0** |
| Generated Setup screens | **0** |

**Enforcement is by contract, not by hiding** — the decisive point. Contracts were rewritten to
`<EventOutputs />` / `<Outputs />`, so even a re-added button would have no channel to fire into.
Registered symbols are `sDefault` per CAT plus `sFault`/`sInterlock` (`IsFaceplate="true"`) on
Five_State.

**Residual — `.event.cs` (INFERRED inert, UNVERIFIED at runtime):** eight `<CAT>.event.cs` files
still *define* `FireEvent_MCNF` / `FireEvent_CTCNF` / `FireEvent_FRCNF` / `FireEvent_cmd_event`
(e.g. `Area_CAT.event.cs:310,326,342`). They are `public` methods compiled into `HMI.dll` with
**no caller anywhere** and no declared output in the contract. They were left in place because
`<CAT>.def.cs` depends on the file. Whether invoking one would reach the controller with an empty
contract is **UNVERIFIED** — untested, and untestable without deliberately adding a caller.

---

## 9. Model-variant behaviour

Screens and captions are derived with **no per-component code**:

- **Drawable** = the emitted CAT declares `<Type>_HMI.fbt` **and** a faceplate template exists.
- **Ownership** = each `Process1_Generic` instance's compiled `Recipe` parameter in the syslay:
  `CmdTargetName` → the actuator's `actuator_name`; `Wait1Id` → the component's `actuator_id`/`id`.
- **Many-to-many** handled: a component appears on every process screen that owns it (37 placements
  vs 25 distinct components in `_se`). Duplicates within one canvas are rejected.
- **Sentinels** (`cycle_ready`, a process naming itself) resolve to no component and are dropped.
- **Residual** components claimed by no process go to `PlantOverviewScreen` — never dropped.
- **Captions** are a pure string transform of the syslay FB `Name`; `actuator_name` (the lower-case
  ring/MQTT key) is never displayed.

| Variant | Coverage |
|---|---|
| `_se` (clamp, 13-state) | **VERIFIED** — temp generation, 12 canvases, 0 problems |
| `_vc` (no clamp, 13-state) | **VERIFIED** — temp generation, 12 canvases, 0 problems |
| `_sw5` (clamp, 5-state) | **VERIFIED** — temp generation, 11 canvases, 0 problems |
| `_sw5_noclamp` (no clamp, 5-state) | **VERIFIED** — this is what the live tree contains, §4 |

All four variants are now covered by real generations. The swivel axis changes only which faceplate
one tile uses; the clamp axis changes the component inventory by one tile.

### Comparison with Jyotsna's reference — VERIFIED

`…\Jyotsna\SMC_Rig_Expo_withClamp_RevPi_20260625_Jyostna-125057240.sln (1)\HMI`:

| | Reference | Generated (live) |
|---|---|---|
| Canvases | **2** (`MainScreen`, `StartCanvas_3`) — hand-composed single mimic | **12** — derived, process-grouped |
| Contracts declaring outputs | **4** | **0** |
| Non-navigation buttons | **24** | **0** |
| `FireEvent` call sites in `.cnv.cs` | **2** | **0** |

The reference is a command HMI with a hand-drawn plant mimic; ours is a derived monitoring HMI.
Neither is a superset: the reference has richer bespoke graphics and real command controls; ours
covers every component automatically and adapts to model changes with no hand editing.

---

## 10. Validation coverage

| Condition | Behaviour |
|---|---|
| Command-capable symbol placed in read-only mode | **HARD FAIL** (throws) |
| Dangling Designer reference after stripping | **HARD FAIL** |
| Unbalanced braces/parentheses after stripping | **HARD FAIL** (added after the CS1513 defect) |
| Canvas overflow | error listed, **not fatal** |
| Duplicate component on one canvas | error listed, **not fatal** |
| Broken navigation target | error listed, **not fatal** |
| `TagName` not an FB in the syslay | error listed, **not fatal** |
| CAT with `_HMI.fbt` but no template | warning; instances silently not placed |
| `HmiReadOnly = false` | **HARD FAIL** — false does not enable commands |

### Where validation gives false confidence — the important part

1. **`HmiReadOnly` is checked at the top of the run, but the read-only *evidence* checks only run at
   the end.** If an earlier step throws, no read-only assertion has executed. Absence of a violation
   message is not proof of absence of violations.
2. **The stripper only iterates library templates.** Any CAT folder under `HMI\` without a matching
   `Template Library\HMI\Faceplates` folder is **never stripped and never validated**. The live tree
   contains one: `Seven_State_Actuator_CAT` (7 files, compiled by `HMI.csproj`). **VERIFIED benign
   today** — 0 contract outputs, 0 buttons, 0 `FireEvent` call sites — but it is an unaudited path.
3. **Canvas overflow, duplicates, broken nav and unresolved `TagName` are non-fatal.** Generation
   reports success with any of them present.
4. **No check that the loaded `CodeGen.dll` is current.** This is the single highest-value missing
   check — it has produced two false "fixed" claims today.
5. **Static binding ≠ live value.** Every `TagName` resolving to a syslay FB proves the reference is
   well-formed, not that a value ever arrives. Nothing here verifies runtime display.
6. **No syslay↔sysres mirror check for HMI bindings.** The plan validates against the syslay only.
7. **"Generation failed" does not mean "nothing was written".** The fatal throw fires after every
   file is on disk (D9). A run that ends in a read-only rejection still leaves a complete HMI
   project — and the operator has no way to tell from the tree alone that it was rejected.
8. **One generate path emits no HMI at all and says nothing** (D10). Success is reported; the HMI
   is simply whatever was there before, possibly from a different model.

---

## 11. Known defects and unavailable-by-contract features

**Defects**

| # | Defect | Severity |
|---|---|---|
| D1 | `Robot_Task_CAT_sDefault` is an empty stub — the robot tile renders nothing | monitoring loss |
| D2 | `component_name` is bound on every actuator tile but never populated: the CAT wires `actuator_name → FB13(ANY2ANY) → IThis.component_name`, and **nothing drives `FB13.REQ`**, so `name_event` never fires. Generated canvas captions compensate | cosmetic given captions |
| D3 | Stale `HMI\Seven_State_Actuator_CAT` folder — no template, never stripped, still compiled | audit gap |
| D4 | `.event.cs` `FireEvent_*` definitions remain with no callers | residual, §8 |
| D5 | Sensor tile does not display its own name (`TagName=""`) | cosmetic |
| D6 | Process tile shows only `ThisStepText`, and that is an engine-phase literal | see below |
| D7 | Live tree appears to be `_sw5_noclamp` while `_se` was intended | **must be resolved** |
| D8 | **`SetTopologyEquipmentToNoConf` blanks EVERY topology equipment file, not just M262** | **latent, high** |
| D9 | The read-only rejection throws *after* every file is written | design flaw |
| D10 | `MainForm.cs:1398` generates a syslay with **no HMI**, silently | gap |

### D8 — the highest-value finding, and my earlier report missed it

**VERIFIED.** `M262SysdevEmitter.cs:377-401`. The comment says "Force every **M262** Topology
Equipment endpoint to NOCONF", but the loop is unfiltered:

```csharp
foreach (var path in Directory.EnumerateFiles(topoDir, "Equipment_*.json"))
    ... Regex.Replace(text, "\"ipAddress\"\\s*:\\s*\"[^\"]*\"", "\"ipAddress\": \"0.0.0.0\"")
    ... Regex.Replace(rewritten, "\"domain\"\\s*:\\s*\"[^\"]*\"", $"\"domain\": \"{ZeroDomain}\"")
```

It would blank the IP and zero the broadcast domain of **every device in the topology** — the HMI
host `Workstation_1` (192.168.1.2), `HMIP6_1`, `M580dPAC_1` (.20), `HMIB1X_1` (.151/.209) and the
EtherNet/IP cover coupler `EtherNetIPDevice_1` (.210). It runs at `:96`, inside `if (!preserveDevice)`,
and **after** `HmiRuntimeEmitter` has written and self-validated the HMI topology.

**It did NOT fire on the run that produced this tree — VERIFIED.** Every real address survives:

```
EtherNetIPDevice_1  192.168.1.210      M580dPAC_1  192.168.1.20
HMIB1X_1            192.168.1.151/.209 M262dPAC_1  192.168.1.10
HMIP6_1             192.168.1.1/.2     Workstation_1  192.168.1.2
```

The `0.0.0.0` values present are unconfigured second NICs (template defaults), not clobber residue.

**So this is latent code, not active corruption — but it is a landmine.** On any generation where
`preserveDevice` is false (e.g. after a Clean that deletes and recreates the M262 device) it would
simultaneously break HMI deployment *and* BX1 cover I/O. It is out of HMI scope to fix; it is
reported here because the HMI topology is one of its victims.

### D9 — rejection happens too late to be safe

**VERIFIED by construction** (`HmiGenerator.cs:70-94`): canvases, `HMI.csproj`,
`CanvasesResolutionList.xml`, every `<CAT>.cfg`, the HMI logical device and the topology
registrations are all written *before* `HmiPlanValidator.Validate` runs and the fatal throw fires.
A "rejected" HMI is therefore fully present on disk, and the exception propagates out of
`GenerateFeedStationSyslayToPath`, aborting the remaining generation stages and leaving a
half-generated Demonstrator. **This is my own design error:** the validation should run against a
staged copy, or the throw should roll back. As written, "generation failed" and "the HMI on disk is
safe" are not the same statement.

**Unavailable by contract** — cannot be delivered without changing control logic:

- **Operator-readable recipe instruction.** `RecipeStep.dt` is
  `StepType / CmdTargetName / CmdStateArr / Wait1Id / Wait1State / NextStep` — no text field.
  `ThisStepText` is `'Command step'`, `'Wait step'`, `'Waiting for target state'`.
- **Manual mode.** The reference `Process1_Generic_HMI.fbt` declares 13 ports ours lacks
  (`ManualExecuteStep`, `ManualNextStep`, `ManualStepReady`, `ManualStepComplete`,
  `OperatorInstruction`, `ProcessName`, `ProcessComplete`, `CurrentStep`, `CurrentStepType`,
  `WaitSatisfied`, `ModeCMD`, `MREQO`, `NSREQO`), and the engine has no hold state.
- **Human-readable state names**, **live interlock blocked reason**, **mode/cycle-type names**.
- **Any command** — Auto/Manual/Setup selection, Run/Stop/Reset, actuator jog.

---

## 12. Exact steps to produce a trustworthy HMI build

1. Confirm the intended VueOne model is the one actually loaded — check the `Control.xml`
   `<SystemID>`/`<Name>` header, not the folder (defect D7).
2. Close MapperUI and VueOne.
3. `dotnet build CodeGen\CodeGen\CodeGen.csproj`
4. `dotnet build MapperUI\MapperUI\MapperUI.csproj --no-incremental`
5. Copy `CodeGen.dll` to all five paths in §3 and **verify the hashes match**.
6. Relaunch, generate, and read the generation log for `[Hmi]` errors — a throw means rejection.
7. In EAE: **Build HMI** and confirm `Finished building HMI, success=True` in the Studio log.
8. Deploy, then confirm on the panel that the Info dialog lists exactly the expected controllers.
9. Confirm the banner reads `MONITORING ONLY - HMI COMMANDS DISABLED` on every screen.
10. Only then treat the HMI as trustworthy **for monitoring**.

---

## 13. Evidence index

| Claim | Evidence |
|---|---|
| HMI builds | `StudioLog\Log-2026-07-26_19-51-53.log:511` `Finished building HMI, success=True` |
| HMI deploys | same log `:569,:570` `Files downloaded (269 ms)` / `SendCommand command is success` |
| Engine ignores CycleType | `IEC61499\ProcessRuntime_Generic_v1.fbt` — 11 ECTransitions, none reference Mode/CycleType |
| STOP fired CycleType=0 | previous build `HMI\Station_CAT\Station_CAT_sDefault.cnv.cs:68,71` |
| No contract outputs | grep `<EventOutputs>|<Outputs>|<Output Name=` over `HMI\**\*.cnv.xml` → no matches |
| Only navigation buttons | grep `= new *Button()` over `HMI\**\*.Designer.cs` → all `ChangeCanvasButton` |
| No click wiring | grep `\.Click\s*\+=` over `HMI\` → no matches |
| `FireEvent` definitions only | `HMI\Area_CAT\Area_CAT.event.cs:310,326,342` |
| No Setup registered | all 10 `<CAT>.cfg` list only `sDefault` (+ `sFault`/`sInterlock`) |
| Live variant | syslay: `Bearing_PnP Type="Five_State_Actuator_CAT"`, no `Clamp` FB, 34 FBs |
| Robot stub | `HMI\Robot_Task_CAT\Robot_Task_CAT_sDefault.cnv.Designer.cs:26-29` |
| Stale CAT folder | `HMI\Seven_State_Actuator_CAT\` — no template; 7 `HMI.csproj` entries |
| DLL provenance | six paths hashed at 19:33; newest source `HmiCommandStripper.cs` 19:26:38 |

---

## Closing statement

The HMI can no longer issue a command. That is **VERIFIED** for the generated tree and enforced by
contract rather than by hiding controls. It is a genuine improvement over a panel that displayed a
STOP button which could not stop anything.

It is **not** a safety improvement in the regulated sense. The process engine still ignores
`CycleType`; a real operational Stop requires a control-logic change, and the machine's protective
stop must remain in the certified hardware safety system. The rig's documented clamp damage and
swivel collision risk are unaffected by anything in this report.
