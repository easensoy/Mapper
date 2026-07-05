# Invariants

The load-bearing facts. Each item below is **source-proven**, cited with
`file:line` (or `file:method`), with a one-line "why it matters" and a one-line
"what breaks if you change it". If you propose anything that contradicts one of
these, you are wrong. Cite the invariant, don't re-derive it.

Read once, then keep in your head. These do not change without an explicit
multi-iteration source review.

---

## I-1. `Process1_Generic` has **no data or event outputs other than `INITO`**

**Where:** `Template Library/CAT/Process1_Generic.cat.zip` → `Process1_Generic.fbt`,
mirrored at runtime in `C:\Demonstrator\Demonstrator\IEC61499\Process1_Generic\`.
Interface declares `EventInputs = [INIT]`, `EventOutputs = [INITO]`, no
`OutputVars`, plus only the two adapter plugs `stateRptCmdAdptr_out` and
`stationAdptr_out`.

**Why it matters:** every `Process.<name>.{state_update, actuator_name, state_val}`
source pin reference is a **phantom** — the source does not exist. The Process
commands actuators **only** over the `stateRprtCmd` adapter ring.

**What breaks if you change it:** any direct event/data wire from `Process` to
an actuator silently fails at runtime. Bearing_PnP stuck at `INIT`,
`pst_event = 0`, command never arrives. Confirmed via a multi-iteration
investigation in May 2026 — see `Docs/REVERTED_FIXES.md` #5.

**Citation:** `SystemLayoutInjector.cs` line 1216-1218 comment ("Process1_Generic
declares ONLY INIT/INITO events — no state_update").

---

## I-2. `updateComponentState.BREQ` uses a **case-sensitive STRING `=`** on `dest_name vs name`

**Where:** `updateComponentState.fbt` (the basic FB), BREQ algorithm:

```
IF component_state_in.dest_name = name THEN
    state_cmd := component_state_in.state;
