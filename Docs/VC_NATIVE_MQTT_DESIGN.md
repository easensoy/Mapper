# VC Native MQTT Sync — Design

Move the SMC-rig → Visual Components state sync off the custom raw-socket MQTT
loop in `vc_shadow.py` and onto **Visual Components 5.0 Premium's native MQTT
Client** (Connectivity). VC becomes a *binding layer*: rig state → VC signal.
No socket, no timers, no mini-controller inside VC.

> Ground truth used here: `Tools/statesync/statesync.py` (UNS topics + payload),
> `Tools/statesync/sync-map.generated.json` (component list), and the VC scene
> dump `Tools/statesync/vc_scene.log` (bindable signals). The VC-connector UI
> specifics (exact formula syntax, CSV path) are flagged **[verify in VC]** where
> the docs are the authority, not this repo.

---

## 0. The one blocker you must accept first (material sync)

The rig publishes **exactly four** `Type=Sensor` material topics (confirmed in
`sync-map.generated.json`; the twin `Control.xml` has only these four sensors):

| Rig topic | VC component | Meaning |
|---|---|---|
| `smc/PartInHopper` | `Part Sensor` | part present in the hopper (feed **start**) |
| `smc/BearingSensor` | `BearingSensor` | bearing present at assembly |
| `smc/ShaftSensor` | `ShaftSensor` | shaft present at assembly |
| `smc/TopCoverSenosr` | `TopCoverSensor` | cover present at assembly |

**`PartAtChecker`, `PartAtSlider`, `PartAtAssembly` are NOT published by the
rig.** They exist only as VC photo-eyes (`PartAtCheckerSensor`,
`PartAtSliderSensor`, `PartAtAssemblySensor` in the scene) and, on the rig side,
only as internal M262 DI bindings the Mapper synthesised — they are not twin
components and never reach MQTT.

**Consequence:** the rig can master the part's location at the **two endpoints**
(in-hopper; and by type at assembly) but **not** the intermediate checker/slider
positions. Continuous part motion between hopper and assembly cannot be rig-driven
today. The fix for that is **more rig sensors or upstream synthesis — never more
VC timing code.** This is the honest limit; §8 records it as the primary residual.

---

## Confirmed VC connector capabilities (VC 5.0 docs, high confidence)

Read from the actual VC 5.0 help HTML (MQTT Client + Connectivity pages), cross-
checked against the 5.0 release notes and 4.9 pages:

- **Pairing is template-based.** Add a topic → define a JSON **Message Template**
  → in **Add Variable Mapping**, pair a sim property/signal (left) to a payload
  field (right). Ingress expressions read the payload via the keyword **`Payload`**
  (dot/index): `Payload.state`, `Payload.jointValues[0]`.
- **JSON→VC type rules:** `Boolean → VC_BOOLEAN` (direct); `Number →
  VC_INTEGER / VC_REAL`; `String → VC_STRING`. **A JSON Number does NOT pair
  directly to a VC_BOOLEAN** — that needs a formula or an upstream boolean.
- **Formula editor exists** (ingress `Payload`, egress `Value`). Documented tokens
  are only `@Array/@Object/@Pair/@Json`, member/index access, and `Convert.ToString()`.
  ⚠️ **The docs show NO comparison/logical operator** (`==`, `||`, `&&`) — so
  `Payload.state == 1 || Payload.state == 2` is *plausible but undocumented* and
  must be proven in the formula editor (it validates on add) before relying on it.
- **Event-Based vs Cyclic is per Variable Group**, as is direction (Server→Sim =
  read/ingress; Sim→Server = publish). Event-based = every message processed
  immediately; Cyclic = rate-limit on an Update Interval.
- **Wildcards `+`/`#` are NOT supported** — explicit topics only (confirmed).
- **`Update Only in Simulation = False` requires Event-Based mode.**
- **Mappings CSV round-trip:** *Mappings Control → Export/Import topic definitions
  and variable pairings in **.CSV***. Whole-connection config also exports as
  JSON/XML via **Configure**. (Message *recordings* — not mappings — log to
  `%LOCALAPPDATA%\Visual Components\<ver>\Plugins\MQTT\Messages`.)
