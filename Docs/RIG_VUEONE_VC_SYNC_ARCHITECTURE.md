# Synchronising the running SMC rig, VueOne STD, and Visual Components 5.0

**Status:** READ-ONLY investigation. No code changed, no mappings imported, no model touched.
**Evidence classes:** *proven* (source/log/binary), *inferred* (follows from source, not executed),
*testimony* (asserted, no artifact), *open* (needs one controlled experiment).

---

## 1. Executive conclusion and recommended architecture

**Feed rig MQTT into VueOne. Do not reproduce the command protocol over MQTT.**

The reason is not preference — it is that **the command exists only in Control.xml**. `{state:N}` is an
ECC state label (~2.6 bits); a 6-DOF pose is ~192 bits. Nothing recovers the second from the first.
Something must read the twin and author `target + speed + operator + routine`. VueOne already does
this, in-process, from the live twin. Reproducing it over MQTT means building a **second** command
compiler over a **second, generated copy** of the twin's tables — and those tables already
demonstrably drift (`statesync.config.json` says broker `192.168.1.50`; `sync-map.generated.json`
says `127.0.0.1`, despite its own "do not hand-edit" banner).

**Recommended: Option A — rig MQTT → VueOne semantic interpreter → existing VC socket → VC dispatcher.**

It is the smallest architecture because **every leg already exists**:

| leg | status |
|---|---|
| rig → broker | *proven* — 25,123 publishes, QoS1, 100% 9 bytes |
| broker → VueOne (`VcMqttClient` → `DrainMqttQueue` → cycle-synchronous drain at `CycleTick`, ConcurrentQueue cap 20,000) | *proven present*, well built |
| VueOne authors command from twin (`nState.Position`, `aState.LinearSpeed`, `nState.Operator`, `VcRobotStateHelper.GetOutgoingStateName`) | *proven* |
| socket → VC dispatcher → `control.moveJoint` / `executor.callRoutine` / SignalActions grasp | *proven* — real motion durations measured |

**It needs exactly one keystone change, plus two hygiene fixes:**

1. **Split emission from self-advance** — `FormSimulationView.cs:676`. Today `Emulator_WorkerCompleted`
   calls `ChangeState(...)` as soon as the socket write is *enqueued*; send and self-advance are wired
   to the same BackgroundWorker (`:630-638`). This is why VueOne free-runs and why VC's `complete`
   gates nothing. **In rig mode the rig must be the sole writer of `CurrentState`.** ⚠️ *Do not* set
   `DynamicStateEmulatorOn = false` — that deletes `:659`, the only `SendToVcPython` call site, and VC
   goes silent.
2. **One writer per actuator** — retire the CSV mappings, `vc_shadow.py`, `vc_robot_driver.py`; unbind
   `EventFromVcCSharp` **in code** (not by config).
3. **Bounded lag** — make the *cylinder* consumer non-blocking; keep `callRoutine` blocking for robots.

**What this buys:** the rig supplies *timing*; the twin supplies *meaning*; VC supplies *motion*.
**What it does not buy:** a metric shadow. VC renders the twin's poses, gated by rig events — not the
rig's real trajectory. That is unreachable without position telemetry from the PLCs.

---

## 2. Evidence table

