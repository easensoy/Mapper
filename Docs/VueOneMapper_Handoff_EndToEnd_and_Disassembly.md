# VueOneMapper — End‑to‑End Achievement & Open Struggles (Handoff)

**Date:** 2026‑06‑30  ·  **Author context:** Alper / WMG ASG — OSDA Mapper  ·  **Purpose:** hand this to a fresh chat so it can pick up with full context.

---

## 0. TL;DR (read this first)

- **The win:** This afternoon the **entire SMC rig ran end‑to‑end from one VueOne digital‑twin `Control.xml`** — Feed (M262) → Assembly (M580) → Disassembly (M580) → Covers (BX1) → Ejector → Robot — generated in one click by **VueOneMapper**, deployed to EcoStruxure Automation Expert (EAE 24.1), running on three real PLCs. That is the headline result: change the twin, click one button, the new layout drives the physical rig. No engineer hand‑writes IEC 61499.
- **What broke after:** We then tried to make the recipe **fully data‑driven** (derive Assembly/Disassembly motion straight from the `Control.xml` state machine instead of replaying a hardcoded recipe). **Assembly derived 1:1 with the proven recipe; Disassembly broke** — the generic walk can't command the centre‑home swivel and the cross‑station handoffs the way the hardcoded recipe does, so it stalled and left the swivel parked unsafely. **Reverted to the proven hardcoded recipe** (`DataDrivenRecipes = false`).
- **Where we are stuck right now:** a **BX1 EtherNet/IP scanner** that EAE compiles **EMPTY** (`EIPSCANNER2.xml` = 333 bytes, no `192.168.1.210` coupler). With the scanner empty the **cover I/O is physically dead** — `cover_hr` can be neither commanded nor homed because nothing the logic does reaches the coupler. Root cause **found and fixed in the Mapper** (it now recreates the HwConfiguration project shell and registers the coupler). **The last step is a real EAE Build** (close → reopen → Build) to regenerate the scanner to ~1200 bytes. Until that file changes, the cover will not move regardless of the logic.

---

## 1. What VueOneMapper is

VueOneMapper is a **C# / .NET 10 code generator** that turns a VueOne digital‑twin `Control.xml` into a **complete, deployable EAE 24.1 IEC 61499 project** for the SMC rig and the simulator.

- **`CodeGen`** (`C:\VueOneMapper\CodeGen`) — the engine. Parses `Control.xml` (components, states, sequence conditions, interlocks) and emits the IEC 61499 system: CAT instances, the inter‑component ring, process recipes, interlock tables, HCF I/O bindings, devices/topology, and the BX1 EtherNet/IP cover I/O.
- **`MapperUI`** (`C:\VueOneMapper\MapperUI`) — a WinForms front end with one generation button (**Generate IEC61499 Code**, formerly "Test Runtime") plus validators surfaced in an activity log.
- **Template Library** (`C:\VueOneMapper\Template Library`) — the pre‑validated CAT/Basic/Composite `.fbt` building blocks (Five_State, Seven_State centre‑home swivel, Sensor_Bool, the `ProcessRuntime_Generic` recipe engine, the `PLC_RW_BX1` EtherNet/IP broker, etc.) that get deployed and instance‑wired.

Architecture principle: **template‑based (CAT injection), not freehand code generation.** Component → CAT type, state → `state_val`, sequence condition → event/ring wiring, interlock condition → guard rule. The digital twin is the single source of truth.

---

## 2. The generated system — the SMC three‑PLC rig

| PLC | Role | I/O path |
|---|---|---|
| **M262** | Feed_Station (feeder, checker, transfer, ejector, robot) | direct dPAC I/O via `.hcf` |
| **M580** | Assembly_Station + Disassembly (clamp, bearing swivel, shaft P&P) | direct dPAC I/O via `.hcf` |
| **BX1** (Soft‑dPAC) | Covers (CoverPNP_Hr / Vr / Gripper) + MQTT | **EtherNet/IP** → TM3BC coupler @ `192.168.1.210` |

Key generated mechanisms:

- **The `stateRprtCmd` ring** — every component publishes its `{id, state}` and the process engines drive commands around a token ring. The **BX1 covers are folded into the M580 Assembly/Disassembly flow** via two cross‑device adapter hops (M580↔BX1); EAE bridges these at runtime (rig‑proven).
- **The recipe engine** — `ProcessRuntime_Generic_v1` reads `RecipeStep` arrays (`StepType` 1=CMD / 2=WAIT / 9=END, `CmdTargetName`, `Wait1Id/State`, `NextStep`). Recipe row data lives in `Config/recipes.yml`; the station classes (`AssemblyRecipe`, `DisassemblyRecipe`) select/gate blocks.
- **The Assembly↔Disassembly handshake** — Assembly publishes a sentinel `assembly_handshake_done=7`; Disassembly `WAIT(AssemblyProcessId, 7)`. M580‑local, reliable.
- **Interlocks** — per‑actuator `RuleTable` (Count + `[From, To, SourceID, BlockedState]`) read by `CommonInterlockEvaluator`.
- **Devices/topology** — the Mapper owns the full device lifecycle (logical sysdev + sysres, physical topology); Clean wipes them, Generate recreates them.

The behaviour‑preserving safety net is the **byte‑identical generated‑Demonstrator gate** (`_gate/gate.exe`) which regenerates into a temp dir, runs HCF / PARITY / CONNECTIVITY / EMBEDDED‑MQTT / COVER‑SAFE‑START validators, and never touches the live `C:\Demonstrator`.

---

## 3. The VueOne → Mapper → EAE integration (V‑Dev / VC version / Published_Alper)

The Mapper is now invoked **under the hood from VueOne itself**:

- **VueOne source:** `C:\V-Dev\VueOneVcVersion\VueOneVcVersion\vueone_vc\` — the active VueOne **VC version**.
- **Published builds:** `…\vueone_vc\Published_Alper\vueOneSystem.exe` (VC) and `C:\V-Dev\VueOneFullVersion\…\Published_Alper\vueOneSystem.exe` (Full) — **Alper's published `vueOneSystem.exe`**, the system editor the user actually runs.
- **The button:** `Development/vueOneSystem/FormSystemEditor.cs` → **"Generate IEC61499 Code"** (`btnGenerateIEC61499_Click`). It launches the hidden runner and shows `FormIec61499GenerationResult`.
- **The bridge:** `Development/VueOneMapperHiddenRunner/Program.cs` → **`VueOneMapperHiddenRunner.exe`**, deployed into the Mapper bin (`C:\VueOneMapper\MapperUI\MapperUI\bin\Debug\net10.0-windows\`) and **referencing the Mapper's `CodeGen.dll`** (accepts `--control <path>`).

End‑to‑end flow on one click:

```
VueOne (vueOneSystem.exe, open model)
   └─ "Generate IEC61499 Code"
        └─ VueOneMapperHiddenRunner.exe
             1. WIPE C:\Demonstrator   (DemonstratorWiper.Wipe — clears devices/app/layout/FB types)
             2. Hand the OPEN model's Control.xml to CodeGen
             3. Generate the full 3-PLC IEC 61499 project into C:\Demonstrator
             4. Touch the .dfbproj so EAE prompts "Reload Solution"
   └─ EAE: Reload → Build → Deploy → Login → the rig runs
