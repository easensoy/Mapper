# Finalising the Mapper's HMI patch against the four deployed models

**Date:** 2026-07-26
**Question:** given the four VueOne models actually deployed into EAE IEC 61499 — `_se`, `_vc`,
`_sw5`, `_sw5_noclamp` — and Jyotsna's `BarCodeReaderFB.pptx`, can the Mapper's HMI generation be
**finalised without touching any FB, MQTT, wiring, I/O or topology**?

**Answer: yes for the operator-facing HMI; no for two items in the deck, which are structurally
blocked by that constraint and must be declared unavailable rather than worked around.**

Investigation only. Nothing was generated, modified, committed or deployed. `C:\Demonstrator`
was read, never written. Claims are **VERIFIED** (read from Control.xml, a generated syslay, a
deployed CAT or the library), **INFERENCE**, or **UNKNOWN**.

---

## 1. The four models are a clean 2 × 2 matrix

**VERIFIED** — parsed from each `Control.xml` under `…\OneDrive\Documents\VueOne\system\`.

| | Bearing_PnP **13-state** | Bearing_PnP **5-state** |
|---|---|---|
| **Clamp present** | `_se` — 32 components | `_sw5` — 32 components |
| **Clamp absent** | `_vc` — 31 components | `_sw5_noclamp` — 31 components |

Control-relevant inventory is otherwise **identical in all four**: 9 shared actuators
(Checker, CoverPNP_Hr, CoverPNP_Vr, Ejector, Feeder, Shaft_Hr, Shaft_Vr, Transfer, Bearing_PnP),
4 `Robot`-typed components (Bearing_Gripper, Shaft_Gripper, CoverPnp_Gripper, and the 7-state
UR3e `Robot`), 4 sensors, 3 processes. The only name-set difference is `Clamp`; the only
state-count difference is `Bearing_PnP` (13 vs 5).

### The two axes are genuinely orthogonal

**VERIFIED** — process transition chains compared state-by-state, condition-by-condition:

* `_vc` vs `_sw5_noclamp` — **Assembly, Disassembly and Feed all byte-identical** (30 / 30 / 8 states).
* `_se` vs `_sw5` — Assembly (31) and Feed (10) **identical**; Disassembly differs only in
  condition *ordering*, with identical condition sets.
* `_se` vs `_vc` — genuinely different: `Clamping_Part` / `Unclamping` in the clamp models versus
  `TransferReturning` / `TransferReturned` in the no-clamp models; Feed 10 vs 8 states.

So **four models reduce to two process topologies** (clamp / no-clamp) and **one independent
faceplate choice** (which CAT the swivel resolves to).

### What that leaves the HMI to absorb

Exactly three degrees of freedom:

1. one Five_State tile present or absent (`Clamp`);
2. `Bearing_PnP` rendering with either the Seven-State centre-home faceplate or the Five-State one;
3. `Transfer` claimed by Feed alone (clamp) versus Feed **and** Assembly **and** Disassembly
   (no-clamp — the documented transfer-hold design).

**All three are already handled by the existing data-driven generator.** `TemplateMap.ResolveActuatorCatType`
(**VERIFIED**) routes on `stateCount == 7 || isBranchedSeven` → `Seven_State_Actuator_Centre_Home_CAT`,
else `Five_State_Actuator_CAT`; `IsBranchedSevenState` keys on PARALLEL + ALTERNATIVE transition
*structure*, never on a name. The 5-state swivel is linear, so `_sw5` / `_sw5_noclamp` fall
through to the Five-State CAT with no model-specific rule.

**Evidence it already adapts** — generated canvases from the earlier three-model run:

| model | canvases | note |
|---|---|---|
| `_se` | 16 | `SevenStateActuatorCentreHomeScreen` present, 2 Five-State pages |
| `_vc` | 16 | same shape, one fewer Five-State tile (no Clamp) |
| `_sw5` | 17 | **no** Seven-State screen; a third Five-State page instead — the swivel joined that family automatically |

**Conclusion: no new per-model logic is needed for the fourth model. `_sw5_noclamp` is `_vc`'s
plan with the Bearing_PnP tile using the Five-State faceplate** (INFERENCE, but tightly
constrained: identical component names, byte-identical process chains, and a CAT rule that keys
only on state-graph structure).

---

## 2. What the syslay makes available to the HMI

The HMI generator's only input is the finished syslay. Under the "touch nothing" constraint that
is also its only *permitted* input. Three things matter:

**2.1 Instance identity — VERIFIED.** Every FB carries `ID` (the `TagName` binding) and `Name`,
the verbatim Control.xml component name: `Bearing_PnP`, `CoverPNP_Hr`, `PartInHopper`. A caption
is a pure string transform of `Name` — no FB, parameter, event or wire involved.

**2.2 Ownership — VERIFIED, and this is the key finding.** Each `Process1_Generic` instance
carries its compiled `Recipe` as an FB parameter in the syslay:

```
Recipe="[(StepType:=2, CmdTargetName:='', CmdStateArr:=0, Wait1Id:=0, Wait1State:=1, NextStep:=1),
         (StepType:=1, CmdTargetName:='feeder', … ), … ]"
