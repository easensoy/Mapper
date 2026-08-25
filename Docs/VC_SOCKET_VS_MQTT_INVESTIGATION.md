"out": "# Why the socket path is smooth and the MQTT path is not

**Scope:** mechanism, from source and logs. No implementation. Evidence is labelled: *proven* (source/log/artifact), *inferred* (follows from source but not executed), *testimony* (asserted, no artifact).

---

## 1. Root cause — mechanism first

### 1.1 The one-sentence answer

**The socket carries a command. MQTT carries an observation. We are using MQTT to transport an observation and then asking the mapping layer to reconstruct a command from it — inverting the direction of the information.** Every defect below follows from that inversion.

### 1.2 What actually travels

**Socket, per move** (`FormSimulationView.cs:659`): component id, type, **target position** (the settled state's `<Position>`), **speed** (the moving state's `<Speed>`), operator, and a state name — or, for robots, a taught-routine name. Every value traces to a `<State>` node in Control.xml. The socket is VueOne reading its own twin and telling VC what to do.

**MQTT, per event:** `{state:4}`. Nine bytes. Census of the whole 40 MB broker log: **25,123 rig publishes, 100% exactly 9 bytes, zero exceptions.** The formatter FB's entire input surface is one INT. *There is no pin on which a position could travel.*

`{state:N}` is an ECC state label — a symbol from a 5- or 7-element alphabet, ≈2.6 bits. A 6-DOF pose is six reals, ≈192 bits. `smc/robot` carries three states (≈1.6 bits) for a whole UR3e.

**You cannot reconstruct 192 bits from 2.6.** That is not a tuning problem. No QoS setting, no retain flag, no broker configuration creates information that was never put on the wire.

### 1.3 The circularity

The MQTT consumer needs a target pose. It gets one from `vc-positions.json` — which `extract_vc_positions.py` generates **from Control.xml**.

> Control.xml → *(discarded at the PLC)* → 2.6 bits over MQTT → *(rebuilt from a table generated from Control.xml)*

We take the command out of the twin, throw it away, ship a label, and rebuild the command from the twin at the far end. Path B just reads the twin.

### 1.4 The four compensations

| missing on the wire | compensation | cost |
|---|---|---|
| pose | `vc-positions.json` lookup | second source of geometry; drifts from Control.xml |
| trajectory | `EASE=0.12` / `TICK=0.03` | a constant with no physical referent (measured rig durations for one transition: min 0.922 s, median 3.740 s, max 16.142 s) |
| mappable joints | file-polling driver | last-target-wins |
| signal | `cmd` boolean | 1 bit — and the bearing gripper's is inverted |

None is checked, because there is nothing to check with. **The mapping CSV has 6 rows: `Direction = {ServerToSimulation: 6}`, `SimulationToServer = 0`.** VC reports nothing back — no completion, no arrival, no error, no lag. The loop is open **by configuration, not by accident**. The bearing-gripper inversion shipped and *could not have been caught*.

statesync emits an 11-field envelope (`seq, epoch, ts, sourceTopic, station, component, twinName, vcId, state, stateName, quality`). The CSV binds **1 of 11**. The nine discarded include `seq` (total ordering), `ts` (freshness) and `epoch` (restart detection) — exactly the three that would detect reordering, staleness and state loss. Generated 35,971 times, bound zero times.

### 1.5 Where smoothness actually comes from

| contributor | cylinders | robots |
|---|---|---|
| VC servo interpolator (`control.moveJoint`) | **~100%** | — |
| taught routine (`executor.callRoutine`) | — | **~100%** |
| speed value from the twin | scales the profile (via a fragile binding — §7) | **discarded** |
| VueOne sequencing | **0% within a move** — owns order and non-overlap only | 0% |

`moveJoint` **blocks** until motion completes in *simulation* time and returns the actual motion time. Proven twice: the API returns a value that cannot exist before execution, and the vendor's own stock ServoController signals "closed" on the very next line. Empirically confirmed by the run artifacts — TransferComp Advanced 5.008 s (n=12), pnp_vertical 2.272 s (n=36). The profile comes from `vcJoint.MaxSpeed`/`MaxAcceleration`, scaled by `vcServoController.Speed`.

`callRoutine` suspends the script until the taught routine finishes — VC's full stack: planning, interpolation, via-points, per-statement speed and zone. **`MoveRobot` accepts a `speed` argument and never references it.** All robot timing is the routine's.

`p.Value = x` teleports because three things are absent: the interpolator, simulation time, and the profile. Easing substitutes for the first two and cannot substitute for the third.

> **VueOne contributes nothing to smoothness.** It contributes a target and a number. If MQTT delivered the same target and the same number to the same script, the motion would be *identically* smooth.
>
> **"The socket is smooth" is not an argument for sockets. It is an argument for commands.** The transport is nearly irrelevant to smoothness. The payload is everything.

### 1.6 What the socket *does* give you — and why the rig destroys it

One thing, and it isn't smoothness: **a queue.**

The engine loops at 10 Hz (`Thread.Sleep(100)`) and emits when four conditions hold: the per-component latch is open (`CurrentState[0] != PreviouExecState`), sequence conditions pass, the destination state is **dynamic**, and its interlocks pass.

**The prior analysis's central claim — that the engine waits for VC's `complete` — is false.** Nine lines below the send, `Emulator_WorkerCompleted` (`FormSimulationView.cs:676`) calls `ChangeState(..., Transitions[0].DestinationState)`. VueOne advances as soon as the socket write is *enqueued*. Send and self-advance are wired to the **same** BackgroundWorker (`:630-638`) — you cannot have one without the other. The gate `mbDynamicStateEmulatorOn` is set true at two sites and never set false anywhere in the tree.

So the socket's real guarantee is **order, not time.** Minimum spacing between two commands to one actuator is ~one 100 ms cycle, whether VC's move takes 7 s or 0. VC's serial, blocking `RecMsg` loop drains a TCP-buffered backlog *in order*, at its own pace.

> **Path B is smooth, correctly ordered, and lagging — and nothing measures the lag, so nobody noticed.** `MsgAckNo` exists and is hardcoded to −1 on this path.

That queue is exactly what the rig destroys. VC's ceiling is `1/(T_motion + 0.3)` commands per simulation second — a hard floor of ~3.3/s at *zero* motion time, and ~0.12/s during a measured 7.8 s robot move. The rig publishes at 0 ms spacing (`rig_watch.log`: `bearing_pnp{4}` and `bearing_gripper{1}` on the same 10 ms tick). **Two orders of magnitude oversubscribed.** The queue grows without bound and never drains, because every dequeued message triggers another blocking move.

> **Path B works because (a) it ships commands, and (b) nobody is waiting for VC. The rig destroys (b).**

### 1.7 Two more mechanism facts

**The rig and the twin disagree about what a *state* is.** The twin models a move as a **dynamic state** — a command-carrier holding speed and interlocks, which the engine never rests in (`FormDiagram.cs:540` says it verbatim: *"dynamics have no position so take position from following static state"*). The rig's ECC reports motion **as a state**: rig 1 = `Advancing`, rig 3 = `Returning`, both `StaticState=False` in the twin. Mapping rig→twin state-for-state parks `CurrentState` on a state the engine is not designed to hold — and `ChangeStateTo(null, dest)` teleports there with **no source validation**.