| # | claim | evidence | confidence |
|---|---|---|---|
| 1 | Rig payload is one INT, always | `mosquitto.log`: 25,123 rig publishes, **100% exactly 9 bytes**; formatter FB input surface = one INT (`TemplateLibraryDeployer.cs:564`), algo `payload := CONCAT('{state:', INT_TO_STRING(state), '}')` (:582) | **proven** |
| 2 | PLCs never subscribe → MQTT is one-way telemetry here | broker log: zero SUBSCRIBE from rig clients | **proven** |
| 3 | Socket carries a real command | `FormSimulationView.cs:659` — VCID, Type, `LinearSpeed`, `nState.Position`, `nState.Operator`, state/routine name, MsgType 2 | **proven** |
| 4 | VueOne does **not** wait for VC | `FormSimulationView.cs:676` `Emulator_WorkerCompleted → ChangeState(...)` on the same worker as `:659`; gate `mbDynamicStateEmulatorOn` set true at 2 sites, never false | **proven** |
| 5 | VC's `complete` changes nothing | `ResolveIncomingState` name-matches the state `:676` already committed; `ChangeState` re-writes it and re-nulls `PreviouExecState` | **proven** |
| 6 | Smoothness is VC's, not the socket's | `moveJoint` blocks in sim time and returns actual motion time; measured: TransferComp Advanced 5.008 s (n=12), pnp_vertical 2.272 s (n=36), CoverPnp_Vertical Down 1.395 s (n=35) | **proven** |
| 7 | Robots have taught routines | `Partpick` 7.704 s, `Partplace` 4.736 s, `Home` 0.726 s in the cycle-time corpus; `GetOutgoingStateName` rewrites twin names before send | **proven** |
| 8 | Cross-topic ordering is not guaranteed and matters | **40.0% of adjacencies cross-topic within 60 ms over 27 cycles** (`rig_cadence.log`), 41.1% in the clean cycle; bearing arm/gripper pairs at **0.0 ms** on two topics | **proven** |
| 9 | VC is oversubscribed by the rig | VC ceiling `1/(T_motion + 0.3)` ⇒ ~3.3 cmd/s at zero motion, **~0.12/s during a 7.8 s robot move**; rig bursts at 0 ms | **proven (arithmetic on measured inputs)** |
| 10 | Feedback loop is open **by configuration** | `VcMqttFeedMappings.csv`: `ServerToSimulation` = 6, `SimulationToServer` = **0** | **proven** |
| 11 | VC *can* publish outbound | `Plugin.MQTT.dll`: `SimulationToServerPairCommand`, `PublishAsync`, `subscribeToValChangeEvents`; resource string `"Simulation To Server Pair"` | **proven** |
| 12 | Expression cannot see other fields | resource: *"Use 'Value' as a key to access the property value"* ⇒ freshness (`ts`/`seq`/`epoch`) gating impossible in a mapping | **proven** |
| 13 | Retained commands are hazardous | statesync `retain=True` unconditional; `cmd` bound with `IsTrigger=True`; 18 retained replays measured at 24.9–25.7 min stale | **proven** |
| 14 | `GRIP_CMD_STATES` bearing gripper inverted | part leaves BearingSensor 219.57; only prior actuation gripper 1→2 at 218.25. `(3,4)` window = 120/60 ms (release pulse); `(1,2)` = 1480/1340 ms | **proven** |
| 15 | ★ — the rig already commands VC, uninterlocked | `ChangeState` → `StateReceived` → `onStateReceived` (`FormSystemEditor.cs:2508`) → `SetActuator` → dynamic → `doDynamicStateEmulator` → send **+ self-advance**, no interlock check | **inferred (source-traced, not executed)** |
| 16 | In rig mode the engine's legitimate path emits nothing | rig reports the dynamic state directly; remaining transition is static → `:320` else-branch → no emission ⇒ ★ is the only send path | **inferred** |
| 17 | Socket has no reconnect | `syncSocket.cs:919-921 AvoidDuplicateCon() { return; }`; `Send()` dequeues then discards when disconnected (:785/:797) | **proven** |
| 18 | `VCID` is a catalogue **type** GUID, not an instance id | `pnp`/`coverpnp` share one GUID; `Checker`/`Transfer`/`Transfer #3` share one | **proven (partial agent result)** |
| 19 | Two different UR3e STDs exist | model tree | **partial — needs resolution** |
| 20 | "Smooth" itself | no video/screenshot/frame-timing artifact anywhere; low-variance durations prove *not teleporting* | **testimony** |
| 21 | 8 July cycle-time runs are empty | three consecutive `{"cycles": []}` vs 30–33 pairs on 23 June | **proven, unexplained** |

---

## 3. Socket command vs current MQTT payload

