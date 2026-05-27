# Seven_State_Actuator_CAT — Data-Driven Gap & Roadmap

Status as of 2026-05-27. Open architectural debt that the current Mapper output works around, not yet fixed at the CAT level.

---

## 1. What "data-driven" means here

`Five_State_Actuator_CAT` is the reference. Every 5-state cylinder, gripper and clamp on the SMC rig is **one instance of the same CAT type**; per-instance behaviour comes entirely from Parameter values the Mapper emits at deploy time. The ECC inside the CAT does not know which actuator it is — it reads `actuator_id`, `TargetWork1State`, `RuleCount[]`, fault timers, and so on, and behaves accordingly.

A new actuator does not require touching the CAT XML. Mapper emits a new `<FB Type="Five_State_Actuator_CAT">` with different parameter values and the runtime picks it up.

## 2. What Seven_State_Actuator_CAT actually is today

A single hardcoded ECC for one specific motion pattern: `Pick → Place → Home`. The instance-side parameter surface is **4 fields** (vs Five_State's 17):

| Parameter | Five_State | Seven_State |
|---|---|---|
| `actuator_name` / `actuator_id` | ✅ | ✅ |
| `process_state_name`, `state_val` | (state-table driven) | ✅ (command rail) |
| `WorkSensorFitted`, `HomeSensorFitted` | ✅ | ❌ — sensors hardwired via SYMLINK |
| `toWorkTime`, `toHomeTime` | ✅ | ❌ |
| `faultTimeoutWork`, `faultTimeoutHome`, `enableTo*FaultTimeout` | ✅ | ❌ |
| `TargetWork1State`, `TargetHomeState` | ✅ | ❌ — slot IDs baked in ECC |
| `RuleCount` + `RuleFromState[10]` + `RuleToState[10]` + `RuleSourceID[10]` + `RuleBlockedState[10]` | ✅ | ❌ |

Inside `SevenStateActuator2.fbt`, the transitions encode the slot IDs literally:

```
START   --state_val = 1--> ToPick
AtPick  --state_val = 2--> ToPlace
AtPlace --state_val = 0--> timerStart → ToHome → athome
```

There is **no path** for Pick2/AtPick2/Place2/AtPlace2, no interlock rule evaluation, no per-instance timing, no per-instance target encoding.

## 3. The 13-state Bearing_PnP gap

Control.xml's `Bearing_PnP` is a 13-state component with **PARALLEL + ALTERNATIVE** transitions — a branched-swivel actuator with two pick sources and two place destinations:

```
Primary  : ReturnedHome → TurningPick  → AtPick  → TurningPlace  → Place   → TurningHome  → AtHome
Alternate: …             → TurningPick2 → AtPick2 → TurningPlace2 → AtPlace2 → TurningHome2 → Athome2
```

The Mapper's `IsBranchedSevenState()` already detects this shape and routes Bearing_PnP onto `Seven_State_Actuator_CAT`. The CAT then only services the primary leg.

For **current** Assembly_Station (single pick from feed, single place onto shaft), the primary leg is enough. For **Disassembly_Station** the alternate leg is required.

## 4. Current Mapper workaround

`ProcessRecipeArrayGenerator.MapSevenStateCommandFromConditionName` (added 2026-05-27) keyword-maps the Control.xml condition name to a CAT command value:

- `…/AtPick` / `…/Picking` → `state_val = 1`
- `…/AtPlace` / `…/Place` → `state_val = 2`
- `…/AtHome` / `…/ReturnedHome` → `state_val = 0`

This is enough to fire Pick → Place → Home on the primary leg. It silently drops branched commands — the alternate-leg states (AtPick2, AtPlace2) match the same Pick/Place keyword and route to the primary leg's `state_val = 1` / `state_val = 2`. That is wrong but currently invisible because the primary leg is the only one exercised.

## 5. Roadmap

### Phase 1 — Mirror Five_State's parameter surface (CHOSEN, scheduled post-acceptance)

Goal: every actuator CAT on the rig presents the same parameter shape. Five_State and Seven_State both accept `Target*` + `Rule*` + fault timers.

Add to `Seven_State_Actuator_CAT.fbt` InputVars:

```
TargetPickState : INT          // default 1 — slot the CAT routes "go pick" to
TargetPlaceState : INT         // default 2
TargetHomeState : INT          // default 0
toPickTime : TIME
toPlaceTime : TIME
toHomeTime : TIME
faultTimeoutPick / Place / Home : TIME
enableTo*FaultTimeout : BOOL
RuleCount + RuleFromState[10] + RuleToState[10] + RuleSourceID[10] + RuleBlockedState[10]
```

ECC stays single-path; transitions read `state_val = TargetPickState` instead of literal `1`, etc.

Mapper-side changes:
- Extend `SystemLayoutInjector.BuildActuatorParameters` (or its Seven_State sibling, if added) to emit the new param block.
- Extend `ProcessRecipeArrayGenerator` to derive `TargetPickState/TargetPlaceState/TargetHomeState` from the Control.xml State_Numbers (so `Bearing_PnP/AtPick` State_Number=2 sets `TargetPickState=2` for that instance, and the runtime publishes 2 not 1 when settled — uniform with Five_State's "publish the Control.xml State_Number" convention).
- Drop the keyword-mapping shim in `MapSevenStateCommandFromConditionName` once `state_val == waitState == Control.xml State_Number` again.

Estimated effort: ~1 day.

### Phase 2 — Branched-path support (scheduled when Disassembly enters integration)

Goal: Bearing_PnP's alternate leg works on Disassembly_Station.

Two options:

- **2a — Add Pick2/Place2 to the existing ECC.** Add `TargetPick2State`, `TargetPlace2State` parameters and `BranchSelector` (the recipe row chooses 1 or 2). ECC grows by ~6 states. Manageable.
- **2b — Two Seven_State_Actuator_CAT instances per branched component.** Mapper emits one per leg, both bound to the same physical actuator via shared SYMLINKs. No ECC change. Recipe writes to whichever instance is active. Slot ID space doubles for branched actuators only.

Decision deferred to Phase 1 completion + Disassembly recipe shape.

Estimated effort: ~3 days.

### Phase 3 — Universal_Actuator_CAT (post-rig acceptance)

Goal: one CAT type covers every actuator topology. State count, transition matrix, action table, interlock rules — all data parameters.

A `Universal_Actuator_CAT.fbt` would deprecate both `Five_State_Actuator_CAT` and `Seven_State_Actuator_CAT`; existing instances become parameter sets. Cleanest architecture, hardest rewrite.

Estimated effort: 1–2 weeks. Run as a clean refactor once the rig is shipped.

## 6. Code locations

- `Seven_State_Actuator_CAT.fbt` — the hardcoded CAT.
- `SevenStateActuator2.fbt` — the basic FB inside, holds the ECC.
- `ProcessRecipeArrayGenerator.MapSevenStateCommandFromConditionName` — keyword-map shim, becomes redundant after Phase 1.
- `IsSevenStateCommandable` / `IsBranchedSevenState` — gate functions, currently route every 7-state and branched-13-state target through this CAT.
- `SystemLayoutInjector.ResolveActuatorFBType` — Mapper-side type picker; the place that decides Bearing_PnP becomes a Seven_State_Actuator_CAT instance.

Update this doc at each Phase milestone.
