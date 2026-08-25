# AGENTS.md

Universal agent brief for the **VueOneMapper** repo. Cursor, Aider, GitHub Copilot
Workspace, Claude Code, Continue, Cody and every other agentic tool reads this
file on session start. Read it before generating anything.

## What this codebase is

VueOneMapper is a C# code generator that turns a **VueOne digital-twin
`Control.xml`** into a complete **EAE 24.1 IEC 61499 project** for the SMC rig
(M262 + M580 + BX1) and the EAE simulator. Change the digital twin, click one
button, the new layout drives the real PLCs. No engineer hand-writes IEC 61499
for each layout change.

The generator is `CodeGen/CodeGen/` (a .NET 10 class library). The UI is
`MapperUI/MapperUI/` (a WinForms front-end whose **Test Runtime** button
generates the EAE project for the physical rig). The old **Test Simulator**
button and its `SimulatorEndToEndHarness` were removed (2026-06-16); the
behaviour-preserving gate is now a byte-identical generated-Demonstrator diff
(see "How to verify a behaviour-preserving change" below).

## READ THESE BEFORE GENERATING ANY CODE

In this order:

1. **`CLAUDE.md`** — current loop focus, scope clamps, Status log updated every
   iteration. Tells you what is being worked on *right now*.
2. **`Docs/ARCHITECTURE.md`** — the system, the CAT library, the generation
   pipeline. Tells you *what the code is doing*.
3. **`Docs/INVARIANTS.md`** — the load-bearing facts. Touching these breaks the
   rig or the simulator. Tells you *what you cannot change without consequence*.
4. **`Docs/REVERTED_FIXES.md`** — things that look right but have been tried and
   reverted, with the reason. Tells you *what not to re-attempt*.

If you propose anything that contradicts these, you are wrong. Read first.

## Standing rules (non-negotiable)

- **Commit each file separately.** No bundling. **No `Claude` attribution** in
  commit messages. **No `Co-Authored-By` lines.**
- **HTTPS push only** — never SSH. Don't touch `git config` or
  `~/.git-credentials`.
- **Push target:** `github.com/easensoy/Mapper`.
- **Never regenerate `MapperTests/TestData/SMC_Rig_IO_Bindings.xlsx`** — it is
  hand-crafted per-CAT content.
- **The rig is currently UNSAFE** (damaged clamp, swivel collision risk). Don't
  touch the **Test Runtime** path (`MainForm.btnTestStation1_Click` and
  everything it calls) or propose changes that only take effect on the rig until
  cleared explicitly. Sim-only work is the default scope.
- **Verification gate:** `MapperTests` (216 tests) and `Gate/` (8 combinations,
  determinism, A->B->A, placement on every target). See below.
- **Generation runs only via the MapperUI WinForms buttons.** After any CodeGen
  change, the user must close MapperUI, rebuild MapperUI (which recompiles
  CodeGen), and relaunch before clicking Test Runtime. State that in any
  status update so the user knows.
- **Never commit unless explicitly asked.** Per global instructions, an agent
  must not create commits on its own.

## How to verify a change

Two gates, both live, answering different questions. Run both.

```bash
dotnet test MapperTests/MapperTests.csproj          # meaning: guards, plans, refusals
dotnet build Gate/Gate.csproj
Gate/bin/Debug/net10.0/gate.exe all                 # behaviour: 8 combinations, determinism, A->B->A, placement
```

`Gate/` is versioned with the compiler and calls the same
`GenerateProject.Execute` that VueOne and MapperUI call, so it validates the
production path rather than a stand-in. `gate all` exits non-zero if ANY
combination fails to generate, if a repeat generation differs, if A->B->A does
not close, or if a process the roster places on a target is not emitted on that
target's resource.

WHAT it gates is data: `Gate/gate.fixtures.json` declares the twins, the
per-component target selections and the baseline project. The twins are checked
in under `Gate/fixtures/models`, so a default run reproduces anywhere; set
`VUEONE_MODELS` to gate authored twins instead. It writes only beneath `C:\_gate`
(override with `VUEONE_GATE_ROOT`) and refuses a root overlapping the live
project, which it derives from the configured output root unless the manifest or
`VUEONE_LIVE_PROJECT` names one. A missing manifest, a missing twin or an
overlapping root each fail with a message and exit 1.

`gate snapshot <label>` and `gate compare <a> <b> [--core]` are there for a
byte-comparison across a change; `--core` leaves out the HMI project, which is
owned separately.

A behaviour-PRESERVING change should compare byte-identical. A change that moves
bytes on purpose has to say so and show why.

## What "ridiculous output" usually means here

If a session starts:

- re-investigating settled questions like *"why are the bearing PnP rig outputs
  reading 1970"* or *"should the Process FB have a state_update event"* or
  *"should we use SSH for git"*;
- proposing changes to the Test Runtime / rig path while the rig is unsafe;
- proposing the recipe-test-isolation allowlist, an extra Area FB in Assembly,
  cross-process-aware clamp auto-retract, or any other item listed in
  `Docs/REVERTED_FIXES.md`;
- proposing direct `Process.state_update → actuator.pst_event` wires (Process
  has no such output — it's the convergent finding in `Docs/INVARIANTS.md`);

STOP and read `Docs/INVARIANTS.md` and `Docs/REVERTED_FIXES.md` first. The
convergent findings already prove the answers. Don't re-derive them; cite them.

## Sister repo: Mapper 2

`Mapper 2` (if visible in your IDE recents) is the sim-only branch of
VueOneMapper post 2026-05-30. Same standing rules apply. The Simulator harness
is the ground truth for both. Cross-port any fix that touches the shared
generation pipeline.
