# StateSync — SMC rig → UNS + VueOne STD bridge

A small, standalone, **read-only** bridge that makes the VueOne State-Transition
Diagram and the Visual Components model follow the **real rig**. The map is
generated **automatically from `Control.xml`** — no per-component wiring by hand.
Nothing here is deployed to a PLC.

```
  EAE / PLC runtime ──(existing, unchanged)──► Mosquitto  smc/<component> {state:N}
                                                    │  subscribe smc/#  (read only)
                                                    ▼
                                            ┌──────────────────┐
                                            │  statesync.py    │  loads sync-map.generated.json
                                            │  seq · epoch · ts │  drops duplicates
                                            └───┬───────────┬──┘
                        retained UNS JSON       │           │   VueOne STD socket
     uns/wmg/smc_rig/v1/<station>/<comp>/state              │   127.0.0.1:51000  {...}EOM
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
| `statesync.config.json` | The **only hand-edited** file: broker IP, UNS prefix, `unsQos`, VueOne socket. No components. |
| `gen_sync_map.py` | Generates the **complete** component map from a `Control.xml` (stdlib only). Discovers every publishable component + state table; merges in the config. |
| `statesync.py` | The bridge. Fully generic — knows no component names. `paho-mqtt` the only dependency. |
| `sync-map.generated.json` | **Generated** runtime map the bridge loads (git-ignored — regenerate from Control.xml). |
| `visual-components-topics.txt` | **Generated** VC subscribe list (git-ignored). |
| `README.md` | This file. |

---

## Three-step flow

```bash
# 1. (once / when the site changes) edit runtime settings only:
#    statesync.config.json  ->  broker, unsPrefix, unsQos, vueone

# 2. generate the full map from the ACTIVE Control.xml (the one the Mapper deploys from):
python Tools/statesync/gen_sync_map.py <path/to/Control.xml> --out Tools/statesync/sync-map.generated.json

