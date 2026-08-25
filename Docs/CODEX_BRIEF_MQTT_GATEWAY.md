# Driving a Visual Components model from a live rig over MQTT — full brief for Codex

We have a working digital shadow *almost* end to end. One hop is unsolved: how to drive a
Visual Components servo joint **non-blocking and concurrently** from a single Python behaviour.
Everything else is proven. This document gives you the whole system so you can settle that hop.

---

## 1. Goal and the three roles

A physical SMC assembly rig (3 PLCs — M262 feed, M580 assembly/disassembly, BX1 covers, IEC 61499
on Schneider EAE) must drive a **Visual Components 5.0 Premium OLP** model as a live visual shadow.

- **Rig = physical truth / timing.** It emits the state of each mechanism as it changes.
- **VueOne / twin (Control.xml) = semantics.** A rig state is a *label*; the twin holds what that
  label *means* geometrically (target position) and how long the real stroke takes.
- **Visual Components = visual follower.** It renders motion. It decides nothing.

Key information fact that shapes everything: **the rig only ever transmits a discrete state
integer** (`{state:N}`, ~2.6 bits). A pose is ~192 bits. So VC/anything must reconstruct the
target + duration from the twin — the rig cannot carry them.

---

## 2. Software / backend versions

| component | version / detail |
|---|---|
| Visual Components | **Premium OLP 5.0** (IronPython **2.7** for component scripts — `from vcScript import *`; a separate Python 3 `vcCore` API also exists, different surface) |
| VC MQTT client | `Plugin.MQTT.dll` on **MQTTnet 4.3.6**; **MQTT 3.1.1 max**; JSON payloads only; QoS 0/1 (default **0**); retain; LWT; CleanSession/KeepAlive; **no wildcard subscriptions**; Update Mode **Cyclic or Event-Based** |
| Broker | Mosquitto, `127.0.0.1:1883`, `listener 1883 0.0.0.0` |
| Bridge (`statesync.py`) | Python 3 + paho-mqtt 2.x |
| Rig runtime | Schneider EAE (IEC 61499), publishes `smc/<component> {state:N}` (9 bytes, QoS1) |

---

## 3. UNS namespace, topics, and subscribers

`statesync.py` normalises the raw rig stream into a UNS and adds a command channel.

```
RAW (rig -> broker):   smc/<component>                     {state:N}   (9 bytes)

UNS (statesync -> broker, retained):
   uns/wmg/smc_rig/v1/<station>/<component>/state          normalised observation JSON
   uns/wmg/smc_rig/v1/_bridge/status                       bridge online/LWT

COMMAND channel (statesync -> broker, NON-retained, QoS1):
   uns/wmg/smc_rig/v1/vc/command                           enriched command (see §5)
   uns/wmg/smc_rig/v1/vc/event                             VC -> completion/lag telemetry
```

**Subscribers today:**
- **VC native MQTT client** subscribes the per-component `.../state` topics (for the components
  still on the simple boolean path) **and** `.../vc/command`.
- **VueOne** connects to the same broker directly ("Connect SMC Rig") and follows the UNS.
- Our diagnostic scripts subscribe `smc/#`, `uns/#`, `_bridge/status`.

Normalised observation payload example (retained):
```json
{"seq":4313,"epoch":"2026-07-17T13:57:10Z","ts":"2026-07-19T13:43:33.753Z",
 "sourceTopic":"smc/bearing_gripper","station":"assembly_station","component":"bearing_gripper",
 "twinName":"Bearing_Gripper","vcId":"Swivel Arm","state":4,"stateName":"CloseGripper",
 "quality":"GOOD","cmd":true}
```

---

## 4. Two ways VC is driven (deliberate split)

| mechanism | components | how |
|---|---|---|
| **A — native MQTT boolean mapping** (no code) | checker, transfer, clamp, bearing_gripper, shaft_gripper | a CSV mapping row binds the payload field `cmd` (bool) → a VC signal (`PushJoint_ActionSignal` / `IN_J1_Action`). VC's stock component logic moves it. Works. |
| **B — MQTT command → Python gateway** | **Pusher(feeder), Ejector, shaft (pnp X/Z), cover (coverpnp X/Z), swivel (SviwelArmComp)** | statesync publishes a full command; VC maps the **whole message → one VC_STRING property**; a Python "gateway" behaviour parses it and drives the joint. This is where we are stuck. |

**One-writer rule:** a component is on A **or** B, never both. Robot UR3e is out of scope (Phase 3;
needs taught routines / `callRoutine`, its pose table is empty).