**And that has a live consequence: the rig already commands VC today, and nobody designed it.** `ChangeState` fires `StateReceived`; `onStateReceived` (`FormSystemEditor.cs:2508`) is behaviourally identical to `onStateChanged` — both call `SetActuator(DestinationStateID)`. A dynamic state routes into `doDynamicStateEmulator`, which **sends a VC command and self-advances** — with **no interlock check**, racing the engine's own decision for the same stroke.

Call this **★**. Two honest corrections to how it's been described:

- It is a **race**, not a reliable doubling. The engine only emits if it evaluates `ReturnedHome → Advancing` before the rig's report lands. You get one or two commands per stroke *nondeterministically*, by cycle phase. Nondeterministic is harder to diagnose than reliably-doubled.
- **The engine's legitimate emission path produces *nothing* in rig mode.** *(inference from source; confirmed by review; not executed — see §8.3.)* The engine emits on *entry into* a dynamic state. The rig *reports* the dynamic state directly. Once the rig writes `CurrentState = Advancing`, the only transition left is `Advancing → Advanced`, which is **static → no emission**. **★ is the only thing carrying rig motion to VC.** Closing it silences VC — the same trap as `DynamicStateEmulatorOn = false`, which deletes `:659`, the only send site.

★ is a *correct instinct implemented as an accident*: the rig says "I'm advancing", VueOne says "then VC should render the advance". That is the right idea. The bugs are that it also self-advances (corrupting the record), races, and derives from a state the engine can't legally hold.

---

## 2. Socket vs our MQTT — your list, item by item

