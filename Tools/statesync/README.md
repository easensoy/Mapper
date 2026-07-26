# SMC rig -> Visual Components digital shadow

The physical EAE-controlled rig is the source of truth. This turns its live MQTT state
stream into motion in a Visual Components 5.0 model, in real time, one writer per
mechanism.

## Four runtime files, nothing else

| File | Runs where | Owns |
|---|---|---|
| `statesync.py` | external CPython, alongside the broker | all rig semantics |
| `shadow-config.json` | read by `statesync.py` only | the rig description |
| `vc_gateway.py` | pasted into VC component `0 #2` (CPython 2.7) | all VC execution |
| `VcMqttMappings.csv` | imported into VC once | the two mailbox rows |

Why two executables and not one: the VC API (`setJointTarget`, `callRoutine`,
SignalActions) exists only inside a `vcScript` behaviour, which runs only while the
simulation runs; the broker side needs a durable client that survives Stop/Reset/Play.
Two different lifetimes, so two files. That is the floor, not a preference.

**The gateway holds no rig knowledge.** No component names, positions, durations, lanes,
routine names or grasp specs — all of it travels in the command envelope. That is what
keeps the pasted script stable: a rig, twin or measurement change never re-pastes it.

## Flow

```
PLC --smc/#--> statesync --+--> uns/.../<station>/<component>/state   retained, QoS1
                           |     the observation stream (dashboards, VueOne)
                           |
                           +--> uns/.../vc/command                    volatile, QoS1
                                       |
                                  [VC native MQTT]  ONE subscription
                                       v
                                  CommandJson -> vc_gateway (20 ms tick)
                                       |
                                  StatusJson -> uns/.../vc/event       volatile, QoS1
                                       |
                                       +--> statesync (ready -> resync, failures logged)
```

## Three execution contracts

| Contract | Mechanisms | How |
|---|---|---|
| `signal` | Pusher, Checker, Transfer, Clamp, Ejector | shape the servo (speed/accel solved from stroke + measured duration), then set the component's own `PushJoint_ActionSignal`. The stock `ServoController_Script` sweeps the joint — genuinely swept motion, never `moveImmediate`. |
| `axis` | bearing swivel, shaft H/V, cover H/V | 20 ms `setJointTarget` + **one** `moveImmediate` per controller, so shared-controller axes stay coherent. |
| `routine` | bearing / shaft / cover grippers, UR3e | `callRoutine(r, False)` — never blocking. Completion is *observed*: `OnScopeExecuted`, or a start-latched idle transition, or positive parent-chain evidence. Never assumed. |

`execution` is structural, not a preference: a binary `PushJoint` servo is `signal`, a
multi-position controller is `axis`, an executor with taught routines is `routine`.

## Why nothing blocks

A blocking `callRoutine` suspends the whole coroutine, including the axis interpolator in
the same loop. Measured on the old gateway: a 6939 ms UR3e `Partpick` stretched an
unrelated 2009 ms Ejector stroke to 8957 ms, then teleported it. Here a routine is an
in-flight item like any other and the tick keeps running.

`CurrentStatement` is **not** a completion test on its own — it also reads `None` before a
routine starts and when the executor is disabled (`IsEnabled` is `False` in this model).
It is consulted only after execution has been latched as started.

## Resync

The gateway emits `ready` on Run and Reset. `statesync` replays a snapshot per mechanism:

- cylinders and axes — absolute state, `durationMs: 0` (snap, no animation)
- grippers — **verify only**, against the last grasp/release actually commanded; publishes
  `desync` on a parentage mismatch and never replays a grasp
- robot — never replayed; resyncs on its next live transition

Commands are non-retained by design (a retained command would re-execute on every VC
connect), so resync is explicit rather than accidental.

## Tests

```bash
python tests/test_shadow.py     # 45 checks: gateway on a mock VC runtime + statesync
python tests/test_replay.py     # 10 checks: a recorded rig cycle end to end
python tests/check_live.py      # broker acceptance, after a live cycle
```

`tests/mock_vc.py` models the VC API surface the gateway uses, on a virtual clock, so
results are deterministic — no simulator, no broker, no sleeps.

## Deploying to VC

1. Paste `vc_gateway.py` into the Python Script behaviour of component `0 #2`.
2. Delete every existing MQTT mapping, then import `VcMqttMappings.csv` (CSV import is
   additive — it will not remove stale rows for you).
3. Set `vc/command` to **SubscribeOnly** and `vc/event` to **PublishOnly**.
4. Save the `.vcmx`, and untick *Auto Remove Pairings*.
5. `python tests/check_live.py` after a cycle to confirm VC holds exactly one subscription.

## Do not

- Regenerate positions from `Control.xml`. `Bearing_PnP` re-uses `State_Number 0` five
  times, so a naive extract swaps home and work and drops states 5/6. The values in
  `shadow-config.json` are hand-verified against VC's joint limits.
- Flatten the swivel's phase-keyed durations. Assembly and disassembly strokes do not
  overlap and are inverted (441 vs 1260 ms on the same transition).
- Derive sensor polarity from the twin. Bearing and shaft part-present DIs are active-low
  on the physical rig.
- Add a second per-actuator MQTT mapping. That is the duplicate-writer defect this
  architecture exists to remove.
