# StateSync — SMC rig → UNS + VueOne STD bridge (Slice 1: feed station)

A tiny, standalone, **read-only** bridge that makes the VueOne State-Transition
Diagram and the Visual Components model follow the **real rig**. It lives
entirely outside the Mapper control pipeline — it changes nothing that gets
deployed to a PLC.

```
  EAE / PLC runtime ──(existing, unchanged)──► Mosquitto  smc/<component> {state:N}
                                                    │  subscribe smc/#  (read only)
                                                    ▼
                                            ┌──────────────────┐
                                            │  statesync.py    │  reads sync-map.json
                                            │  seq · epoch · ts │  drops duplicates
                                            └───┬───────────┬──┘
                        retained UNS JSON       │           │   VueOne STD socket
   uns/wmg/smc_rig/v1/feed/feed_station/<c>/state           │   127.0.0.1:51000  {...}EOM
                                                ▼           ▼
                                     Visual Components   VueOne STD
                                     (native MQTT)       (highlights states)
```

The physical rig / EAE runtime is the **single source of truth**. VueOne and VC
are **followers only**. The bridge never publishes to `smc/#` and never sends
anything toward the PLCs.

---

## Files

| File | What it is |
|---|---|
| `statesync.py` | The bridge. One file, `paho-mqtt` the only dependency. |
| `sync-map.json` | All component/state mappings + broker/VueOne config. Hand-written for slice 1; regenerate from Control.xml later. Nothing rig-specific is hardcoded in the Python. |
| `README.md` | This file. |

---

## Run it

```bash
pip install paho-mqtt
python Tools/statesync/statesync.py                       # uses ./sync-map.json next to the script
python Tools/statesync/statesync.py path/to/sync-map.json # explicit map
python Tools/statesync/statesync.py --selftest            # offline: prints what each sample WOULD emit (no broker, no socket)
```

`--selftest` needs no broker and no VueOne — it feeds a handful of synthetic
`smc/...` messages through the exact normalization path and prints the UNS
payloads and the VueOne socket lines. Run it first to sanity-check the map.

Broker, UNS prefix, and the VueOne socket are all set in `sync-map.json`:

```json
"broker":  { "host": "192.168.1.50", "port": 1883 },
"unsPrefix": "uns/wmg/smc_rig/v1",
"vueone":  { "enabled": true, "host": "127.0.0.1", "port": 51000, "clientId": "VC" }
```

Set `"vueone": { "enabled": false }` to run the UNS side only (no VueOne).

---

## UNS topics it publishes (slice 1)

All **retained**, valid JSON:

```
uns/wmg/smc_rig/v1/feed/feed_station/feeder/state
uns/wmg/smc_rig/v1/feed/feed_station/checker/state
uns/wmg/smc_rig/v1/feed/feed_station/transfer/state
uns/wmg/smc_rig/v1/feed/feed_station/partinhopper/state
uns/wmg/smc_rig/v1/_bridge/status
```

State payload:

```json
{
  "seq": 42,
  "epoch": "2026-07-02T21:00:00Z",
  "ts": "2026-07-02T21:01:05.123Z",
  "sourceTopic": "smc/transfer",
  "area": "feed",
  "station": "feed_station",
  "component": "transfer",
  "twinName": "Transfer",
  "vcId": "TransferComp",
  "state": 3,
  "stateName": "Returning",
  "quality": "GOOD"
}
```

`_bridge/status` (retained, with an MQTT Last-Will that flips `online:false` if
the bridge dies):

```json
{ "online": true, "epoch": "2026-07-02T21:00:00Z", "seq": 42,
  "messagesIn": 100, "messagesOut": 98, "unknownTopics": 0 }
```

- `seq` — monotonic over every emitted event. A follower can drop `seq <= lastSeq`.
- `epoch` — bridge start time. A **new epoch = the bridge restarted**; re-read the retained state topics to resync.
- Retained state topics = the startup snapshot: a follower that connects late immediately gets each component's last state.

---

## Visual Components (Premium OLP 5.0)

VC 5.0 has a **native MQTT client** (Connectivity → MQTT; Professional/Premium
only). It needs **valid JSON** and **explicit topic subscriptions** — so VC
consumes the `uns/...` topics, **never** the raw `smc/...` (`{state:N}` is not
valid JSON).

1. **Connectivity → MQTT → add a connection.** Host `192.168.1.50`, port `1883`,
   no auth (unless your broker is configured otherwise). MQTT 3.1.1.
2. **Add each topic explicitly** (VC does not support `+`/`#` wildcards):
   `uns/wmg/smc_rig/v1/feed/feed_station/feeder/state`, `.../checker/state`,
   `.../transfer/state`, `.../partinhopper/state`.
3. **Read the JSON fields** with the Variable/Formula editor: `Payload.state`
   (integer) and/or `Payload.stateName` (string).