END_IF;
```

**Why it matters:** the ring command only reaches the actuator if the broadcast
`dest_name` is byte-for-byte equal to the actuator's `name` parameter
(lowercased per `SystemLayoutInjector.cs:1649`). Five_State and Seven_State
share this node — so recipe targeting works identically for both.

**What breaks if you change it:** if the recipe emits `CmdTargetName` in any
case but lowercase, the BREQ never fires, the actuator never receives `state_cmd`,
and the recipe stalls. The Mapper guarantees lowercase via
`InstanceNameResolver` and `ProcessRecipeArrayGenerator`.

---

## I-3. `SevenStateActuator2` ECC gates **every commanded transition** on `process_state_name = actuator_name`

**Where:** `SevenStateActuator2.fbt`. All four commanded transitions
(`START→ToPick`, `AtPick→ToPlace`, `*→timerStart`, `START→ToPlace`) carry
`process_state_name = actuator_name AND state_val = Target{Pick,Place,Home}State`.
Only `tohome AND home_done = FALSE` bypasses the name match.

**Why it matters:** the ring command path (via `StateHandling`) delivers
`state_val` + `pst_event` but **does not deliver `process_state_name`**.
`process_state_name` is a separate data input. If it's never driven, it
defaults to the STRING `''`, and `'' = 'bearing_pnp'` is FALSE forever.

**The fix that handles this:**
`SystemLayoutInjector.BuildMinimalActuatorParameters` Seven branch (line
1664-1668 region) parameterises `process_state_name = <lowercased name>` as a
**static Parameter** on the instance, statically satisfying the gate. The CAT
also data-wires `CAT.input process_state_name → FB4.process_state_name`, but the
input has no upstream driver (the only candidate `proc.actuator_name` doesn't
exist per I-1), so the parameter value wins at runtime.

**What breaks if you change it:** drop the parameter and Bearing_PnP runs as
Seven_State but never honours Pick/Place/state_val=Home commands — only the
bare `tohome` event moves it.

---

## I-4. `MapperConfig.StubSevenStateActuatorsAsFiveState` gates Bearing_PnP between Five_State and Seven_State **at multiple sites that must stay consistent**

**Where:** `CodeGen/CodeGen/Configuration/MapperConfig.cs` line 42 (or thereabouts).

Sites that read it:

- `SystemLayoutInjector.cs:~1601` — CAT type routing in the actuator-type
  helper above `IsBranchedSevenState`.
- `SystemLayoutInjector.cs:~2237` — `BuildStation2Wiring.stationChain` Seven
  exclusion.
- `SystemLayoutInjector.cs:~1664` — `BuildMinimalActuatorParameters` Seven
  branch (TargetPick/Place/Home + process_state_name params).
- `ProcessRecipeArrayGenerator.cs:~1196` — `IsFiveStateCommandable` (Seven
  routes to Five_State commands under the stub).
- `ProcessRecipeArrayGenerator.cs:~1216` — `IsSevenStateCommandable` (returns
  FALSE under the stub so no Pick/Place/Home keywords are emitted).
- `M580SymbolBinder.cs` static ctor — `.hcf` channel→port map swaps between
  Five_State pins (`OutputToWork`/`OutputToHome`) and Seven_State pins
  (`current_state1_to_plc`/`current_state2_to_plc`).

**Why it matters:** all 6 sites must agree on the same answer for the same
actuator instance, or the deployed sysres, syslay, recipe, and `.hcf` will
disagree about what type Bearing_PnP actually is. Disagreement → EAE "Missing
Instances" / unresolved adapter / phantom-pin errors.

**What breaks if you change it:** flipping the flag without auditing all 6
sites = silently broken deploy.

---

## I-5. `NoStationAdapterTypes` is shared between sysres-side and syslay-side; they **must stay in sync**

**Where:**

- `ResourceWireEmitter.cs:~107-108` — sysres side: `NoStationAdapterTypes = {
  "Seven_State_Actuator_CAT" }`. Used by `HasStationAdapter` to skip Seven from
  the CaSBus chain.
- `SystemLayoutInjector.cs:~2230-2240` — syslay side: `BuildStation2Wiring`
  `stationChain` loop now mirrors this by skipping Seven (since 2026-05-30).
  Before that, the syslay hardcoded `"Five_State_Actuator_CAT"` for all M580
  actuators and dangled `Bearing_PnP.stationAdptr_in/out` on a Seven instance
  that has no such port.

**Why it matters:** sysres and syslay are two halves of the same deploy. EAE's
Solution Integrity / unresolved-adapter check throws if they disagree.

**What breaks if you change it:** Seven_State on M580 with the syslay including
it in the station chain = `Bearing_PnP.stationAdptr_in/out` is referenced
against a port that doesn't exist → import error.

---

## I-6. Every `MapperUI` Generate **purges the EAE compile cache** (`bin/`, `obj/`, `snapshot.xml`)

**Where:** `CodeGen/CodeGen/Devices/Core/CompileCachePurger.cs`, called near the
top of `MainForm.FinalizeM262StackAsync` (and the simulator pipeline).
`DemonstratorWiper.FoldersToDelete` also lists CAT folders that get deleted on
an explicit Clean.

**Why it matters:** EAE caches compile state structurally. A regen *requires*
a fresh compile or you ship stale wiring. Any deploy/download done *before* the
last Generate is already stale by the time the user tests.

**What breaks if you change it:** the rig outputs read a 1970-epoch timestamp
because the running image was never recompiled against the latest design
files. See `Docs/REVERTED_FIXES.md` for the multi-day investigation that
pinned this.

---

## I-7. `ExtractToEae` is **copy-if-absent**; `DemonstratorWiper.FoldersToDelete` is the **deploy-revert trap**

**Where:** `TemplateLibraryDeployer.cs` — `ExtractToEae` skips files that
already exist in the deployed tree. `DemonstratorWiper.cs:59` — `FoldersToDelete`
explicitly includes `Seven_State_Actuator_CAT`, so an Explicit Clean wipes the
deployed CAT folder.

**Why it matters:** the .cat.zip in `Template Library/CAT/` IS the source of
truth. A plain Generate does NOT overwrite a deployed CAT, but a Clean does
delete it — and the next Generate re-extracts the committed zip. If the zip is
stale (no surgery / wrong content), the surgical version on disk is silently
lost on the next Clean.

**Citation:** `TemplateLibraryDeployer.cs:~2663-2671` (copy-if-absent);
`DemonstratorWiper.cs:59,264` (delete list).

**What breaks if you change it:** a Clean is what someone reaches for when
things look off — and that is exactly what re-introduces the broken CAT
version. **Always keep the committed .cat.zip in sync with the surgical
deployed `.fbt`.**

---

## I-8. The `.hcf` binds channels by **Form-1 GUID triple** `{resId}.{fbId}.{port}`

**Where:** `M580SymbolBinder.cs` line ~206 (`var boundVal = $"{resId}.{fbId}.{map.Port}"`).
`M262/HcfPatchService.cs` (`Sym` helper) emits byte-identical Form-1 triples.

**Why it matters:** Form 2 (per-instance symbolic `'<ResName>.<FBName>.<port>'`,
quoted) populates only EAE's Symbolic Link side panel — the device-tree IO view
shows blank Value columns. Form 1 populates both. Switched back from Form 2 to
Form 1 on 2026-05-26.

**Citation:** `M580SymbolBinder.cs` lines ~188-205 (the long comment explaining
the form choice).

**What breaks if you change it:** the device-tree IO view goes blank, the user
can't see channel values, debugging on the rig becomes impossible.

---

## I-9. The Mapper sets each sysres FB's `Mapping` attribute = `ID with the first nibble XOR'd by 0x8`

**Where:** `SysresFbMirror.cs` (and friends), per `MainForm` deployment audit
2026-05-29. Example FB IDs in the deployed M580 sysres:

```
Bearing_PnP        ID=F633272FE8DC12FB  Mapping=7633272FE8DC12FB   (F→7)
Bearing_Gripper    ID=F0E8EEBF5B201F15  Mapping=70E8EEBF5B201F15   (F→7)
Shaft_Hr           ID=6C87D797727FF5F4  Mapping=EC87D797727FF5F4   (6→E)
Clamp              ID=2CB6DD22361664F5  Mapping=ACB6DD22361664F5   (2→A)
```

**Why it matters:** the `Mapping` attribute references a `<Mapping>` element
elsewhere. It is a **separate GUID** by Mapper convention, not the same as the
FB ID. Symlinks resolve via the FB ID, not the Mapping GUID.

**What breaks if you change it:** if you write code that assumes Mapping == ID,
the sysres becomes inconsistent with what EAE expects, and re-deploys may
emit broken Mapping references.

---

## I-10. Simulator no-sensor / SimSwivelForce synthesis — REMOVED (2026-06-16)

The Test Simulator path and `CodeGen/Services/SimulatorPostProcessor.cs` (which
held `OverrideSimActuatorsNoSensor` and `InjectSimSwivelForce`) were **deleted**.
There is no sim no-sensor or swivel-force synthesis in the code today. This
number is kept as a placeholder so the I-11..I-15 cross-references stay stable.

---

## I-11. `SimulatorEndToEndHarness` gate — REMOVED (2026-06-16)

`MapperTests/SimulatorEndToEndHarness.cs` was **deleted** and `MapperTests` now
runs no active tests (all quarantined under `<Compile Remove>`). The
behaviour-preserving gate is now the **byte-identical generated-Demonstrator
diff** (`_gate/`, golden under `C:\_gate`) — see `AGENTS.md` "How to verify a
behaviour-preserving change". It reads a fixed base and never writes the live
`C:\Demonstrator`.

---

## I-12. `SimulatorFullSystem` 3-PLC collapse — REMOVED

**Status:** the `SimulatorFullSystem` flag and every branch that read it have been
**deleted** — there is no `SimulatorFullSystem` anywhere in the code. Do not
re-introduce a 3-PLC-collapse flag. (`SimulatorRecipeMode` is a separate,
still-LIVE flag — the `StateTransitionTableForm` "Data → State-Transition Table"
menu feature sets it true to build its recipe preview; do not remove it.)

**(Historical) What it did:** the downstream pipeline gated on the flag and
collapsed every `<Component Type="Process">` into a single `Process1_Generic` FB
in one SIM resource (M580/BX1 device emission skipped), so cross-PLC `Wait1Id`
references resolved on the single SIM ring.

---

## I-13. Recipe `state_val` for Seven_State instances is `1=pick`, `2=place`, `0=home/returned`

**Where:** `ProcessRecipeArrayGenerator.MapSevenStateCommandFromConditionName`
(line ~1248-1261). Lock-step with
`SystemLayoutInjector.BuildMinimalActuatorParameters` Seven branch's defaults
(`TargetPickState = 1`, `TargetPlaceState = 2`, `TargetHomeState = 0`).

**Why it matters:** the recipe's `CmdStateArr` and the CAT's
`TargetPick/Place/HomeState` parameters MUST match, or the `state_val` arm of
the ECC gate fails and the swivel never advances even with the name match.

**What breaks if you change it:** change one without the other and Seven_State
silently fails to advance, with no error message.

---

## I-14. The `stateRprtCmd` ring is the **only** command channel from `Process1_Generic` to actuators

**Where:** `SystemLayoutInjector.cs:~2254-2272` (`BuildStation2Wiring` ring loop).
The Five_State CAT and the surgical Seven_State CAT both expose
`stateRprtCmd_in/out` adapter sockets/plugs. The Process commands every
actuator over this ring; updateComponentState on the actuator side matches by
`dest_name` (see I-2).

**Why it matters:** there are no direct `Process → actuator` event wires (I-1).
The ring IS the command path.

**What breaks if you change it:** removing the ring or skipping an actuator
from it = that actuator never receives commands.

---

## I-15. `MapperTests` runs **no active tests**; every test is quarantined under `<Compile Remove>`

**Where:** `MapperTests/MapperTests.csproj` — every legacy test `.cs` file is
under `<Compile Remove>` because they reference `CodeGen.Devices.Shared` (a
namespace renamed to `CodeGen.Devices.Core`), and the one formerly-active test
(`SimulatorEndToEndHarness`) was deleted 2026-06-16. So the project builds but
runs 0 tests — it is NOT a behaviour gate (use the `_gate` byte-identical
Demonstrator diff instead).

**Why it matters:** restoring a legacy test means removing it from the
`<Compile Remove>` list AND fixing its using statements (`Shared` → `Core`).
Don't reintroduce the old `Shared` namespace.

**What breaks if you change it:** a re-enabled test that still references the old
`Shared` namespace fails to compile and breaks the `MapperTests` build.
