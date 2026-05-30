# Reverted Fixes

Things that have been tried and reverted. **Do not re-attempt** without reading
the reason. Each entry: what was tried, why it seemed right, why it was
reverted, what the working alternative is.

If you find yourself proposing one of these, STOP. Cite the entry, don't
re-derive it.

---

## R-1. Adding an `Area` FB to the Assembly Station

**Tried (multiple times across mid-May 2026):** mirror the M262 Feed Station's
`Area` FB onto the Assembly Station's M580 sysres so Assembly's INIT chain has
a top-level `Area.INIT` to dispatch from.

**Why it seemed right:** Feed Station has an `Area` FB and works; symmetry
suggested Assembly should too.

**Why it was reverted:** the M580 Assembly Station's INIT chain on the
**syslay** routes through `Station2 → Assembly_Station.INIT` directly; the
sysres mirror added a phantom `Area` FB that EAE couldn't resolve, throwing
"Missing Instances" on import.

**What works instead:** Station2 → Assembly_Station.INIT (no Area FB on M580).
The Feed Station's Area lives on M262 only and is M262-specific.

**Source:** memory `feedback_assembly-m580-pitfalls.md`.

---

## R-2. Cross-process-aware clamp auto-retract

**Tried (May 2026):** emit a synthetic clamp-retract step in the Disassembly
recipe whenever Assembly's recipe completed a Clamping_Part state, so the clamp
auto-retracted before Disassembly took over.

**Why it seemed right:** symmetry — Assembly clamps the part, Disassembly
should un-clamp before doing anything else, and the digital twin doesn't
explicitly encode that hand-off.

**Why it was reverted:** the synthetic step turned out to depend on
cross-process state that the Process FB doesn't surface. The clamp auto-retract
fired on stale state and physically collided with the bearing PnP swivel. The
cleanest fix is to encode the handoff in the digital twin (Control.xml), not in
Mapper logic.

**What works instead:** Disassembly is **parked** in sim
(`ShouldParkOnIntraPlcProcessHandoff` emits a single `END` row) so it doesn't
attempt the hand-off until the user models it in Control.xml.

**Source:** memory `feedback_assembly-m580-pitfalls.md`.

---

## R-3. Bearing-only `RecipeTestActuatorAllowlist`

**Tried (2026-05-29, early in the session):** restrict the Assembly recipe to
ONLY drive `bearing_pnp` and `bearing_gripper` (everything else parked) so the
user could force values on the rig without other actuators moving.

**Why it seemed right:** the rig was unsafe and the user wanted to test only
the bearing path without the shaft side colliding.

**Why it was reverted:** the rig went unsafe entirely (clamp damage + collision
risk) so the test isolation no longer made sense — the user moved to sim-only.
And the allowlist applied to BOTH hardware and sim paths, blocking the
full Assembly cycle in sim.

**What works instead:** `RecipeTestActuatorAllowlist = new string[0]` so the
full Assembly cycle regenerates. Documented in `MapperConfig.cs` comment:
"CLEAR the allowlist (= new string[0]) to restore the full Assembly cycle once
Jyotsna's Seven_State CAT lands." (Note: the surgical Seven_State CAT landed
in this repo before Jyotsna's — fix landed anyway.)

---

## R-4. Direct `proc.state_update → actuator.pst_event` event wires

**Tried (multiple times since the start of the M580 work):** wire the Process
FB's `state_update` event output to each actuator's `pst_event` input as the
command path, then drive `actuator.process_state_name`, `state_val`, and
`current_state_to_process` over matching data connections.

**Why it seemed right:** symmetry with how a "normal" IEC 61499 application
fans commands out from a coordinator FB. The reference rig project even has
similar wires.

**Why it was reverted:** the deployed `Process1_Generic.fbt` has
**`EventOutputs = [INITO]` and `OutputVars = empty`**. The `state_update`,
`actuator_name`, `state_val`, and `<actuator_name_lowercased>` source pins
don't exist. All four wires emit dotted/phantom in EAE and carry zero events.
For Five_State actuators it's harmless dead noise because the actuator has no
`pst_event` input either — the ring carries the real command. For Seven_State
actuators it's catastrophic — `pst_event` exists, so the absent source
silently means the actuator never receives a command and parks at `INIT` /
`START` forever.

