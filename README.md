# VueOne to IEC 61499 Mapper

Converts VueOne `Control.xml` exports into IEC 61499 `.syslay` files for Schneider EcoStruxure Automation Expert (EAE 24.0). Produces FB instances and wiring; does not generate CAT type definitions or `.hcf` hardware configuration.

## How to run

Open `VueOneMapper.sln` (or `MapperUI/MapperUI/MapperUI.csproj`) in Visual Studio 2022 or run from the command line:

```
dotnet run --project MapperUI/MapperUI/MapperUI.csproj
```

Requires .NET 10 SDK on Windows. The WinForms app launches and presents the toolbar with the generation buttons.

## Workflow

The toolbar is a three-step progression. Run the buttons in order to validate the pipeline incrementally.

**1. Generate Test (Pusher)** — blue. Smallest possible `.syslay` output: one `Five_State_Actuator_CAT` FB named `Pusher` with all 10 parameters set (no sensors, 2-second travel, 4-second fault). No `Control.xml` required. Validates that the syslay schema is correct and that EAE imports without parse errors.

**2. Generate Process FB** — orange. Generates a Process FB syslay with the data-driven step table arrays (`st_type`, `cmd_target`, `cmd_state`, `st_wait_comp`, `st_wait_state`, `st_next`, `cr_name`, `Text`) without surrounding station infrastructure. Validates the recipe arrays in isolation. Requires `Control.xml` loaded via Browse.

**3. Generate Full Feed Station** — green. End-to-end. Reads the loaded `Control.xml`, finds the Process named `Feed_Station`, walks its referenced components, and emits a `<ProjectName>.syslay` with: 1 `PLC_Start` (`SE.AppBase.plcStart`), 1 `Area_HMI`, 1 `Area`, 1 `Station1`, 1 `Station1_HMI`, 1 `Process1` (`Process1_Generic`), one `Five_State_Actuator_CAT` per actuator, one `Sensor_Bool_CAT` per sensor, plus station-level and area-level `CaSAdptrTerminator`. Wires: INIT chain triggered by `PLC_Start.FIRST_INIT`, `Area→Station→Term` daisy-chain, `Station→Actuators→Process1→Stn1_Term` CaSBus chain (sensors are skipped because `Sensor_Bool_CAT` lacks station ports), closed `stateRptCmdAdptr` ring across all components plus `Process1`, and `Area_HMI→Area`/`Station1_HMI→Station1` HMI adapters. The chain ends with `Process1.INITO → PLC_Start.ACK_FIRST`.

## Prerequisite CAT and Basic templates

Before opening a generated `.syslay`, the target EAE project's `IEC61499` folder must already contain these types (deploy via the Mapper's Template Library Deployer or import manually):

CATs: `Five_State_Actuator_CAT`, `Sensor_Bool_CAT`, `Process1_Generic`, `Station_CAT`, `Area_CAT`, `Station`, `Area`, `CaSAdptrTerminator`.

Internal helper Basic FBs: `ProcessRuntime_Generic_v1`, `ProcessStateBusHandler`, `FiveStateActuator`, `Sensor_Bool`, `Station_Core`, `Station_Fault`, `Station_Status`, `FaultLatch`, `actuatorStateEvents`, `updateComponentState`, `updateComponentState_Sensor`, `No_Sensor_Handler`. Composite `faultDetection`. Adapters `CaSAdptr`, `AreaHMIAdptr`, `StationHMIAdptr`, `stateRptCmdAdptr`. Plus `SE.AppBase.plcStart` from EAE's standard library.

## v1 limitations

- **Process recipe is default-empty.** Button 3 emits `process_name` and `process_id` only; it does NOT populate the step-table recipe arrays inside `ProcessRuntime_Generic_v1.initialize`. `Process1` will start but will not sequence anything until you either run Button 2 to emit the recipe, or hand-edit `ProcessRuntime_Generic_v1.initialize` after import. The generated `.syslay` includes an XML comment near the top stating this limitation.
- **`DataConnections` are not auto-generated** by Button 3. Manual wiring is required for sensor-to-process status feeds and PLC I/O routing after EAE import.
- **`Type=Robot` components are skipped** with a warning logged to the Validation Output panel.
- **Multi-Process per Station is not supported.** Button 3 works only for single-Process stations like `Feed_Station`.
- **CaSBus chain skips sensors.** Verified against `.fbt` files: `Sensor_Bool_CAT` has only `stateRprtCmd_in/out` ports; it lacks `stationAdptr_in/out`. The chain therefore goes `Station → Actuators → Process1 → Stn1_Term`, with sensors participating only in the `stateRptCmdAdptr` ring.

## Run tests

From the repo root:

```
dotnet test MapperTests/MapperTests.csproj
```

23 xUnit tests cover deterministic FB ID generation, station grouping (filters out Process and Robot references), parameter derivation (sensor flags, fault timeouts, value formatting), `SyslayBuilder` XML output and round-tripping, and end-to-end Pusher + Feed Station `.syslay` generation against the bundled `Feed_Station_Fixture.xml`.