| field | socket (`VcComponentArg`) | rig MQTT | recoverable from Control.xml? |
|---|---|---|---|
| component | `ComponentName`/`ComponentId` ← VCID | topic `smc/<component>` | yes (sync-map) |
| type | `ComponentType` Actuator/Robot/Conveyor | — | yes |
| **target position** | `ComponentPos` ← `nState.Position` | **absent** | **yes — deterministic** |
| **speed** | `ComponentSpeed` ← `aState.LinearSpeed` | **absent** | **yes — deterministic** |
| **operator** | `OperatorType` ← `nState.Operator` | **absent** | **yes** |
| **routine name** | `StateName` ← `GetOutgoingStateName` | **absent** | **yes** |
| expected duration | — (implicit in routine/profile) | **absent** | partly (measured corpus) |
| sequence identity | `MsgAckNo` — **hardcoded −1** | `seq` exists in UNS, **bound 0×** | n/a |
| completion | `complete` echo — **not load-bearing** | none | n/a |
| state label | `StateName` | **`{state:N}` — the only thing present** | — |

**Not safely guessable inside VC:** the real trajectory, the rig's actual speed, and which of two
work positions a centre-home swivel is heading to (the twin disambiguates by transition; a bare
state integer does not).

---

## 4. Architecture comparison

| | A — rig→VueOne→socket→VC | B — command compiler→MQTT→thin VC executor | C — native mapping only | D — hybrid (mapping for cylinders, executor for the rest) |
|---|---|---|---|---|
| smoothness | native (proven) | native (same script) | native for cylinders only | mixed |
| determinism | one ordered emitter | needs single ordered topic | none cross-component | two clocks |
| robots/grasp | ✅ `callRoutine` + SignalActions | ✅ | ❌ **impossible** — joints not mappable, no routine field | ✅ |
| reconnect | ❌ **none** (`AvoidDuplicateCon(){return;}`) | ✅ MQTTnet ManagedClient | ✅ | mixed |
| duplicates | possible (`:748`/`:752`) | spec'd via `cmd_id` | n/a | mixed |
| bounded lag | ❌ unbounded queue today | same problem — **transport doesn't fix it** | n/a | worse |
| effort | **smallest — one keystone line + hygiene** | second compiler + second table + broker SPOF | n/a | dual writers |
| writers/actuator | 1 (after retiring CSV) | 1 | 1 | **2 — disqualifying** |

**C is disqualified on evidence**, not opinion: no joint/routine mapping target, `Expression` sees only
`Value`, 0 feedback rows. **D is disqualified** by the one-writer rule unless VueOne is told never to
command cylinders — extra config for no gain (`cmd` is lossless for cylinders, but the command path
already covers them). **B is viable but strictly larger than A** and *still cannot delete the VC script*.

---

## 5. If MQTT command topics are ever built (Option B contingency)

```
vue/v1/cmd            → VC   QoS1  retain=FALSE   ONE ordered stream (component is a field)
vue/v1/evt            ← VC   QoS1  retain=FALSE   started / completed / failed
vue/v1/status/{a,vc}         QoS1  RETAINED       birth + LWT
vue/v1/snapshot/{req,rsp}    QoS1  retain=false   reconnect resync
uns/v1/smc/<station>/<comp>/state   QoS0  RETAINED  observations (unchanged)
```

```json
{ "schemaVersion":1, "epoch":17, "commandId":"01JD3K7Q8XN4V2",
  "sourceSeq":2841, "sourceTs":"2026-07-16T09:14:22.317Z",
  "vcId":"Pusher", "type":"Actuator",
  "command":"move", "target":{"position":116.0,"operator":"equal"},
  "speed":45.0, "validUntilMs":2000 }

{ "schemaVersion":1, "commandId":"...", "vcId":"UR3e", "type":"Robot",
  "command":"routine", "routine":"Partpick", "validUntilMs":15000 }
```

**Commands are never retained** (retained command = stale actuation on connect; `cmd` binds to
`IsTrigger=True` motion signals). **QoS1**, never 2 — `commandId` gives exactly-once semantically.
**One command topic**, because cell causality is cross-component (40% of adjacencies) and MQTT orders
per-topic only. **Clean session + explicit snapshot resync** — do not claim both queued delivery and
clean start. `command ∈ move | routine`; **there is no joint field — the absence is the enforcement.**

