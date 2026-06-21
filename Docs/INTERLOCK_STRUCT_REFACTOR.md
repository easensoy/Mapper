# Design report — STRUCT-style interlock interface

Refactor the interlock interface from four parallel `Rule*` arrays to one `RuleTable : ARRAY[N] OF
InterlockRule` STRUCT array. Behaviour (the rules) is preserved; the FB interface intentionally changes.

## 1. Which CATs expose interlock arrays today

Two actuator CATs declare the four parallel `ARRAY[10] OF INT` interlock inputs + `RuleCount`, each
wired to an internal `CommonInterlockEvaluator` sub-FB:

| CAT | Internal evaluator FB | Interlock inputs |
|---|---|---|
| `Five_State_Actuator_CAT` | `InterlockManager` | RuleFromState, RuleToState, RuleSourceID, RuleBlockedState, RuleCount |
| `Seven_State_Actuator_Centre_Home_CAT` | `CommonInterlockManager` | same four + RuleCount |

The screenshot of `Bearing_PnP` (Centre-Home CAT) shows exactly these four arrays as public inputs.

## 2. Which files patch / build interlocks already

- **`Planning/Interlocks/InterlockPlanner.cs`** — `BuildRules(actuator, allComponents, scopedIds) ->
  InterlockPlan(Count, From[], To[], Src[], Blocked[])`. Already does scoped-id resolution, the
  predecessor `FromState`, the StateNumber-4→0 home-family remap, and the home-rest (`Blocked==0`) drop.
  This is the source of truth for the rules; **it stays**.
