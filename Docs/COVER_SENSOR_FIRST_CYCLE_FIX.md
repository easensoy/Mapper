# Cover Sensor First-Cycle Fix — Addressed Sensor Refresh

**Status: RIG-CONFIRMED, 2026-07-26.** A cover already sitting in place at power-on is now picked up on cycle 1, with no take-out-and-put-back, regardless of the order the three controllers come up.

**Scope of the change:** `Sensor_Bool` (2 ports + 1 ECC state), `Sensor_Bool_CAT` (2 connections added, 1 removed), `ProcessCompiler` (1 emitted row per sensor gate), `SyslaySysresParityValidator` (1 accepted parameter name). No new FB type, no timer, no poll, no change to MQTT, HCF, recipes-beyond-the-refresh-row, or any actuator.

---

## 1. Symptom

The Assembly recipe would not proceed into the cover pick-and-place block on the first cycle after deployment. Physically removing the cover and replacing it released the sequence, and every subsequent cycle then ran correctly without intervention.

The behaviour appeared on all three VueOne models (`_se`, `_sw5`, `_vc`).

---

## 2. Root cause

The publish chain from the physical cover bit to the M580 recipe is **edge-only at every stage**, and nothing in it could re-announce a level that had not changed.

| Stage | Gate |
|---|---|
| `EIPInputs_Bool.bit5` → `BX1_IO.CoverPnpSensor` | decoded on `REQ` only |
| broker change detector (`changeEventM262_2`) | fires only when bit5 **flips** |
| `BX1IO_Sense_TopCover` → `TopCoverSensor.Input` symlink | published only on that change event |
| `Sensor_Bool` ECC | `Sensor_TRUE` reachable **only** from `START` or `Sensor_FALSE` — **no same-level self-transition** |
| `FB1.CNF → StateHandling.REQ` | the only publish trigger in the CAT |

A cover that was already present before the PLC started therefore produced no edge at all.

### 2.1 What actually happens at boot

The BX1 INIT chain is:

```
FB1 → TopCoverSensor → CoverPNP_Hr → CoverPNP_Vr → CoverPnp_Gripper → BX1_IO
```

The sensor is **first** and the broker is **last**. So:

1. `TopCoverSensor.INIT` samples a symlink that nothing has written yet, reads **0**, latches its ECC in `Sensor_FALSE`, and publishes 0.
2. `BX1_IO.INIT` then decodes the input word (the INIT-time decode added 2026-07-25(m)), sees bit5 TRUE, publishes it to the symlink, and `SRC.CNF → TopCoverSensor.RD` re-samples. TRUE ≠ `Sensor_FALSE`, so a correction **is** emitted.

That correction is a **one-shot, emitted during BX1 INIT**. If M580's ring input is not live at that instant the frame is lost — and the ECC is now sitting in `Sensor_TRUE`, so every later re-sample is silent. The engine's validity gate then correctly refuses `WAIT [6] == 1` forever, because `state_table[6].name` was never written.

Deploying all three controllers together makes this a coin flip.

### 2.2 Why cycle 2 always worked

