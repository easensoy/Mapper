# VueOne to IEC 61499 Mapper

Converts VueOne `Control.xml` exports into IEC 61499 `.syslay` files for Schneider EcoStruxure Automation Expert (EAE 24.0). Produces FB instances and wiring; does not generate CAT type definitions or `.hcf` hardware configuration.

## How to run

Open `VueOneMapper.sln` (or `MapperUI/MapperUI/MapperUI.csproj`) in Visual Studio 2022 or run from the command line:

```
dotnet run --project MapperUI/MapperUI/MapperUI.csproj
```

Requires .NET 10 SDK on Windows. The WinForms app launches and presents the toolbar with the generation buttons.

## Buttons

**Generate Pusher Only (Test)** — Produces a minimal `Pusher_Test.syslay` containing one `Five_State_Actuator_CAT` FB named `Pusher` with all 10 parameters set (no sensors fitted, 2-second travel times, 4-second fault timeouts). Smoke test for the pipeline. Output goes to a folder picker (default `%USERPROFILE%/Documents/MapperOutput`).

**Generate Full Feed Station** — Reads the loaded `Control.xml`, finds the Process named `Feed_Station`, walks its referenced components, and emits a `<ProjectName>.syslay` with: 1 `Area_HMI`, 1 `Area`, 1 `Station1`, 1 `Station1_HMI`, 1 `Process1` (`Process1_Generic`), one `Five_State_Actuator_CAT` per actuator, one `Sensor_Bool_CAT` per sensor, plus station-level and area-level `CaSAdptrTerminator`. Wires the INIT chain, the `Area→Station→Components→Stn1_Term` `CaSAdptr` chain, and the closed `stateRptCmdAdptr` ring among components plus `Process1`. HMI adapters wire `Area_HMI→Area` and `Station1_HMI→Station1`.

## Prerequisite CAT and Basic templates

Before opening a generated `.syslay`, the target EAE project's `IEC61499` folder must already contain these types (deploy via the Mapper's Template Library Deployer or import manually):

CATs: `Five_State_Actuator_CAT`, `Sensor_Bool_CAT`, `Process1_Generic`, `Station_CAT`, `Area_CAT`, `Station`, `Area`, `CaSAdptrTerminator`.

Internal helper Basic FBs: `ProcessRuntime_Generic_v1`, `ProcessStateBusHandler`, `FiveStateActuator`, `Sensor_Bool`, `Station_Core`, `Station_Fault`, `Station_Status`, `FaultLatch`, `actuatorStateEvents`, `updateComponentState`, `updateComponentState_Sensor`, `No_Sensor_Handler`. Composite `faultDetection`. Adapters `CaSAdptr`, `AreaHMIAdptr`, `StationHMIAdptr`, `stateRptCmdAdptr`. Plus `SE.AppBase.plcStart` from EAE's standard library.

## v1 limitations

- Process recipe arrays (`StepType[]`, `CmdTargetName[]`, etc.) are default-empty; `Process1` will not actually sequence anything until the `ProcessRuntime_Generic_v1.initialize` algorithm is hand-edited or the upcoming step-table generator is wired in.
- `DataConnections` are not auto-generated; manual wiring is required for sensor-to-process status feeds and PLC I/O routing.
- Components of `Type=Robot` in `Control.xml` are skipped with a warning logged to the Validation Results panel.
- Multi-Process per Station is not supported in this build; Button B works for single-Process stations like `Feed_Station`.

## Run tests

From the repo root:

```
dotnet test MapperTests/MapperTests.csproj
```

23 xUnit tests cover deterministic FB ID generation, station grouping (filters out Process and Robot references), parameter derivation (sensor flags, fault timeouts, value formatting), `SyslayBuilder` XML output and round-tripping, and end-to-end Pusher + Feed Station `.syslay` generation against the bundled `Feed_Station_Fixture.xml`.