- **`Planning/SystemLayoutInjector.cs`** — still contains interlock translation/filtering **inline** at
  three sites (the thing this refactor removes):
  - main Five_State path (~2176): emits the 4 arrays from the plan + the cover-zeroing (`IsBx1CoverActuator`).
  - centre-home path (~1418-1489): plan + a **0..6 range filter** (drops rules whose From/To fall outside
    the core's 0..6 CurrentRawState, e.g. the "2"-suffixed Disassembly route that numbers 12→0) + the
    `DropSwivelInterlockForTest` bench flag + emit.
  - zero-default path (~1972): RuleCount=0 + four zero arrays.
- **`Artefacts/Templates/TemplateLibraryDeployer.cs`** — the **collapse machinery already exists**:
  `NormalizeFiveStateRuleArrays(cat, evaluatorFb, reduce, …)` (bidirectional + idempotent) and
  `NormalizeCommonInterlockEvaluatorRules(reduce, …)`, plus the field map
  `RuleFromState→FromState, RuleToState→ToState, RuleSourceID→SourceID, RuleBlockedState→BlockedState`.
  They are currently called with **`reduce=false`** (keep the 4 arrays). `reduce=true` rewrites the CAT's
  four array InputVars into a single `RuleTable : InterlockRule ArraySize=10` InputVar + its INIT With +
  boundary Input + DataConnection to the evaluator's `.RuleTable`.
- **Recoverable from git** (reuse, do not reinvent): `DeployInterlockRuleDatatype` + the `InterlockRule.dt`
  STRUCT (commit `352c681`, removed in `5dc779f`) and `SyslayBuilder.FormatRuleTable(from,to,src,blk,count)`
  — the instance-side STRUCT-array literal formatter (removed in `edd627f`). These were the dormant
  simulator direction; they are exactly the pieces the rig path now needs.

## 3. What generated interface will change

The actuator FB **public interface** and its instance parameters change:

- CAT `.fbt` InterfaceList: four `Rule*State : ARRAY[10] OF INT` InputVars → one
  `RuleTable : ARRAY[10] OF InterlockRule` InputVar (+ the `InterlockRule.dt` datatype is deployed).
- Instance params in `.syslay` / `.sysres`: `RuleFromState=[…] RuleToState=[…] RuleSourceID=[…]
  RuleBlockedState=[…]` → `RuleTable=[(FromState:=…, ToState:=…, SourceID:=…, BlockedState:=…), …]`.
  `RuleCount` is unchanged.
- The `CommonInterlockEvaluator` Basic FB is reshaped (RuleTable instead of four arrays) so the wiring
  still resolves.

`RuleCount`, the recipe arrays, the ring wiring, HCF, MQTT, and the cover fold are all untouched.

## 4. Byte-identical, or intentional?

**Intentional** — the public FB interface changes, so the generated `.syslay`/`.sysres`/CAT `.fbt`
differ for every actuator that carries interlock inputs (the Five_State actuators + the Bearing_PnP
Centre-Home). This is allowed by the task (#7). Semantic equivalence is exact and provable rule-by-rule:

> `RuleTable[i] == (FromState=From[i], ToState=To[i], SourceID=Src[i], BlockedState=Blocked[i])`
> for `i` in `0 .. RuleCount-1`, in the same order. The same `InterlockPlan` feeds both forms.

## Plan — two slices

**Slice A (BYTE-IDENTICAL): config + module isolation.** Keeps the 4-array output.
- New `Config/interlock.yaml` + `InterlockConfig` (loader in `Planning/Interlocks`): `ruleArraySize`
  (moves `interlockRuleCap` out of `config.yaml`), `centreHomeStateRange` (0..6), `dropForTest`.
  No interlock size magic number remains in C#.
- New `Planning/Interlocks/InterlockEmitter.cs`: owns ALL interlock policy + emission — the base plan
  (InterlockPlanner), the cover-zeroing, the centre-home 0..6 filter, the drop-for-test, and the param
  formatting. `SystemLayoutInjector` calls `InterlockEmitter.Emit(actuator, fbType, plan)` and consumes
  the returned params; it performs **no** interlock translation/filtering inline.
- Gate must be **byte-identical**.

**Slice B (INTENTIONAL): STRUCT RuleTable.**
- `InterlockEmitter` emits one `RuleTable` STRUCT-array param (recovered `FormatRuleTable`) instead of the
  four arrays.
- Type side: recover `DeployInterlockRuleDatatype` (deploy `InterlockRule.dt`) and flip the two
  `NormalizeFiveStateRuleArrays` + `NormalizeCommonInterlockEvaluatorRules` calls to `reduce=true`.
- Report the exact generated diff and prove rule-by-rule equivalence; run HCF + PARITY + MQTT validation.

## Results (implemented + gated)

**Slice A — BYTE-IDENTICAL.** `Config/interlock.yaml` + `InterlockConfig` (ruleArraySize/centreHome/
dropForTest) own the interlock constants; `interlockRuleCap` left `config.yaml`, `DropSwivelInterlockForTest`
left `MapperConfig`. New `Planning/Interlocks/InterlockEmitter` owns the plan, cover-zeroing, the
centre-home range filter, the bench drop, the param emission, and the guards; `SystemLayoutInjector`'s
three inline sites are one-line calls with no interlock translation left. Gate: **0 diffs / 359 files**,
HCF + PARITY + MQTT PASS, exit 0.

**Slice B — INTENTIONAL STRUCT.** Driven by `interlock.yaml useStruct: true` (mirrors the rig-proven
`UseRecipeStruct` pattern: datatype + CAT reshape + instance param flip together). Gate diff vs baseline =
exactly **8 changed + 1 new, interlock-only**:

| File | Change |
|---|---|
| `DataType/InterlockRule.dt` *(new)* | STRUCT FromState/ToState/SourceID/BlockedState : INT |
| `IEC61499.dfbproj` | registers `DataType\InterlockRule.dt` |
| `Five_State_Actuator_CAT.fbt` | 4 `Rule*State : INT[10]` InputVars → 1 `RuleTable : InterlockRule[10]` |
| `Seven_State_Actuator_Centre_Home_CAT.fbt` | same reshape (CommonInterlockManager) |
| `CommonInterlockEvaluator.fbt` | InputVars reshaped + Evaluate ST `RuleX[i]` → `RuleTable[i].X` |
| `.syslay` + 3× `.sysres` | instance params: 4 arrays → 1 `RuleTable` struct literal |

Recipes, ring wiring, HCF, MQTT — **no diff**. HCF + PARITY + MQTT PASS, exit 0.

**Rule-by-rule equivalence (proven from the generated M580 sysres).** The one non-trivial rule
(CoverPNP_Hr, RuleCount=1): before `RuleFromState=[0,…] RuleToState=[2,0,…] RuleSourceID=[14,0,…]
RuleBlockedState=[2,0,…]`; after `RuleTable=[(FromState:=0, ToState:=2, SourceID:=14, BlockedState:=2),
(0,0,0,0)×9]`. The five RuleCount=0 actuators → all-zero RuleTable. `RuleCount` unchanged; the evaluator
loop is bounded by `RuleCount` and reads the same values via struct members. So
`RuleTable[i] == (From[i], To[i], Src[i], Blk[i])` for every actuator, same order.

**EAE-verify (the one rig-only check).** The gate proves generation; only EAE confirms the reshaped CATs
compile with `RuleTable`. The mechanism is the same as the rig-proven `RecipeStep` struct (a `.dt`
datatype + a STRUCT-array InputVar + a struct-literal param), so the risk is low. Revert path:
`interlock.yaml useStruct: false` (one rebuild) restores the four arrays.

## Correction — bench-test drop removed; real Bearing_PnP interlock restored

The first cut carried forward a pre-existing bench-test flag (`DropSwivelInterlockForTest=true`, ported as
`dropForTest`) that **zeroed** the Bearing_PnP centre-home interlock. A STRUCT refactor must not weaken any
Control.xml-derived interlock, so the drop is **deleted entirely** — no test-drop in the shipping
generator. `Config/interlock.yaml` no longer has `dropForTest`; `InterlockConfig` no longer has it; the
`CentreHomePlan` short-circuit and the `GuardCentreHome` `!drop` exemption are gone (the guard now always
throws if Bearing_PnP has in-scope interlocks but emits RuleCount=0).

**Result:** removing the drop changed exactly **2 files** (syslay + M580 sysres — the Bearing_PnP
RuleTable), nothing else. Bearing_PnP now emits its real interlock:

```
RuleCount = 2
RuleTable[0] = (FromState:=2, ToState:=4, SourceID:=10, BlockedState:=2)   # block Pick(2)->Place(4) while Shaft_Hr(10) at AtWork(2)
RuleTable[1] = (FromState:=2, ToState:=4, SourceID:=14, BlockedState:=2)   # block Pick->Place while CoverPNP_Hr(14) Advanced(2)
```

Verified against Control.xml (independent read): the `TurningPlace` state carries three interlock
conditions — Shaft_Hr/AtWork, CoverPNP_Hr/Advanced, Transfer/ReturnedFinished. Two are kept (ids 10 / 14,
confirmed by the FBs' own `actuator_id` and `smc-rig.yml` `coverpnp_hr: 14`); the Transfer rule is correctly
dropped as inert (its blocked state remaps 4→0 = source-at-home = an inverted rule that would deadlock the
swivel). From=2 is the AtPick predecessor, To=4 is Place, Blocked=2 is the source's work/advanced state.

## Correction 2 — cover-detour zeroing removed; CoverPNP_Hr emits its real rule

A second pre-existing special-case zeroed the BX1 cover actuators' interlocks (`if IsCoverDetourActuator
return Empty` in `FiveStatePlan`, with a matching `GuardFiveState` exemption). That wrongly suppressed
CoverPNP_Hr's Control.xml interlock. Both are **removed** — every actuator now emits whatever
`InterlockPlanner` leaves after its normal semantic drops (in-scope check + home-rest `Blocked==0`), and the
guard throws for any actuator (covers included) that has in-scope conditions but emits RuleCount=0.

Result (generated): **CoverPNP_Hr RuleCount=1** `(FromState:=0, ToState:=2, SourceID:=10, BlockedState:=2)`
— block its advance to Advanced(2) while Shaft_Hr(10) is at AtWork(2), the surviving Shaft_Hr/AtWork rule.
**CoverPNP_Vr / CoverPnp_Gripper stay RuleCount=0** (no Control.xml interlock conditions). Bearing_PnP
stays RuleCount=2. Shaft_Hr and CoverPNP_Hr are now symmetrically interlocked (each blocks while the other
is at its work/advanced state). Gate HCF + PARITY + MQTT PASS, exit 0; equivalence check PASS (26 FBs,
arrays == struct row-by-row).

## Correction 3 — full CAT encapsulation: one `RuleTable : InterlockTable` input

The earlier struct form still exposed two CAT inputs (`RuleCount : INT` + `RuleTable : InterlockRule[10]`).
Now the CAT exposes **exactly one** interlock input — a nested struct:

```
TYPE InterlockRule  : STRUCT FromState:INT; ToState:INT; SourceID:INT; BlockedState:INT; END_STRUCT END_TYPE
TYPE InterlockTable : STRUCT Count:INT; Rules:ARRAY[0..ruleArraySize-1] OF InterlockRule; END_STRUCT END_TYPE
```

Changes:
- New `DataType/InterlockTable.dt` (Count + Rules array; `Rules` ArraySize from `interlock.yaml ruleArraySize`).
- `Five_State_Actuator_CAT`, `Seven_State_Actuator_Centre_Home_CAT`, `CommonInterlockEvaluator` each expose
  one `RuleTable : InterlockTable` input. **`RuleCount` removed entirely** — VarDeclaration, the event WITH
  entry, the FBNetwork Input pin, the DataConnection. The 4 arrays are gone.
- Evaluator `Evaluate` ST: `FOR i := 0 TO RuleTable.Count - 1` and `RuleTable.Rules[i].FromState/ToState/
  SourceID/BlockedState`.
- Instance param: one `RuleTable := (Count:=N, Rules:=[(FromState:=…, …), …])`. No `RuleCount` param.

Verified on the generated tree: **0** occurrences of `RuleCount`, `RuleFromState`, `RuleToState`,
`RuleSourceID`, `RuleBlockedState` anywhere under `IEC61499` (CATs, `_HMI` faceplates, syslay, all sysres,
dfbproj); each CAT carries exactly one `RuleTable : InterlockTable`. Bearing_PnP `Count:=2`, CoverPNP_Hr
`Count:=1`. Gate HCF/PARITY/MQTT PASS, exit 0; equivalence check PASS (26 FBs, nested struct == array form).

**⚠️ EAE-verify (the one thing I cannot check headlessly).** A precedent search (EAE library + both
reference projects + the working tree) found **no precedent** for a STRUCT whose member is an
ARRAY OF another STRUCT, nor for a nested instance literal `(Count:=…, Rules:=[(…)])` — every proven
array-of-struct is *flat* (`RecipeStep`/`InterlockRule` used as a top-level `ARRAY OF <struct>`). IEC
61131-3 permits the nesting and the `.dt`/CAT declarations will almost certainly parse; the **nested
instance literal round-trip is the unverifiable gate** — only an EAE Build/Deploy confirms EAE emits and
re-parses it. If EAE rejects the nested literal, that is the exact limitation to report (do not fall back to
parallel arrays or split scalar fields). Revert path: `interlock.yaml useStruct: false` (one rebuild)
restores the legacy 4-array + scalar RuleCount interface.

## Slice — Target states encapsulated; Timers reported blocked

Next CAT-cleanup slice: fold the loose target/timer inputs into structs. A 5-agent precedent search (EAE
library + both reference projects + working tree, ~110 `.fbt`) split it cleanly:

- **Target — CLEAN, implemented.** New `DataType/TargetStates.dt` = `STRUCT { Work1, Work2, Home : INT }`,
  driven by `interlock.yaml useTargetStruct`. The Five_State + Centre-Home CATs and the shared
  `CommonInterlockEvaluator` each replace `TargetWork1State`/`TargetWork2State`/`TargetHomeState` with one
  `Target : TargetStates` that flows **whole** into the custom evaluator (no struct-member connection —
  same proven mechanism as `RuleTable : InterlockTable`). Evaluator algorithms read `Target.Work1/Work2/
  Home`. Instance param: `Target := (Work1:=N, Work2:=N, Home:=N)`. Five_State has no Work2 → 0 (it was an
  unconnected 0 before). Before→after (effective values identical):

  | Actuator | Before | After |
  |---|---|---|
  | Bearing_PnP (Seven) | TargetWork1State=2, TargetWork2State=4, TargetHomeState=6 | `Target := (Work1:=2, Work2:=4, Home:=6)` |
  | CoverPNP_Hr (Five) | TargetWork1State=2, TargetHomeState=4 | `Target := (Work1:=2, Work2:=0, Home:=4)` |
  | Feeder (Five) | TargetWork1State=2, TargetHomeState=4 | `Target := (Work1:=2, Work2:=0, Home:=4)` |
  | Shaft_Hr (Five) | TargetWork1State=2, TargetHomeState=4 | `Target := (Work1:=2, Work2:=0, Home:=4)` |

  Verified: 0 scalar `TargetWork1State/Work2/Home` params in instances (26 `Target` structs); evaluator
  algorithms use `Target.Work1/Work2/Home`; gate HCF/PARITY/MQTT PASS; interlock equivalence unaffected.

- **Timers — BLOCKED, reported (kept scalar by user decision).** `toWorkTime`/`toHomeTime` feed
  `E_DELAY.DT` and the enable bools feed `AND.IN2` — **standard** library FBs that can't be reshaped to
  take a struct. Sourcing them from one `ActuatorTimers` struct would need a struct **member** as a
  DataConnection source (`Source="Timers.ToWorkTime"`) — **zero precedent** across ~110 `.fbt` files (every
  struct flows whole or is unpacked only in ST), and no struct-demux FB exists. Per "stop and report if
  EAE cannot parse these STRUCT parameters cleanly," the timer/fault inputs stay **discrete scalars** (the
  proven pattern). The only path to fold them is a custom demux BasicFB (`demuxHscAction` pattern) — held
  pending a decision.

## Equivalence check (`_gate/interlock_equiv_check.py`)

A repeatable two-pass harness: runs the gate with `useStruct:false` (emits the four arrays) and
`useStruct:true` (emits RuleTable), then compares **every** actuator FB across the syslay + all sysres.
It **fails** if any FB's RuleCount differs, any `RuleTable[i] != (From[i],To[i],Src[i],Blk[i])`, any nonzero
RuleCount collapses to 0, or Bearing_PnP RuleCount==0.

Result — **PASS**, 26 FB instances compared, arrays==struct row-by-row, no rule lost. The FBs carrying a
real interlock (all preserved exactly): Bearing_PnP `[(2,4,10,2),(2,4,14,2)]`, Feeder `[(0,2,5,2),(0,2,6,2)]`,
Checker `[(0,2,6,2),(0,2,4,2)]`, Transfer `[(0,2,5,2),(0,2,4,2)]`, Ejector `[(0,2,6,2)]`, Shaft_Hr `[(0,2,14,2)]`.

## Acceptance mapping
- `Config/interlock.yaml` owns interlock generation constants — Slice A.
- No interlock-size magic number in C# — Slice A (`ruleArraySize`).
- Interlock planning isolated under `Planning/Interlocks` — Slice A (`InterlockEmitter` + `InterlockPlanner`).
- SystemLayoutInjector no longer translates/filters interlocks inline — Slice A.
- STRUCT-style interlock array in the generated FB — Slice B ✓ (`RuleTable : InterlockRule[10]`).
- Recipes unchanged, cover steps stay in Assembly/Disassembly, no Cover_Station — both slices ✓ (gate: no recipe diff).
- No test-drop / no disabled Bearing_PnP interlock in the shipping generator ✓ (`dropForTest` deleted).
- Bearing_PnP nonzero RuleCount with the same rules its arrays held ✓ (RuleCount=2; equivalence check PASS).
- Verification check fails on any nonzero→0 or Bearing_PnP==0 ✓ (`_gate/interlock_equiv_check.py`).

- Every actuator with Control.xml interlock conditions emits its surviving rules — the cover-detour zeroing
  + guard exemption are removed (Correction 2). CoverPNP_Hr emits its Shaft_Hr/AtWork rule; Vr/Gripper stay
  0 only because they have no conditions.