- **Connected Variables panel** (Variable Pair Table): per-pair **Status** (error
  on hover), Prepared value (stage → *Write Prepared Values*), Latest value, plus
  **statistics** — avg/max update time, avg/max plugin time, **Pairs with errors**,
  **Errors on this run**. A missing payload field is a *silent no-update*, not a
  hard error; a type mismatch is caught at pairing time.
- QoS per-topic (default 0). MQTT v3.1.0/v3.1.1; ports 1883/8883. Host without
  `mqtt://` prefix.

## 1. Architecture recommendation

```
   rig (EAE/UAO) ─smc/#─► Mosquitto 192.168.1.50:1883
                              │  statesync.py (on-change, retained, latest-only)
                              │  + one boolean field per component  ◄── small add
                              ▼  uns/wmg/smc_rig/v1/<station>/<component>/state
        VC 5.0 native MQTT Client (Event-Based, Server→Sim group)
          explicit SUB topic  →  Payload.cmd  →  PushJoint_ActionSignal
```

**Recommended (robust): branch upstream, pair directly.** VC's formula operators
(`==`/`||`) are undocumented, but a **direct `Boolean → VC_BOOLEAN` pair is
guaranteed**. So do the branching in `statesync` (a tiny, safe addition) and let
VC do a direct type-compatible pair — no formula, no operators, no VC Python:

- `statesync.emit` adds one boolean per component: five-state pneumatic actuator →
  `"cmd": state in (1,2)` (advance/extend/down); sensor → `"present": state == 1`.
  Existing `state`/`stateName` stay; old consumers ignore the new field. This is
  the "small upstream mapper" the spec allows, kept **inside the existing bridge**
  (no new process, no new topic).
- VC binds `Payload.cmd → <owner>.PushJoint_ActionSignal` (Boolean→VC_BOOLEAN).
  The component's `ServoController_Script` does the timed stroke — no teleport.

**Alternative (zero statesync change, must be tested): VC formula on `state`.**
Pair `Payload.state → PushJoint_ActionSignal` with formula
`Payload.state == 1 || Payload.state == 2` — **only if the formula editor accepts
`==`/`||`** (validate on one pair first). If it errors, use the upstream-boolean
path above.

- **`seq`/stale-drop is already solved upstream** — `statesync` publishes only on
  change, retained, one latest value per topic, so VC gets clean latest-only
  messages. No seq handling in VC.
- **Retire `vc_shadow.py` for feeder/checker/transfer only** in Phase 1; leave it
  running for swivel/pnp/cover/robots until later phases.

## 2. Exact MQTT topics to subscribe (explicit — no wildcards)

VC cannot use `+`/`#`, so subscribe each topic literally. Derived from
`statesync.uns_topic_for` = `uns/wmg/smc_rig/v1/<station>/<component>/state`
(component names are lower-case as stored in the sync-map).

**Phase 1 — commands:**
```
uns/wmg/smc_rig/v1/feed_station/feeder/state       (Pusher)
uns/wmg/smc_rig/v1/feed_station/checker/state      (CheckerComp)
uns/wmg/smc_rig/v1/feed_station/transfer/state     (TransferComp)
```
**Material anchors the rig actually provides (bind read-only, Phase 1b):**
```
uns/wmg/smc_rig/v1/feed_station/partinhopper/state (Part Sensor)
uns/wmg/smc_rig/v1/unassigned/bearingsensor/state
uns/wmg/smc_rig/v1/unassigned/shaftsensor/state
uns/wmg/smc_rig/v1/unassigned/topcoversenosr/state
```
**Bridge health (optional, for a "connected/online" tile):**
```
uns/wmg/smc_rig/v1/_bridge/status
```
Regenerate the full list any time with:
`python statesync.py --list-topics`

**Payload shape on every `.../state` topic** (from `statesync.emit`; all original
fields unchanged — the boolean is **appended**):
```json
{ "seq": 710, "epoch": "…Z", "ts": "…", "sourceTopic": "smc/feeder",
  "station": "feed_station", "component": "feeder", "twinName": "Feeder",
  "vcId": "Pusher", "state": 2, "stateName": "Advanced", "quality": "GOOD",
  "cmd": true }
```
- The **3 feed cylinders** (vcId `Pusher`/`CheckerComp`/`TransferComp`) carry
  **`cmd`** = `state ∈ {1,2}`. The **4 sensors** carry **`present`** = `state == 1`.
  Every other component keeps the original payload with no extra field.