---

## 6. Bounded-lag playback policy

The rig cannot wait; an ACK cannot backpressure physics. So the policy is per-command-shape:

| class | command shape | policy | rationale |
|---|---|---|---|
| cylinders, shaft/cover axes, swivel | **absolute target — idempotent** | **level: latest-wins, coalesce** | re-applying a target converges |
| robot routines, grasp/release | **invocation — NOT idempotent** | **one-in-flight, depth 1, never coalesce** | calling `Partpick` twice grasps twice |

**Essential transitions (grasp/release/handoff) are never coalesced.** Coalescing is legal *only*
because the command is an absolute pose, not a delta.

**The real fix is VC-side and non-blocking:** drive an interpolator toward the latest target each tick
instead of blocking inside `moveJoint`. Then VC never falls more than one tick behind and coalescing
rarely triggers. Robots keep blocking `callRoutine` — you cannot interpolate a taught program, robot
commands are rare (3 states), and the routine's duration *is* the truth. **This is where the effort
goes — not into broker tuning.**

*Verified cost of getting this wrong:* `ServoController_Script_hardened.py` polls a level and
`continue`s when unchanged — if the level flips *and returns* during a blocking move, **the whole
stroke never renders.**

---

## 7. Ownership — one writer per sink

| sink | sole writer | everyone else |
|---|---|---|
| twin `CurrentState[0]` | rig observations (via `VcMqttClient` → drain → `ChangeState`) | VC `complete` → **telemetry only** |
| VC joints/routines | the socket dispatcher script | CSV mappings **retired** |
| `uns/v1/#` | statesync | — |

**Currently there are up to four potential writers per actuator** (CSV mapping, `vc_shadow.py`,
`vc_robot_driver.py`, socket dispatcher) plus the component's own `ServoController_Script`.
**Provenance is currently inexpressible:** three transports bind to one handler
(`EventFromVcPython`/`EventFromVcCSharp`/`EventFromVcMqtt` → `OnEventFromVc`), `OnEventFromVc` never
reads `ClientId`, and **both rig routes forge `ClientId="VC"`**.

**Safety stays in the PLC.** In rig mode the PLC sequences and owns its ECC RuleTable; a VC collision
is a display artifact. VueOne's interlocks belong to the **conformance model**, not the command path.
No safety logic may live only in the shadow.

---

## 8. Instrumentation plan

Timestamp the same command at four points, keyed by `MsgAckNo` (un-hardcode it — the mechanism exists
and `RF Learning.cs:415/436/441` already uses it correctly):

| stamp | where | gives |
|---|---|---|
| `t_rig` | rig publish (broker log) | source of truth |
| `t_drain` | `DrainMqttQueue` at `CycleTick` | transport + queue |
| `t_send` | `SendToVcPython` | semantic conversion |
| `t_start` | VC dispatcher, before `moveJoint`/`callRoutine` | command-start latency |
| `t_done` | VC `SendEvent('complete')` | animation duration |

**End-to-end lag = `t_done − t_rig`.** Nobody has ever measured it. `cycle_times_*.json` **cannot**
show it — `_cycleStart` is stamped at parse time, immediately before the blocking move, so per-move
durations stay accurate even if the whole batch arrived at once.

---

## 9. Acceptance criteria

1. **Bounded lag:** `t_done − t_rig` ≤ **1000 ms** at the 95th percentile for cylinders; robots ≤
   routine duration + 500 ms. Must not grow across cycles.
2. **No growing queue:** VueOne→VC in-flight depth returns to 0 between cycles, over ≥10 consecutive
   cycles.
3. **No skipped essential phase:** every grasp/release/handoff in `rig_watch.log` appears in VC's
   `evt`/`complete` stream. Count in == count out.
4. **No transfer/clamp collision** across 10 cycles.
5. **Correct handoffs:** bearing/shaft/cover attach at pick and detach at place — verified from the
   part's parent node, not by eye.
