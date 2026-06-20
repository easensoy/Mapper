# Design report — removing Cover_Station

Decision report for folding covers **only** into the Assembly/Disassembly flow and removing the
separate `Cover_Station` engine. No code has been changed by this report.

## Current state — two cover modes, one flag

Covers can be driven two ways, selected by `HandoffPlanner.CoversOnM580Ring` (and its inverse
`MapperConfig.DeployBx1CoverEngine`):

| Mode | Flag | How covers run |
|---|---|---|
| **M580-folded (default, shipping)** | `CoversOnM580Ring = true` → `DeployBx1CoverEngine = false` | Cover P&P actuators are spliced onto the M580 Assembly/Disassembly ring (the cross-device cover detour). Assembly emits the `coverPlace` block, Disassembly emits `coverRemove`. **No Cover_Station FB is generated.** |
| **BX1-local (alternate)** | `CoversOnM580Ring = false` → `DeployBx1CoverEngine = true` | A synthesized stateless `Cover_Station` Process engine on BX1 runs the cover pick/place locally (`CoverRecipe`). Assembly/Disassembly skip their cover blocks. |

**The shipping default is M580-folded, which is exactly "covers folded only into Assembly/Disassembly."**
The `Cover_Station` engine exists only in the alternate BX1-local mode.

### Evidence (default snapshot, `C:\_gate\snap_comments2`)
- `Name="Cover_Station"` occurrences: **0**
- Folded cover commands present in Assembly/Disassembly: `coverpnp_vr` ×16, `coverpnp_hr` ×8,
  `coverpnp_gripper` ×8.

## What "removing Cover_Station" changes in generated output

- **Default config (`.syslay` / `.sysres` / `.hcf`): BYTE-IDENTICAL — no change.** Cover_Station is
  already inactive and absent; the covers are already on the M580 ring. Removing the gated-off code
  produces the same bytes.
- **Alternate BX1-local mode: removed.** Setting `CoversOnM580Ring = false` would no longer produce a
  Cover_Station engine. This mode is **not** the shipping config, but it **is** the gate's flag-on
  verification path (the only path that exercises `CoverRecipe`, the Cover_Station synthesis, and the
  BX1 cover ring).

So removing Cover_Station is a **behaviour change only for the non-default flag-on mode**; the default
generated output is unaffected.

## Removal surface (what code goes / simplifies)

12 files reference the cover-engine path; all of it is gated on
`DeployBx1CoverEngine` / `CoversOnM580Ring`, so removal is collapsing the always-false branch:

- `Planning/Recipes/CoverRecipe.cs` — **deleted** (the Cover_Station recipe).
- `Planning/Wiring/HandoffPlanner.cs` — `CoversOnM580Ring` becomes a fixed `true` and is then
  eliminable; `CoverDetour` / `IsCoverDetourActuator` stay (the fold uses them).
- `Input/Settings/MapperConfig.cs` — `DeployBx1CoverEngine`, `Bx1CoverMinimalCycle` removed.
- `Planning/SystemLayoutInjector.cs` — the synthesized Cover_Station Process FB + the
  `IsBx1CoverActuator` interlock-zeroing special-case removed.
- `Planning/Recipes/ProcessRecipeArrayGenerator.cs` — the Cover_Station `RecipeRunOnce` exemption +
  the degenerate-case `CoverRecipe.Apply` call removed.
- `Planning/Recipes/AssemblyRecipe.cs` / `DisassemblyRecipe.cs` — the `CoversOnM580Ring` gate on the
  cover block becomes unconditional (covers always folded).
- `Artefacts/Sysres/ResourceWireEmitter.cs`, `Station2WireEmitter.cs`,
  `Planning/Wiring/RingWiringPlanner.cs` — the BX1-local Cover_Station ring node + its sweep removed;
  the cross-device cover detour stays.
- `Mapping/ComponentRegistry.cs` — the `Cover_Station` registry row removed; the cover actuators keep
  their M580-ring ownership.

Net effect: the cover special-casing collapses to "covers are M580 ring actuators folded into the
Assembly/Disassembly recipes," which is the user's target.

## Trade-offs

1. **Lost capability:** covers can no longer be deployed BX1-local. If the rig ever needs covers driven
   by BX1 independently of the M580 flow, that mode would have to be re-introduced.
2. **Lost verification path:** the gate's **flag-on** proof (the only run that exercises `CoverRecipe`
   + Cover_Station) disappears. After removal, the cover fold is still exercised by the **default**
   gate (Assembly `coverPlace` + Disassembly `coverRemove`), so covers remain gate-covered — just not
   the BX1-local engine.
3. **Interlock zeroing:** the `IsBx1CoverActuator` rule-zeroing on the BX1 covers is part of the
   BX1-local mode. On the M580 ring the covers get their Control.xml interlocks normally — confirm
   that is the intended behaviour (it likely is, since the fold is the real process model).

## Recommendation

The removal is **low-risk for the shipping config** (byte-identical default) and meaningfully reduces
the cover special-casing — it matches the stated goal of representing covers as part of the real
process flow rather than an artificial station. The only real loss is the alternate BX1-local mode,
which is not the shipping path.

**Proposed approach if approved:** do it as one behaviour-preserving slice — collapse `CoversOnM580Ring`
to `true`, delete the now-dead BX1-local branches, and prove the **default** gate stays byte-identical.
Because the flag-on mode is being deliberately removed, that one mode's output legitimately changes
(it ceases to exist); everything the rig actually ships stays identical.