Input-word **bit5 feeds two sinks**: `BX1IO_Sense_TopCover.VALUE1` (the cover sensor) and `BX1IO_Sense_CoverPnp_Gripper.VALUE2` (the gripper's at-work input). It is the same physical bit.

During cycle 1 the gripper grips and releases the cover, flipping bit5 twice. Those are genuine transitions, so fresh frames land and the slot stays populated from then on. The machine's own cover handling repaired the startup race after the first pass.

This was never a sensor fault, a wiring fault, or an intermittent signal.

---

## 3. Approaches evaluated and rejected

Recording these so they are not re-attempted.

| Approach | Why rejected |
|---|---|
| Re-sample on **every** inbound ring frame (`stateRprtCmd_in.CNF → FB2.REQ`) | Only rescues a **wrongly** latched level. Once the ECC holds the right level, re-reading emits nothing. Half a fix; superseded and reconciled away. |
| `StateHandling.CNF → StateHandling.REQ` (re-announce the cached value) | **Race.** `updateComponentState.REQ` publishes the cached `state_sts` (fed from `FB1.Status`). Publishing from the same event that starts the sample is a fan-out with unspecified ordering, so it can report the pre-sample value. On an active-low input that briefly reports the wrong level and can release the cover sequence early. |
| Blanket same-level self-transition on `Sensor_Bool` | Would make **every** I/O-scan push publish. `SYMLINKMULTIVARDST.CNF` fires on the publisher's push with no local `REQ`, so every HCF-bound sensor (PartInHopper, BearingSensor, ShaftSensor, PartAtAssembly) would publish on every scan — the 2026-07-25(k) ring storm. |
| Reorder the BX1 INIT chain so the broker initialises first | **Not implementable.** `PLC_RW_BX1` exposes no `INITO`, so it cannot be spliced into the INIT chain, and fanning `FB1.INITO` to both the broker and the sensor guarantees no ordering. Its intent is already met by the broker's INIT-time word decode, which corrects a stale INIT sample regardless of chain order. |
| Bring M580 up before BX1 | Valid as a workshop workaround only — it does not fix the generator, and it depends on operator discipline. Retained as a fallback, not a solution. |

---

## 4. The implemented contract

> **addressed sensor request → fresh physical input sample → exactly one report → recipe evaluates WAIT**

### 4.1 `Sensor_Bool` — the ports that make an unchanged level report

Added: event input `RPT`, event output `SMP`, and one transient ECC state `Arm` whose only action is `SMP`.

```
START | Sensor_TRUE | Sensor_FALSE  --[RPT]-->  Arm  --[1]-->  START
                                     Arm action: SMP
```

Parking the ECC in **START** is the whole mechanism. From `START` both level transitions (`REQ AND Input=1`, `REQ AND Input=0`) are unconditionally available, so the sample that `SMP` triggers always fires one of them and therefore **always emits `CNF`**. That is what makes an unchanged level report — with no flag, no gate, and no algorithm.

`Arm` returns to `START` unconditionally, so the ECC never lingers there. Normal change-gated behaviour resumes as soon as the sample lands.

### 4.2 `Sensor_Bool_CAT` — strictly serial, never fanned out

```
StateHandling.CNF → FB1.RPT → FB1.SMP → FB2.REQ → FB2.CNF → FB1.REQ → FB1.CNF → StateHandling.REQ
      (request)                          (sample)            (evaluate)          (one report)
```

Because the chain is serial rather than a fan-out, the value published is the one just read, never a cached one. This is what closes the race in §3.

Removed: the superseded `stateRprtCmd_in.CNF → FB2.REQ`. The removal is a reconcile, not merely a not-added, so a tree already deployed with it self-heals on the next deploy.

`FB2.REQ` is now driven by exactly `RD`, `StateHandling.INITO`, and `FB1.SMP`.

### 4.3 `ProcessCompiler` — ask before waiting

An addressed refresh row is emitted immediately before every Control.xml-derived sensor WAIT:

```
CMD TopCoverSensor=0  ·  WAIT [6] == 1
```

The commanded state value carries no meaning — nothing on a sensor consumes `state_cmd`. The frame is purely a request to report.

Generated gates, all three models:

| Process | Refreshed gates |
|---|---|
| Feed_Station | `PartInHopper` |
| Assembly_Station | `BearingSensor`, `ShaftSensor`, `TopCoverSensor` |
| Disassembly | none (no Control.xml sensor conditions) |

---

## 5. Why it cannot storm

Proven from the ECCs, not asserted.

- `StateHandling.CNF` is emitted **only** by `updateComponentState`'s `BREQ` state, which is entered only when `component_state_in.dest_name = name`.
- The `REQ` algorithm sets `component_state_out.dest_name := ''`, so **no report can ever trigger any sensor**. Only a frame that explicitly names a component reaches its `CNF`.
- The `BREQ_PASS` state — frames addressed to someone else — emits `BCNF` only, never `CNF`.

Net: one request in, one report out, and **zero traffic while the line is idle**. There is no path by which a sensor's own report can re-trigger itself or any other sensor.

The free-running I/O-scan push (`FB2.CNF → FB1.REQ`) keeps its change gate completely untouched, which is precisely why the blanket self-transition in §3 was rejected.

---

## 6. Case-sensitivity trap (second occurrence)

The refresh must address the sensor by its **verbatim component name**, not `TemplateMap.RingKey`.

`updateComponentState.BREQ` compares `component_state_in.dest_name = name` — case-sensitive ST string equality. Actuators are claimable in lower case because `SystemLayoutInjector` lower-cases `actuator_name`; a `Sensor_Bool_CAT` is parameterised with `name` **verbatim**. A lower-cased target circles the ring unclaimed and the sensor never answers.

`SyslaySysresParityValidator` caught this on the first build:

```
[Parity][DIVERGENCE] 'Assembly_Station' commands 'topcoversensor', which no component answers to
```

The guard added on 2026-07-25(c) paid for itself. The validator's claimable set now also accepts the `name` parameter — which is what `BREQ` actually compares — making the check strictly more accurate than before.

---

## 7. Verification

All three models generated with the real hidden runner into isolated output roots. `C:\Demonstrator` untouched throughout (mtime checked before and after).

| Check | Result |
|---|---|
| `[Hcf][Validate]` | PASS (all three) |
| `[Parity]` | PASS (all three) |
| `[BX1][Scanner]` | OK (all three) |
| Generation errors | 0 |
| Determinism | two consecutive runs byte-identical — 429 files, 0 differing |
| `RPT` / `SMP` / `Arm` + all 4 arcs | present, XML well-formed |
| `FB2.REQ` drivers | exactly `RD`, `StateHandling.INITO`, `FB1.SMP` |
| Publish trigger | still solely `FB1.CNF → StateHandling.REQ` |
| Superseded per-frame wire | absent (reconciled away) |
| Cover gate | `CMD TopCoverSensor=0 · WAIT[6]==1` (`_se` r36, `_sw5` r35); `WAIT[16]==1` (`_vc` r34) |
| Sensor gates unrefreshed | 0 |
| Recipe growth | exactly the refresh rows — `_se` 16/54/63 → 17/57/63, `_sw5` 16/53/61 → 17/56/61, `_vc` 20/52/64 → 21/55/64 |

Prior fixes confirmed intact in every model: BX1 INIT-time word decode present, cyclic TopCover tick absent, four `FiveStateActuator` INIT arcs, ring command latch cleared at INIT.

**Rig result:** confirmed working by the operator on 2026-07-26.

---

## 8. Known costs and residuals

1. **One extra MQTT publish per sensor gate per cycle.** The refresh also fires `FB1.CNF → MqttFmt.REQ`, so each refreshed gate emits one additional `smc/<component>` message. Bounded and proportional to cycles, not to time.

2. **The material bridge is deliberately not refreshed.** The Mapper-injected `WAIT PartAtAssembly==0 · WAIT PartAtAssembly==1` pair wants a genuine 0→1 edge. Refreshing it would only establish the 0, which is not the point of that gate.

3. **Slot sharing remains, and remains benign.** `WAIT[6]==2` following `CMD transfer` on the M262 Feed ring is Transfer, not the cover sensor; the two sit on separate rings. Likewise `WAIT[16]` after `CMD coverpnp_gripper` on `_vc`. These are actuator settle-waits and correctly carry no refresh.

4. **The cover-present bit is shared with the gripper.** Slot 6 is fed by input-word bit5, which also serves `CoverPnp_Gripper.atwork`. The refresh guarantees the slot always carries a freshly read value; it does not separate the two meanings. An independent cover-magazine sensor on a spare coupler input would be required for that, and is a hardware change, not a generator one.

---

## 9. Files changed

| File | Change |
|---|---|
| `CodeGen/CodeGen/Artefacts/Templates/SwivelCatPatcher.cs` | `EnsureSensorBoolRefreshPath` added (`Sensor_Bool` RPT/SMP/Arm); `EnsureSensorBoolReadEvent` wires the serial refresh chain and reconciles away the superseded per-frame re-sample |
| `CodeGen/CodeGen/Artefacts/Templates/TemplateLibraryDeployer.cs` | calls `EnsureSensorBoolRefreshPath` beside the existing CAT patch |
| `CodeGen/CodeGen/Planning/Recipes/ProcessCompiler.cs` | emits the addressed refresh before every Control.xml-derived sensor WAIT, using the verbatim component name |
| `CodeGen/CodeGen/Validation/Output/SyslaySysresParityValidator.cs` | claimable set accepts `name`, matching what `BREQ` compares |

Both FB edits are deploy-time patches applied to force-re-extracted pristine templates, so they are idempotent and cannot drift.
