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

## I-3. RETIRED (2026-08-25). The `SevenStateActuator2` name-gate is not in the shipped path.

The swivel ships as `Seven_State_Actuator_Centre_Home_CAT` over
`SevenStateCentreHomeActuator`, which is commanded on `state_val` alone; no
emitter parameterises `process_state_name` any more, and there is no
`SevenStateActuator2.fbt` in the Template Library. What replaced it: the CAT is
selected from the twin's state graph (`TemplateMap.ResolveActuatorCatType`) and
its command vocabulary is declared in `Config/templates.yml`. The `SevenStateActuator2` row was
deleted from the catalogue on 2026-08-25 (c) - no archive backs it - so the
type is gone from the manifest as well as from the shipped path. The
historical text is kept because anyone reviving it inherits this trap.

### Historical: `SevenStateActuator2` ECC gated every commanded transition on `process_state_name = actuator_name`

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

## I-4. RETIRED (2026-08-25). The stub flag is deleted; the twin decides the CAT.

`StubSevenStateActuatorsAsFiveState` no longer exists. Every site listed below
now reads one answer computed from the TWIN: `TemplateMap.ResolveActuatorCatType`
routes on the actuator's own state count and transition structure, and the
per-CAT protocol (`command`/`settled`/`interlock`/`target`) is declared in
`Config/smc-rig.yml`. There is nothing left to keep consistent by hand — which
is the point. The site list is kept as a map of what a CAT-routing change still
touches.

### Historical: the sites the flag used to gate

**Where:** `CodeGen/CodeGen/Configuration/MapperConfig.cs` line 42 (or thereabouts).

Sites that read it:

- `SystemLayoutInjector.cs:~1601` — CAT type routing in the actuator-type
  helper above `IsBranchedSevenState`.
- `ResourceWiringPlan.cs` — the CaS-chain filter (the syslay-side successor of
  `BuildStation2Wiring.stationChain`, deleted 2026-08-25 (c)).
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
- `ResourceWiringPlan.cs` — syslay side: `ResourceWiringPlanner.For` filters
  the CaS chain with `TemplateMap.LacksStationAdapter`, which reads the same
  `stationAdapter` column. Since 2026-08-25 (c) ONE planner serves every target
  (`BuildFeedStationWiring`/`BuildStation2Wiring`/`BuildBx1Wiring` are deleted),
  so the two sides can no longer disagree about who is on the chain. Before
  2026-05-30 the syslay hardcoded `"Five_State_Actuator_CAT"` for all M580
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

`MapperTests/SimulatorEndToEndHarness.cs` was **deleted**. What replaced it is
NOT "no tests": `MapperTests` is live again and `Gate/` is the behaviour gate.
See I-15 for what each one answers.

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

**Where:** `Config/smc-rig.yml`, the `command:` and `target:` rows on the
swivel's CAT protocol entry — today `command: { work1: 1, work2: 3, home: 5 }`
and `target: { work1: 2, work2: 4, home: 6 }`. The compiler emits the `command`
value and the instance is parameterised from `target`, so the two are read from
one declaration rather than kept in step by hand.

**Why it matters:** the recipe's `CmdStateArr` and the CAT's
`TargetPick/Place/HomeState` parameters MUST match, or the `state_val` arm of
the ECC gate fails and the swivel never advances even with the name match.

**What breaks if you change it:** change one without the other and Seven_State
silently fails to advance, with no error message.

---

## I-14. The `stateRprtCmd` ring is the **only** command channel from `Process1_Generic` to actuators

**Where:** `ResourceWiringPlan.cs` (`ResourceWiringPlanner.For`, the report-ring
block) and `RingWiringPlanner.Render`, which draws it.
The Five_State CAT and the surgical Seven_State CAT both expose
`stateRprtCmd_in/out` adapter sockets/plugs. The Process commands every
actuator over this ring; updateComponentState on the actuator side matches by
`dest_name` (see I-2).

**Why it matters:** there are no direct `Process → actuator` event wires (I-1).
The ring IS the command path.

**What breaks if you change it:** removing the ring or skipping an actuator
from it = that actuator never receives commands.

---

## I-15. `MapperTests` and `Gate/` are BOTH live, and they check different things

**Where:** `MapperTests/MapperTests.csproj` compiles every `.cs` beside it - there
is no `<Compile Remove>` - and the suite runs on `dotnet test`. `Gate/Gate.csproj`
is the behaviour gate, versioned with the compiler it gates.