Why B exists: VC's mapping can only bind a message field to a **signal or property**. It **cannot**
map onto a robot/servo **joint**, and multi-position axes cannot be a boolean. So those go through a
string "mailbox" + a Python executor that calls VC's motion API. (Confirmed from the official VC
MQTT docs: whole-message pairing = `MessagePropertyType: TopicMessage`, expression
`Convert.ToString(Payload)` inbound, `@Json(Value)` outbound.)

---

## 5. The command envelope (statesync → `vc/command`, non-retained, QoS1)

```json
{ "schemaVersion":1, "epoch":"2026-07-17T13:57:10Z",
  "commandId":"2026-07-17T13:57:10Z#1030", "sourceSeq":4312,
  "sourceTs":"2026-07-19T13:43:33.7Z",
  "ComponentName":"SviwelArmComp", "ComponentId":"SviwelArmComp", "ComponentType":"Actuator",
  "StateName":"ToWork1", "ComponentPos":181.0, "ComponentSpeed":0.0,
  "OperatorType":"equal", "ExpectedDurationMs":1220, "MsgType":2, "MsgTo":"VC" }
```

- `ComponentPos` = **destination** state's position, from the twin (`vc-positions.json`, generated
  from Control.xml). Rig motion-start states 1/3/5 map to destination states 2/4/6.
- `ExpectedDurationMs` = **measured** median rig stroke from `rig_cadence.log` (n=27–108 cycles),
  e.g. feeder advance 705 ms / return 1114 ms; swivel 1220/1356/1200. **Speed is deliberately not
  sent — the VC side must make the on-screen stroke last ExpectedDurationMs.**

The VC event/telemetry payload (`vc/event`, PUB only):
```json
{"schemaVersion":1,"commandId":"...","vcId":"Pusher","status":"completed","position":116.0,"ts":"..."}
```

---

## 6. End-to-end mechanism (data path)

```
rig  --smc/<c>{state:N}-->  Mosquitto  --sub-->  statesync.py
        statesync: dedup, normalise -> uns/.../state (retained),
                   AND for gateway components: look up twin pos + measured duration
                   -> publish uns/.../vc/command (QoS1, non-retained)
   -->  Mosquitto  --sub-->  VC native MQTT client (Event-Based, QoS1)
        expression Convert.ToString(Payload) writes the whole JSON string to
        CommandGateway.CommandJson (a VC_STRING property)
   -->  VC Python gateway behaviour (IronPython 2.7):
        OnChanged(CommandJson): validate (MsgTo, dedup commandId, monotonic sourceSeq, epoch),
                                enqueue into a PER-COMPONENT FIFO
        OnRun loop (20 ms tick): start next command for each IDLE component (concurrent across
                                 components, sequential within one), drive the joint, poll done
        on completion: write StatusJson -> VC maps @Json(Value) -> publish uns/.../vc/event
```

---

## 7. What is PROVEN working (do not re-litigate)

1. **Rig → statesync → vc/command**: statesync has published 1000+ commands, verified on the broker;
   positions and durations correct.
2. **VC native MQTT receives the command**: the whole-message → `CommandJson` string mapping works;
   `Convert.ToString(Payload)` is correct; the live command shows in VC's Topic Payload Preview.
3. **Gateway ingestion**: `OnChanged` fires on the connectivity-driven property write (this was an
   open question — answered yes), validates, dedups, enqueues per component. Logs prove
   receive → enqueue → start → completed with correct commandId/order.
4. **Single-component pilot (blocking `moveJoint`)**: the feeder moved end to end — **17 ms** start
   latency, **702 ms** VC stroke vs **705 ms** rig, completion event returned with matching
   commandId. So the whole architecture is validated; only the drive method for concurrency is open.
5. **Duration control**: the stock cylinder script sets `MaxAcceleration = PushSpeed` (speed written
   into accel), forcing a fixed ~1 s ramp; commanding speed cannot hit a 705 ms stroke. We solved the
   trapezoid `t = D/v + 1/k → v = D/(t − 1/k)` (k=4) and hit 705 ms within 0.4 %. **This worked only
   with the blocking `moveJoint`.**
6. **Event feedback path**: VC *can* publish (SimulationToServer), so lag telemetry is available.

---

## 8. WHERE WE ARE STUCK — the one open question

**How do we drive a `vcServoController` joint to a target over a specified duration,
NON-BLOCKING, so that N servo controllers move concurrently from a single PythonScript
behaviour's `OnRun` loop — in Visual Components 5.0, IronPython 2.7 (`vcScript`)?**

The rig moves ~5 mechanisms **at once**. Our gateway is one script thread. We need concurrent motion.

What we've established empirically (each proven from logs / on-screen behaviour):

