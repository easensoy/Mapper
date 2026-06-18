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
- **Verification gate:** the `SimulatorEndToEndHarness` was removed and
  `MapperTests` runs no active tests today. Prove a behaviour-preserving change
  with the byte-identical generated-Demonstrator gate (see below) — every
  `.syslay` / `.sysres` / `.hcf` must be byte-identical pre/post.
- **Generation runs only via the MapperUI WinForms buttons.** After any CodeGen
  change, the user must close MapperUI, rebuild MapperUI (which recompiles
  CodeGen), and relaunch before clicking Test Runtime. State that in any
  status update so the user knows.
- **Never commit unless explicitly asked.** Per global instructions, an agent
  must not create commits on its own.

## How to verify a behaviour-preserving change

`MapperTests` runs no active tests today (all quarantined; the old
`SimulatorEndToEndHarness` was deleted). The real gate is the byte-identical
generated-Demonstrator diff — it reads a fixed base (`C:\_gate\base`), generates
into `C:\_gate\work`, and NEVER writes the live `C:\Demonstrator`:

```powershell
cd C:\VueOneMapper
dotnet build _gate\Gate.csproj -c Debug
_gate\bin\Debug\net10.0\gate.exe C:\_gate\snap_pre     # before the change
# ...make the change, rebuild _gate (recompiles CodeGen)...
_gate\bin\Debug\net10.0\gate.exe C:\_gate\snap_post    # after the change
# then SHA256-diff snap_pre vs snap_post
```

A behaviour-preserving change must produce a **byte-identical** snapshot (all
`.syslay` / `.sysres` / `.hcf` identical). The gate also runs the
`HcfReferenceValidator` + `SyslaySysresParityValidator` + `MqttConnectionValidator`
and exits non-zero on a split-brain or parity divergence.

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
