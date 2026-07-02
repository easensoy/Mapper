# StateSync / UNS Bridge — Investigation & Design Report

**Date:** 2026-07-02 · **Status:** DESIGN ONLY — no code implemented, per task scope.
**Goal:** synchronise the physical SMC rig (EAE controllers = source of truth) with two *state followers* — VueOne STD and Visual Components 5.0 — from the same ordered MQTT event stream. One-way digital shadow; **no control feedback into the PLCs**.

**Frozen surfaces (untouched by this design):** PLC control logic, recipes, interlocks, HCF, CATs, MQTT_CONNECTION, MqttStateFormatter, EAE runtime, `C:\Demonstrator`. The bridge is a *consumer* of what already exists.

---

## 1. Recommended architecture (executive summary)

```
                         (existing, frozen)
 M262 ─┐                                        ┌──────────────────────────────┐
 M580 ─┼─ embedded MqttFmt/MqttPub ──► Mosquitto│ smc/<component>  {state:N}   │
 BX1  ─┘   (QoS1, retain=false)      192.168.1.50:1883                         │
                                                └──────────────┬───────────────┘
                                                               │ subscribe smc/#
                                                    ┌──────────▼──────────┐
                                                    │  StateSync bridge   │  (NEW, one process,
                                                    │  sync-map.json      │   Python 3 + paho-mqtt,
                                                    │  seq/epoch/cycleId  │   runs on the rig PC)
                                                    └───┬─────────────┬───┘
                            publish (retained, JSON)    │             │  TCP 127.0.0.1:51000
                 uns/v1/smc/<station>/<component>/state │             │  VcComponentArg JSON + "EOM"
                                                        ▼             ▼
                                            ┌───────────────┐   ┌───────────────┐
                                            │ Visual        │   │ VueOne STD    │
                                            │ Components 5.0│   │ (existing     │
                                            │ (MQTT subscr.)│   │  socket srv)  │
                                            └───────────────┘   └───────────────┘
```

- **One new process** (`statesync`), on the same PC as Mosquitto and VueOne STD. Everything else already exists.
- The PLC side publishes exactly what it publishes today (`smc/<component>` `{state:N}`, event-driven, QoS1). The bridge **normalizes** that into a UNS namespace with the metadata the raw stream lacks (seq, epoch, cycleId, timestamps, state names, retained last value) and **pushes** VueOne STD updates over its already-implemented localhost socket protocol.
- **One-way by construction:** the PLCs subscribe to nothing (publish-only architecture — verified); the bridge publishes only under `uns/…`; the VueOne socket is a display-side channel; VC only subscribes. There is no topic or channel through which the shadow can influence the rig.
- **No independent timing in the followers:** VC and VueOne only ever *react* to received state events (VC servo/animation converges to the commanded state; VueOne highlights the received state). Neither runs its own sequence logic.
- No Sparkplug B in v1: single site, single broker, ~20 topics, one consumer — Sparkplug's birth/death certificates and metric aliasing solve fleet-scale problems we don't have. The bridge's epoch + retained snapshot gives us the two Sparkplug features we'd actually use (rebirth + last-known-state) for free. Revisit only if a plant-wide UNS mandates it.

---

## 2. Findings

### 2.1 What the Mapper publishes today (verified in code + on the wire)

| Fact | Value | Source |
|---|---|---|
| Topic pattern | `smc/<component_name>` (flat, no station level) | `TemplateLibraryDeployer.cs:3576-3594`; RootPath = `MqttTopicRoot` default `"smc"` |
| Payload | literal `{state:N}` (NOT strict JSON — unquoted key) | MqttStateFormatter ST: `payload := CONCAT(CONCAT('{state:', INT_TO_STRING(state)), '}')` (`TemplateLibraryDeployer.cs:3980`) |
| Trigger | event-driven, on every state change (`ActuatorCore.pst_out` / sensor `FB1.CNF`) | `PatchCatMqttPublish` call sites `:437-467` |
| QoS / retain | QoS 1, **retain = FALSE**, CleanSession = FALSE, QueueDepth 100 | `MapperConfig.cs:579-594` |
| Connections | one per PLC (Telemetry_M262/M580/BX1), ConnectionID `SMC`, broker `mqtt://192.168.1.50:1883` | `Config/telemetry.yml` |
| CATs with embedded publisher | Five_State_Actuator_CAT, Seven_State_Actuator_Centre_Home_CAT, Robot_Task_CAT, Sensor_Bool_CAT | `TemplateLibraryDeployer.cs:437-467` |