| approach | non-blocking? | actually moves the joint? | notes |
|---|---|---|---|
| `control.moveJoint(i, target)` | **NO** — blocks the script thread until the move finishes | **YES**, native interpolation, returns motion time | serialises everything → measured **24 s backlog** when 5 components move at once; queue `qdepth 0→15`, start-lag up to 24.8 s |
| `control.setJointTarget(i, target)` alone | yes | **NO** — proven no-op: log shows `arrived:false`, joint `cur` never changes; nothing moves on screen | it only *sets* the target; something must then *execute* it |
| manual linear interpolation each tick + `control.setJointTarget(i, val)` + `control.moveImmediate()` | yes | **UNVERIFIED** (current attempt) | `moveImmediate` doc says *"Moves **robot** to a given target immediately"* — may be robot-controller-only, not a servo drive; and it's "immediate" (zero sim time) so we'd step it ~50×/s to fake smoothness |

**Relevant `vcServoController` methods (from VC 5.0 `api.xml`):**
- `moveJoint(index, value)` — *"Moves a joint … and then returns the actual motion time."* (blocking)
- `setJointTarget(index, value)` — *"Sets the target value of a joint … to a given value."* (set only)
- `moveImmediate([target_mode])` — *"Moves **robot** to a given target immediately."*
- `setMotionTime(t)` — *"**Forces the servo to execute joint movements at a given motion time.**"* ← looks promising
- `calcMotionTime()` — *"Returns the calculated motion time of servo using current joint values and listed targets."*
- `getJointValue(index)`, `getJointTarget(index)`, `move(...)`, `moveImmediate`, `findJoint`, `getJoint`

**Our leading hypothesis (please confirm or correct):** the non-blocking pattern is
`setJointTarget(i, target)` **then `setMotionTime(durationMs/1000)`** — i.e. queue the target and
force the servo to run it over a set time *without* the script blocking, letting `OnRun` return and
the servo drive across subsequent sim ticks. Is that how `setMotionTime` works? Does it block? Can
multiple servos each be given a target + motion time and all advance concurrently while the single
script thread loops? Or is the intended concurrency model different (e.g. one PythonScript behaviour
**per component**, each blocking on its own `moveJoint` — true OS-level concurrency; or a
`vcSimStatement`/`delay`-based cooperative scheme)?

Secondary: the components are `vcServoController`s reached via
`node.Dof.Properties['Controller']` (single joint each: Pusher=PushJoint, swivel=J1, pnp=X/Z as
separate nodes, etc.). Is `moveImmediate` valid on a plain servo (not a robot controller), and if
so does stepping it every 20 ms with an interpolated target produce smooth motion, or will it fight
the servo?

---

## 9. Constraints (hard)

- No Sparkplug. No external Python process driving VC (deleted). No file-polling driver (deleted).
- No `sleep`/arbitrary timing patches to hide lag. No teleporting normal motion.
- Prefer VC-native motion (`moveJoint`/servo interpolation) / `callRoutine` for robots.
- Keep DRY/KISS/YAGNI. One writer per actuator.
- The rig cannot be modified (no PLC/EAE changes); it only publishes state.
- VC config (mappings/expressions/QoS/Pub-direction) **reverts on model reload unless the .vcmx is
  saved** — a recurring source of "it worked, now it doesn't." (`Auto Remove Pairings` also silently
  deletes a pairing when its target disappears.)

---

## 10. What we need from you

1. **The correct non-blocking, concurrent servo-drive pattern** for VC 5.0 vcScript (IronPython 2.7)
   — the definitive answer to §8. Ideally the exact call sequence per tick, and whether
   `setJointTarget` + `setMotionTime` is it, or whether concurrency needs a different structure.
2. Whether `moveImmediate` is safe/appropriate on a plain `vcServoController` (vs robot controller).
3. If single-thread concurrency is impossible, the cleanest VC-native alternative (per-component
   behaviours? a `vcSimStatement`/executor pattern?) that keeps one-writer and stays native.
4. A sanity check on the **bounded-lag playback policy**: for absolute-target servo moves,
   latest-wins/coalesce-when-behind is safe; is there a better VC-native way to keep the shadow
   current without skipping essential strokes?
5. Anything wrong or fragile in the overall design (UNS shape, command envelope, QoS/retain choices,
   the whole-message-string mailbox pattern, completion telemetry).

Relevant files (share on request): `statesync.py` (bridge), `vc_gateway_mqtt.py` (the VC gateway
behaviour), `vc-positions.json` (twin positions), `rig_cadence.log` (measured durations),
`VcMqttGatewayMappings.WORKING.csv` (the proven mapping export).
