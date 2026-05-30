# Test Simulator — Iteration Plan

Sequential iterations the loop walks through. Each iteration has a **goal**, the **edits**, the **verification** (always `dotnet test` against the harness), and a **DoD** (definition of done). Tick the box when DoD is met. Do not start the next iteration until the current one is green.

Update `C:\VueOneMapper\CLAUDE.md` `## Status` section after every iteration finishes.

---

## Iteration 1 — Tighten checklist C to per-row recipe assertion ✅

**Goal:** "Assembly_Station.Recipe references all actuators" today is a coarse substring scan. Replace with a real assertion: at least one `StepType=1` (CMD) row in the Recipe parameter targets each Assembly actuator. Concrete end-to-end claim, not coincidence-friendly.

**Edits:**
- `MapperTests\SimulatorEndToEndHarness.cs` — replace `CheckRecipeReferencesActuators` with a parser that splits the Recipe parameter, finds StepType=1 entries, and asserts each `AssemblyActuators[]` name appears as a `CmdTargetName` in at least one CMD row.

**Verification:** `dotnet test … --filter SimulatorEndToEndHarness`.

**DoD:** C still green after the tighter assertion. Other items unchanged.

---

## Iteration 2 — Refactor MainForm_simulator post-processors to public statics ✅

**Goal:** Clear the last 3 red checklist items (B5 sysres + D×2) by making the simulator post-gen helpers callable from the harness instead of trapped behind a partial WinForms class.

**Edits:**
- New file `CodeGen\CodeGen\Services\SimulatorPostProcessor.cs` (public static class) with the bodies of these 4 MainForm methods moved verbatim:
  - `InjectSimHopperForce(string syslayPath, MapperConfig cfg) → int`
  - `OverrideSimActuatorsNoSensor(string syslayPath, MapperConfig cfg) → int`
  - `VerifySimActuatorsNoSensorOrAbort(string syslayPath, MapperConfig cfg)` (throws on violation)
  - `DumpSimRecipeAndInterlockArrays(string syslayPath) → string[]` (return the lines it would log so the harness can echo them)
- `MapperUI\MapperUI\MainForm_simulator.cs` — replace each method's body with a one-line pass-through (`SimulatorPostProcessor.Foo(...)`), so the live "Test Simulator" button keeps working byte-identical.
- `MapperTests\SimulatorEndToEndHarness.cs` — delete the local `ApplyNoSensorOverride` replica, call `SimulatorPostProcessor.OverrideSimActuatorsNoSensor` instead. Also call `SimulatorPostProcessor.InjectSimHopperForce` so D clears.

**Risk:** MainForm_simulator drives the live "Test Simulator" button. The thin pass-through pattern keeps behaviour byte-identical. The harness verifies the publics — if green, MainForm calling the same publics is green too.

**Verification:**
1. `dotnet build` everything (MapperUI must compile).
2. `dotnet test … --filter SimulatorEndToEndHarness` — expect B5 sysres + D×2 green.
3. Visual inspection that MainForm_simulator's signatures and call order are unchanged.

**DoD:**
- All previous greens stay green.
- B5 sysres and D×2 turn green.
- MapperUI.dll still builds (the live button isn't blocked from compiling).

---

## Iteration 3 — Audit where Clamp comes from on the live rig deploy ✅

**Goal:** Decide whether Clamp belongs on the harness B4 checklist. It's in the deployed M580 sysres but NOT a `<Component>` in `SMC_Vue2VC_With_Processes/Control.xml`. Either it's emitted from a hardcoded path (and the harness should expect it) or it was hand-added in a prior project state (and the harness is right to leave it off).

**Investigation:**
- `grep -r '"Clamp"' CodeGen/` — find the hardcoded source.
- `grep -r 'Clamp' "Template Library/"` — check for template-level injection.
- Look at `Station2DeviceEmitter`, `SysresFbMirror`, `SystemLayoutInjector` for any Clamp insertion.

**Decision rule:**
- If Clamp is hardcoded into the simulator path: add it back to `AssemblyActuators` in the harness, assert presence.
- If Clamp only appears on the hardware path (Test Runtime, not Test Simulator): leave the harness as-is and document the asymmetry in the comment above `AssemblyActuators`.
- If Clamp is from a different VueOne Control.xml the user sometimes loads: document the fixture choice (`Full_System_Fixture.xml` doesn't contain it) and add a `Component` to the fixture later if needed.

**Verification:** harness still green after the decision.

**DoD:** the comment above `AssemblyActuators` accurately states where Clamp comes from and why the checklist treats it the way it does.

---

## Iteration 4 — Audit the deeper "end-to-end" properties ✅ (partial)

**Goal:** Once 1-3 are green, push the harness from "shape is right" toward "behaviour would actually cycle." Specifically:

- ✅ **Recipe terminates in END (`StepType=9`)** — Assembly's recipe has 1 END row. Harness check F1.
- ⬜ **Wait1Id resolves** — deferred to Iteration 5.
- ✅ **Init chain is connected** — Harness check F2: INIT graph BFS reaches `Assembly_Station.INIT` from any `*.INITO` root.
- ⬜ **Cross-PLC waits resolve** — folded into Iteration 5 (Wait1Id resolution implicitly covers it because cross-PLC waits surface as Wait1Id values).
- ✅ **Repeatability (F)** — Harness check F3 green after seeding `SimHopperForce` FB IDs without the absolute path (was the lone source of non-determinism).

---

## Iteration 5 — Wait1Id resolution ✅

Result: F4 green. All 8 distinct non-zero Wait1Ids in `Assembly_Station.Recipe` resolve to an `actuator_id`/`id` parameter on an FB in the SIM resource. Available id set has 16 entries (the 12 Five_State actuators + 4 sensors), Wait1Id set is a clean subset.

---

## Iteration 6 — Cleanup + EAE handoff ✅

**Goal:** Tidy the harness (4 xUnit warnings about `Assert.True(false)` → `Assert.Fail`; remove the dead local `ApplyNoSensorOverride` replica replaced by `SimulatorPostProcessor.OverrideSimActuatorsNoSensor` in Iteration 2) and write the EAE-side verification steps the user runs after the harness goes green.

**Edits:**
- `MapperTests\SimulatorEndToEndHarness.cs` — `Assert.True(false, msg)` → `Assert.Fail(msg)` ×4; delete `ApplyNoSensorOverride` body.
- `CLAUDE.md` — add an `## EAE verification steps` section: the click sequence in the live MapperUI + EAE that takes the harness-proven generation into a running simulator (Reload Solution, set Active Network Profile = "Local Test", clean Build, Deploy, log in, Watch list to add).

**Verification:** `dotnet test` still 100% green, zero warnings.

**DoD:** harness is warning-free, dead code removed, CLAUDE.md tells the user exactly what to do in EAE next.

**Edits:** harness only.

**Verification:** `dotnet test … --filter SimulatorEndToEndHarness`.

**DoD:** all of the above either turn green or are surfaced as concrete RED items the user can act on.

---

## Loop convention

After each iteration:
1. Tick the iteration's heading (`## Iteration N — … ✅`) so the next loop run knows where to resume.
2. Append a one-line bullet to `CLAUDE.md` `## Status` with what changed and the resulting pass/fail count.
3. Do not commit. Commits wait for the user's explicit go.