```

So the operator workflow is: **change the twin in VueOne → one button → (in EAE) Build → Deploy.** Note the live twin used end‑to‑end is `MapperUI\bin\Debug\net10.0-windows\Input\Control.xml`; VueOne hands the *currently open* model's `Control.xml` to the runner.

---

## 4. The achievement — full cycle on the real rig

This afternoon the **complete assembly/disassembly cycle ran end‑to‑end on the physical SMC rig**, all three PLCs deployed together, all driven from the one twin:

1. **Feed (M262):** feeder → checker → transfer deliver the part.
2. **Assembly (M580):** clamp closes → bearing swivel pick/place/home → shaft P&P → **covers placed (BX1)** → handshake.
3. **Disassembly (M580):** covers removed (BX1) → shaft out → bearing out → unclamp.
4. **Discharge (M262):** ejector pushes → robot picks → returns the part.

This proved the hardest, longest‑standing risk: **EAE bridges the M580↔BX1 cross‑device adapter ring at runtime**, so the M580 process can command the BX1 covers in one recipe. That was the cornerstone. With the proven **hardcoded** recipes (`recipes.yml`), the rig cycles correctly.

---

## 5. Current struggles (detailed)

### 5.1 The data‑driven Disassembly regression (the "automatic" attempt that broke it)
**Goal:** make the recipe fully model‑driven — derive Assembly/Disassembly **motion** directly from each process's `Control.xml` state machine (the generic walk the Feed station already uses), instead of replaying hardcoded rows. Flag: `MapperConfig.DataDrivenRecipes`.

**Result:**
- **Assembly derived essentially 1:1** with the rig‑proven recipe (clamp → bearing pick/grip/place/release/home → shaft → covers), gate‑decoded and diffed.
- **Disassembly broke.** Two real gaps: (a) the generic walk **cannot command the Seven‑state centre‑home swivel** (`bearing_pnp`) with the work/home vocabulary the hardcoded recipe uses, and (b) the **cross‑station handoffs** (Feed→Assembly material gate; Assembly→Disassembly handshake; the ejector/robot discharge tail) **are not expressible in the per‑process twin**, so the walk dropped them. The derived Disassembly stalled and **left the swivel parked at a work position** — directly in `CoverPNP_Hr`'s path.
- **Reverted** `DataDrivenRecipes = false`. The proven hardcoded recipe is live again. The data‑driven generator and a `DataDrivenHandoffInjector` (which wraps the derived motion with exactly the missing handoffs) exist and are gate‑verified at the recipe level, but stay **off** until the derived Disassembly is proven end‑to‑end on the rig.

### 5.2 The `bearing_pnp` ↔ `cover_hr` collision
The **centre‑home swivel (M580)** and the **horizontal cover (BX1)** physically share the assembly volume. The twin even encodes the guard (`Bearing_PnP/TurningPlace` blocks on `CoverPNP_Hr/Advanced`), but it's a **cross‑PLC interlock** — a BX1 actuator can't reliably read M580 state and vice‑versa; restoring it both ways deadlocks the cover sequence. Our safe, M580‑local solution (live):
- **Mutual exclusion** between Assembly and Disassembly (Disassembly publishes an idle sentinel; Assembly waits for it) so the two never drive the shared swivel/cover at the same time.
- An explicit **bearing‑clear gate** (`WAIT bearing_pnp=0`) before every `cover_hr` advance.
- The **M580‑side `bearing_pnp` interlock** kept (blocks turn‑to‑place while `cover_hr` advanced); the BX1 cover's own RuleTable stays `Count=0` (no deadlock).

### 5.3 The `cover_hr` deploy/clean safe‑start
`cover_hr` is **double‑acting** and driven through the BX1 coupler, which **holds** its last output (unlike the spring‑return M580 clamp that falls home on de‑energise). So a cover left at Work stays at Work on deploy/clean — a swivel‑collision hazard. **Fix (live):** a new Basic FB **`Bx1CoverFailsafe`** spliced into the `PLC_RW_BX1` broker. On every INIT/cold/warm start it forces `cover_hr` HOME (`ToWork=0`, `ToHome=1`) and Vr/gripper off, **holds until the at‑home sensor confirms**, then passes the live coils. So `cover_hr` can never auto‑energise Work on deploy, and is actively retracted if left at Work. Gate‑verified (`COVER‑SAFE‑START PASS`). *(2026-08-25: `MapperConfig.Bx1CoverSafeStart` is deleted — nothing ever set it false, so the safe-start splice is now unconditional. Behaviour unchanged.)*
> Note: a pure *Clean with no logic running* still can't home a double‑acting cover (no FB to drive `ToHome`); it homes on the next Deploy. The only way to also home it while stopped is a coupler‑side TM3DQ16T fallback setting (`ToHome` channel → "Set to 1") — coupler config, not the Mapper.

### 5.4 The EAE empty‑scanner blocker — **the current open issue**
**Symptom:** `cover_hr` won't move at all (home or work). **Root cause (found, file‑evidenced):** the BX1 `EIPSCANNER2.xml` EAE compiles is **EMPTY (333 bytes, no `192.168.1.210`)**. EAE builds that file from the `HwConfiguration` **device model**, and the model was **not registered** in `HwConfiguration.hwconfigproj`. Chain:
1. The wipe deletes the whole `HwConfiguration/` folder (project file included).
2. The Mapper re‑deploys the coupler model *files*, but `RegisterBx1HwConfigScannerModel` **silently no‑op'd** when the project file was missing (it only *adds* to an existing project).
3. EAE then writes its own project file **without** the coupler → compiles an empty scanner → **the cover I/O has no path to the coupler.** The Mapper's own `SCANNER‑GUARD` even logged `HwConfiguration.hwconfigproj MISSING`, but it was swallowed as a non‑fatal error.

**Fix (committed, gate‑verified):** the Mapper now **recreates the HwConfiguration project shell from the Template Library** (`Template Library/EtherNetIP/HwConfiguration/`: `.hwconfigproj` + `AssemblyInfo.cs` + `ImageStorage.xml`) when the wipe removed it, then registers the coupler. Verified: deleting the project file (exactly what the wipe does) and regenerating brings it back **with the coupler registered (`EIPSolutionsV2` ×4)**. Confirmed on the live tree: the regenerated `HwConfiguration.hwconfigproj` now carries 4 coupler refs.

**What's still needed (the live blocker):** the deployed `EIPSCANNER2.xml` is still the **stale 333‑byte** build output. EAE must **rebuild** it from the now‑correct project. **A runtime Clean/Deploy is not enough — it needs a project Build/Compile.** Reliable sequence: **close EAE completely → reopen → Build → confirm `…\System\RuntimeData\BX1\boot\EIPSCANNER2.xml` is ~1200 bytes and contains `192.168.1.210`** (not 333) → Deploy → Login. If it stays 333 after a true close‑reopen‑Build, EAE is dropping the registration even though it's in the project (a deeper, historically stubborn EAE HwConfiguration cache issue) — that becomes the next investigation.

---

## 6. Current state & immediate next steps

**Generation is clean:** 3 devices (M262/M580/BX1), recipes present (Feed; Assembly + Disassembly hardcoded), the `Bx1CoverFailsafe` safe‑start gate in the broker, the coupler now registered in `HwConfiguration.hwconfigproj`. No orphan/empty sysres. All `_gate` validators pass.

**Do next, in order:**
1. **Regenerate from VueOne** (writes the project with the coupler + the failsafe gate).
2. In EAE: **close completely → reopen → Build (compile)** — not just Deploy.
3. **Verify the scanner file changed:** `…\RuntimeData\BX1\boot\EIPSCANNER2.xml` ≈ 1200 bytes with `192.168.1.210`. *This single file's size is the whole go/no‑go.*
4. **Deploy all three PLCs → Login.** Expect `cover_hr` driven HOME on start by the failsafe; then the proven hardcoded cycle runs end‑to‑end again.
5. **Then, separately:** re‑attempt the data‑driven Disassembly with the `DataDrivenHandoffInjector` proven on the rig before flipping `DataDrivenRecipes` on.

---

## 7. Key files / pointers

- **Mapper engine:** `C:\VueOneMapper\CodeGen\CodeGen\`
  - Recipes: `Planning\Recipes\{AssemblyRecipe,DisassemblyRecipe,DataDrivenHandoffInjector}.cs`, `Config\recipes.yml`
  - Interlocks/safety: `Planning\Interlocks\InterlockEmitter.cs`
  - BX1 broker + safe‑start: `Devices\BX1\Broker\Bx1IoBrokerInjector.cs` (`InjectCoverFailsafeIntoBrokerType`), `Template Library\Basic\Bx1CoverFailsafe\…\Bx1CoverFailsafe.fbt`
  - Scanner model + the fix: `Devices\Common\Station2DeviceEmitter.cs` (`DeployBx1HwConfigScannerModel` / `RegisterBx1HwConfigScannerModel`), `Devices\BX1\Bx1ScannerValidator.cs`
  - Config flags: `Input\Settings\MapperConfig.cs` (`DataDrivenRecipes`, `SerializeAssemblyDisassembly`). *(2026-08-25: `Bx1CoverSafeStart` and `Bx1BridgeInsideComposite` are deleted — both were always true, so the safe-start splice and the in-composite bridge are unconditional.)*
- **VueOne integration:** `C:\V-Dev\VueOneVcVersion\…\vueone_vc\Development\` — `vueOneSystem\FormSystemEditor.cs` (button), `VueOneMapperHiddenRunner\Program.cs` (runner); published `…\Published_Alper\vueOneSystem.exe`.
- **EAE project (generated):** `C:\Demonstrator\Demonstrator\` — `IEC61499\System\…` (sysdev/sysres), `IEC61499\PLC_RW_BX1.fbt` (broker), `HwConfiguration\` (scanner model + `EIPSolutionsV2`).
- **Verification gate:** `C:\VueOneMapper\_gate\gate.exe` (regenerates to a temp dir, runs all validators — never touches live `C:\Demonstrator`).
- **Live loop record:** `C:\VueOneMapper\CLAUDE.md` `## Status` (dated bullets, newest first).

---

*Hand this whole file to the next chat. The one number that matters right now: the byte size of `EIPSCANNER2.xml` after a real EAE Build. Everything else is in place.*
