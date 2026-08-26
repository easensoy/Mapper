# Architecture

What VueOneMapper is, what it generates, and how the pieces fit together. Read
this once before touching the code. Pair with `Docs/INVARIANTS.md` (the facts
you cannot break) and `Docs/REVERTED_FIXES.md` (the things not to re-attempt).

---

## 1. The end-to-end picture

```
  VueOne digital twin (Control.xml)
              │
              ▼
   ┌────────────────────────┐
   │  CodeGen (C# library)  │   ← this repo
   │  SystemInjector        │
   │  + Devices/* emitters  │
   └────────────────────────┘
              │  writes
              ▼
   EAE 24.1 IEC 61499 project on disk
   (syslay + sysres + .hcf + sysdev + Equipment JSON + topology + …)
              │  EAE Build + Download
              ▼
   ┌──────────────────────────────────────────────┐
   │ Real rig  (M262 + M580 + BX1)                │
   │ or EAE software simulator (single SIM res)   │
   └──────────────────────────────────────────────┘
```

The Mapper does NOT talk to the PLCs directly. EAE owns Build + Download.
The Mapper's job ends when the on-disk project is correct.

## 2. The physical rig

**SMC assembly demonstrator**, 3 PLCs:

| PLC  | Hardware             | Role                              |
|------|----------------------|-----------------------------------|
| M262 | Modicon M262         | **Feed Station** — Feeder, Checker, Transfer, hopper sensor |
| M580 | Modicon M580 dPAC    | **Assembly + Disassembly** — Bearing_PnP, Shaft_Hr/Vr, grippers, clamp, sensors |
| BX1  | Soft-dPAC (PC-hosted)| **Cover PnP** — CoverPNP_Hr/Vr, cover gripper, MQTT bridge |

On the rig they are three separate `.sysres` files bound to three separate
physical devices. (The old Test-Simulator 3-PLC collapse and its
`SimulatorFullSystem` flag were removed — see I-12.)

## 3. The CAT library (function block types we instantiate)

The deployed EAE project under `C:\Demonstrator\Demonstrator\IEC61499\` ships
with a fixed set of CATs. The Mapper picks one per Control.xml component.

| CAT type                             | When picked                                   | Sensors / outputs                       |
|--------------------------------------|-----------------------------------------------|-----------------------------------------|
| `Five_State_Actuator_CAT`            | Default actuator: 5-state cylinders, mechanical grippers (Type=Robot, 5 states), and a swivel the twin models with only two positions | `athome`, `atwork` / `OutputToWork`, `OutputToHome` |
| `Five_State_Actuator_No_Sensors_CAT` | 4-state actuators                             | (no external sensor — internal timer)   |
| `Seven_State_Actuator_Centre_Home_CAT` | 3-position swivel: 7-state OR branched (PARALLEL+ALTERNATIVE) in the twin | `athome`, `atwork1`, `atWork2` / `OutputToWork1`, `OutputToWork2` |
| `Vacuum_Gripper_CAT`                 | Named vacuum gripper instances                | vacuum-specific                          |
| `Sensor_Bool_CAT`                    | 2-state sensors                                | `Input`                                 |
| `Process1_Generic`                   | Every `<Component Type="Process">` — Feed_Station, Assembly_Station, Disassembly | INITO + 2 adapter plugs (`stateRptCmdAdptr_out`, `stationAdptr_out`); **no data/event outputs** |
| `Station_CAT`                        | One per station (Station1, Station2)          | HMI + station-bus host                   |

The actuator CAT routing decision lives in `TemplateMap.ResolveActuatorCatType`
and is taken from the TWIN's own state graph — a state count, or the
PARALLEL+ALTERNATIVE transition structure `IsBranchedSevenState` detects. No
flag selects it: change the twin's swivel from three positions to two and the
CAT follows. Where more than one CAT could serve a shape, the declared
`protocol.priority` decides and an equal priority is REFUSED, so selection never
depends on the order rows happen to be written in.

## 4. Two command channels (this is the most-misunderstood part of the system)

Process FBs do not have a direct event wire to actuators. They command on adapters.

### 4a. `stateRprtCmd` ring (THE command path)

Sensors + actuators + Processes are all chained on one **stateRprtCmd ring**
(adapter type `stateRptCmdAdptr`). On every ring cycle the active Process
broadcasts a command message with a `dest_name` and a `state` value. Every
actuator/sensor has an internal `updateComponentState` basic FB whose `BREQ`
algorithm matches `IF component_state_in.dest_name = name THEN state_cmd := …`
(case-sensitive STRING `=`). On match the actuator's ECC sees the command via
`state_cmd → state_val` and `CNF → pst_event`.

This is how Process1_Generic talks to actuators. **Not** via direct
`Process.state_update → actuator.pst_event` event wires — that source pin does
not exist on Process1_Generic (see `Docs/INVARIANTS.md` #1).

### 4b. `stationAdptr` (CaSBus) chain (NOT a command path)

Station_CAT → station-bus actuators → Process → Stn2_Term. Carries
station-mode / fault propagation only. **Sensor_Bool_CAT and
Seven_State_Actuator_CAT have no `stationAdptr` port** — they are excluded from
this chain. The exclusion set lives in TWO places that must stay in sync:

- `ResourceWiringPlan.cs` — `ResourceWiringPlanner.For`'s CaS-chain filter,
  which reads the `stationAdapter` column of `Config/templates.yml`. ONE planner
  answers it for both documents, so the canvas and the resource cannot disagree;
  `ResourceWireEmitter` renders the plan and decides no membership of its own.

If you wire a CAT with no `stationAdptr` port into the station chain, EAE
rejects on import with "unresolved adapter" / Missing Instances. Since
2026-08-25 (d) every port a chain uses is DECLARED in templates.yml and checked
against the archive that ships the type before the project is cleaned, so a
missing port is a diagnostic rather than an import failure.

## 5. The generation pipeline

One entry point: `GenerateProject.Execute(GenerationRequest, log)`. MapperUI's
**Test Runtime** button, the VueOne runner and the in-repo gate all call it, so
there is no second sequence to drift from.

```
Control.xml  --SystemXmlReader-->  VueOneComponent[]        (frontend)
             --TwinModel.Build-->  resolved semantic IR     (every reference closed)
             --ProcessGraph.Build-->  per-process control flow (validated, one successor per state)
             --GenerationContext.Plan-->  the plan          (validated, immutable)
             --emitters + backends-->  EAE IEC 61499        (backend)