**Why it matters:** they answer different questions and neither replaces the
other. The tests pin MEANING (a guard keeps its truth, a renamed plant compiles
to the same plan, a model the backend cannot render is refused). The gate pins
BEHAVIOUR end to end (all eight model x controller combinations generate, twice
identically, and a generation carries nothing into the next).

**What breaks if you change it:** a change that keeps the tests green can still
move generated bytes, and a byte-identical change can still be semantically
wrong. Run both.

```bash
dotnet test MapperTests/MapperTests.csproj
dotnet build Gate/Gate.csproj && Gate/bin/Debug/net10.0/gate.exe all
```

---

## I-19. A process state has ONE successor, and every guard leaf is accounted for

**Where:** `Domain/Twin/ProcessGraph.cs` (`Build`), and
`Planning/Recipes/GuardCoverage.cs` proved in `GenerationContext.Plan`.

**Why it matters:** the deployed recipe engine executes a linear row list with
one `NextStep` per row. It expresses a LOOP (an arbitrary back-edge) but has no
branch row: nothing in `RecipeStep` can say "go to X if this guard holds,
otherwise to Y". So a process state with two outgoing transitions cannot be
lowered without discarding one, and `ProcessGraph.Build` REFUSES it by name
before any file is written. The same build refuses a process with no
`Initial_State` or with two, a transition with no destination, and a
destination that is not a state of that process; it REPORTS a state the entry
cannot reach, because such a state never executes.

`GuardCoverage` is the other half. Every `<Condition>` the twin writes must end
as one of: a WAIT row, a requirement already standing in the recipe, a
requirement proved by the command that drove the component there, an outcome a
DECLARATION authorises, an unreachable state, or a self-reference. A leaf that
reaches none of those stops the run — a control semantic that reaches nothing is
a defect, not a warning.

**What breaks if you change it:** serialising the higher-priority branch and
dropping the other ships a plant that silently ignores half its own model; and
any new `return` in the guard lowering that does not record an outcome fails the
coverage proof rather than quietly losing a condition.

### The limitation, stated precisely, and what would lift it

This is a limitation of the deployed RUNTIME, not of the compiler, and it was
re-verified against the shipped archive
(`Template Library/Basic/ProcessRuntime_Generic_v1.*.Basic.zip`):

- `CurrentStep` is assigned in exactly TWO places, `check_wait`
  (`CurrentStep := Recipe[grp].NextStep`) and `AdvanceStep`
  (`CurrentStep := Recipe[CurrentStep].NextStep`). Both read a single value, and
  `grp` is pinned to the group head BEFORE any guard is evaluated.
- `sat` is declared `BOOL`. `check_wait` records only THAT one alternative of a
  WAIT held, never WHICH — the cursor has walked past the whole group by the time
  it jumps. So the winning alternative's identity is discarded by construction.
- No field of `RecipeStep` can carry a second destination: `NextStep` is
  singular, and `AltCount`/`TermCount` are counts.

**The bounded extension that would support it** — recorded so it is a scoped
piece of work rather than a rediscovery: a `RecipeStep` field carrying a
per-alternative destination, an internal var to hold the winning alternative's
head, and three lines in `check_wait` (capture the head before the term loop;
`IF ok AND NOT sat THEN sat := TRUE; win := head; END_IF;`; jump to
`Recipe[win].<that field>`). **No ECC state or transition changes** — the branch
resolves inside the algorithm, before `WaitSatisfied` is tested.

**Why it is not implemented here.** It needs three new EAE artefacts (a datatype,
an engine and the composite that wires it) and EAE Buildtime is not installed on
this machine, so none of them could be compiled — and a `.fbt` EAE rejects fails
the import of the WHOLE project, including models that do not branch. It must be
done with EAE in hand, as a versioned type selected only when a process actually
branches, so linear models keep the engine they run today.

**One question must be answered first**, and only EAE can answer it:
`Iec61499Literal.FormatRecipeTable` emits a hard-coded 8-member struct literal
rather than reflecting the `.dt`, so adding a field does not by itself move any
byte. Whether EAE accepts a struct literal that OMITS a declared member decides
whether linear recipes stay byte-identical or gain one member per row. No
artefact in this repository exercises that case.

---

## I-20. A cross-process reference the recipe cannot simply wait for is answered by a DECLARATION

**Where:** `Config/smc-rig.yml` `handoff:`, read as `PlantFacts.Handoff` and
applied in `ProcessCompiler.EmitHandoff`.