**What works instead:** the `stateRprtCmd` ring (see `Docs/INVARIANTS.md` I-14).
Process → ring → `updateComponentState` BREQ → `state_cmd` → actuator. The
phantom direct wires were removed; comment block lives at
`SystemLayoutInjector.cs:~1215-1228`.

**Citation for the convergent finding:** the deep investigation 2026-05-29 with
the research-agent co-author. See `CLAUDE.md` "Convergent findings" #2 and #4.

---

## R-5. The un-surgical Seven_State CAT body

**Tried (before 2026-05-27):** ship the original
`Seven_State_Actuator_CAT.cat.zip` from Jyotsna's CAT library — no
`stateRprtCmd` sockets/plugs, no `StateHandling` (updateComponentState) node.
Drive Seven_State via the assumed direct-wire path R-4.

**Why it seemed right:** that's the shape Jyotsna delivered; the
ring-socket-less body matches the SE-published interface.

**Why it was reverted:** combined with R-4 it gave the swivel **no command
path at all** — neither the (phantom) direct wires nor the (missing) ring
attached. Bearing_PnP stuck at `INIT`, `pst_event = 0`. The CAT body was
surgically modified to add the ring socket + `StateHandling` node, then the
.cat.zip was committed (2026-05-27 + 2026-05-30) so a Clean + re-extract
doesn't lose it.

**What works instead:** the surgical CAT body shipped in
`Template Library/CAT/Seven_State_Actuator_CAT.cat.zip` at HEAD. Verified
byte-identical to the deployed `IEC61499/Seven_State_Actuator_CAT/Seven_State_Actuator_CAT.fbt`
on 2026-05-30. **Do not revert the .cat.zip to the original Jyotsna version
without restoring the rig path's command channel.**

---

## R-6. Form 2 (per-instance symbolic) `.hcf` channel bindings

**Tried (early-to-mid May 2026):** bind M580 `.hcf` channels using
`'<ResourceName>.<FBName>.<port>'` — quoted, dot-separated, per-instance
symbolic form. Reads cleanly in EAE's Symbolic Link side panel.

**Why it seemed right:** symbolic names are human-readable in the Symbolic
Link view; debugging is easier than chasing GUID triples.

**Why it was reverted (2026-05-26):** the device-tree IO view
(`System → Devices → M580 → M580_RES → BMEXBP0400 → BMXDDM16025`) left every
channel's Value column **blank**. EAE's Hardware Configurator parses ONLY the
Form-1 GUID triple into that view. Operators couldn't see channel values on
the device tree.

**What works instead:** Form 1 — `{resourceId}.{fbId}.{port}` — populates BOTH
the device-tree IO view AND the symlink panel. M262 uses the same form. See
`Docs/INVARIANTS.md` I-8 for the full citation.

**Source:** `M580SymbolBinder.cs:~188-205` comment block.

---

## R-7. Local `ApplyNoSensorOverride` replica inside the harness