- Bind against `Payload.cmd` / `Payload.present` (Boolean → direct pair). `state`,
  `stateName`, `seq` remain available.

## 3. Exact VC variables/signals to bind

From `vc_scene.log`. Each feed cylinder is a "Physics Pusher" component; the
**node** is inside an **owner component** that carries the signals:

| vcId (node) | owner component | writable input signal | status outputs (optional S→S) |
|---|---|---|---|
| `Pusher` | `PusherNode` | `PushJoint_ActionSignal` (bool) | `PushJoint_OpenState`, `PushJoint_ClosedState` |
| `CheckerComp` | `Checker` | `PushJoint_ActionSignal` (bool) | `PushJoint_OpenState`, `PushJoint_ClosedState` |
| `TransferComp` | `Transfer` | `PushJoint_ActionSignal` (bool) | `PushJoint_OpenState`, `PushJoint_ClosedState` |

**Do not bind `PushJoint` (the raw joint value).** It teleports and threw
out-of-range errors before. Bind `PushJoint_ActionSignal` only.

Material sensor targets (read-only, rig-mastered where a topic exists):

| Rig topic | VC component | writable boolean state signal **[verify in VC]** |
|---|---|---|
| `…/partinhopper/state` | `Part Sensor` | `SensorBooleanSignal` / `BooleanSignal` |
| `…/bearingsensor/state` | `BearingSensor` | its sensor boolean |
| `…/shaftsensor/state` | `ShaftSensor` | its sensor boolean |
| `…/topcoversenosr/state` | `TopCoverSensor` | its sensor boolean |

`PartAtCheckerSensor` / `PartAtSliderSensor` / `PartAtAssemblySensor` have **no
rig source** — leave them driven by VC's own model, or disconnect them.

## 4. Binding table (two options per row)

Semantics confirmed against each component's state table (all three cylinders:
`1=Advancing/Lowering`, `2=Advanced/Down` → extend; `0/3/4` → retract).

| Topic | VC signal | Robust: direct pair | Alt: formula on `state` |
|---|---|---|---|
| `…/feed_station/feeder/state` | `PusherNode.PushJoint_ActionSignal` | `Payload.cmd` | `Payload.state == 1 \|\| Payload.state == 2` |
| `…/feed_station/checker/state` | `Checker.PushJoint_ActionSignal` | `Payload.cmd` | `Payload.state == 1 \|\| Payload.state == 2` |
| `…/feed_station/transfer/state` | `Transfer.PushJoint_ActionSignal` | `Payload.cmd` | `Payload.state == 1 \|\| Payload.state == 2` |
| `…/partinhopper/state` | `Part Sensor` bool | `Payload.present` | `Payload.state == 1` |
| `…/bearingsensor/state` | `BearingSensor` bool | `Payload.present` | `Payload.state == 1` |
| `…/shaftsensor/state` | `ShaftSensor` bool | `Payload.present` | `Payload.state == 1` |
| `…/topcoversenosr/state` | `TopCoverSensor` bool | `Payload.present` | `Payload.state == 1` |

The **direct** column pairs `Boolean → VC_BOOLEAN` (guaranteed) and needs the
upstream `cmd`/`present` boolean (§1). The **formula** column needs zero statesync
change but the `==`/`||` operators must be proven in the formula editor first.

## 5. Steps to configure in the VC UI

1. **Connectivity → Add Server → MQTT Client.** Host `192.168.1.50`, port `1883`
   (no `mqtt://`). Connect; confirm **Connected = True**.
2. In the reading group set **Update Mode = Event-Based** and **Update Only in
   Simulation = True** (only needs False if you drive VC with the sim stopped).
   Keep this **Server→Sim** group separate from any future numeric group — never
   mix fast numeric data into an event-based discrete group.