```

`CmdTargetName` → the actuator's `actuator_name`; `Wait1Id` → the component's `actuator_id` / `id`.
Both are already parameters on the instances. Resolving them across the three generated models:

| model | drawable FBs | claimed by a process | orphans |
|---|---|---|---|
| `_se` | 25 | 19 | Area_HMI, Station1_HMI, Station2_HMI |
| `_vc` | 24 | 17 | Area_HMI, Station1_HMI, Station2_HMI, **PartAtAssembly** |
| `_sw5` | 25 | 19 | Area_HMI, Station1_HMI, Station2_HMI |

Worked example (`_se`):

```
Assembly_Station  CMD  Bearing_Gripper, Bearing_PnP, Clamp, CoverPNP_Hr, CoverPNP_Vr,
                       CoverPnp_Gripper, Shaft_Gripper, Shaft_Hr, Shaft_Vr
                  WAIT BearingSensor, PartAtAssembly, PartInHopper, ShaftSensor, TopCoverSensor
Disassembly       CMD  … + Ejector, Robot
Feed_Station      CMD  Checker, Feeder, Transfer      WAIT PartInHopper, TopCoverSensor
```

**Process → actuators + sensors is fully derivable from the syslay, with no name list and no
model-specific rule.** This is exactly the grouping the deck's manual/setup screens imply.

Three consequences the implementation must respect:

* **Ownership is many-to-many, not a partition.** Bearing_PnP, the shafts and the covers are
  claimed by both Assembly and Disassembly; in the no-clamp models Transfer is claimed by all
  three. A component should therefore appear on *every* owning process's screen. Duplicate
  `TagName` across different canvases is legitimate; duplicates *within one canvas* are the error
  the validator must catch.
* **Sentinels must be filtered.** Unresolved `CmdTargetName` values are `assembly_station`,
  `disassembly`, `feed_station`, `cycle_ready` — process self-announcements and the CycleReady
  handshake, not components. They resolve to no FB and must be dropped silently.
* **`PartAtAssembly` is orphaned in `_vc`** — a Mapper-injected synthetic sensor no `_vc` recipe
  waits on. A residual group is required; an unclaimed component must never be dropped.

**2.3 Everything else the deck wants is *not* in the syslay** — see §4.

---

## 3. Read-only is achievable, and nearly true already

**VERIFIED** — `Five_State_Actuator_CAT_sDefault.cnv.Designer.cs` instantiates only
`FreeText` × 2 and `Label` × 1, with 11 × `IsOnlyInput = true` and **zero** command bindings
(no `OnClick`, no `WriteValue`, no `IsOnlyInput = false`). The placed tile cannot issue a command.

Command capability lives entirely in `sSetup`, which is the canvas that reaches
`IThis.cmd_event → ActuatorCore.setup_event`. **Not generating `SetupScreen` therefore delivers a
genuinely read-only v1**, not a cosmetic one.

⚠️ One gap the validator must close: `sDefault` opens `sFault` and `sInterlock` as pop-ups via
`DoOpenFaceplate`. A read-only assertion that inspects only the canvas set would miss a command
control reachable through a pop-up. **UNKNOWN** whether those two contain any; the check must
cover them.

⚠️ Independently of the HMI: Setup mode drives actuators directly and the bench rig is flagged
unsafe (damaged clamp, swivel collision risk). Read-only-by-default is the correct posture on
engineering grounds, not just scope grounds. Operational interlocks still apply and are **not**
a machine-safety function.

---

## 4. The deck, item by item, under "touch nothing"

| Deck item | Where the data would come from | HMI-only? |
|---|---|---|
| Component identity on every tile | syslay FB `Name` → static caption | ✅ **yes** |
| Human-readable screen titles | derived from CAT type / process name | ✅ **yes** |
| Station grouping (slides 7–9) | process `Recipe` in the syslay | ✅ **yes** — proven §2.2 |
| Process + its actuators + sensors together | same | ✅ **yes** |
| Setup mode screen (slide 7) | `sSetup` template already exists and is fully wired | ✅ exists — **gate it off by default** |
| Automatic-mode status view (slide 6) | `ThisStepText` is already live on the HMI | ⚠️ partial — see below |
| `StepText` per recipe step (slide 9) | **does not exist anywhere** | ❌ **blocked** |
| `StationID` / `ActuatorID` / `TargetState` / `AutoNextAllowed` | **not in `RecipeStep`** | ❌ **blocked** |
| Manual mode + `HMI_ExecuteStep` / `NextStep` / … | **not on the process FB** | ❌ **blocked** |
| Actuator name inside the faceplate header | `component_name`, dead at the CAT | ❌ blocked — **but bypassed by the static caption** |

### Why the three blocked items are genuinely blocked

**`StepText` and the slide-9 columns.** **VERIFIED** — `IEC61499/DataType/RecipeStep.dt` is
`StepType / CmdTargetName / CmdStateArr / Wait1Id / Wait1State / NextStep`. There is no text
field, no station field, no target-state field. Adding them changes a datatype the controllers
compile and the engine reads — an FB/datatype change by any definition.

**VERIFIED** — the engine does publish step text today, but as engine-phase literals:
`ProcessRuntime_Generic_v1.fbt` assigns `ThisStepText := 'Command step' | 'Wait step' |
'Waiting for target state' | 'Advancing to command' | 'End step'`. Those describe the engine, not
the machine. The HMI can display them; it cannot turn them into "Move feeder cylinder to Work"
without the model-derived string existing in the recipe.

**Manual mode.** **VERIFIED** — diff of `Process1_Generic_HMI.fbt` against the reference shows 13
ports we do not declare: `ManualExecuteStep`, `ManualNextStep`, `ManualStepReady`,
`ManualStepComplete`, `OperatorInstruction`, `ProcessName`, `ProcessComplete`, `CurrentStep`,
`CurrentStepType`, `WaitSatisfied`, `ModeCMD`, `MREQO`, `NSREQO`. Beyond the interface, the engine
ECC has no hold state — `AdvanceStep` would have to wait for an operator event when `Mode = 2`.
That is control-logic work on the rig-proven engine.

**The faceplate's own name header.** **VERIFIED** — `actuator_name → FB13(ANY2ANY).IN1 →
OUT1 → IThis.component_name` and `FB13.CNF → IThis.name_event` are wired, but **nothing drives
`FB13.REQ`**, and `ANY2ANY` computes `OUT1` / fires `CNF` only on `REQ`. Same in the reference.
The same author's `Area.fbt` shows the intended form (`INIT → Name.REQ`), so it is an omission —
but fixing it is a CAT edit. **The static caption makes it unnecessary for v1**, which is why
that approach is preferable to the CAT fix I recommended previously.

Note also that even if triggered it would print `bearing_pnp`: `actuator_name` is the **ring
protocol key**, lower-cased because `updateComponentState.BREQ` does a case-sensitive
`component_state_in.dest_name = name` compare, and it doubles as the MQTT topic. It must never
be the operator label.

---

## 5. Pagination: one template is mis-declaring its size

**VERIFIED** — declared `SymbolSize` versus actual drawn extent:

| Faceplate | Declares | Draws | |
|---|---|---|---|
| Five_State_Actuator_CAT | 300 × 204 | 268 × 188 | correct |
| Area_CAT | 1000 × 190 | 798 × 144 | correct |
| Station_CAT | 832 × 400 | 640 × 260 | correct |
| **Sensor_Bool_CAT** | **600 × 400** | **56 × 26** | ~10 × oversized |

With a 1024-wide canvas and a 608-high content band, 600 × 400 admits exactly one tile per row and
one row per page — hence **five sensor pages for five sensors**, in every model.

**The packer is not at fault**; it honours the declared footprint, which is what EAE uses for
placement. The fix is the declared size in
`Template Library\HMI\Faceplates\Sensor_Bool_CAT\…_sDefault.cnv.Designer.cs` — an HMI template
edit, fully inside scope. Making the packer ignore declared sizes would overlap the three
faceplates that declare theirs correctly.

---

## 6. The finalised HMI-only scope

Everything below touches **only** `CodeGen/CodeGen/Hmi/**`, `Template Library/HMI/**` and one
`MapperConfig` flag. No CAT, no `RecipeStep`, no engine, no syslay parameter, no MQTT, no HCF, no
EIPScanner, no topology, no `HmiRuntimeEmitter`, no `Control.xml`.

1. **Static captions** on every screen, from the syslay FB `Name`, humanised (`Bearing_PnP` →
   "Bearing PnP"); literal text, no tag binding; never `actuator_name`.
2. **Process-grouped overview screens** derived from each process's own `Recipe`, with sentinels
   filtered and a residual group for unclaimed components; CAT-type screens retained behind a
   diagnostic flag.
3. **`Sensor_Bool_CAT` declared `SymbolSize` corrected**; Previous Page added beside Next Page.
4. **`MapperConfig.HmiEnableCommandScreens`, default `false`** — suppresses `SetupScreen` and any
   command-capable canvas.
5. **Validator extensions**: canvas overflow, per-canvas duplicate instance, missing instance,
   broken navigation, command-capable canvas *or pop-up* while read-only, and
   `TagName` → syslay FB → sysres mirror consistency.
6. **Explicit unavailable-by-contract report** for `StepText`, the slide-9 step columns, manual
   mode, and the faceplate's internal name header — surfaced as generation diagnostics, not
   silently omitted.

**Items 1–5 fully satisfy slides 5 (modes 1 and 3), 6 and 7 of the deck.** Slide 9's step table
and mode 2 are item 6.

---

## 7. Verification plan

The four models are an unusually good test matrix: they vary the component inventory and the
faceplate selection **independently**, so a single pass exercises both axes.

1. Generate all four into a temp root via the hidden runner's `--output-root`
   (`MapperUI\bin\Debug\net10.0-windows\VueOneMapperHiddenRunner.exe`) — **never** the live tree.
2. Assert, per model: captions present and human-readable on every tile; grouping matches the
   recipe-derived ownership computed independently; sensors fit one page; Previous/Next resolve;
   **zero** command-capable canvases; every `TagName` resolves to a syslay FB and to exactly one
   sysres mirror.
3. Assert `_vc` and `_sw5_noclamp` produce the **same screen set**, differing only in the
   Bearing_PnP faceplate type — the orthogonality claim, tested rather than assumed.
4. Prove every non-HMI artifact byte-identical to the current `C:\Demonstrator` baseline, and
   `HmiRuntimeEmitter`'s seven outputs unchanged.
5. Only then regenerate live through MapperUI, build the HMI in EAE, and redeploy.

⚠️ Step 5 last, deliberately. `C:\Demonstrator` currently holds the deployment that succeeded on
the panel at 14:13; regenerating into it before the temp runs pass would destroy the known-good
baseline.

---

## 8. Verdict

**The HMI patch can be finalised HMI-only, and the four models require no per-model code.** The
constraint costs exactly two things from the deck — model-derived step text and manual mode —
both of which need a `RecipeStep` field and process-FB ports that cannot be added without
touching control artifacts. Those should be written up as a contract gap for Jyotsna rather than
approximated, because an HMI that invents step descriptions the controller does not hold would be
worse than one that honestly shows the engine phase.

**UNKNOWN / not investigated:** whether `sFault` / `sInterlock` contain command controls;
`_sw5_noclamp`'s generated syslay (predicted, not generated); whether the reference HMI displays
names by some path not found in its CATs; barcode selection and multi-recipe `StartIndex` /
`StepCount` windowing, excluded by instruction — note that adopting it later would make
`RecipeStep` a shared pool and require the engine to take a start/count window.
