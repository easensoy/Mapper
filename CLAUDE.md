# VueOneMapper — Autonomous Loop Brief

**For Claude Code agents running on `/loop` or autonomously.** Read this *before* doing anything. Update `## Status` at the bottom every iteration so the next loop run knows where you stopped.

---

## End goal

A C# generator that turns a VueOne digital-twin `Control.xml` into a complete EAE 24.1 IEC 61499 project for the SMC rig (M262 + M580 + BX1) and the simulator. Change the digital twin, click one button, the new layout drives the real PLCs. No engineer hand-writes IEC 61499 for each layout change.

## Current focus — SEVEN_STATE END-TO-END IN TEST SIMULATOR

The bench rig is unsafe (damaged clamp, swivel collision risk), so **all testing is in the EAE simulator**, not on the rig. The button is `btnGenerateFullSystemSimulator_Click` in `MapperUI\MapperUI\MainForm_simulator.cs`. The flag is `Cfg().SimulatorFullSystem = true`. All three PLCs collapse into one SIM resource and the single ring resolves cross-PLC waits.

**Session goal (2026-05-30 onwards):** make `Bearing_PnP` actually run as the real 3-position `Seven_State_Actuator_CAT` end-to-end in the simulator (recipe cycles it Pick → Place → Home, sim sensors close on a timer, harness verifies). The prior loop landed end-to-end Assembly with the Five_State stub (13/16 green); this session removes the stub and the swivel becomes a real 3-position actuator in sim.