**Observed topics (19, from `MQTT/smc.db` + `smc_*.jsonl` captures):**
`smc/feeder, smc/checker, smc/transfer, smc/ejector, smc/robot, smc/PartInHopper, smc/PartAtChecker, smc/PartAtAssembly, smc/bearing_pnp, smc/bearing_gripper, smc/shaft_hr, smc/shaft_vr, smc/shaft_gripper, smc/clamp, smc/BearingSensor, smc/ShaftSensor, smc/coverpnp_hr, smc/coverpnp_vr, smc/coverpnp_gripper, smc/TopCoverSenosr` (the `TopCoverSenosr` typo is in the topic — carry it verbatim, never "fix" it).

Sample wire reality: `smc/bearing_pnp {state:1}` … `{state:0}` — integers only; 5-state actuators publish 0–4, the centre-home swivel 0–6, sensors 0/1, robot task 0/1/2.

### 2.2 What is NOT published — station step / recipe row

**Confirmed: Process FBs publish nothing.** `PatchCatMqttPublish` is never applied to Process1_Generic / ProcessRuntime_Generic_v1; `CurrentStep`, recipe row, and the process handshake sentinels exist only on the ring/state_table, not on MQTT.

Consequences and options (deferred, YAGNI for v1):
- **(A) Bridge-side inference (zero PLC change):** the bridge knows each process's recipe (rows exportable into sync-map.json from the generated sysres) and can maintain a *virtual* CurrentStep by pattern-matching the observed component-state stream. Approximate but useful for dashboards.
- **(B) PLC-side additive publish:** embed a small CurrentStep publisher in ProcessRuntime — this touches the engine + MQTT surface, so it is explicitly out of scope now; if ever wanted it must go through the byte-identical gate + rig verify as its own task.
- **MVP needs neither** — component states alone drive both followers.

### 2.3 VueOne STD follower path (verified in VueOne source)

VueOne STD **already has a working inbound TCP socket server** — no VueOne changes needed:

| Aspect | Detail |
|---|---|
| Server | `AsyncSocketServer` (`VueOne2VC/syncSocket.cs:556-941`) |
| Ports | `127.0.0.1:51000` (VC C# client), `:52000` (VC Python), `:56000` (RF/sensors) — `ConnectionHelper.cs:26-49` |
| Framing | ASCII JSON + literal `"EOM"` delimiter, no length prefix |
| Message | `VcComponentArg` — key fields: `componentName`, `stateName`, `clientId:"VC"`, `msgType:1` (STATUSUPDATE) — `VcComponentArg.cs:9-46` |
| Effect | deserialized → component looked up by name → `VcRobotStateHelper.ResolveIncomingState` maps `stateName` → the component's VOState → `OnEventFromVc` → the STD UI updates/highlights the state — `ConnectionHelper.cs:139-153`, `FormSystemEditor.cs:96-97` |
| Startup | manual — the socket server starts from the `btn_VcConn` button in FormSystemEditor (`FormSystemEditor.cs:1009`) |
| Constraints | localhost-only (fine — bridge runs on the same PC); effectively **one client per port**; no auth; fire-and-forget (no acks) |

So the bridge's VueOne output is simply: connect to `127.0.0.1:51000`, and per state event send `{"clientId":"VC","componentName":"Transfer","stateName":"Returning","msgType":1,…}EOM`. `stateName` must match the twin's state names exactly (only robot aliases get fuzzy-matched), which is exactly what sync-map.json provides.

### 2.4 Visual Components 5.0 MQTT capability (verified by web research, official docs/academy/forum)

**VC 5.0 has a NATIVE MQTT client** in its Connectivity feature — new in 5.0 (4.x had none). Facts that shape this design:

| Fact | Detail | Consequence for us |
|---|---|---|
| Licensing | MQTT connector in **Professional and Premium** (+OLP/Digital Twin); Essentials/Robotics have NO connectivity in 5.0 | confirm our license tier before slice 1 |
| Protocol | MQTT **v3.1.0/v3.1.1** only; host without `mqtt://` prefix; 1883/8883 tested; QoS configurable (default 0) | plain Mosquitto 1883 works as-is |
| **Payload must be JSON** (or a per-topic Message Template) | the raw PLC payload `{state:N}` (unquoted key) is **not valid JSON** | **VC cannot consume `smc/#` directly — the bridge's normalized JSON is required, not optional** |
| **No wildcard subscriptions** (`+`/`#` unsupported) | every topic added explicitly per connection | ~20 explicit `uns/v1/.../state` topics — trivial, listed by sync-map |
| JSON field access | Variable Formula Editor, dot/index notation: `Payload.state` | our envelope fields map directly (`Payload.state`, `Payload.seq`) |
| Variable pairing | Connectivity variable groups, *Server to simulation*: message field → **component property / behavior property / signal** (`VC_INTEGER` etc.) | pair `Payload.state` onto an Integer property per component |
| Integer → motion | **no native "int → predefined state/animation" mapping**; standard pattern = Integer property/signal + a small **Python 3 script behavior** (VC 5.0 runs CPython **3.12.2**, pip-capable) dispatching the pose/servo move per state value | one reusable dispatch script per component type (five-state cylinder, 7-state swivel, gripper, sensor) |
| Known pitfall | pairing a joint that a servo-controller behavior also owns → "two things fighting" rubber-banding (forum t/6891) | bind the joint to a property expression, pair the property |
| Update modes | event-based (on value change) or cyclic (Update Interval); run the sim at real-time speed when externally driven | event-based for state topics |

Sources: `help.visualcomponents.com/5.0/.../Connectivity/mqtt_client.htm`, 5.0 Release Notes, Academy "MQTT Client Tutorial", forum t/9290 / t/6891 / t/135. Unverified minor points (TLS cert UI, MQTT-specific rate ceilings, paused-sim behavior) are not load-bearing for this design.

Fallbacks (only relevant if stuck on a non-Professional license): VC Python 3 script with an MQTT client chunked into the simulation tick (`client.loop(0.04)`/`delay(0.04)` pattern — proven but lossy), or an MQTT→OPC UA gateway + VC's OPC UA connectivity.

---

## 3. StateSync bridge design

**One Python 3 process** (`Tools/statesync/statesync.py`, paho-mqtt — same stack as the existing `MQTT/mqtt_to_sqlite.py`, so the parsing regex `state\s*:\s*(-?\d+)` and the connection pattern are already proven in this repo). Config = `sync-map.json` + a few CLI flags (broker host, ports). No Mapper/pipeline change.

**Input:** subscribe `smc/#` on the existing Mosquitto.

**Normalization per message:**
1. Parse `{state:N}` with the proven regex; look the topic up in sync-map (unknown topics → counted, logged once, ignored).
2. Drop consecutive duplicates per component (same state twice = no event).
3. Assign `seq` (monotonic uint64, bridge-wide) and stamp `ts` (bridge clock — the PLC payload has no timestamp and must not be changed).
4. Update the in-memory last-state table.

**Output 1 — UNS topics for VC (and any future consumer):**
- `uns/v1/smc/<station>/<component>/state` — **retained**, JSON:
  `{"seq":1234,"epoch":"2026-07-02T16:20:00Z","cycleId":7,"state":3,"stateName":"Returning","prev":2,"ts":"…","src":"smc/transfer"}`
- `uns/v1/smc/_bridge/status` — retained + MQTT Last-Will: `online/offline`, message counters, per-component `lastSeen` ages (also surfaces a silent BX1 — see risks).
- Retained state topics double as the **startup snapshot**: a late-joining VC immediately receives every component's last state.

**Output 2 — VueOne STD:**
- TCP client to `127.0.0.1:51000`; on connect, **replay the full last-state table** (snapshot), then stream per-event `VcComponentArg{componentName, stateName, msgType:1}` + `"EOM"`.
- Reconnect with backoff; if the port is closed (socket server not started in VueOne), keep running the UNS side and retry — degraded, not dead.

**Explicit non-goals (safety):** the bridge never publishes on `smc/#`, never writes OPC UA to the PLCs, never opens EAE artifacts, and has no code path that transmits toward the controllers. The PLCs remain fully autonomous with or without the bridge.

---

## 4. `sync-map.json` — generated from Control.xml

Single source of truth for all mapping; generated read-only from the same twin the Mapper consumes (plus the CAT-type knowledge mirrored from `ComponentRegistry`). Schema:

```json
{
  "version": 1,
  "source": "SMC_Vue2VC_With_Processes_vc/Control.xml",
  "sourceMtime": "2026-07-02T16:10:00Z",
  "topicRoot": "smc",
  "components": [
    {
      "name": "transfer",                          // runtime/topic name (lowercased actuator_name)
      "twinName": "Transfer",                      // VueOne componentName (exact)
      "componentId": "C-c9b8c68c-5030-4d93-91ca-06310726ddc5",
      "station": "feed",                           // feed | assembly | disassembly | covers
      "plc": "M262",
      "catType": "Five_State_Actuator_CAT",
      "mqttTopic": "smc/transfer",
      "unsTopic": "uns/v1/smc/feed/transfer/state",
      "states": {
        "0": { "name": "ReturnedHome",     "vueOneState": "ReturnedHome" },
        "1": { "name": "Advancing",        "vueOneState": "Advancing" },
        "2": { "name": "Advanced",         "vueOneState": "Advanced" },
        "3": { "name": "Returning",        "vueOneState": "Returning" },
        "4": { "name": "ReturnedFinished", "vueOneState": "ReturnedFinished" }
      },
      "vcTarget": { "component": "Transfer", "variable": "state" }
    }
  ]
}
```

Rules the generator must encode (all already known facts, not new logic):
- **Runtime state numbers, not twin State_Numbers**, key the `states` map. For Five_State they coincide (0..4; 4 = transient AtHome the runtime re-reports as 0 — keep both entries). Bearing_PnP (Seven_State centre-home) publishes runtime 0..6 and needs its own table (0=centre/ReturnedHome, 1=ToWork1, 2=AtWork1/AtPick, 3=ToWork2, 4=AtWork2/AtPick2, 5=ToHome, 6=AtHome transient). Sensors: 0/1 (Off/On). Robot task: 0=ready,1=running,2=complete.
- Topic strings are copied **verbatim** (incl. `TopCoverSenosr`).
- `station` comes from which Process's transitions reference the component (Feed/Assembly/Disassembly), covers → `covers`.
- Generation = a small standalone read-only script (`Tools/statesync/gen_sync_map.py`, python, parses Control.xml exactly like the investigation scripts in this design phase did). It runs on demand — **not** wired into the Mapper pipeline (no pipeline change, no gate impact). MVP slice 1 may ship a hand-written map for 4 components; the generator lands in slice 2 when the 7-state table makes hand-writing error-prone.

---

## 5. Sequencing, ordering, snapshots

| Concern | Design |
|---|---|
| Per-component ordering | Guaranteed end-to-end already: each component has exactly ONE publisher (its own CAT) and MQTT preserves per-topic order per publisher; the bridge is a single consumer. |
| Cross-component ordering | Bridge arrival order = the order followers see (sufficient for a shadow; no cross-topic ordering guarantee is claimed). |
| `seq` | Bridge-assigned monotonic uint64 over ALL events. Followers keep `lastSeq` per component and **drop `seq <= lastSeq`**. |
| `epoch` | Bridge start timestamp, in every payload. A follower that sees a new epoch resets its `lastSeq` and re-syncs from retained state (handles bridge restarts cleanly — poor man's Sparkplug rebirth). |
| `cycleId` | v1 heuristic: increments on `smc/feeder` state 0→1 rising edge (start of a feed cycle). Documented as approximate until/unless station-step publishing exists. |
| Retained last state | On every `uns/.../state` topic (bridge sets retain=true). Late joiner ⇒ instant full posture. The raw `smc/#` stream stays retain=false — **do not flip the PLC retain flag** (frozen surface). |
| Startup snapshot | VC: retained topics. VueOne: bridge replays the full table on socket (re)connect. Fresh-boot gap: components that have never published since PLC boot are unknown → published as `"state":null,"stateName":"unknown"` until first event (honest, no invented state). |
| Stale/dup rejection | Consecutive-duplicate drop at the bridge; `seq/epoch` rule at followers; `_bridge/status.lastSeen` ages expose a silently-dead publisher (e.g. BX1 RC101). |

---

## 6. MVP and rollout order (per task item 9)

1. **Slice 1 — Feed:** `feeder`, `checker`, `transfer`, `PartInHopper`. Bridge skeleton + hand-written sync-map + retained UNS topics + VueOne socket PoC + VC scene with the three cylinders bound. *All five-state/sensor — no state-table subtleties.*
2. **Slice 2 — Assembly:** + `bearing_pnp` (7-state map), grippers, `shaft_*`, `BearingSensor`/`ShaftSensor`; ship `gen_sync_map.py`.
3. **Slice 3 — Disassembly/discharge:** + covers (BX1), `ejector`, `robot`; cycleId hardening; `_bridge/status` completeness.
4. **Later (separate decisions):** station-step (inference vs gated PLC publish), Sparkplug/plant-UNS alignment, remote-host followers.

---

## 7. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **VueOne socket unknowns:** manual start (UI button), one client per port, fire-and-forget, undocumented; `stateName` must match twin names exactly. | Slice-1 PoC before anything else on that leg; bridge auto-reconnect; sync-map carries exact twin state names. |
| 2 | **VC licensing/config:** MQTT needs VC 5.0 **Professional or Premium**; no wildcard subs (explicit topic list); integer→pose needs a small dispatch script per component type. | Confirm license tier first; sync-map emits the explicit topic list; one reusable Python 3 dispatch script per CAT type. |
| 3 | Raw payload `{state:N}` is not strict JSON. | Followers never consume `smc/#`; only the bridge parses it (proven regex). |
| 4 | **BX1 goes silent** after Clean/rebuild (RC101 insecure-app until full EAE restart — documented). | `_bridge/status.lastSeen` per component makes it visible instead of a silently-frozen shadow. |
| 5 | **Logical vs physical lag** — the shadow follows *reported* states; a physically slow/stuck cylinder (e.g. the single-solenoid Transfer) still reports its logical sequence. | Accepted for v1 and stated on the dashboard; the shadow mirrors the controller's truth, which is the declared source of truth. |
| 6 | Bridge restart mid-cycle → cycleId drift. | `epoch` + retained snapshot re-sync; cycleId documented as heuristic. |
| 7 | Two writers to the VueOne STD (a user clicking + the bridge). | Shadow is read-only display; recommend a dedicated "follower" STD instance during demos. |
| 8 | Scope creep into control (someone "just" wants VC to push a command back). | Architecture rule stated here: no subscriber exists on the PLC side; any future command path is a separate, explicitly-approved design. |

---

## 8. Acceptance tests (slice 1)

1. **Baseline:** `mosquitto_sub -t 'smc/#' -v` during a rig cycle shows `{state:N}` events for feeder/checker/transfer/PartInHopper (already proven by `MQTT/smc.db`; re-run as smoke).
2. **Normalization:** with the bridge up, `mosquitto_sub -t 'uns/v1/#' -v` shows JSON with strictly increasing `seq`, correct `stateName` per sync-map, and a fresh subscriber immediately receives all four retained last-states without waiting for a rig event.
3. **Replay determinism (no rig needed):** replaying a recorded session (`MQTT/smc_*.jsonl`) through `mosquitto_pub` yields the same UNS event count and order twice; injected duplicate and stale messages are dropped and logged.
4. **VueOne follower:** socket server started in the STD → bridge connects → snapshot paints current posture → during replay/rig-run the STD highlights follow within ~1 s, with zero manual interaction; killing the bridge stops updates without crashing VueOne; restarting re-syncs.
5. **VC follower:** the VC scene's three cylinders + hopper indicator follow the same replay in event order; starting VC *after* the cycle still shows the correct posture (retained snapshot).
6. **Safety/no-regression:** the bridge publishes only `uns/…` (verified by broker log); no Mapper/PLC artifact changed — `_gate` re-run is byte-identical; EAE runtime behavior unchanged with the bridge on/off.

---

## 9. Files / tools involved

**Existing (read/consume only):** Mosquitto @ `192.168.1.50:1883` (`MQTT/mosquitto.conf`); the generated `smc/#` publishers (frozen); `MQTT/mqtt_to_sqlite.py` + `MQTT/smc_*.jsonl` (parsing pattern + replay corpus); VueOne STD `AsyncSocketServer` ports 51000/52000/56000 (`syncSocket.cs`, `ConnectionHelper.cs`, `VcComponentArg.cs` — in the VueOne codebase, untouched); `SMC_Vue2VC_With_Processes_vc/Control.xml` (sync-map source, read-only).

**New (implementation phase, all outside the Mapper pipeline):** `Tools/statesync/statesync.py` (the bridge), `Tools/statesync/sync-map.json` (hand-written slice 1, generated from slice 2), `Tools/statesync/gen_sync_map.py`, `Tools/statesync/README.md`. VC scene + connectivity config live in the VC project.

---

## 10. Exact first implementation slice (definition of done)

> **Slice 1 = "Feed station shadow":** `statesync.py` subscribing `smc/#`, normalizing per §3/§5 (seq, epoch, dup-drop, retained UNS publish, `_bridge/status` + LWT), hand-written `sync-map.json` for `feeder/checker/transfer/PartInHopper`, VueOne socket client (connect 51000, snapshot replay + live `VcComponentArg` updates), and a VC scene binding those four UNS topics. Done when acceptance tests §8.2–8.6 pass against a recorded replay AND one live rig run.

No PLC, Mapper, EAE, recipe, interlock, HCF, CAT, or MQTT-connection change is part of slice 1 — the gate must remain byte-identical, trivially, because nothing generated is touched.