3. Rename/confirm the **Server→Sim variable group** (e.g. `RigToVC_DiscreteSignals`).
4. For each Phase-1 row: **MQTT Messages → Sub** the explicit topic; set the
   **Message Template** to the UNS JSON (§2); **Add Variable Mapping** and pair
   `<owner>.PushJoint_ActionSignal` (left) to the payload field (right):
   - **direct** → pair to `Payload.cmd` (Boolean→VC_BOOLEAN, no formula); or
   - **formula** → pair to `Payload.state` and enter the boolean formula (§4);
     the editor validates on **Update**. If it errors, use the direct path.
5. Do **one** mapping fully, then **Mappings Control → Export (.CSV)** and reuse
   that CSV as the template for the rest — do not hand-invent the format.
6. Repeat for the 4 sensor topics (read-only → the sensor's boolean).
7. Open **Connected Variables** — every pair's **Status** clean, and **Pairs with
   errors / Errors on this run** stay **0**.
8. Turn off `vc_shadow.py`'s handling of feeder/checker/transfer (see §7).

## 6. Exported mapping/config file

VC's **Mappings Control** exports topic definitions + variable pairings as **CSV**
(a Save dialog — VC does not pin a path). Save it into the repo, committed, so the
mapping is reproducible, diff-able, and survives a VC reinstall/restart:
```
C:\VueOneMapper\Tools\statesync\vc-mqtt-mapping.csv
```
The whole-connection config can also export as JSON/XML via **Configure** in the
ribbon; keep the CSV as the canonical, human-diffable artefact. (The
`…\Plugins\MQTT\Messages` folder is a message *recording* log for debugging, not
the mapping file.)

## 7. Test protocol + acceptance

1. **Disable the custom loop for these 3 only.** In `vc_shadow.py`, comment out
   the Pusher/Checker/Transfer branches (or stop the script) so there is **no**
   `subscribed uns/wmg…` from Python for feeder/checker/transfer.
2. Start `statesync.py`; run the rig (or a replay). VC MQTT server shows
   **Connected**; **Connected Variables** shows healthy pairs, **0 errors**.
3. Run one rig cycle. **Accept when:**
   - Pusher/Checker/Transfer move via `PushJoint_ActionSignal` (strokes), **not**
     raw joint teleport, and follow rig state with **no collapse**.
   - The native connector **performance stats** (avg update time / plugin time /
     errors) are stable and error-free.
   - The mapping **exports and re-imports** and survives a VC restart.
4. If material still drifts between checker and slider, **do not add VC timing
   code** — that is the §0 blocker (missing rig topics), to be raised as a rig
   change, not patched in VC.

## 8. Explicit "do NOT" list

- ❌ No Sparkplug B.
- ❌ No custom raw-socket MQTT client inside VC (`vc_shadow.py` MQTT loop retired
  for feeder/checker/transfer).
- ❌ No deferred/held retract, no synthetic stroke timers, no `sleep`.
- ❌ No per-message scene search.
- ❌ No `moveImmediate` / raw `PushJoint` for feeder/checker/transfer — action
  signals only.
- ❌ No part teleport as the sync mechanism (debug-only visual anchor at most).
- ❌ No wildcard (`+`/`#`) subscriptions — explicit topics only.
- ❌ Do not "solve" missing checker/slider material data with VC logic — it is a
  **missing-rig-topic** problem (§0), reported as such.

## Residual / next

- **One code change to approve:** the recommended path adds a `cmd`/`present`
  boolean in `statesync.emit` (few lines, additive, existing fields unchanged).
  Not yet applied — it is the one edit the design asks for. The zero-code
  alternative is the VC formula, gated on proving `==`/`||` in the formula editor.
- **The only remaining in-app unknown** is whether VC's formula editor accepts
  `==`/`||`. Choosing the direct-boolean path removes even that — nothing about
  the design is then unverified.
- **Primary residual (§0):** rig publishes hopper + assembly-by-type only; no
  checker/slider material topics. Options, in order of preference: (a) add real
  rig sensors + `smc/…` topics; (b) synthesise them upstream in a small mapper
  from the actuator states (e.g. "part left hopper when feeder Advanced");
  (c) accept endpoint-only anchoring and let VC's own conveyor carry between.
- **Later phases:** once feeder/checker/transfer are proven native, extend the
  same pattern to Clamp/Ejector, then the robots/swivel (which use routines, not
  `PushJoint_ActionSignal`, and need a different binding — a signal that triggers
  a VC routine, not a joint).