| # | your claim | verdict |
|---|---|---|
| 1 | command-level data | **CONFIRMED.** Target and speed are genuinely on the wire; VC consumes exactly those. |
| 2 | VC native motion | **CONFIRMED — empirically, not just by vendor doc.** `moveJoint` at `:193`, `SendEvent` immediately after, so measured duration *is* motion time: 5.008 s / 2.272 s / 2.183 s means. **This discharges the doc's "moveJoint blocking is inferred — confirm in step 1" risk row.** |
| 3 | taught routines | **CONFIRMED — and this contradicts the analysis doc.** It claimed UR3e has no taught routines; the artifacts show `Partpick` 7.704 s, `Partplace` 4.736 s, `Home` 0.726 s. The doc compared *raw twin* names; `VcRobotStateHelper.GetOutgoingStateName` rewrites before sending. |
| 4 | completion events | **PARTLY.** Fully wired, but **not load-bearing** — `:676` already advanced. Excellent as a *data source* (it produced the cycle-time corpus); does nothing as *control*. |
| 5 | existing working implementation | **CONFIRMED BY EVIDENCE — with a material caveat.** Machine-written, not testimony: 16 runs on 23 June, 13 with 30–33 distinct `component\\|state` pairs, counts to n=36 (~12 cycles), whole-rig coverage. **But the three most recent runs (8 July) are all `{"cycles": []}` — zero round trips, unexplained.** "It works" is proven for June, not today. |
| 6 | smooth visual behaviour | **PARTLY — testimony.** No video, screenshot or frame-timing artifact exists anywhere. *Proven:* motion goes through the interpolator and takes real, low-variance time (CoverPnp_Vertical Down 1.395 s over n=35, min 0.884 max 2.347). So **"not teleporting" is proven; "smooth" is a sound inference**, not an observation. |
| 7 | point-to-point TCP | **CONFIRMED — and understated.** All three servers bind `127.0.0.1`. Not merely point-to-point: **localhost-only.** VC must run on the same box as VueOne; rig/VC/VueOne can never be distributed without changing this. |
| 8 | fragile framing | **CONFIRMED — worse one way, better the other.** VueOne→VC has **no framing at all** (raw ASCII JSON; the only three `EOM` splits are receive-side). VC's `recv(1024)` + `re.findall(r'\\{.*?\\}')` *is* the framing → a boundary-split message loses **both halves**, silently. VC→VueOne *does* append EOM and VueOne *does* accumulate — but hands the whole buffer to the handler (`:748`) while consuming only the first frame (`:752`) → duplicate delivery on coalesced reads. |
| 9 | limited buffering / correlation | **CONFIRMED.** `Send()`'s `return true` sits *inside* the `while` → one message per call. A command sent while VC is down is **dequeued at `:785` then silently discarded** (`:797`). `MsgAckNo` = −1 hardcoded; VC's reply omits it entirely. |
| 10 | weak error handling | **CONFIRMED — worse than "weak".** `Debug.Print("Error in splitting msg", ex.Message)` binds the `(format, params)` overload with **no placeholders** → **the exception detail is never printed.** Five sites. |
| 11 | silent hangs if routine missing | **REFUTED as stated — the real failure is worse.** VueOne does *not* hang, because `:676` already advanced. A missing routine yields **silent divergence**: the twin marches on believing the move happened. A hang is visible; divergence is not. (Also: `MoveRobot`'s `try/except` prints and then falls through to an **unconditional** `SendEvent('complete')` *outside* the try — a routine that throws still reports complete.) |
| 12 | not industrial pub/sub | **CONFIRMED.** Three loopback servers, one client slot each, no topics/retain/QoS/LWT/broker/discovery. By construction **only one consumer can ever attach.** |
| 13 | hard to inspect vs MQTT | **PARTLY — your weakest claim; don't lean on it.** A bidirectional trace already exists and is wired: `Trace("VueOne -> VC")` on every send, `Trace("VC -> VueOne")` on every receive, and `FormDebugConsole` is a live UI consuming it. The *real* limits are narrower: `VODebugTraceLogger` is an in-memory ring capped at 4000 with **no file persistence**, and the single client slot means you cannot attach a passive second consumer the way `mosquitto_sub -t 'smc/#'` can. MQTT is meaningfully easier out of the box (we have a 40 MB log as proof) — but the gap is **durable logging and a multi-subscriber tap**, not visibility. |
| 14 | no schema / versioning | **CONFIRMED — with live proof of the cost.** VC emits `"CompoentPos"` (typo) against VueOne's `ComponentPos`; `System.Text.Json` is name-exact → binds nothing, field reads 0. `MoveConveyor` calls `SendEvent` with 6 args against a 5-param def → guaranteed TypeError, swallowed. Two schemas share one wire, discriminated by a `ClientId` string compare. |
| 15 | completion may not be rigorous | **CONFIRMED — stronger than you put it.** For actuators and robots it is **not a handshake at all.** No thread waits, no ack correlates, VC's reply carries no ack field. |

**And the MQTT side, symmetrically:** rig→broker QoS 1 (25,123/25,123) and statesync→UNS QoS 1 — but **VC subscribes at QoS 0** (48 subs at QoS 0 vs 25 at QoS 1). Effective delivery = min(pub, sub) = **at-most-once**. The QoS 1 chain terminates in a QoS 0 subscriber and buys VC nothing. And VC's mapping clients use **paho auto-generated UUID client-ids** — a new id per connection — so a persistent session is **impossible by construction**; each of 73 reconnects starts with an empty backlog.

**Retain is inverted where it matters.** Rig `retain=0` (so a reconnecting bridge replays nothing); statesync `retain=1` unconditionally. A retained *state* is a claim about the world — passive, corrected by the next live message; retain is genuinely right there. A retained *command* is an instruction to actuate — and `cmd` is bound to `PushJoint_ActionSignal` / `IN_J1_Action` with `IsTrigger=True`. **Measured: 18 retained messages replayed on connect, all 24.9–25.7 minutes stale.** The consumer cannot defend itself: freshness is *unrepresentable* — `ts`/`epoch`/`seq` are not bound, and `Expression` can only transform bound fields.

**And the silent state loss.** All four preconditions are present simultaneously: statesync assigns only `on_connect`/`on_message` (**no `on_disconnect`**); `last_state` persists across the drop; the dedupe `if last_state.get(key) == state: return` fires *before* `seq` increments; and the rig's `retain=0` means resubscribe replays nothing. A drop spanning a full cycle (~40 s vs 60 s keepalive — the **likely** case) is swallowed with **no warning, no counter, no log line.** `replay_snapshot()` exists but is wired to the *VueOne socket* reconnect, not the MQTT one. `epoch` is set once at start and does not change on MQTT reconnect — so followers see an unbroken, monotonic `seq` across a drop that ate a cycle.

**Ordering is the deepest one.** 19 flat topics, 3 publishers, no common clock. MQTT orders per-topic/per-publisher/per-QoS and guarantees **nothing** across topics. Measured over one clean cycle: **65 of 158 adjacencies (41%) are cross-topic within 60 ms; 39 of those at exactly 0 ms.** The bearing pick/place is the smoking gun:

```
218.25  smc/bearing_pnp      {state:4}   arm ARRIVES
218.25  smc/bearing_gripper  {state:1}   gripper ACTUATES   ← 0.0 ms, TWO topics
218.31  smc/bearing_gripper  {state:2}   closed
218.31  smc/bearing_pnp      {state:1}   arm DEPARTS        ← 0.0 ms, TWO topics
```

Reordered, VC renders: *gripper closes on empty space → arm arrives → arm departs.* Nothing detects it. Per-topic order is **component-level** sequencing; a cell's interlocks are *between* components. Splitting the cell across 19 topics discards the exact relation the cell is built on. Timing can't re-infer it either — the duration spread (seconds) dwarfs the inter-component gaps (0–60 ms).

**`cmd` is exact for cylinders and only cylinders.** A double-acting cylinder with two end sensors **is a 1-bit system** — the sync-map maps both state 0 and state 4 to `ReturnedFinished` (the same physical place); 1/3 are transient. So `cmd = state in (1,2)` is **lossless**, and `PushJoint_ActionSignal` is itself boolean. Bit → bit, exact. That is **5 of 19 components** (feeder, checker, transfer, clamp, ejector). The CSV has 6 rows: 4 cylinders + 2 grippers — **and both grippers are wrong.**

---

## 3. An MQTT contract that could match the socket — and what it costs

Stated up front, because it is load-bearing: **while `FormSimulationView.cs:676` stands, `ack`/`done` are observability-only.** VueOne self-advances on send; nothing consumes a completion or a rejection. A protocol whose failure responses are unhandled is not a protocol. The schema below is ready for gating the day `:676` is split; it does not create gating.

### Namespaces (ACL-enforced, not by convention)

| namespace | direction | content | retained | QoS | consumers |
|---|---|---|---|---|---|
| `uns/v1/smc/…` | rig → all | observations, `{state:N}` normalised | **yes** | 0 | many (HMI, logger, twin) |
| `vue/v1/…` | authority ↔ VC | commands + feedback | **no** (except status) | 1 | exactly one executor |

### Topics

```
vue/v1/cmd                 → VC     QoS1  retain=false   ONE ordered stream
vue/v1/done                ← VC     QoS1  retain=false   (observability)
vue/v1/status/{authority,vc}         QoS1  RETAINED  (birth + LWT)
vue/v1/resync/{request,response}     QoS1  retain=false
uns/v1/smc/<station>/<component>/state  QoS0  RETAINED
```

**One command topic, not one per component.** The cell's causality is cross-component (0 ms apart, two topics); MQTT orders per-topic only. **Component is a field, not a topic level.** Secondary: VC 5.0 has **no wildcard subscriptions**, so per-component topics force hand-enumerated subscriptions.

> **Honest:** this is an **improvement over the socket, not fidelity to it.** `doDynamicStateEmulator` spawns *one BackgroundWorker per state change*, each independently enqueueing and sending — cross-component order on the socket is *at risk today*. It probably holds because the window is microseconds. **Do not justify the single topic as "matching the socket."**
>
> ⚠️ **Fragile invariant:** if anyone ever splits `vue/v1/cmd` per component "for cleanliness", ordering evaporates **with no error.** Document it at the topic.

### Schema (actuator / robot)

```json
{ "kind":"cmd", "cmd_id":"01JD3K7Q8XN4V2", "epoch":17, "seq":2841,
  "ts":"2026-07-16T09:14:22.317Z", "mode":"fire_and_forget",
  "component":{"vcid":"Feeder","type":"Actuator"},
  "transition":{"from_state":"AtHome","via_state":"Advancing",
                "to_state":"AtWork","to_state_id":"S-c9c8f259"},
  "motion":"move", "target":{"position":120.0,"operator":"equal"}, "speed":45.0 }

{ ... "component":{"vcid":"UR3e","type":"Robot"},
      "motion":"routine", "routine":"PickBearing", "speed":100.0 }
```

`motion ∈ move | routine | teleport`. **Joint poking is not representable — there is no joint field. The absence *is* the enforcement.**

### The five decisions that differ from the naive version

1. **`seq` is a single global monotonic counter on the command stream — not per-component.** Per-component `seq` stays perfectly contiguous through exactly the cross-component reorder the single-topic decision exists to prevent. It would be blind to the only failure worth detecting.
2. **Never reject a second command while EXECUTING — queue depth 1 per component.** Rejecting converts *"queued and late"* (today's behaviour, which works) into ***dropped***. Because `:676` reopens the latch on *enqueue*, the engine emits the next command ~100 ms later while VC is 6 s into a 7 s move — **the normal case.** The socket's queue **is** the mechanism that makes path B work. And VueOne could do nothing with a reject anyway: `:676` already committed, so a reject is silent divergence.
3. **`clean_start=true` on the command connection + explicit resync.** You cannot both sell *"delivery across an outage"* and discard the whole session queue on arrival. Pick one. Commands do **not** survive an outage; resync explicitly. That collapses the staleness layer to `epoch` + resync — simpler and honest — and it incidentally kills the *"cmd_id dedup lives in VC's RAM"* hole (a VC-only restart naturally receives nothing and asks for a resync).
4. **Resync is explicit — never "read retained `uns/v1`".** `{state:N}` has **no pose**, so that would require a state→pose table in the VC script (a second geometry source, drift-prone) — and in VC-only mode `uns/v1` **doesn't exist at all**, so the "recommended" recovery would resync against an empty namespace in the default configuration.
5. **Never publish from the UI thread.** Publishing inside `SetActuator` puts network I/O on the twin's WinForms message pump — with an off-box broker (the headline capability), a network stall **hangs the twin's UI**. Use one dedicated publisher thread with a bounded, ordered handoff; stamp `ts` at *enqueue*, in engine order, so it measures decision-to-render rather than broker latency. *(Note: whether `ReportProgress` posts arrive in emission order is a **WinForms implementation detail, not a contract** — see §8.5. This evaporates if a separate translator publishes.)*

**Commands are never retained.** A retained command is a stale command that executes on connect — `cmd` is bound to `IsTrigger=True` motion signals. On the rig-shadow path that is a physical-motion hazard; in VC-only it silently desynchronises the twin.

**QoS 1 on cmd/done/status; QoS 0 on telemetry; never QoS 2** — `cmd_id` gives exactly-once *semantically*; QoS 2's four-way handshake buys nothing extra.

### Cost vs the socket — honest

**Gains.** Reconnect that works — the socket has **none** (`AvoidDuplicateCon()` is `{ return; }`; `so` is a single field overwritten on re-accept; `Receive(StateObject soc)` **ignores its parameter** and uses the field, so an old connection's in-flight callback re-arms on the *new* socket). Delivery instead of dequeue-then-drop. Framing solved by construction. Many consumers free. Off-box (a *capability*, not an optimisation). Speed as a real number. **Lag measurable for the first time.**

**Costs.** An extra hop and a new SPOF — the failure mode goes from *"VC down"* to *"VC down **or** broker down **or** network down"*. Ordering becomes a constraint you must actively hold. **Duplicates become a spec, not a bug** (though the socket already delivers them today via `:748`/`:752`). Ops surface: ACLs, credentials, retained hygiene. **VC 5.0 MQTT is Professional/Premium only** — a procurement dependency the socket didn't have. And, decisively: **MQTT does not let you delete the VC-side script.** Native `Payload.state`→Integer pairing cannot consume this schema. It changes what the script reads *from*.

---

## 4. Recommended architecture — rig + VueOne + VC together

The decision axis is **not** socket vs MQTT. It is **who authors the VC command** — and it must be *something that reads Control.xml*, because the command exists only there.

Four cells; **three are already occupied**:

|  | socket transport | MQTT transport |
|---|---|---|
| **engine-authored** (interlock-checked) | path B — exists, free-running | not built |
| **rig-translated** (no interlocks) | **★** — exists, uninterlocked, racing | statesync `cmd` — exists, running |

This is not greenfield. It is a decision about which live path to delete.

### Two viable homes for the enrichment

**Home A — VueOne authors (★ made deliberate).** rig → statesync → MQTT → `VcMqttClient` → cycle-synchronous drain → `ChangeState` → `onStateReceived` → `SetActuator` → dynamic → **send, do not self-advance.**
- **Pro:** one source of the state→command table, live and in-process — cannot drift. VueOne already holds position and speed.
- **Con:** VueOne's socket is the weakest transport in the system (no reconnect at all), and VueOne sits in VC's critical path.
- **Requires:** split emission from self-advance; add coalescing.

**Home B — a translator authors (statesync).** rig → statesync (owns the Control.xml-derived table) → `vue/v1/cmd` enriched → VC executor.
- **Pro:** separate process; already reconnects; already drains a queue; already generates the table. VueOne stays a pure observer — one job, no cycle. Sidesteps the UI-thread publisher problem entirely.
- **Con:** the table is a *generated* second source and **can drift** — already documented live: `statesync.config.json` says broker `192.168.1.50`, the generated map says `127.0.0.1`, despite the file's own "do not hand-edit" banner.
- **Con:** today the translator publishes a *boolean*, not a command. The enrichment must be built.
- **Con (decisive, honest):** ***"it's already built" is UNPROVEN.*** The publisher side is verified in git; whether VC *consumes* it is GUI mapping config — **not in git, not verified**. The entire effort argument rests on it.

**Interlocks do not decide this.** Interlocks protect the *sequencer's* decisions. In rig mode the **PLC sequences and has its own ECC RuleTable**; VC is a renderer, and a collision in VC is a display artifact, not a hazard. VueOne's interlocks belong to the **conformance model**, not the command path. That removes the strongest-looking argument for Home A.

### Single writer per sink

| sink | one writer | everyone else |
|---|---|---|
| twin `CurrentState[0]` | VueOne engine (fed by rig observations) | — |
| VC joints | the executor script, fed by **one** command stream | — |
| `vue/v1/cmd` | the authoring component (A or B) | ACL-denied |
| `uns/v1/#` | statesync | ACL-denied |

**Edges to delete:** VueOne→VC socket (if Home B — a second writer on VC's joints); VC `complete` → `ChangeState` (a renderer writing into the model — **and a cycle**); statesync → VueOne socket 51000 (a second rig route). Today's graph has a cycle *plus* the uninterlocked echo. **Cycles plus multiple writers is exactly why it's a mess.**

**Provenance is currently inexpressible, not merely unimplemented.** Three transports bind to **one** handler:

```csharp
mcConHplr.EventFromVcPython += OnEventFromVc;   // VC
mcConHplr.EventFromVcCSharp += OnEventFromVc;   // statesync socket
mcConHplr.EventFromVcMqtt   += OnEventFromVc;   // rig MQTT
```

`VcComponentArg` **has** a `ClientId`. `OnEventFromVc` never reads it — and **both rig routes set it to `"VC"`** (`VcMqttClient.cs:150`; `statesync.config.json`). The one field that could carry provenance is falsified on both.

Fix it **structurally — unbind `EventFromVcCSharp` in code**, not by flipping `vueone.enabled: false`. That is configuration, and statesync has `_ensure()` reconnect plus `replay_snapshot` on connect: a stale config on any machine silently reintroduces the second writer *and replays a snapshot into it*.

**Honest limit:** this cannot remove VueOne's *internal* double-write, because both writers are inside VueOne — the emulator (`:676`, UI thread) and `OnEventFromVc` (`:2069`, IOCP thread) both mutate `mlCurrentState`, a plain **unlocked** `List<VOLogicEngineState>`, while the engine loop concurrently reads it. Nothing volatile, nothing locked. Transport-independent.

### Backpressure — answered mechanically

**Never block VueOne on VC.** The rig is physical; it does not wait. Blocking means diverging from the truth to stay synced with a renderer — and the rig's observations would then pile into `mqttQueue` and hit the 20,000 cap (oldest dropped). You'd lose rig truth to preserve VC smoothness. Backwards.

**Queue** (today's socket): lag grows linearly and unboundedly. After ten minutes you are watching ten-minute-old motion. Correct, and useless.

**Skip-to-latest** is legal **only because the command is an absolute target, not a delta** — `nState.Position` is a pose. Re-applying a target converges; re-applying a delta diverges.

But the two policies are **not interchangeable across component types**, and that is the mechanical answer:

| | cylinders | robots |
|---|---|---|
| command shape | absolute target — **idempotent** | routine invocation — **not** idempotent (calling `Partpick` twice grasps twice) |
| correct policy | **level** — latest wins, coalesce | **one-in-flight, queue depth 1** |
| failure if wrong | stroke silently invisible | double grasp / interleaved routines |

The lost-stroke cost is **verified**: `ServoController_Script_hardened.py` polls a level every 50 ms and `continue`s when `v == last_val`. If the level flips *and returns* during the blocking `moveJoint`, the loop resumes, sees no change, and **the entire stroke never renders.**

> **A shadow that must show every stroke and a shadow that must be current are different products.** You cannot have both while the producer outruns the consumer.

The way out is **not transport**. It is making VC's *cylinder* consumer **non-blocking** — drive an interpolator toward a target each tick instead of blocking inside `moveJoint`. Then VC never falls more than one tick behind and coalescing almost never triggers. Robots keep the blocking `callRoutine`, because you cannot interpolate a taught program — and that's fine: robot commands are rare (3 states) and the routine's duration *is* the truth. **That is where the effort should go.**

### What `complete` is for now

It never gated anything: `ResolveIncomingState` does a case-insensitive **name** match against the state `:676` already committed, then re-nulls `PreviouExecState`. A redundant second write.

Its only honest job is **liveness and lag telemetry**:
- Un-hardcode `MsgAckNo`. The mechanism exists and `RF Learning.cs:415/436/441` uses it correctly (*"Action {n} Sent to VC"* / *"Wating Responce for {n}"* / *"Action {n} Complete Recieved from VC"*). **We are filling a hole the codebase already shaped.**
- VueOne computes `now − t_sent(id)` = **VC render lag** — a number nobody has ever measured.
- Absence of completes = VC crashed. Today that is silent.
- **It must not call `ChangeState`.**

> **The trap:** if you delete the socket, you delete this. With VC consuming retained levels, **a hung VC is invisible** — topics keep updating, VC freezes, nothing notices. Path B is *observable-but-broken*; MQTT-only as usually drawn is **unobservable**. A VC→broker `done` publish is **not optional and not free** — cost it into Home B.

### What VueOne is *for*

Once the PLC sequences, VueOne is **not a sequencer**. Its remaining value: the STD view, and **conformance monitoring** — telling you where the twin is wrong.

Today disagreement is **invisible by construction**: `ChangeStateTo(null, dest)` teleports with no check. The twin silently accepts whatever the rig says, so you can never learn the model is wrong. That is the most valuable thing VueOne could be doing in rig mode, and it is discarded.

The check needs **two** predicates, not one:
1. **Reachability** — is `dest` reachable from `CurrentState[0]` within N transitions? Reachable ⇒ intermediates skipped (benign: transient overwrite — your own `AtHomeEnd=4→0` case — or transport coalescing, or dup-drop). Unreachable ⇒ real divergence.
2. **Static/dynamic** — the rig parking the twin on a *dynamic* (command-carrier) state is **always** a model note, independent of reachability. Reachability alone would rate `Advanced → Advancing` as benign; it isn't — it's the ontology mismatch surfacing.

**Policy: follow the rig, log loudly.** Refusing would make the twin wrong about a world that has already moved on. An unlogged teleport is a lost defect.

---

## 5. Minimal pilot

Ordered so the cheapest test that can **overturn the plan** runs first. No code decisions yet.

| # | test | cost | what it can overturn |
|---|---|---|---|
| **P0** | **Does VC consume the MQTT command path at all?** Stop statesync's socket leg, leave MQTT up, drive one Feeder stroke from the rig, watch the Pusher joint. Also: is VC double-written *right now* (native mapping **and** VueOne2VC socket client both active)? | ½ d | **Everything.** If it doesn't move, "already built" is false and the effort comparison inverts. Two writers on Pusher's joint would explain erratic behaviour on its own — possibly the whole bug, free to find. **Non-negotiable first.** |
| **P1** | **Is ★ real and load-bearing?** `VODebugTraceLogger` already traces every send. Drive one rig stroke; count `SendToVcPython` calls and origin. Predictions: (a) sends occur via `onStateReceived`; (b) count varies run-to-run (the race); (c) **stub ★ → sends drop to zero.** | ½ d | (c) is a one-line experiment that validates or refutes the single most load-bearing structural finding here. |
| **P2** | **Measure the lag nobody has measured.** Un-hardcode `MsgAckNo`; log `t_sent`/`t_complete`; run path B alone for one cycle. Separately: timestamp VueOne's state changes against VC's command **arrival**. | 1 d | Whether the problem is real. Note `cycle_times_*.json` **cannot** show this — `_cycleStart` is stamped at parse time, immediately before the blocking move, so per-move durations stay accurate even if the whole batch arrived at once. |
| **P3** | **Why are the 8 July runs empty?** Three consecutive `{"cycles": []}` where 23 June recorded 30–33. | ½ d | The premise of everything. Until explained, "path B works" is *June* evidence. Resolve before interpreting P0–P2. |
| **P4** | **Enrich one component end-to-end.** Feeder (a cylinder — the case `cmd` gets right). Real command (target + speed) on `vue/v1/cmd`; VC script calls `moveJoint`; compare side-by-side against path B. Success is **not** "it moves" — it is *same duration, same profile, and a `done` that lets you measure lag*. | 2–3 d | Whether the contract in §3 is sufficient. |
| **P5** | **Force the backpressure decision.** Instrument `ServoController` to count `v == last_val` skips during a fast cycle. | 1 d | Whether the VC-side interpolator is optional or **mandatory**. |

---

## 6. Reuse (specific)

- **`VOLogicEngine`** — the four-condition emission rule and the `PreviouExecState` latch. **The latch is already the backpressure primitive; `:676` is the only thing cheating it open.**
- **`ConnectionHelper.DrainMqttQueue` + `VOLogicEngine.CycleTick`** — cycle-synchronous drain at the head of the cycle, `ConcurrentQueue` capped at 20,000, on the worker thread. **Genuinely well built.** Keep as the inbound rig path.
- **`VcMqttClient`** — the rig→VueOne ingress.
- **`MsgAckNo` on `VcComponentArg` + `RF Learning.cs:415/436/441`** — the correlation mechanism *was built* and the simulator path opted out. **Revive it; don't invent one.**
- **`VODebugTraceLogger` + `FormDebugConsole`** — already traces both directions on every send/receive. Usable as-is for P1/P2.
- **`statesync.py`** — the drain, dedupe, `seq`/`epoch`/`ts` envelope, `replay_snapshot`. It generates exactly the fields the mapping layer throws away. **The bridge is careful and well-built; the contract it was asked to satisfy is unsatisfiable.**
- **`gen_sync_map.py` / `extract_vc_positions.py`** — the Control.xml→table derivation. Right idea; it must be the **only** one, not a shadow of VueOne's in-process copy.
- **`control.moveJoint` / `executor.callRoutine` / `SignalActions`** — all of VC's motion. Never replace with property pokes. `robot_executor.IsEnabled = False` is **deliberate and correct** (it stops the taught main program free-running alongside external commands).
- **`ServoController_Script`'s `PushSpeed → OnChanged → MaxSpeed` binding — DO NOT DELETE.** `MoveActuator` bypasses the script's queue but **depends on the script's module-level `OnChanged` registration.** The *property* persists in the saved `.vcmx`; the *callback* does not. Delete the script and `getProperty('PushSpeed')` still returns an object, `if pushSpeed:` passes, you write a number **nothing reads**, and speed control fails as a **silent no-op.** *(This refutes the analysis's §7 "retire ServoController_Script_hardened".)*

---

## 7. Retire (specific)

- **`FormSimulationView.cs:676` self-advance** — the single most consequential line. It is why `done` gates nothing and why VueOne free-runs. **`DynamicStateEmulatorOn = false` is NOT the fix** — it deletes `:659`, the only `SendToVcPython` call site, and VC goes silent. **Split emission from advance.**
- **`FormSimulationView.cs:649-653`** — recomputes `nState` by looping **all** transitions with **no break** (takes the *last*) while `:676` advances via `Transitions[0]`. They disagree the moment a dynamic state has >1 transition: VC is commanded toward one target while the twin advances to another. The author's own `//neeed to review` marks the spot.
- **`syncSocket.cs:919-921 AvoidDuplicateCon()`** — `{ return; }`. **There is no reconnect.** Plus: `so` is a single field overwritten on re-accept; `Receive(StateObject soc)` **ignores its parameter**; a receive error at `:704-706` returns **without re-arming** → that connection stops receiving permanently.
- **`syncSocket.cs:828-846 OnSendCallBack`** — entire body commented out **including `EndSend`**. Every `BeginSend` unreaped; `Sent()` never fires.
- **`syncSocket.cs:781-802 Send()`** — `return true` inside the `while`; and a command sent while VC is disconnected is **dequeued then silently discarded**.
- **`syncSocket.cs:748` vs `:752`** — whole buffer to the handler, first frame only consumed → duplicate delivery on coalesced reads.
- **VueOne→VC raw-JSON send with no delimiter** — the reason the VC script must regex-scrape.
- **The VC script's `recv(1024)` + `re.findall(r'\\{.*?\\}')` with no accumulator** — a boundary split loses **both halves**. Also IronPython-2.7-only (`str is bytes`). And its `except` sits **outside** the `for msg in msgs` loop → one bad message **aborts the whole batch** (`[good, bad, good]` → the third is silently dropped despite being intact).
- **`MoveRobot`'s unconditional `SendEvent('complete')` outside its `try`** — a routine that throws still reports complete.
- **`MoveActuator`'s `SendEvent` inside the joint-match branch** — no match ⇒ no completion.
- **`GRIP_CMD_STATES = {"Swivel Arm": (3, 4)}`** — **inverted.** Rig-proven `(1,2)`: the part leaves BearingSensor at 219.57 and the only actuation before it is gripper 1→2 at 218.25. Measured windows: `(3,4)` → 120/60 ms (that's the **release** pulse); `(1,2)` → 1480/1340 ms (grip at pick, hold through carry, release at place). `shaft_gripper`, correctly `(1,2)`, measures 2960/3010 ms. **The VC bearing gripper closes exactly when the rig opens.**
- **`vc_robot_driver.py` (`EASE`/`TICK`/`p.Value`)** — last-target-wins. Verified: `Swivel Arm.J1 → 0.0` then `→ 90.0` **10 ms apart**, less than one 30 ms tick; the Place pose is **never rendered**.
- **Retained `cmd`** — statesync's unconditional `retain=True` on a field bound to `IsTrigger=True` motion signals.
- **statesync's `vueone` socket leg (51000)** — unbind `EventFromVcCSharp` **in code**.
- **VC `complete` → `ChangeState`** — the renderer writing into the model; also the cycle.
- **`Debug.Print("...", ex.Message)`** at five sites — binds the format overload with no placeholders; **the exception is never printed.**

---

## 8. Assumptions that must be tested

Each labelled by evidence class and by what it would overturn.

1. **VC consumes the MQTT command path.** *(unverified — GUI mapping, not in git.)* Overturns the entire "already built / low effort" argument. → **P0**
2. **VC is not double-written today.** *(unverified.)* If both the native mapping and the socket client are live, VC's joints have two writers **right now** — possibly the whole bug. → **P0**
3. **The engine cannot author commands in rig mode** (the rig consumes the transition; ★ is the only send path). *(inference from source; confirmed by review; **not executed**.)* Overturns the architecture choice. → **P1**
4. **Path C is only sensor-gated as shipped.** *(inference: follows from `:676` + the 100 ms cycle + sensor states being static so the emulator never fires — never observed.)* If true, rig *actuator* observations are decorative and only the sensor subset gates. → **P1/P2**
5. **`ReportProgress` posts arrive in emission order.** *(framework assumption — `Post` is async; serialisation is real, **order is not contracted**.)* A UI-thread publisher's entire ordering guarantee rests on it. Moot under Home B.
6. **The socket delivers total cross-component order.** *(contradicted by its own mechanism — one BackgroundWorker per state change, N concurrent.)* Probably holds because the window is microseconds. **Making it guaranteed is an improvement, not fidelity.**
7. **VC tolerates a real (non-integer) speed.** *(untested.)* `Convert.ToInt32` rounds — any speed < 0.5 becomes 0. But if the script does `int(payload['speed'])` or feeds an integer-typed property, `45.7` may throw or truncate differently. **Test before claiming the fix.**
8. **`cmd_id` dedup survives a VC restart.** *(it doesn't — the tables are in VC's RAM.)* Mitigated by clean-session + explicit resync. **Do not claim "exactly-once for free".**
9. **`IsTrigger` writes the value but doesn't fire the signal event.** *(inference from code + the existence of the hardened workaround — **never observed** in VC's mapping runtime.)* This is the feeder trigger bug, and it is **independent** of the robot teleport. Load-bearing and unproven.
10. **The Servo Controller / ServoController_Script are enabled.** `vc_detail.json` reports `"Enabled": "False"` on **both** behaviours of PusherNode. If faithful, that is an **alternative root cause** for the feeder failure: a disabled script never runs its module-level code, so `PushSpeed` is never created and the binding never registers. **Check before accepting #9.**
11. **The stock ServoController_Script is identical on every cylinder.** *(recovered from one probe dump, not a live `.vcmx`.)* Also latent: `changeSpeed` hardcodes `servo.Joints[0]` while `moveJoint` uses the name-matched index — safe only for single-joint cylinders. And the vendor's own bug: it writes the **speed** value into **MaxAcceleration**.
12. **Cross-topic reordering has actually occurred.** *(proven **possible and undetectable**; never observed — with 0 SimulationToServer rows, VC reports nothing.)* **The asymmetry is the point: the architecture cannot distinguish "never happened" from "happened and nobody noticed."**
13. **The retained-command actuation hazard has fired.** *(established from configuration — retain=True + cmd bound to motion triggers + 73 reconnects + 25-min staleness measured — not caught in the act.)* The CSV path leaves **no log at all**.
14. **The 8 July empty runs.** *(unexplained.)* Three consecutive `{"cycles": []}`. Until resolved, "path B works" is **June** evidence.
15. **The Swivel Arm gripper routines execute taught motion.** `CloseGripper` measures **3 ms** (n=18), `OpenGripper` 30 ms — vs `pnp CloseGripper` 971 ms, `coverpnp grasp` 969 ms. Sub-10 ms suggests those routines are **empty or signal-only**. Candidate real defect; cannot distinguish "empty routine" from "instant signal set" without opening the layout.
16. **Smoothness itself.** *(testimony — no video, screenshot or frame-timing artifact exists anywhere.)* "Not teleporting" is proven; "smooth" is inferred from the primitive plus tight low-variance durations.
17. **The VC-side mapping is reproducible.** *(it isn't — GUI config, not in git.)* Home B leans harder on it. **Export and version-control it before committing, or you've traded a bad transport for an unreproducible one.**
18. **The 41% / 25% cross-topic figures** come from **one cycle** (`rig_watch.log`, 159 events). `rig_cadence.log` (27 cycles) corroborates the duration spread but not the adjacency.
19. **The duplicate-delivery path** (`:748`/`:752`). Proven by construction, not traced live; needs two EOM frames in one recv, which `delay(.2)` makes rare now and **likely at rig pace**. Currently masked because `ChangeState` is idempotent-ish — but it also **re-nulls `PreviouExecState`**, re-arming the component. Whether that can cause a duplicate emission is untraced.
20. **`moveJoint`'s profile is trapezoidal.** *(inference from MaxSpeed + MaxAcceleration + MaxDeceleration; `api.xml` documents the parameters, not the curve.)* Blocking and the parameter source are proven; the curve is not.

---

### The whole thing in five lines

1. Smoothness is **VC's**, not the socket's. The socket ships a target and a number; VC's interpolator and taught routines do the rest.
2. MQTT fails because we ship an **observation** (2.6 bits) where a **command** (192 bits) is required, then rebuild the command from a table generated from the twin we already had.
3. Path B works because nobody waits for VC. **The rig destroys that** — VC's ceiling is ~0.12 cmd/s during a robot move against rig bursts at 0 ms.
4. So: *something must read Control.xml and turn `{state:N}` back into a command* (two candidate homes, one untested premise), and *you must choose current-vs-complete* (level for cylinders, one-in-flight for robots) — because you cannot have both while the producer outruns the consumer.
5. And the real fix for the pacing is **VC-side and non-blocking**, not broker tuning. **Run P0 and P1 first; either can overturn the plan for a day's work.**"
  },
  "workflowProgress": [
    {
      "type": "workflow_phase",
      "index": 1,
      "title": "Mechanism"
    },
    {
      "type": "workflow_phase",
      "index": 2,
      "title": "Design"
    },
    {
      "type": "workflow_phase",
      "index": 3,
      "title": "Review"
    },
    {
      "type": "workflow_phase",
      "index": 4,
      "title": "Synthesize"
    },
    {
      "type": "workflow_agent",
      "index": 1,
      "label": "mech:emission-and-sequencing",
      "phaseIndex": 1,
      "phaseTitle": "Mechanism",
      "agentId": "ab1f14aea547e18e7",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784236840888,
      "queuedAt": 1784236833802,
      "attempt": 1,
      "lastToolName": "StructuredOutput",
      "lastToolSummary": "VueOne STD emission path to Visual Components — trigger sem…",
      "promptPreview": "FIRST read C:\\VueOneMapper\\Docs\\VC_SHADOW_ARCHITECTURE_ANALYSIS.md for established context (do not redo it; build on it).

Then answer MECHANISTICALLY, with file:line, from C:\\V-Dev\\VueOneVcVersion\\VueOneVcVersion\\vueone_vc (vueOneSystem, vueOneLogicEngine, wntovc/VueOne2VC):

1. EMISSION: exactly what makes VueOne decide to send a VC command? Trace: VOLogicEngine.mcWorker_DoWork -> ReportProgress…",
      "lastProgressAt": 1784237610640,
      "tokens": 390620,
      "toolCalls": 27,
      "durationMs": 769752,
      "resultPreview": "{"area":"VueOne STD emission path to Visual Components — trigger semantics, sequencing guarantee, concurrency model, and socket transport. Sources: C:\\\\V-Dev\\\\VueOneVcVersion\\\\VueOneVcVersion\\\\vueone_vc\\\\Development\\\\ (vueOneLogicEngine, vueOneSystem, wntovc/VueOne2VC, vueOneData).","mechanism":"EMISSION TRIGGER. VOLogicEngine.mcWorker_DoWork (VOLogicEngine.cs:251) loops at 10 Hz (Thread.Sleep(100…"
    },
    {
      "type": "workflow_agent",
      "index": 2,
      "label": "mech:vc-execution-and-smoothness",
      "phaseIndex": 1,
      "phaseTitle": "Mechanism",
      "agentId": "aeb8e75db6daf6cb8",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784236843811,
      "queuedAt": 1784236833803,
      "attempt": 1,
      "lastToolName": "StructuredOutput",
      "lastToolSummary": "VC 5.0 motion mechanics: attribution of "smoothness" among …",
      "promptPreview": "FIRST read C:\\VueOneMapper\\Docs\\VC_SHADOW_ARCHITECTURE_ANALYSIS.md for context. Then read C:\\Users\\alper\\.codex\\attachments\\fe815326-4056-4739-b7a1-a5ad1ee6f2e5\\pasted-text.txt (the working VC dummy script) and the VC 5.0 API docs (search for api.xml under the Visual Components install, e.g. C:\\Program Files\\Visual Components).

Answer MECHANISTICALLY with line/doc evidence — WHERE DOES SMOOTHNESS…",
      "lastProgressAt": 1784237553592,
      "tokens": 353272,
      "toolCalls": 20,
      "durationMs": 709781,
      "resultPreview": "{"area":"VC 5.0 motion mechanics: attribution of \\"smoothness\\" among VueOne sequencing, VC native moveJoint/callRoutine, taught routines, and speed values — plus completion semantics and receive-path fragility in the VC dummy bridge script (pasted-text.txt).","mechanism":"SMOOTHNESS IS ENTIRELY VC'S. VueOne contributes a destination + (cylinders only) a speed scalar, and a serialisation guarantee…"
    },
    {
      "type": "workflow_agent",
      "index": 3,
      "label": "mech:mqtt-forensics",
      "phaseIndex": 1,
      "phaseTitle": "Mechanism",
      "agentId": "a2df234f925c8fd20",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784236838520,
      "queuedAt": 1784236833803,
      "attempt": 1,
      "lastToolName": "StructuredOutput",
      "lastToolSummary": "Why OUR MQTT usage is structurally inadequate for driving V…",
      "promptPreview": "FIRST read C:\\VueOneMapper\\Docs\\VC_SHADOW_ARCHITECTURE_ANALYSIS.md for context. Then examine C:\\VueOneMapper\\Tools\\statesync (statesync.py, VcMqttFeedMappings.csv, sync-map.generated.json) and C:\\VueOneMapper\\MQTT\\mosquitto.log + rig_cadence.log.

Explain PRECISELY why OUR MQTT USAGE is weak for driving VC (not why MQTT is bad generally):
1. The rig payload contract: {state:N}, 9 bytes, retain fla…",
      "lastProgressAt": 1784237368260,
      "tokens": 359660,
      "toolCalls": 18,
      "durationMs": 529740,
      "resultPreview": "{"area":"Why OUR MQTT usage is structurally inadequate for driving Visual Components — payload contract, VC Mapping Control limits, retain, reconnect state-loss, QoS, cross-topic ordering, and the cmd boolean. Evidence: MQTT/mosquitto.log (443,153 lines / 40 MB), MQTT/rig_cadence.log (27 cycles), Tools/statesync/{statesync.py, VcMqttFeedMappings.csv, sync-map.generated.json, rig_watch.log, vc_shad…"
    },
    {
      "type": "workflow_agent",
      "index": 4,
      "label": "mech:claim-audit",
      "phaseIndex": 1,
      "phaseTitle": "Mechanism",
      "agentId": "a27519b4848cbf6a2",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784236839579,
      "queuedAt": 1784236833803,
      "attempt": 1,
      "lastToolName": "StructuredOutput",
      "lastToolSummary": "Validation of the 15 claimed strengths/weaknesses of the Vu…",
      "promptPreview": "FIRST read C:\\VueOneMapper\\Docs\\VC_SHADOW_ARCHITECTURE_ANALYSIS.md for context. Then VALIDATE or REFUTE each of the user's stated strengths/weaknesses of the socket path, individually, with file:line evidence. Mark each CONFIRMED / REFUTED / PARTLY / UNPROVEN and explain.

STRENGTHS claimed: (1) command-level data; (2) VC native motion; (3) taught routines; (4) completion events; (5) existing work…",
      "lastProgressAt": 1784237428969,
      "tokens": 387891,
      "toolCalls": 29,
      "durationMs": 589390,
      "resultPreview": "{"area":"Validation of the 15 claimed strengths/weaknesses of the VueOne→VC socket path (path B/C), against C:\\\\V-Dev\\\\...\\\\vueone_vc source, the VC dummy script (pasted-text.txt), the vc2vueone plugin, and run artifacts in OneDrive\\\\Masaüstü\\\\Output","mechanism":"MECHANISM FROM FIRST PRINCIPLES (and the one place the analysis doc is flatly wrong).\
\
TRANSPORT. VueOne is the LISTENER, VC is the c…"
    },
    {
      "type": "workflow_agent",
      "index": 5,
      "label": "design:mqtt-command-contract",
      "phaseIndex": 2,
      "phaseTitle": "Design",
      "agentId": "ac28232a8ced739f1",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784237613758,
      "queuedAt": 1784237611992,
      "attempt": 1,
      "promptPreview": "Design an MQTT contract that could match the VueOne->VC socket path's behaviour. This is the user's KEY question. Do NOT propose Sparkplug.

Established mechanism (respect it):
[{"area":"VueOne STD emission path to Visual Components — trigger semantics, sequencing guarantee, concurrency model, and socket transport. Sources: C:\\\\V-Dev\\\\VueOneVcVersion\\\\VueOneVcVersion\\\\vueone_vc\\\\Development\\\\ (vue…",
      "lastProgressAt": 1784237894657,
      "tokens": 289154,
      "toolCalls": 0,
      "durationMs": 280899,
      "resultPreview": "# MQTT contract for the VueOne → VC command path

Design derived from the socket path's actual behaviour, not from what the docs claim it does. Every decision below is justified against a specific mechanism site.

---

## 0. The property being preserved

The socket's real guarantee is **order, not time**: one command in flight per component (the `PreviouExecState` latch, `VOLogicEngine.cs:278`/`:3…"
    },
    {
      "type": "workflow_agent",
      "index": 6,
      "label": "design:three-way-architecture",
      "phaseIndex": 2,
      "phaseTitle": "Design",
      "agentId": "a075ce60809abe3d9",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784237613541,
      "queuedAt": 1784237611992,
      "attempt": 1,
      "lastToolName": "Bash",
      "lastToolSummary": "cd "C:/VueOneMapper/Tools/statesync"; echo "=== FEED_CMD_VC…",
      "promptPreview": "Design and HONESTLY compare the architecture for running rig + VueOne STD + Visual Components SIMULTANEOUSLY.

Established mechanism (respect it):
[{"area":"VueOne STD emission path to Visual Components — trigger semantics, sequencing guarantee, concurrency model, and socket transport. Sources: C:\\\\V-Dev\\\\VueOneVcVersion\\\\VueOneVcVersion\\\\vueone_vc\\\\Development\\\\ (vueOneLogicEngine, vueOneSystem, …",
      "lastProgressAt": 1784238192392,
      "tokens": 347426,
      "toolCalls": 29,
      "durationMs": 578850,
      "resultPreview": "I verified the mechanism against source rather than trusting the summary. The handed-over mechanism is **correct** on every load-bearing point I checked — including its correction of the prior doc. But I found two things that change the shape of the problem, and one of them means the option list you gave me is mis-framed.

## Verified (confirming your mechanism)

`Emulator_DoWork` (send, `FormSimu…"
    },
    {
      "type": "workflow_agent",
      "index": 7,
      "label": "review:0",
      "phaseIndex": 3,
      "phaseTitle": "Review",
      "agentId": "a96f56abe55d4d365",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784238196143,
      "queuedAt": 1784238194120,
      "attempt": 1,
      "promptPreview": "You are a hostile principal engineer reviewing this design. Find what is WRONG, hand-wavy, unproven, or would fail in the field. Be specific and constructive. Do not rubber-stamp; if a claim rests on an untested assumption, name it as an assumption that must be tested.

Established mechanism/context: [{"area":"VueOne STD emission path to Visual Components — trigger semantics, sequencing guarantee,…",
      "lastProgressAt": 1784238311481,
      "tokens": 293537,
      "toolCalls": 0,
      "durationMs": 115338,
      "resultPreview": "Below is the review. I've read the mechanism dossier carefully and cross-checked the design's claims against it.

## (1) The five most serious problems

**A. §5 and §7's `expires_at` are load-bearing on wall-clock, and §5's chosen publish site makes the TTL unusable.**

You publish from the UI thread inside `SetActuator`. The UI thread is a WinForms message pump — it is not a real-time thread, and…"
    },
    {
      "type": "workflow_agent",
      "index": 8,
      "label": "review:1",
      "phaseIndex": 3,
      "phaseTitle": "Review",
      "agentId": "abda5c96a4d448868",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784238196630,
      "queuedAt": 1784238194121,
      "attempt": 1,
      "lastToolName": "Bash",
      "lastToolSummary": "cd "C:/VueOneMapper/Tools/statesync"; echo "=== ServoContro…",
      "promptPreview": "You are a hostile principal engineer reviewing this design. Find what is WRONG, hand-wavy, unproven, or would fail in the field. Be specific and constructive. Do not rubber-stamp; if a claim rests on an untested assumption, name it as an assumption that must be tested.

Established mechanism/context: [{"area":"VueOne STD emission path to Visual Components — trigger semantics, sequencing guarantee,…",
      "lastProgressAt": 1784238464008,
      "tokens": 317191,
      "toolCalls": 21,
      "durationMs": 267378,
      "resultPreview": "I verified the review's claims rather than trusting them. **Every structural assertion it makes is true** — including the ★ finding, which is real and which I confirmed end-to-end. But verification surfaced a defect in its central artifact: the comparison table that drives the decision.

---

## 1. The five most serious problems

### 1.1 Option 1 isn't "high effort" — it's incoherent in rig mode, …"
    },
    {
      "type": "workflow_agent",
      "index": 9,
      "label": "final (retry 1)",
      "phaseIndex": 4,
      "phaseTitle": "Synthesize",
      "agentId": "a97eff7a6a2a75c53",
      "model": "claude-opus-4-8",
      "state": "done",
      "startedAt": 1784238467963,
      "queuedAt": 1784238465406,
      "attempt": 2,
      "lastAttemptReason": "stalled",
      "promptPreview": "Write the final engineering investigation for a WMG automation lead. He asked for MECHANISM, not implementation, and explicitly rejected "path C exists / click the button" as an answer. No code. No Sparkplug. No new VC polling script.

HIS CORE QUESTION: Why does VueOne -> C# socket -> VC python script -> VC model work smoothly end-to-end, while running rig + MQTT + VueOne + VC does not? And: how …",
      "lastProgressAt": 1784240114232,
      "tokens": 346639,
      "toolCalls": 0,
      "durationMs": 1646269,
      "resultPreview": "# Why the socket path is smooth and the MQTT path is not

**Scope:** mechanism, from source and logs. No implementation. Evidence is labelled: *proven* (source/log/artifact), *inferred* (follows from source but not executed), *testimony* (asserted, no artifact).

---

## 1. Root cause — mechanism first

### 1.1 The one-sentence answer

**The socket carries a command. MQTT carries an observation. W…"
    }
  ],
  "totalTokens": 3085390,
  "totalToolCalls": 144
}