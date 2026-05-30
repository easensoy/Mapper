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
   │  + SimulatorPostProc.  │
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

The simulator collapses ALL three into ONE SIM resource (`SimulatorFullSystem
= true`). On the rig they are three separate `.sysres` files bound to three
separate physical devices.

## 3. The CAT library (function block types we instantiate)

The deployed EAE project under `C:\Demonstrator\Demonstrator\IEC61499\` ships
with a fixed set of CATs. The Mapper picks one per Control.xml component.

| CAT type                             | When picked                                   | Sensors / outputs                       |
|--------------------------------------|-----------------------------------------------|-----------------------------------------|
| `Five_State_Actuator_CAT`            | Default actuator: 5-state cylinders, mechanical grippers (Type=Robot, 5 states), plus the Bearing_PnP stub when `StubSevenStateActuatorsAsFiveState=true` | `athome`, `atwork` / `OutputToWork`, `OutputToHome` |
| `Five_State_Actuator_No_Sensors_CAT` | 4-state actuators                             | (no external sensor — internal timer)   |
| `Seven_State_Actuator_CAT`           | 3-position swivel: 7-state OR branched (PARALLEL+ALTERNATIVE) — Bearing_PnP when `StubSevenStateActuatorsAsFiveState=false` | `athome`, `atwork1`, `atwork2` / `current_state1_to_plc`, `current_state2_to_plc` |
| `Vacuum_Gripper_CAT`                 | Named vacuum gripper instances                | vacuum-specific                          |
| `Sensor_Bool_CAT`                    | 2-state sensors                                | `Input`                                 |
| `Process1_Generic`                   | Every `<Component Type="Process">` — Feed_Station, Assembly_Station, Disassembly | INITO + 2 adapter plugs (`stateRptCmdAdptr_out`, `stationAdptr_out`); **no data/event outputs** |
| `Station_CAT`                        | One per station (Station1, Station2)          | HMI + station-bus host                   |

The actuator CAT routing decision lives in
`SystemLayoutInjector.cs` around lines 1585-1606 (the helper just above
`IsBranchedSevenState`), gated by `MapperConfig.StubSevenStateActuatorsAsFiveState`.

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

- `ResourceWireEmitter.cs` — `NoStationAdapterTypes` (sysres side).
- `SystemLayoutInjector.cs` — `BuildStation2Wiring`'s `stationChain` loop
  (syslay side). Excludes Seven_State by checking the CAT type computed from
  `StubSevenStateActuatorsAsFiveState` + `IsBranchedSevenState`.

If you wire Seven into the stationChain, EAE rejects on import with
"unresolved adapter" / Missing Instances.

## 5. The generation pipeline (what runs per button)

### `MainForm.btnTestStation1_Click` ("Test Runtime", **rig path**)

1. `PrepareDemonstratorForGeneration` — DemonstratorWiper cleanup.
2. `injector.GenerateStation1TestSyslay` — writes the shared syslay (all 3 PLCs
   plus the 3 Processes plus all actuators/sensors/Robots).
3. `FinalizeM262StackAsync` (private to MainForm) —
   `M262SysdevEmitter.Emit` (emits M262 sysdev + mirrors FBs into M262 sysres),
   `M262TopologyEmitter.Emit`, `Station2DeviceEmitter.EmitAll` (M580 + BX1
   sysdev + sysres + HCF copy), `CompileCachePurger.Purge` (deletes
   `IEC61499/bin/` and `obj/` and resets `snapshot.xml`), `FoldersXmlEmitter`,
   `BroadcastDomainEmitter`.
4. `Station2SysresMirror.EmitStation2Sysres` — mirrors Station-2 FBs onto the
   M580 + BX1 sysres files (this was added 2026-05-29 to fix the recurring
   "Missing Instances: Bearing_PnP" EAE error — see `Docs/REVERTED_FIXES.md`
   for what we tried before).
5. `Station2WireEmitter.EmitStation2Resources` — wires the Station-2 sysres.
6. `M580SymbolBinder.BindM580` + `BX1SymbolBinder.BindBX1` — patches the
   deployed `.hcf` so EAE's Symbolic Link view resolves. Emits Form-1 GUID
   triples (`{resourceId}.{fbId}.{port}`).

### `MainForm_simulator.btnGenerateFullSystemSimulator_Click` ("Test Simulator")

1. Sets `cfg.SimulatorFullSystem = true` — collapses ALL 3 PLCs into ONE SIM
   resource. M580 / BX1 sysdev / sysres / HCF emission is **skipped**.
2. `DeployUniversalTemplatesAsync` — copies the CAT zips into the EAE project.
3. `PrepareDemonstratorForGeneration` + `GenerateStation1TestSyslay` (same).
4. `FinalizeM262StackAsync` (same — but only the M262 resource is the SIM resource).
5. `M262SysresWireEmitter.Emit` — wires the SIM sysres.
6. `HcfPatchService.PatchDeployed` — patches the .hcf.
7. **Simulator-only post-processors** (`CodeGen/Services/SimulatorPostProcessor.cs`):
   - `InjectSimHopperForce` — injects a SYMLINKMULTIVARSRC publishing
     `PartInHopper.Input = TRUE` so the hopper sensor reads TRUE at startup.
   - `OverrideSimActuatorsNoSensor` — forces `WorkSensorFitted = FALSE` /
     `HomeSensorFitted = FALSE` on every `Five_State_Actuator_CAT` instance, so
     the internal `No_Sensor_Handler` timer self-advances the ECC.
   - `VerifySimActuatorsNoSensorOrAbort` — re-reads syslay + sysres, throws if
     any Five_State lacks the no-sensor params.
   - `InjectSimSwivelForce` — for every `Seven_State_Actuator_CAT` instance,
     injects one `SimSwivelForce_<name>` SYMLINKMULTIVARSRC publishing
     `atwork1` / `atwork2` from the actuator's own `current_state{1,2}_to_plc`
     outputs (so the swivel's `ToPick → AtPick` and `ToPlace → AtPlace` gates
     close in sim without physical sensors).
   - `DumpSimRecipeAndInterlockArrays` — surfaces the emitted recipe + rule
     arrays in the Activity log.
8. EAE: Reload Solution → set Active Network Profile to **"Local Test"** → clean
   Build → Deploy → Login → Watch.

## 6. The CAT instance routing decision

For each `<Component Type="Actuator">` in Control.xml, the Mapper picks a CAT
type (`SystemLayoutInjector.cs` ~line 1585-1606):

```
if (name is in VacuumGripperNames)                          → Vacuum_Gripper_CAT
if (!stub && (States.Count == 7 || IsBranchedSevenState))   → Seven_State_Actuator_CAT
if (States.Count == 4)                                      → Five_State_Actuator_No_Sensors_CAT
else                                                        → Five_State_Actuator_CAT
```

`Robots` (Type="Robot", 5-state — Bearing_Gripper, Shaft_Gripper, etc.) are
routed through `Robots()` and currently also emit as `Five_State_Actuator_CAT`.

`StubSevenStateActuatorsAsFiveState` is the master switch for whether
Bearing_PnP is a 2-position Five_State stub or the real 3-position Seven_State
swivel. Both sim and rig paths read the same flag — there is no per-path
override.

## 7. The recipe

Each Process1_Generic instance carries a `Recipe` parameter that is a
serialised `ARRAY[100] OF RecipeStep` STRUCT. Each row is:

```
StepType        : 1=CMD, 2=WAIT, 9=END
CmdTargetName   : the actuator the row commands / waits on (lower-cased)
CmdStateArr     : the state value (or 0)
Wait1Id         : actuator/sensor id to wait on
Wait1State      : the state value the wait satisfies on
NextStep        : 1-based row index of the next step (loops back at END)
```

Built by `ProcessRecipeArrayGenerator.Generate` from the Control.xml
transitions. For Seven_State-commandable targets,
`MapSevenStateCommandFromConditionName` translates the condition's name
keyword (`place` → 2, `pick` → 1, `home`/`returned` → 0) into the `state_val`
the SevenStateActuator2 ECC expects. Defaults match
`SystemLayoutInjector.BuildMinimalActuatorParameters`'s Seven branch
(`TargetPickState=1`, `TargetPlaceState=2`, `TargetHomeState=0`).

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
│   │   │   ├── ProcessRecipeStGenerator.cs
│   │   │   ├── ProcessStepTableGenerator.cs
│   │   │   └── ProcessCatGenerator.cs
│   │   ├── IoBindingsLoader.cs            # reads SMC_Rig_IO_Bindings.xlsx
│   │   ├── InstanceNameResolver.cs
│   │   ├── HcfSymbolIndex.cs              # name → PLC bucket guess
│   │   └── FBIdGenerator.cs               # deterministic 16-hex FB IDs
│   ├── Devices/
│   │   ├── Core/                          # shared per-PLC emitters
│   │   │   ├── ResourceWireEmitter.cs    # the per-resource wiring loop
│   │   │   ├── Station2DeviceEmitter.cs
│   │   │   ├── Station2WireEmitter.cs
│   │   │   ├── Station2SysresMirror.cs
│   │   │   ├── SysresFbMirror.cs
│   │   │   ├── CompileCachePurger.cs
│   │   │   ├── HcfBindingSupport.cs
│   │   │   └── …
│   │   ├── M262/                          # M262-specific emitters
│   │   │   ├── M262SysdevEmitter.cs
│   │   │   ├── M262SysresWireEmitter.cs
│   │   │   ├── M262HwConfigCopier.cs
│   │   │   ├── M262TopologyEmitter.cs
│   │   │   └── HcfPatchService.cs
│   │   ├── M580/M580SymbolBinder.cs       # .hcf binding for M580 channels
│   │   └── BX1/BX1SymbolBinder.cs
│   ├── Services/
│   │   ├── DemonstratorWiper.cs           # the Clean step
│   │   ├── TemplateLibraryDeployer.cs     # extracts CAT zips into the project
│   │   ├── SimulatorPostProcessor.cs      # the sim post-gen pipeline (public statics)
│   │   └── …
│   └── Bridge/                            # cross-language bridge to a future server
├── MapperUI/MapperUI/                  # WinForms front-end
│   ├── MainForm.cs                       # btnTestStation1_Click — rig path
│   ├── MainForm_simulator.cs             # btnGenerateFullSystemSimulator_Click — sim
│   └── MainForm.Designer.cs
├── MapperTests/                        # xUnit verification harness
│   ├── MapperTests.csproj                # legacy tests quarantined under <Compile Remove>
│   ├── SimulatorEndToEndHarness.cs       # 18-item checklist — the gate
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
- **Stub flag** — `MapperConfig.StubSevenStateActuatorsAsFiveState`. When TRUE,
  Bearing_PnP is emitted as `Five_State_Actuator_CAT`; when FALSE, as
  `Seven_State_Actuator_CAT`.