4. **Drive the model.** There is no native "int → predefined pose" mapping, so
   pair `Payload.state` onto an integer property and add a small **Python 3
   script behavior** (this VC install embeds CPython **3.13.3**) that moves the
   component per state. Sketch:

   ```python
   from vcScript import *
   POSES = {0: "Home", 1: "Advancing", 2: "Advanced", 3: "Returning", 4: "ReturnedFinished"}
   def OnSignal(signal):
       s = int(signal.Value)          # paired to Payload.state
       comp.getBehaviour("MoveTo").Target = POSES.get(s, "Home")
   ```

   Bind a joint via a **property expression**, not by pairing the joint
   directly, to avoid fighting a servo-controller behavior (VC forum t/6891).
5. Run the sim at real-time speed; use event-based (on-change) update.

> The VC scene component name is usually the VueOne `<VcID>` (`Pusher`,
> `CheckerComp`, `TransferComp`, `Part Sensor`). Confirm the exact in-scene name
> against your `.vcmx` and use it when binding.

---

## VueOne STD

VueOne needs **no MQTT** for the MVP — the bridge talks to its existing socket
server directly.

1. Start **VueOne STD** and open the model (`SMC_Vue2VC_With_Processes_vc`).
2. **Start the socket server**: click the VC-connection button (`btn_VcConn`) in
   the System Editor. It listens on `127.0.0.1:51000`.
3. Start `statesync.py`. It connects to `51000` and streams updates. If the port
   is closed, the bridge keeps the UNS side running and retries the socket.

**How VueOne applies an update (verified in the VueOne source):** the bridge
sends `{"ClientId":"VC","ComponentName":"<VcID>","StateName":"<name>","Value":"<true|false>","MsgType":1}` followed by the literal `EOM`.

- `ComponentName` carries the **`<VcID>`** (e.g. `Pusher`), because VueOne matches
  it against `aComponent.VCID` — *not* `<Name>` (`FormSystemEditor.OnEventFromVc`).
- **Sensors** (`PartInHopper`) use `Value` (`true`/`false`) and update whether or
  not the simulator is running.
- **Actuators** (`Feeder`/`Checker`/`Transfer`) use `StateName` (matched
  case-insensitively against the state `<Name>`) and are applied **only while
  VueOne's logic engine / simulator is running** (`mfSimulator.isLogicEnigneRunning`).
  So: run the model's simulator to see actuator highlights follow the rig.

---

## Tests

**Offline logic check (no infrastructure):**
```bash
python Tools/statesync/statesync.py --selftest
```
Expect monotonic `seq`, a dropped duplicate, sensor `Value:"true"/"false"`, one
warning for an unknown topic, and `stateName:null` for an out-of-range state.

**Watch the UNS output** (with a broker up, in another terminal):
```bash
mosquitto_sub -h 192.168.1.50 -t "uns/wmg/smc_rig/v1/#" -v
```

**Live:** run the rig (or replay below), start `statesync.py`, and confirm the
`uns/...` topics update; a fresh `mosquitto_sub` immediately shows the last
retained state of all four components without waiting for an event.

**Replay a recorded session (no rig needed)** — the real JSONL captures under
`MQTT/` carry `{"topic": "...", "raw": "{state:N}"}` per line:
```bash
# needs jq + mosquitto_pub on PATH
while IFS= read -r line; do
  t=$(echo "$line" | jq -r .topic); m=$(echo "$line" | jq -r .raw)
  [ "$t" != "null" ] && mosquitto_pub -h 192.168.1.50 -t "$t" -m "$m"
done < MQTT/smc_20260601_225844.jsonl
```
(Some `MQTT/smc_rig_*.jsonl` files are raw `mosquitto_sub -v` dumps, not JSONL —
use the `smc_YYYYMMDD_*.jsonl` captures for the jq replay above.)

**Full manual test:** `mosquitto_sub uns/...` in one terminal, `statesync.py` in
another, replay or run the rig → UNS topics update → VueOne STD highlights
follow (simulator running for actuators) → the VC model follows the four feed
components. Restart the bridge mid-run: `epoch` changes and the retained
snapshot resyncs the followers.

---

## What was NOT touched

No PLC control logic, recipes, interlocks, HCF, EIPScanner, `MQTT_CONNECTION`,
MQTT formatter/publisher wiring, adapters, CAT internals, `Control.xml`, the
Mapper pipeline, or the `_gate`. This bridge only **reads** the existing MQTT
stream and **writes** to the UNS namespace and the VueOne socket.

## Known limitations (slice 1)

- **4 feed components only** (`feeder`, `checker`, `transfer`, `PartInHopper`).
  Add more by extending `sync-map.json` — no code change.
- **VueOne actuator highlights require the VueOne simulator/logic engine to be
  running** (sensors update regardless). This is VueOne's own behavior; the
  bridge cannot change it.
- **No process/recipe step** is published by the rig, so there is no `cycleId`
  yet. Component states alone drive both followers.
- **Logical, not physical, truth:** the shadow follows the *reported* controller
  state; a physically slow/stuck cylinder still reports its logical sequence.
- The VueOne socket is one client per port, localhost, fire-and-forget (no acks).
- Slice-1 `sync-map.json` is hand-written; the Control.xml generator is a later
  slice.