```

`ProcessGraph` is where the twin's state machine meets the recipe engine. The
engine executes a linear row list with ONE NextStep per row: it loops (a
back-edge is a cycle) but has no branch row. So a process state with two
outgoing transitions has no faithful lowering and is REFUSED by name; a state
the entry cannot reach is REPORTED, because a state that cannot execute is a
model fact rather than something to walk past silently.

Every guard leaf the twin declares is accounted for. `GuardCoverage` records
what became of each one — waited for, already required, proved by the command
that drove it, or answered by a declaration the deployment makes — and
`Plan` refuses to return if any leaf reached no decision at all.

`GenerationContext.Plan` decides EVERYTHING before a single file is touched:
controller allocation, CAT selection, state-table slots, the report/transport
graph, recipes, handoffs, interlocks, rule capacity and per-instance motion
timing. A model the backend cannot render throws HERE, which is before
`DeepClean`, so a rejected model leaves the project exactly as it was.

After the plan: deep clean, template deploy, application layer, then each
registered target backend takes its turn (device, hardware config, resource
wiring, channel binding), then the output validators. The pipeline itself names
no controller - `TargetRegistry.Backends` does.

## 6. The CAT instance routing decision

A CAT is chosen by the actuator's STATE GRAPH against the protocols declared in
`Config/smc-rig.yml`, never by its name. Each protocol row declares the stop
counts it serves, the command value that drives each stop, the value that means
"arrived", its target states and whether it crosses a shared volume both ways.
`TemplateMap.ResolveActuatorCatType` picks the row whose declared shape the
graph fits; a graph no row serves is REFUSED, naming the actuator and its stop
count, rather than defaulted to something that would compile and mis-drive.

## 7. The recipe

Each `Process1_Generic` instance carries a `Recipe` parameter: a serialised
`ARRAY OF RecipeStep`. Each row is:

```
StepType        : 1=CMD, 2=WAIT, 9=END
CmdTargetName   : the ring key the row commands (lower-cased), or a sensor name verbatim
CmdStateArr     : the state value the command carries
Wait1Id         : the state_table slot this term tests
Wait1State      : the value that satisfies it
NextStep        : the row to run next (an arbitrary index: loops are back-edges)
AltCount        : on the row that HEADS a wait, how many alternatives start here
TermCount       : on the row that heads an alternative, how many rows hold together
```

`AltCount`/`TermCount` are what make a guard keep its truth. VueOne writes a
guard as `ConditionValue -> ConditionGroup* -> Condition*`: the groups are
ALTERNATIVES and the conditions inside one hold together. A row tests one
`(slot, value)`, so alternatives are laid down as one WAIT GROUP and the engine
evaluates it as a disjunction - it releases on the FIRST alternative that holds.
Both counts zero is one alternative of one term, which is the plain single-slot
wait every guard without a choice in it produces.

`check_wait` gates every term on `state_table[..].name <> ''`, so a slot nothing
has written cannot satisfy a wait. `CommonInterlockEvaluator` applies the same
principle in the safe direction: a rule whose source has never reported REFUSES
the move rather than reading a phantom zero as a real position.

## 8. Repo layout

```
VueOneMapper/
├── CodeGen/CodeGen/                  # the C# generator
│   ├── Configuration/MapperConfig.cs     # all behaviour flags live here
│   ├── Models/                            # VueOneComponent / RecipeStep / …
│   ├── IO/                                # XML readers, file writers
│   ├── Translation/                       # the bulk of the logic
│   │   ├── SystemLayoutInjector.cs       # the syslay generator (Generate*Syslay)
│   │   ├── SyslayBuilder.cs              # low-level XML helpers
│   │   ├── Process/
│   │   │   ├── ProcessRecipeArrayGenerator.cs    # the Recipe builder
│   │   │   └── ProcessStepTableGenerator.cs
│   │   ├── IoBindingsLoader.cs            # reads SMC_Rig_IO_Bindings.xlsx
│   │   ├── InstanceNameResolver.cs
│   │   ├── HcfSymbolIndex.cs              # name → PLC bucket guess
│   │   └── FBIdGenerator.cs               # deterministic 16-hex FB IDs
│   ├── Devices/
│   │   ├── Core/                          # shared per-PLC emitters
│   │   │   ├── ResourceWireEmitter.cs    # the per-resource wiring loop
│   │   │   ├── Station2DeviceEmitter.cs
│   │   │   ├── Station2SysresMirror.cs
│   │   │   ├── SysresFbMirror.cs
│   │   │   ├── CompileCachePurger.cs
│   │   │   ├── HcfBindingSupport.cs
│   │   │   └── …
│   │   ├── M262/                          # M262-specific emitters
│   │   │   ├── M262SysdevEmitter.cs
│   │   │   ├── M262HwConfigCopier.cs
│   │   │   ├── M262TopologyEmitter.cs
│   │   │   └── HcfPatchService.cs
│   │   ├── M580/M580SymbolBinder.cs       # .hcf binding for M580 channels
│   │   └── BX1/BX1SymbolBinder.cs
│   └── Services/
│       ├── DemonstratorWiper.cs           # the Clean step
│       ├── TemplateLibraryDeployer.cs     # extracts CAT zips into the project
│       └── …
├── MapperUI/MapperUI/                  # WinForms front-end
│   ├── MainForm.cs                       # btnTestStation1_Click — rig path (the one button)
│   └── MainForm.Designer.cs
├── MapperTests/                        # the unit gate: 199 tests, all active
│   ├── MapperTests.csproj
│   ├── ITERATIONS.md                     # loop log
│   └── TestData/
│       ├── Feed_Station_Fixture.xml       # 8-component Feed-only (legacy)
│       ├── Full_System_Fixture.xml        # 34-component SMC system (current)
│       └── SMC_Rig_IO_Bindings.xlsx       # hand-crafted, NEVER regenerate
├── Template Library/CAT/                # committed .cat.zip bundles
├── Docs/                                # this folder
└── CLAUDE.md                            # autonomous loop brief + Status log
```

## 9. Glossary

- **CAT** — Composite Function Block Type (an EAE-specific term for a composite
  IEC 61499 FB with internal sub-FBs and ECC).
- **sysdev** — system device XML (per PLC, in the EAE project).
- **syslay** — system layer XML (one shared file on the canvas, all PLCs).
- **sysres** — system resource XML (per PLC, holds the compiled FB network).
- **.hcf** — EAE Hardware Channel File (binds physical DI/DO channels to
  application symlink variables).
- **EAE** — EcoStruxure Automation Expert.
- **ECC** — Execution Control Chart (an IEC 61499 state machine inside a basic FB).
- **VueOne** — the digital-twin platform whose `Control.xml` is our input.
- **CAT protocol** — the per-CAT command/settled/interlock/target state
  vocabulary, declared once per CAT in `Config/smc-rig.yml` and consumed as a
  typed `CatProtocol`. Nothing infers it from a CAT's name.