**Tried (early in this loop's iteration 1):** the harness had its own local
replica of `OverrideSimActuatorsNoSensor` to clear `B5 syslay` while the real
implementation was still private to `MainForm_simulator.cs`.

**Why it seemed right:** quick unblock — the harness needed the no-sensor
state-of-the-world, the public API didn't exist yet.

**Why it was deleted (iteration 6):** introduced a two-versions-to-maintain
risk. Iteration 2 extracted the real implementation to
`SimulatorPostProcessor.OverrideSimActuatorsNoSensor` (public static). The
harness now calls that public — no replica.

**What works instead:** call the public static. **Don't reintroduce local
replicas of `SimulatorPostProcessor` methods in the harness; refactor the
public API instead.**

---

## R-8. `Test Runtime` without a prior `Station2SysresMirror.EmitStation2Sysres` call

**Tried (multiple times, recurring until 2026-05-29):** the `btnTestStation1_Click`
hardware-path handler ran `Station2WireEmitter.EmitStation2Resources` without
first re-mirroring the Station-2 FBs onto the M580 sysres.

**Why it seemed right:** the FBs are already there from the prior deploy;
just re-wire them.

**Why it was reverted:** the M580 sysres FB types were never re-synced. When
the user flipped Bearing_PnP between Five_State and Seven_State, the syslay
got the new type but the sysres still carried the old type. EAE Solution
Integrity threw "Missing Instances: Bearing_PnP" on every Reload Solution. The
error recurred several times across sessions because the fix kept being
forgotten.

**What works instead:** `Test Runtime` calls
`Station2SysresMirror.EmitStation2Sysres` **before**
`Station2WireEmitter.EmitStation2Resources`. Landed 2026-05-29 in
`MainForm.cs:~830` region. **Do not remove that call without finding a
different way to re-sync sysres FB types per deploy.**

---

## R-9. Flipping `StubSevenStateActuatorsAsFiveState` to FALSE **without** `SimSwivelForce` in place

**Tried (2026-05-27):** flip the stub flag to FALSE so Bearing_PnP deploys as
the real `Seven_State_Actuator_CAT` in sim. Run the simulator.

**Why it seemed right:** the surgical CAT was in place, the recipe was
correct, "should just work".

**Why it was reverted:** the simulator's no-sensor model only handled Five_State
(see `Docs/INVARIANTS.md` I-10). Seven_State has no sensor synthesis — the ECC
fires its coil on `START → ToPick`, then stalls at `ToPick → AtPick` waiting
for `atwork1 = TRUE`, which the sim never closes. The swivel never reaches
AtPick.

**What works instead:** flip the stub flag **only after**
`SimulatorPostProcessor.InjectSimSwivelForce` is in place and the harness
verifies the wiring. Landed 2026-05-30. See `Docs/INVARIANTS.md` I-10.

---

## R-10. Forcing Bearing_PnP DI/DO channels manually as a workaround

**Tried (2026-05-29, on the unsafe rig):** the user forced `atwork`/`athome`
DI channels and `OutputToWork`/`OutputToHome` DO channels in EAE's Watch panel
to move the swivel to home.

**Why it seemed right:** if the software path is broken, force the I/O.

**Why it was abandoned:** the rig output channels showed a 1970-epoch
timestamp = the running app had never written them, meaning the compiled
image on the dPAC was stale (predated the last regen). Forcing the FB
variables also didn't reach the physical pins because forces in Watch don't
propagate through to the I/O layer reliably. The chain
(stale-image + reversed coil-direction assumption + dangerous swivel
trajectory) was a collision gamble.

**What works instead:**
1. Do a **clean Build + full Download** in EAE (not OnlineChange) after every
   regen so the dPAC runs an image that matches on-disk design.
2. Test in the **simulator**, not on the rig, while the rig is unsafe.
3. To hand-position the swivel, de-energise BOTH coils and back-drive
   mechanically under hand control — see `Docs/INVARIANTS.md` / harness for the
   procedure. Do NOT command via software with an unverified coil-direction
   assumption.

**Source:** convergent findings in `CLAUDE.md` + the safety-led bench
procedure documented in the deep investigation transcript.

---

## R-11. Re-generating `MapperTests/TestData/SMC_Rig_IO_Bindings.xlsx`

**Tried (multiple sessions):** programmatically regenerate the IO bindings
xlsx from a fresh template.

**Why it seemed right:** automation > hand-maintained files.

**Why it was reverted:** the file contains **hand-crafted per-CAT content**
(Channel-to-port mappings, names that match the rig's physical wiring) that no
schema captures. Regenerating it loses information.

**What works instead:** edit it by hand in Excel. The standing rule in
`AGENTS.md` and `CLAUDE.md` is explicit: never regenerate this file.

---

## R-12. Mapping `Bearing_PnP` as `Seven_State_Actuator_CAT` on the rig without first verifying coil direction

**Tried (planned, never executed):** flip the stub flag, deploy to the rig.

**Why it was deferred:** the `M580SymbolBinder` Seven branch maps
`Swivel_Arm_Left_Q → current_state1_to_plc` and
`Swivel_Arm_Right_Q → current_state2_to_plc`, but the physical coil-to-port
assignment is **assumed**, not verified. If the wiring is inverted on the rig,
a `Pick` command drives toward `Place` and collides with the shaft.

**What works instead:** before flipping the rig path, the user must verify
coil direction physically (energise each coil, observe arm direction with shaft
side parked and clear). Sim has no collision risk so the flag is safe to flip
there.

**Citation:** `M580SymbolBinder.cs` static-ctor comment block.