# 3. run the bridge (defaults to sync-map.generated.json):
pip install paho-mqtt
python Tools/statesync/statesync.py
```

`gen_sync_map.py` reads `statesync.config.json` beside it (or pass a second arg).
Use **`--out <file>`** (writes UTF-8 **without a BOM**, LF newlines) rather than a
shell `>` redirect — PowerShell `>` re-encodes to UTF-16 + BOM. (The bridge tolerates
a BOM anyway via `utf-8-sig`.)

**What the generator discovers per component** (all from Control.xml, nothing
hardcoded): `<Name>` (twinName), `<VcID>` (vcId), `<Type>`, `catKind` (the CAT the
Mapper deploys), the MQTT source topic, the UNS station, and the **runtime** state
table (see the state-vocabulary rule below).

**Derivation rules** (matching the Mapper, implemented once in `gen_sync_map.py`):
- **MQTT topic** — actuators/robots publish under `smc/<Name.lower()>`, sensors
  under `smc/<Name>` (original case). This is the Mapper's own rule
  (`actuator_name = Name.ToLowerInvariant()`; sensor `name` = display name), valid
  because no `Instance_Name_Overrides`/`RigAliases` are active on this rig — which
  is why the wire shows `smc/feeder`, not `smc/pusher`.
- **UNS station** — the VueOne Process whose transition conditions reference the
  component (first Process in document order wins); components no Process claims →
  `unassigned`. Station label = Process `<Name>.lower()`.
- **State table = the CAT runtime vocabulary, NOT raw Control.xml numbers.** The rig
  publishes `current_state_to_process` (the CAT's runtime state). For two CATs this
  differs from the twin's `<State_Number>`s, so the generator uses the CAT vocabulary
  (read from the CAT cores): the Seven-State swivel `bearing_pnp` → `0..6`
  (AtHomeInit…AtHome), the Robot_Task arm `robot` → `0..2`
  (HomeInitial/StartTask/Complete). Five-State actuators and sensors already coincide
  (`0..4` / `0..1`), so their Control.xml (twin) names are kept — which is also what
  lets VueOne resolve them. Each entry carries `catKind` ∈ `{five_state, seven_state,
  robot_task, sensor}`.

---

## UNS topics

Convention: `uns/wmg/smc_rig/v1/<station>/<component>/state` — all **retained**,
valid JSON. From the **active (clamp) `Control.xml`** the generator emits **18**
component topics (`feed_station`, `assembly_station`, `disassembly`, `unassigned`)
plus `uns/wmg/smc_rig/v1/_bridge/status`; the no-clamp `_vc` twin yields 17. Get the
exact live list any time:

```bash
python Tools/statesync/statesync.py sync-map.generated.json --list-topics
```

State payload:

```json
{
  "seq": 42, "epoch": "2026-07-02T21:00:00Z", "ts": "2026-07-02T21:01:05.123Z",
  "sourceTopic": "smc/transfer", "station": "feed_station", "component": "transfer",
  "twinName": "Transfer", "vcId": "TransferComp",
  "state": 3, "stateName": "Returning", "quality": "GOOD"
}
```

`_bridge/status` (retained, with a Last-Will that flips `online:false` on death):
`{"online":true,"epoch":"…","seq":42,"messagesIn":100,"messagesOut":98,"unknownTopics":0}`.

- `seq` — monotonic over every event; a follower can drop `seq <= lastSeq`.
- `epoch` — bridge start time; a **new epoch = restart**, re-read the retained state.
- Retained state topics are the startup snapshot for any late subscriber.

---

## Visual Components (Premium OLP 5.0)

**This is manual — VC connectivity has no importable config file.** Investigated:
a `.vcmx` is a ZIP, but its Connectivity configuration lives in a single
proprietary binary blob (`layout.rsc`); there are no per-connection / variable /
MQTT files inside. So the MQTT connection, topic subscriptions, and variable
pairings are configured in the **Connectivity UI** and saved inside the model.
There is no format to generate or import — do not invent one. The bridge instead
emits `visual-components-topics.txt` as a copy-paste subscribe list.

Steps:
1. Connectivity → MQTT → add `192.168.1.50:1883`, no auth, MQTT 3.1.1.
2. Subscribe to each UNS topic **explicitly** (VC has no `+`/`#` wildcards) — use
   `visual-components-topics.txt` (regenerate with `--list-topics`). Consume the
   `uns/...` topics, **never** the raw `smc/...` (`{state:N}` isn't valid JSON).
3. Read `Payload.state` / `Payload.stateName` in the Variable/Formula editor.
4. Pair `Payload.state` onto an integer property + a **Python 3 script behavior**
   (this VC install embeds CPython 3.13.3) that moves the component per state:
   ```python
   from vcScript import *
   def OnSignal(signal):
       s = int(signal.Value)          # paired to Payload.state
       comp.getBehaviour("MoveTo").Target = POSES.get(s)   # POSES from the component's states
   ```
   Bind a joint via a property expression, not by pairing the joint directly.

> The VC scene component name is the VueOne `<VcID>` (`Pusher`, `TransferComp`,
> `SviwelArmComp`, `UR3e`, …) — the generated map's `vcId` field. Confirm the exact
> in-scene name against your `.vcmx`.

---

## VueOne STD

VueOne needs **no MQTT** — the bridge talks to its socket server directly.

1. Start VueOne STD, open the model, and start the socket server (the VC-connection
   button `btn_VcConn`; it listens on `127.0.0.1:51000`).
2. Start `statesync.py`. It connects, replays a snapshot of all known states, then
   streams updates; if the port is closed it keeps the UNS side running and retries.

**Verified against the VueOne source** — the bridge sends
`{"ClientId":"VC","ComponentName":"<VcID>","StateName":"<name>","Value":"<true|false>","MsgType":1}EOM`:
- `ComponentName` carries the **`<VcID>`** (VueOne matches it against `aComponent.VCID`
  in `FormSystemEditor.OnEventFromVc` — *not* `<Name>`).
- JSON keys are **PascalCase** (`System.Text.Json`, case-sensitive).
- **Sensors** use `Value` and update always; **actuators** use `StateName` and are
  applied **only while VueOne's logic engine / simulator is running**.
- On every (re)connect the bridge replays the latest known state of every seen
  component, so the STD catches up at once. (Reconnect is lazy — it fires on the
  next event; a fully idle rig delays the snapshot to the next event.)

---

## Tests (no infrastructure needed)

```bash
# generate + drive the whole generated map through the normalizer:
python Tools/statesync/gen_sync_map.py <Control.xml> --out Tools/statesync/sync-map.generated.json
python Tools/statesync/statesync.py Tools/statesync/sync-map.generated.json --selftest
```
Expect monotonic `seq`, a dropped duplicate, sensor `Value:"true"/"false"`, one
unknown-topic warning, `stateName:null` for an out-of-range state, and a final
`snapshot replayed N component state(s)` line. The samples are taken from the
loaded map, so the test is fully generic.

**Watch the UNS output** (broker up, another terminal):
```bash
mosquitto_sub -h 192.168.1.50 -t "uns/wmg/smc_rig/v1/#" -v
```

**Replay a recorded session** (no rig; JSONL captures under `MQTT/`):
```bash
while IFS= read -r line; do
  t=$(echo "$line" | jq -r .topic); m=$(echo "$line" | jq -r .raw)
  [ "$t" != "null" ] && mosquitto_pub -h 192.168.1.50 -t "$t" -m "$m"
done < MQTT/smc_20260601_225844.jsonl
```

---

## What is automatic vs manual

- **Automatic:** the entire component map (all publishable components, state tables,
  MQTT topics, UNS stations, VcIDs) from `Control.xml`; the bridge; the VueOne
  follower (socket snapshot + live updates); the VC subscribe-topic list.
- **Manual:** `statesync.config.json` (site settings); running the generator when
  Control.xml changes; the Visual Components MQTT connection + subscriptions +
  variable/Python pairing (no importable VC format exists); starting VueOne's
  socket server and its simulator (for actuator highlights).

## What was NOT touched

No PLC control logic, recipes, interlocks, HCF, EIPScanner, `MQTT_CONNECTION`,
CATs, adapters, `Control.xml`, or generated EAE artefacts. This tool only **reads**
the existing MQTT stream and **writes** to the UNS namespace and the VueOne socket.
One-way by construction — no code path transmits toward the PLCs.

## Known limitations

- The **3 non-hopper sensors** (`BearingSensor`, `ShaftSensor`, `TopCoverSenosr`)
  land under `unassigned` — no VueOne Process references them in a condition, so
  ownership can't be derived without inventing it. Move them by editing the
  generated map, or they can be assigned once a Process references them.
- VueOne **actuator** highlights need the VueOne simulator/logic engine running
  (sensors update regardless) — VueOne's own behavior.
- **Swivel & robot VueOne highlighting is approximate.** `bearing_pnp` (0..6) and
  `robot` (0..2) publish the CAT runtime vocabulary, whose names don't all match the
  twin state-machine names, so those two may not highlight every state in the STD
  (Visual Components still gets the correct number/pose). The other 16 components use
  twin names and resolve exactly.
- The **active twin is the clamp model** (`SMC_Vue2VC_With_Processes`, 18 components).
  Regenerate from whichever Control.xml the Mapper actually deploys; the generator’s
  `source.model`/`source.clamp` fields record which one produced the map.
- Snapshot is **reconnect-driven**, not polled (kept small on purpose).
- The MQTT-topic rule assumes no active `Instance_Name_Overrides`/`RigAliases`
  (empty on this rig). If one is added in the Mapper, add it to the config's topic
  map so the bridge matches the new wire topic.
- No process/recipe **step/cycle** is published by the rig, so there is no
  `cycleId`; component states drive both followers.