**The two pieces of work:**
1. **The three deferred rig fixes** — commit surgical CAT to zip, exclude Seven from `BuildStation2Wiring.stationChain`, parameterize `process_state_name = lowercased name` on Seven instances. Small, safe, take effect when the stub flag flips.
2. **Sim sensor synthesis for Seven_State** — the new substantial piece. Mirror the Five_State no-sensor pattern: inject a `SimSwivelForce` post-processor that publishes the actuator's `$${PATH}atwork1` / `$${PATH}atwork2` symlinks, driven off the FB's own `current_state1_to_plc` / `current_state2_to_plc` coil-drive outputs with an `E_DELAY` settle. Without it, flipping the stub stalls the swivel at `ToPick` forever (Seven_State's ECC waits on `atwork1 = TRUE AND atwork2 = FALSE` which the sim never closes today).

**Scope clamp for this session:**
- ✅ Edit `Template Library/CAT/Seven_State_Actuator_CAT.cat.zip` to commit the surgical CAT body.
- ✅ Edit `BuildStation2Wiring` in `SystemLayoutInjector.cs`.
- ✅ Edit `BuildMinimalActuatorParameters` Seven branch in `SystemLayoutInjector.cs`.
- ✅ Add a new public static `CodeGen/Services/SimulatorPostProcessor.cs` (or extend the existing sim post-process scope) for `SimSwivelForce`.
- ✅ Flip `StubSevenStateActuatorsAsFiveState = false` ONLY after the SimSwivelForce post-processor is in place and the harness is updated to verify Seven_State end-to-end.
- ✅ Update the harness to (a) recognise `Bearing_PnP` as `Seven_State_Actuator_CAT`, (b) check `SimSwivelForce` wiring, (c) assert Pick/Place/Home CMDs in the recipe.
- ❌ DO NOT edit the rig path (`btnTestStation1_Click`) — it inherits the rig fixes through shared code and gets verified when the rig comes back.
- ❌ DO NOT re-add the `RecipeTestActuatorAllowlist` entries.
- ❌ DO NOT regenerate the hand-crafted Excel under `MapperTests\TestData\SMC_Rig_IO_Bindings.xlsx`.
- ❌ DO NOT touch `C:\Demonstrator` directly. The Mapper writes there via the pipeline; the harness writes to a temp dir.

## Status — UPDATE EVERY LOOP ITERATION

> Append a dated bullet at the top each iteration. Keep it short — one line of what changed, one line of what to do next. Older entries stay below as history.

- **2026-05-30 — Seven_State CAT surgery session — 18/18 GREEN with stub OFF.** Three pieces landed this session and the harness now verifies Seven_State end-to-end in sim:
  1. **Three deferred rig fixes** (all source-proven, no behaviour change while stub was on):
     - Fix (a) was already done in the working tree — the surgical `Seven_State_Actuator_CAT.fbt` (with `stateRprtCmd` socket + `StateHandling`/`updateComponentState` ring node) is byte-identical between the deployed `IEC61499/Seven_State_Actuator_CAT/` copy and the committed `Template Library/CAT/Seven_State_Actuator_CAT.cat.zip`'s embedded `.fbt` (verified `diff -q` empty; 19 ring/StateHandling/Sockets/Plugs token matches in zip). The "deploy-revert trap" is no longer active in this working tree.
     - Fix (b) — `BuildStation2Wiring` (SystemLayoutInjector.cs ~2230-2240) now skips Seven_State actuators from the `stationChain` (mirroring `ResourceWireEmitter.NoStationAdapterTypes`) so it no longer dangles `Bearing_PnP.stationAdptr_in/out` edges against a CAT that has no stationAdptr port. No-op while stub is on.
     - Fix (c) — `BuildMinimalActuatorParameters` Seven branch (SystemLayoutInjector.cs ~1664-1668) now parameterises `process_state_name = <lowercased actuator name>`, statically satisfying `SevenStateActuator2`'s ECC `process_state_name = actuator_name` gate. Without this the swivel never leaves START on a Pick/Place/state_val=Home command no matter what the ring delivers.
  2. **New post-processor `SimulatorPostProcessor.InjectSimSwivelForce`** (~200 lines, added to `CodeGen/Services/SimulatorPostProcessor.cs`). For every `Seven_State_Actuator_CAT` instance in the SIM syslay AND sysres it injects one top-level `SimSwivelForce_<name>` SYMLINKMULTIVARSRC publishing `'<ResourceName>.<name>.atwork1'` and `'<ResourceName>.<name>.atwork2'`, with VALUE1/2 data-wired from the actuator's own `current_state{1,2}_to_plc` outputs and REQ event-wired from `<name>.plc_out`. Net: atwork1 closes the instant the actuator drives coil 1, mirror for coil 2. ECC's ToPick→AtPick / ToPlace→AtPlace gates fire in sim without physical sensors. `athome` left unpublished (defaults FALSE; the ECC's INIT→START falls through on the atworks=FALSE arm and AtHome is timer-driven). Idempotent re-runs, deterministic IDs (path-independent SHA1 seed). MainForm_simulator now calls it after the no-sensor override; harness also calls it.
  3. **Flipped `StubSevenStateActuatorsAsFiveState = false`.** Bearing_PnP now deploys as `Seven_State_Actuator_CAT` (verified: B4 reports `Seven_State_Actuator_CAT` for Bearing_PnP, `Five_State_Actuator_CAT` for the other Assembly actuators). The recipe's MapSevenStateCommandFromConditionName emits state_cmd 1/2/0 for pick/place/home in lock-step with the CAT's TargetPick/Place/HomeState=1/2/0 parameters.
- **Harness coverage extended:** B4 now computes the expected CAT type per-actuator (Bearing_PnP → Seven_State_Actuator_CAT when stub off, Five_State_Actuator_CAT otherwise). New `D2` check verifies SimSwivelForce wiring for every Seven instance in both syslay and sysres. F3 fixed — `CheckRepeatability`'s second-run pipeline now mirrors the main pipeline by also calling `InjectSimSwivelForce`.
- **Result:** harness 18/18 green. **NOTE:** the stub flip also affects the **rig path** — if Test Runtime is clicked, Bearing_PnP will deploy as Seven_State on M580. That's intentional (fixes a/b/c prepare for it) but unverifiable until the rig comes back. Sim-only commitment held throughout.
- **Next handoff (you):** close MapperUI, rebuild (recompiles CodeGen), launch, click **Test Simulator**, then in EAE: Reload Solution → Active Network Profile = "Local Test" → clean Build → Deploy → Login → Watch `Assembly_Station.ProcessEngine.CurrentStep` + `cmd_target_name` + `cmd_state` + `Bearing_PnP.state` cycle through Pick → Place → Home. If the swivel stalls at any step, paste those four values back and the loop triages from source.
- **2026-05-29 — ITERATIONS.md items 4-6 — harness at FULL coverage, 0 warnings, 0 errors, test green.** Added F1 (recipe terminates in END row — 1 END row in Assembly), F2 (INIT chain BFS reaches `Assembly_Station.INIT` from any `*.INITO` root — 19 roots, 36 reached), F3 (two runs produce byte-identical syslay+sysres after temp-dir normalisation — required dropping the absolute path from `SimHopperForce`'s SHA1 seed in `SimulatorPostProcessor`; deterministic-across-deploys is strictly better and harmless), F4 (every non-zero `Wait1Id` resolves to an `actuator_id`/`id` in the SIM resource — 8 Wait1Ids ⊆ 16 available ids). Iteration 6 cleanup: 4× `Assert.True(false)` → `Assert.Fail`, deleted the dead local `ApplyNoSensorOverride` replica (replaced by `SimulatorPostProcessor.OverrideSimActuatorsNoSensor` in Iteration 2), and added an `## EAE verification steps` section to this file telling you exactly what to click in MapperUI + EAE to take the harness-proven generation into a running simulator. **Harness is now the verification gate for any sim-affecting Mapper change.** Next handoff: you run the EAE steps; if anything stalls, paste `Assembly_Station.ProcessEngine.CurrentStep` + `cmd_target_name` + `cmd_state` back and the loop triages it from source.
- **2026-05-29 — ITERATIONS.md item 3 — Clamp audit done, harness still 16/16 green.** Documented in the harness's `AssemblyActuators` comment that Clamp is not hardcoded into the Mapper — the legacy `C:\VueOne\system\Control.xml` has 1 Clamp Component (which is what the live rig deploys from), the canonical `SMC_Vue2VC_With_Processes/Control.xml` (our fixture) has 0. `SystemLayoutInjector` only includes Clamp in an allow-list and a name-based PLC guess; it does not force-emit. Whether Clamp appears in the syslay is a fixture decision, not a Mapper bug. Next: Iteration 4 — deeper end-to-end properties (recipe END row, Wait1Id resolution, init-chain trace, byte-identical repeatability).
- **2026-05-29 — ITERATIONS.md item 2 — 16/16 GREEN. Harness fully passing.** Extracted the 4 sim post-processors (`InjectSimHopperForce`, `OverrideSimActuatorsNoSensor`, `VerifySimActuatorsNoSensorOrAbort`, `DumpSimRecipeAndInterlockArrays`) out of `MainForm_simulator.cs` into new public `CodeGen/Services/SimulatorPostProcessor.cs`. MainForm keeps the original private signatures as one-line delegations — the live "Test Simulator" button is byte-identical to before. Harness now calls the same publics + `SysresFbMirror.MirrorFbsIntoSysres` (the lower-level public for the FB mirror that `FinalizeM262Stack` normally wraps inside `M262SysdevEmitter.Emit`, which needs a full EAE tree the harness doesn't have). Result: syslay 29 FBs, sysres 31 FBs, all 12 Five_State actuators have no-sensor params in both files, SimHopperForce wired in both, Assembly recipe commands 8 distinct actuators (bearing+gripper, shaft Hr/Vr/gripper, coverPNP Hr/Vr/gripper), Disassembly parked. **MapperUI.dll also builds clean — the live button is not blocked.** Next: Iteration 3 — Clamp audit.
- **2026-05-29 — iteration 3 (ITERATIONS.md item 1) — C tightened, still 13 pass / 3 fail.** Replaced the substring scan in `CheckRecipeReferencesActuators` with a regex parser over the Recipe parameter STRUCT blob (4353 chars). It picks out CMD rows by `StepType:=1` and pulls the `CmdTargetName:='…'` token. Found 8 distinct CMD targets — `bearing_pnp, bearing_gripper, shaft_hr, shaft_vr, shaft_gripper, coverpnp_hr, coverpnp_vr, coverpnp_gripper` — so the full Assembly chain INCLUDING BX1 covers is genuinely being commanded. C remains green and now means something. Next: Iteration 2 — refactor sim post-processors out of MainForm_simulator to clear the last 3 reds.
- **2026-05-29 — iteration 2 — 13 pass / 3 fail.** Two fixes:
  1. Swapped the fixture from `Feed_Station_Fixture.xml` (only 8 components, Feed-only) to a new `Full_System_Fixture.xml` copied from `C:\VueOne\system\SMC_Vue2VC_With_Processes\Control.xml` (34 components — full SMC system with Bearing_PnP 13-state, Shaft_Hr/Vr, the Robot grippers, and the 3 Processes). The "39 Assembly/Bearing/Shaft/Clamp tokens" the prior baseline counted were inside the Processes' transition names, not standalone Component definitions — the old fixture had no M580 actuators to emit.
  2. Added two post-gen calls to the harness: `M262SysresWireEmitter.Emit` and a local replica of `OverrideSimActuatorsNoSensor` (the latter is `private` to `MainForm_simulator.cs`).
- Result: syslay now has all 5 in-Control.xml Assembly actuators (Bearing_PnP, Bearing_Gripper, Shaft_Hr, Shaft_Vr, Shaft_Gripper) typed Five_State_Actuator_CAT under the stub. The Assembly recipe references each one. Greens: A1, A2, B1-B4 (×5), B5 syslay, B6, C, E.
- **Remaining reds (3):**
  - **B5 sysres** — sysres FBNetwork is empty (0 FBs). `M262SysresWireEmitter.Emit` only wires existing FBs; the FB *mirror* into the sysres happens inside `FinalizeM262StackAsync` (private) via `M262SysdevEmitter.Emit`, which expects a real EAE project tree on disk. To clear: either drive `M262SysdevEmitter.Emit` with a stub project tree the harness sets up, or refactor the sim-mirror step out of MainForm into a public static.
  - **D×2 SimHopperForce missing in both syslay and sysres** — `InjectSimHopperForce` is `private` to `MainForm_simulator.cs`. To clear: refactor it (and the no-sensor override + verify) into `CodeGen/Services/SimulatorPostProcessor.cs` (public statics). MainForm calls the public versions; harness does too.
- **Notes for next iteration:**
  - Clamp was correctly dropped from the harness checklist — it's not a `Component` in this Control.xml; on the rig it's emitted from a separate hardcoded path. Audit where Clamp comes from before re-adding it as a required Assembly actuator.
  - C's substring scan over Recipe parameter values is coarse. Tighten to assert per-row that the Recipe STRUCT carries at least one `StepType=1` (CMD) row for each Assembly actuator (`CmdTargetName` field).
  - Don't refactor MainForm_simulator under time pressure if it risks breaking the live MapperUI button — extract the methods behind tests first, then swap MainForm to call the publics.
- **2026-05-29 — baseline captured (5 pass / 15 fail).** Harness compiles + runs against `Feed_Station_Fixture.xml` headless. Greens: `A1` (generation succeeds), `B1-B3` (all three Process FBs present), `B6` (no phantom Process source-pin connections — proving my earlier 'phantom wires emit' worry didn't materialise in sim), `E` (Disassembly parked). Reds cluster into three root issues:
  1. **`B4×6 + C` — M580 Assembly actuators NOT in the SIM syslay.** Top-level FBs in syslay = 17 (Process×3 + Feed actuators/sensors + Station/Area frames) but zero `Bearing_PnP / Bearing_Gripper / Shaft_Hr / Shaft_Vr / Shaft_Gripper / Clamp`. `Assembly_Station.Recipe` consequently references none of them. This is the dominant blocker — the simulator collapse emits the Assembly *Process* but not its actuators.
  2. **`B5 × 6` — no-sensor override never runs in the harness.** Feeder/Checker/Transfer still carry `WorkSensorFitted="TRUE"`; sysres is empty entirely. `OverrideSimActuatorsNoSensor` + `VerifySimActuatorsNoSensorOrAbort` live as private methods on `MainForm_simulator.cs` — the harness can't call them. Either expose them or mirror their logic in the harness.
  3. **`D×2` — `SimHopperForce` not injected** for the same reason — `InjectSimHopperForce` is private to `MainForm_simulator.cs`.
- **Next iteration target:** issue (1). Find where `GenerateStation1TestSyslay` filters out M580 actuators in the SimulatorFullSystem path and include them. That single fix should turn B4 + C green together.
- **2026-05-29 (initial brief written)** — `MapperConfig.RecipeTestActuatorAllowlist` cleared to `new string[0]` so the full Assembly recipe regenerates. Brief + harness skeleton being landed.

## Convergent findings from the May 29 deep investigation

**Do not re-investigate these. They are source-proven; cite line numbers if you need to verify.**

1. **The bearing rig outputs read 1970 timestamp not because of the `.hcf` binding form** (`M580SymbolBinder.cs` emits Form-1 GUID triples, byte-identical to the working M262). The deployed `bin\Deploy\` was purged by `CompileCachePurger` on every Generate; a download done *before* the last Generate is already stale by the time the user tests.

2. **The `Process1_Generic.fbt` interface has no data/event outputs at all** — `EventOutputs = [INITO]`, `OutputVars = empty`, only the two adapter plugs (`stateRptCmdAdptr_out`, `stationAdptr_out`). So all four wires in `SystemLayoutInjector.WireSevenStateActuators` (line 538-541) are phantom — `proc.state_update`, `proc.actuator_name`, `proc.state_val` and `proc.{lc}` do not exist on the Process side. The Process commands actuators only over the `stateRprtCmd` ring.

3. **`updateComponentState.BREQ` (lines 82-89)** matches `IF component_state_in.dest_name = name` (case-sensitive STRING `=`). On match it sets `state_cmd` and fires `CNF`. Shared by Five_State and Seven_State — so the recipe targeting works identically for both.

4. **The Seven_State swivel's deeper fault (decisive):** `SevenStateActuator2`'s Pick/Place/Home transitions gate on `process_state_name = actuator_name AND state_val = TargetPickState/...`. The ring delivers `state_val` + `pst_event` via `StateHandling.CNF` but **does not deliver `process_state_name`**. A `grep` of the Mapper shows `process_state_name` is referenced only at lines 523 and 539 (both phantom wires) and is **never `SetParam`'d**. So on a deployed Seven instance `process_state_name` is the STRING default `''` while `actuator_name = 'bearing_pnp'` → gate FALSE forever → only the bare `tohome` event moves it. The recipe's `state_val` arm is already in lock-step (recipe emits `pick=1/place=2/home=0` via `ProcessRecipeArrayGenerator.MapSevenStateCommandFromConditionName` at lines 1248-1261; CAT defaults match at `SystemLayoutInjector.BuildMinimalActuatorParameters` lines 1664-1668).

5. **The deploy-revert trap:** `ExtractToEae` is copy-if-absent. `DemonstratorWiper.FoldersToDelete` includes `Seven_State_Actuator_CAT` explicitly. A Clean → next Generate re-extracts the committed `fecba79` no-socket zip, which then sticks. **The surgical Seven_State CAT in the deployed `.fbt` is not in the committed `.cat.zip`**, so it dies on a Clean. This is irrelevant to the simulator focus (sim uses Five_State stub for bearing) but matters when the rig comes back.

6. **`BuildStation2Wiring` (sys-lay) vs `ResourceWireEmitter` (sysres) disagree on Seven_State adapters.** Syslay hardcodes Five_State and wires `Bearing_PnP.stationAdptr_in/out`; the deployed Seven_State CAT has no stationAdptr port. Throws on import when Seven is deployed. Same story: only matters when the rig comes back.

### Deferred rig fixes — DO NOT IMPLEMENT IN THIS LOOP

These are the three fixes that make the rig Seven_State path work. They have no effect on the simulator path, so they are out of scope for this loop. Queued for when the rig comes back:

1. Commit the surgical `Seven_State_Actuator_CAT.fbt` into the committed `.cat.zip` so a Clean doesn't revert it.
2. In `BuildStation2Wiring`, exclude Seven from the `stationChain` (mirror `ResourceWireEmitter.NoStationAdapterTypes`), stop hardcoding Five_State.
3. In `SystemLayoutInjector.BuildMinimalActuatorParameters` (the Seven branch around line 1664-1668), add `["process_state_name"] = SyslayBuilder.FormatString(actuator.Name.ToLowerInvariant())` next to `actuator_name`.

## Finish line — Assembly Station end-to-end in simulator

The headless harness asserts these. Each iteration aims to turn red items green.

### A. Generation succeeds end-to-end

- [ ] `injector.GenerateStation1TestSyslay(cfg, fixture, bindings, out report)` returns with no exception when `cfg.SimulatorFullSystem = true`.
- [ ] `FinalizeM262Stack` completes without exception.
- [ ] `M262SysresWireEmitter.Emit(cfg, report)` completes.
- [ ] `HcfPatchService.PatchDeployed(cfg, path, bindings, report)` completes.

### B. Syslay + sysres have the right shape

- [ ] One `Process1_Generic` FB named `Assembly_Station` (M580 process) is present in the SIM syslay.
- [ ] One `Process1_Generic` FB named `Disassembly` is present.
- [ ] The Process FB named `Feed_Station` (M262) is present.
- [ ] All Assembly actuators are present: `Bearing_PnP`, `Bearing_Gripper`, `Shaft_Hr`, `Shaft_Vr`, `Shaft_Gripper`, `Clamp`. All are `Type=Five_State_Actuator_CAT` (because stub flag is true).
- [ ] Every `Five_State_Actuator_CAT` instance in the syslay AND sysres has `WorkSensorFitted="FALSE"` and `HomeSensorFitted="FALSE"`.
- [ ] No FB references a source pin that doesn't exist (phantom checks: `proc.state_update`, `proc.actuator_name`, `proc.state_val` should NOT appear as `Source=` in `EventConnections`/`DataConnections` of the SIM syslay).

### C. Assembly recipe has actuator coverage

- [ ] `Assembly_Station.Recipe` parameter has at least one `CMD` row (`StepType=1`) per Assembly actuator (`bearing_pnp`, `bearing_gripper`, `shaft_hr`, `shaft_vr`, `shaft_gripper`, `clamp`), not just bearing.
- [ ] Recipe terminates in an `END` row (`StepType=9`).
- [ ] Every `Wait1Id` in Assembly's recipe resolves to an FB id present in the SIM resource (no out-of-scope drops the harness silently swallows).

### D. Sim no-sensor model is intact

- [ ] `SimHopperForce` SYMLINKMULTIVARSRC is present in both syslay and sysres.
- [ ] Its wiring matches `MainForm_simulator.InjectSimHopperForce`'s defensive idempotent rebuild (FB1.INITO → SimHopperForce.INIT → Area.INIT; PartInHopper.INITO → SimHopperForce.REQ).
- [ ] `VerifySimActuatorsNoSensorOrAbort` would PASS (every Five_State has the no-sensor params, no duplicates).

### E. Disassembly is parked

- [ ] `Disassembly.Recipe` is a single `END` row (the intra-PLC handoff park guard). This is fine — Assembly is the focus.

### F. Repeatability

- [ ] Harness runs deterministic: same Control.xml + same MapperConfig → byte-identical generated `.syslay`/`.sysres`/`.hcf` two runs in a row.

## How to run the harness

```powershell
# From C:\VueOneMapper
dotnet build MapperTests\MapperTests.csproj -c Debug
dotnet test  MapperTests\MapperTests.csproj -c Debug --filter "FullyQualifiedName~SimulatorEndToEndHarness" --logger "console;verbosity=detailed"
```

Or as a one-shot CLI (the harness prints PASS/FAIL per checklist item to stdout and exits non-zero on any failure, so the loop can `dotnet test` and read the exit code).

## EAE verification steps (after the harness is green)

The harness proves the generation pipeline produces the right artefacts. EAE is the only thing that proves the *runtime* actually cycles. Sequence (once per change to the generator):

1. **Close MapperUI** if it's open. CodeGen.dll is loaded into MapperUI's process — running MapperUI must be restarted before it sees a new build.
2. **Rebuild MapperUI** in Visual Studio (or `dotnet build MapperUI/MapperUI/MapperUI.csproj`). This recompiles CodeGen too.
3. **Launch MapperUI**, Browse to the Control.xml you want to drive the sim with (e.g. `C:\VueOne\system\SMC_Vue2VC_With_Processes\Control.xml`).
4. **Click "Test Simulator"** (NOT Test Runtime — that's the rig path, currently unsafe).
   - The activity log will show the same steps the harness exercises, plus the live `[Simulator][SymCheck]` and `[Simulator][Verify]` lines that confirm SimHopperForce wiring and the no-sensor override.
5. **In EAE 24.1:**
   - Reload Solution (so EAE picks up the regenerated `.syslay` / `.sysres` / `.hcf`).
   - In the device tree, set the dPAC's **Active Network Profile to "Local Test"** (this is what "sim mode" means in EAE — there is no SIM_RES resource type; the resource stays `EMB_RES_ECO` and the simulator runtime is selected by the network profile).
   - **Clean Build** (not a partial build — every Mapper regen purges `bin\Deploy\` and `obj\` via `CompileCachePurger`, so an incremental build is undefined).
   - **Deploy.**
   - **Login** to the dPAC. EAE Online Watch becomes active.
6. **Add this Watch list** to verify the Assembly cycle:
   - `Assembly_Station.ProcessEngine.CurrentStep` — should march from 0 through the recipe.
   - `Assembly_Station.ProcessEngine.cmd_target_name` — the lowercased actuator the engine is currently commanding.
   - `Assembly_Station.ProcessEngine.cmd_state` — the state value being sent (1/2 for Pick/Place under stub, work/home for Five_State).
   - `Assembly_Station.ProcessEngine.CMDREQ` — pulses when a CMD is issued.
   - For each Assembly actuator (`Bearing_PnP`, `Bearing_Gripper`, `Shaft_Hr`, `Shaft_Vr`, `Shaft_Gripper`): the FB's `current_state_to_process` (the internal `FiveStateActuator` output) — should advance 0 → 1 → 2 → 3 → 0 over the recipe's CMD/WAIT cycles, driven by the no-sensor timer.
   - `Feed_Station.PartInHopper.Input` — should read TRUE at startup (SimHopperForce keeps it forced).
7. **Expected sequence on a successful run** (intra-process Assembly only — cross-process Wait1Ids onto Feed_Station get dropped in sim per `SystemLayoutInjector.ClassifyState`'s out-of-scope guard, so Assembly runs standalone):
   - `cmd_target_name` cycles through `bearing_pnp` → `bearing_gripper` → `shaft_hr` → `shaft_vr` → `shaft_gripper` (and the BX1 cover chain). Each actuator's `current_state_to_process` advances on its own toWorkTime/toHomeTime via the No_Sensor_Handler timer.
   - `Assembly_Station.ProcessEngine.CurrentStep` increments past each CMD/WAIT pair until it reaches the END row (StepType=9), at which point it parks (or loops, depending on engine config).
   - `Disassembly.ProcessEngine.CurrentStep` stays at the single END row (intra-PLC handoff park guard).
8. **If the recipe stalls at a row**, copy `CurrentStep`, `cmd_target_name`, `cmd_state`, and the `current_state_to_process` of the named actuator into the loop's next iteration prompt — that's enough to triage the stall from source.

**Fixture used:** `MapperTests\TestData\Feed_Station_Fixture.xml` (full system — Feed + Assembly + Disassembly + BX1 cover components, despite the name).

**Real Control.xml** lives at `C:\VueOne\system\Control.xml` and can be pointed at by the harness for one-off cross-checks, but the loop's deterministic iteration uses the committed fixture.

## Standing rules

- **Commit each file separately.** No bundling. No `Claude` attribution in any commit message.
- **HTTPS push only**, never SSH. Don't touch `git config` or `~/.git-credentials`.
- **Push to `github.com/easensoy/Mapper`.**
- Never regenerate `MapperTests\TestData\SMC_Rig_IO_Bindings.xlsx` — it is hand-crafted per-CAT content.
- Every time you change Mapper code that affects the live MapperUI app, the user must close MapperUI and rebuild it before clicking Test Simulator. State that in the iteration's Status note so the user knows.
- Before claiming a checklist item green, the harness must actually assert it. Don't mark it on confidence alone.

## Memory references

- Project memory index: `C:\Users\alper\.claude\projects\C--VueOneMapper\memory\MEMORY.md`
- Global rules: `C:\Users\alper\.claude\CLAUDE.md`
- This file overrides nothing — it adds project-loop-specific guidance on top of those.