**Why it matters:** two cases have no single right answer, and each reading
drives the plant differently. A reference to a producer's ENTRY phase can mean
"that station is boot-ready" or "that station reached a runtime phase"
(`peerEntryPhase: readinessAssertion | runtimePhase`). And a MATERIAL carrier
reports that material arrived, while a PHASE reports that a producer got
somewhere — different propositions, so a carrier may stand in for a phase only
where `handoff.carriers` says the two coincide on this plant, and says why.
Undeclared is REFUSED in both cases.

**What breaks if you change it:** the shipped profile declares
`readinessAssertion` and authorises NO carrier, so any cross-controller phase
with no transport stops the run. Removing the refusal restores the old
behaviour, where a material level silently stood in for a process phase and the
generated project looked correct.

---

## I-16. Bearing_PnP startup must return the arm to its centre reference

**Where:** `SwivelCatPatcher.PatchSwivelStartupToHome`, called by
`TemplateLibraryDeployer`. It is the SOLE writer of the swivel core's `INIT`
arcs. Full history: `Docs/BEARING_PNP_NEUTRAL_STARTUP.md`.

**Why it matters:** the committed template starts a work MOVE from a startup
sensor reading (`INIT -> AtWork1` holds the Work1 coil, `INIT -> ToWork2`
energises the Work2 coil). Classifying the position with both coils FALSE
instead was tried on the rig on 2026-07-25 and is ALSO wrong: an unheld
three-position pneumatic is not homed either, so the arm drifts and the first
recipe command finds it in an unknown place. The rig-proven Ground Truth routes
a startup work reading to `ToHome`, which drives the arm to its centre
reference and holds it there. The generated
`SevenStateCentreHomeActuator.fbt` is byte-identical to that reference.

**What breaks if you change it:** leaving both work coils FALSE at startup, or
letting a second patch rewrite the same `INIT` arcs, reintroduces first-cycle
movement that only appears on cycle one, so later cycles look correct.

---

## I-17. A guard's boolean shape is load-bearing: ConditionGroups are OR, conditions inside one are AND

**Where:** `SystemXmlReader.ReadGuard` builds `ConditionExpr`;
`ConditionExpr.SumOfProducts` canonicalises it; `InterlockPlanner.Resolve` and
`ProcessCompiler.EmitGuard` are the two consumers.

**Why it matters:** VueOne uses both shapes deliberately, and the four shipped
twins prove it. `Bearing_PnP/TurningPlace` is three separate ConditionGroups —
block on Shaft_Hr OR CoverPNP_Hr OR Transfer. `Feeder/Advancing` is ONE group of
two — block only while Checker is down AND Transfer is advanced. Flattening the
tree turns every AND into an OR, which blocks a machine the twin means to let
run; ANDing the alternatives instead makes a step wait for something the twin
offered as a choice.

**How it is carried:** a recipe WAIT is a group of rows (`AltCount`/`TermCount`
on `RecipeStep`); an interlock is a flattened sum of products (`TermCount` on
`InterlockRule`, `>= 1` heading an alternative and `0` continuing it). Both
runtimes release/block on the first WHOLE alternative.

**What breaks if you change it:** `SemanticFidelityTests` fails, and on the rig
the Feeder, the Checker, Shaft_Hr and CoverPNP_Hr each block on one term of a
guard that names several.

---

## I-18. A safety rule the CAT cannot act on is REFUSED, never emitted

**Where:** `InterlockEmitter.AssertEveryRuleIsEnforceable`, against
`enforcedTargets` on the CAT's protocol row in `Config/smc-rig.yml`.

**Why it matters:** `CommonInterlockEvaluator` computes one verdict per target
stop, and a core acts only on the verdicts it takes as inputs. The five-state
core takes `toHomeInterlock`, so a home-direction rule is real — but only
because its CAT drives `REQ_HOME`; before 2026-08-21 nothing did, and the
verdict was computed and discarded. The centre-home core takes only
`homeWork1Interlock`/`homeWork2Interlock` (one per side, gating both legs) and
has no to-home input at all, so a rule aimed at its home can be evaluated by
nothing.

**What breaks if you change it:** dropping the assertion ships a rule that never
fires, and the generation reports a guarded machine that is not guarded.
Removing `StateHandling.BCNF -> InterlockManager.REQ_HOME` from
`Five_State_Actuator_CAT` does the same silently, because the input it feeds
simply keeps its initial FALSE.

---