6. **Deterministic reconnect/restart:** kill the broker mid-cycle; VC converges to the correct pose
   from a snapshot with no stale actuation.
7. **One writer per actuator:** provable statically — CSV rows = 0, `vc_shadow.py` absent,
   `vc_robot_driver.py` absent, `EventFromVcCSharp` unbound.

---

## 10. Open questions — each needs ONE controlled experiment

| # | question | experiment | can overturn |
|---|---|---|---|
| 1 | **Is ★ real and load-bearing?** | `VODebugTraceLogger` already traces every send. Drive one rig stroke; count sends + origin; **stub ★ → do sends drop to zero?** | the whole architecture |
| 2 | **Is splitting `:676` safe?** (the keystone) | Suppress the `ChangeState` at `:676` in rig mode; run one cycle; does the twin still advance (on rig observations) and does VC still receive commands? | the recommendation |
| 3 | **Is VC double-written right now?** (native mapping *and* socket client both live) | Inspect the live VC connectivity config + which scripts are loaded | possibly the whole bug, free to find |
| 4 | **Are `Dof` variables mappable?** `Plugin.MQTT.dll` exposes `DofVariableInfo`/`CreateSimulationVariable` | Try to pair a joint Dof in the dialog | qualifies "joints not mappable" |
| 5 | **Is PusherNode's Servo Controller / ServoController_Script actually disabled?** `vc_detail.json` reports `"Enabled": "False"` on **both** | Inspect the live component | **alternative root cause of the feeder failure** |
| 6 | **Why are the 8 July runs empty?** | Re-run path B alone | premise of "path B works" |
| 7 | **`VCID` is a type GUID** — how do `pnp`/`coverpnp` and `Checker`/`Transfer`/`Transfer #3` disambiguate on the wire? | Trace one command for each | correlation correctness |
| 8 | **Which UR3e STD is live?** (two exist) | Resolve in the model | robot command authoring |
| 9 | Does VC tolerate a real (non-integer) speed? `Convert.ToInt32` rounds; <0.5 → 0 | Send 45.7 | speed fidelity |
| 10 | Is "smooth" actually smooth? | Record video + frame timing | the premise |

---

## 11. The direct answer

**Feed the rig into VueOne.** Do not reproduce the command protocol over MQTT — not yet, and possibly
not ever.

- The command lives in Control.xml; VueOne holds it in-process. A second compiler over generated
  tables adds a drift surface that has **already** bitten (`192.168.1.50` vs `127.0.0.1`).
- Every leg of Option A exists and is proven; the ingest (`VcMqttClient` → cycle-synchronous drain) is
  the best-built component in the system.
- MQTT command topics would **not** remove the VC script (native mapping can't consume a command
  schema), so the honest gain is reconnect + multiple consumers + off-box — real, but not the current
  problem.
- **The current problem is that VueOne free-runs (`:676`) and VC blocks — and neither is a transport
  problem.** Changing transport fixes neither. Splitting `:676` and making the cylinder consumer
  non-blocking fixes both.

**Smallest architecture that preserves smooth VueOne→VC with the rig as the timing source:**

```
rig ──MQTT{state:N}──▶ VcMqttClient ──▶ DrainMqttQueue @ CycleTick ──▶ ChangeState
                                                                          │  (rig = sole writer)
                                                                          ▼
                                              onStateReceived → SetActuator → dynamic state
                                                                          │  ★ made deliberate:
                                                                          │  SEND, do not self-advance
                                                                          ▼
                              SendToVcPython{VCID, type, Position, Speed, Operator, routine}
                                                                          │  socket 52000
                                                                          ▼
                              VC dispatcher → moveJoint / callRoutine / SignalActions grasp
                                                                          │
                                                                          ▼
                                        complete{MsgAckNo} ──▶ lag telemetry ONLY (never ChangeState)
```

**Then, and only then**, revisit MQTT command topics — when reconnect, multi-consumer, or off-box
distribution becomes the binding constraint. Not before.

**Run experiments 1, 2 and 3 first. Any of them can overturn this in a day.**
