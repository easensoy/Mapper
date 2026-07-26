# Virtual shadow of the SMC rig in Visual Components — state of the work, for review

## What we're trying to do

We have a physical SMC assembly rig driven by three PLCs (M262 feed station, M580
assembly/disassembly, BX1 covers) running IEC 61499. Every actuator's ECC publishes its
current **state integer** to a local Mosquitto broker:

```
topic:   smc/<component>          e.g. smc/feeder, smc/bearing_pnp
payload: {state:N}                NOT strict JSON (unquoted key), N is the ECC state
```

Goal: a **Visual Components 5.0 (Premium OLP) digital shadow** that visually mimics the rig —
the VC robots/cylinders/grippers move as the rig moves. The rig is the source of truth; VC is a
read-only follower. No VC→rig direction.

Hard constraints the user set:
- Prefer **native VC MQTT mapping** (Connectivity → MQTT → Variable Mapping) over pasting Python
  into VC. An external Python process driving VC through the .NET API was explicitly rejected and
  deleted (`vc_shadow.py`). Config/CSV preferred.
- No teleporting, no synthetic timers/last-target-wins, no dual writers.

## The rig data is COMPLETE and CORRECT (proven)

Captured one full clean cycle off the broker (timestamps in seconds). Every actuator publishes a
clean state cycle:

```
feeder        1→2→3→4→0        (advance→return)
checker       1→2→3→4→0
transfer      1→2→3→4→0
clamp         1→2 ... 3→4→0
bearing_pnp   1→2→3→4→5→6→0    (swivel: toWork1→atPick→turnPlace→place→toHome→atHome→home)
bearing_gripper 1→2→3→4→0
shaft_hr / shaft_vr / shaft_gripper   full cycles
coverpnp_hr / coverpnp_vr / coverpnp_gripper   full cycles
ejector       1→2→3→4→0
robot (UR3e)  1→2→0            (home→pick→place→home)
sensors: PartInHopper, PartAtAssembly, BearingSensor, ShaftSensor, TopCoverSenosr (0/1)
```

Per-move durations are visible in the log (e.g. swivel home→pick ≈0.5 s, pick→place ≈1.4 s). So
the rig stream is not the problem — everything is published, cleanly, with timing.

## The bridge — statesync.py (Python, paho-mqtt)

Subscribes `smc/#`, dedups, republishes normalized **retained** UNS JSON
(`uns/wmg/smc_rig/v1/<station>/<component>/state`) and adds fields VC can map:

- `cmd` (bool) for cylinders + grippers — advance/close = true.
- `present` (bool) for sensors.
- `position` (Real) for robot joints — per-state joint angle from `vc-positions.json`, keyed by a
  "vcId" that equals the position-table key.
- Also writes `vc-live-positions.json` = `{ "Swivel Arm": {"J1": 181}, "pnp": {"Z":130,"X":-34}, ... }`
  for the robot driver (see below).

Component map (`sync-map.generated.json`) is generated from Control.xml; the bridge itself is
generic. It also pushes to a VueOne STD socket (127.0.0.1:51000) — a separate follower, irrelevant
to VC. (VueOne separately has its own "Connect SMC rig" button that subscribes to the broker
directly.)

## What works via native VC MQTT mapping (the CSV)

VC's Variable Mapping pairs a **topic field** to a **component signal**, exported/imported as a CSV
(`VcMqttFeedMappings.csv`). Working rows:

- Feed cylinders (feeder/checker/transfer) + clamp: `cmd` → `PushJoint_ActionSignal` (a
  `vcBooleanSignal`). The PushJoint "Physics Pusher" advances/retracts on the boolean.
- Grippers (bearing, shaft): `cmd` → `IN_J1_Action` (`vcBooleanSignal`), with per-vcId polarity
  (bearing_gripper closes on states 3/4, shaft on 1/2).

## Why the robots CANNOT be done with the CSV

The assembly/disassembly robots — `Swivel Arm` (bearing_pnp), `pnp` (shaft), `coverpnp` (cover),
`UR3e` (robot) — are `vcRobotController` components with their own programs. Findings from
read-only probes run inside VC:

- **VC's mapping dialog does not expose robot joints.** The component tree shows properties like
  `ActiveTool`/`CurrentTool` and behaviours, but there is **no `J1`/`X`/`Z` joint** to pair to.
- **vcCore (VC 5, Python 3) has no `createProperty`** on a component (`dir` shows `Properties`,
  `createBehavior`, `createLink` only), so a script can't even create a mappable bridge property.
- Robot `Inputs`/`Outputs` are `vcBooleanSignalMap`s but come back with **empty members** for
  pnp/coverpnp/Swivel Arm — no per-motion input signals to trigger.

So there is no native-mapping path onto a robot joint. Confirmed by the user in the dialog.

## The robot driver — vc_robot_driver.py (current approach, and the one under fire)

A **native VC Python script** (vcCore/Py3) placed in a dummy component's PythonScript behaviour:

- Reads `vc-live-positions.json` (written by the bridge) each 30 ms.
- **Eases** each robot joint toward its target (exponential smoothing) by setting the component
  **property** (`Swivel Arm.J1`, `pnp.Z`=shaft_hr, `pnp.X`=shaft_vr [pnp.X is the vertical axis],
  `coverpnp.X`, `coverpnp.Z`).

VC API facts established by probe:
- Setting the component property (e.g. `Swivel Arm.J1 = 181`) **does move the joint** (verified:
  joint CurrentValue went 90→181). Setting `joint.CurrentValue` also works.
- `RobotController.moveJoint()` throws "no running event loop" unless called inside `async def
  OnRun()`.
- Property-set snaps unless eased; easing (0.12/tick @30 ms) makes it glide.

Bugs found and fixed along the way: file-race `PermissionError` (Windows `os.replace` vs the
driver's concurrent read → retry/skip-and-retry, made the write non-blocking), and joints jerking
to the home seed on Play (driver now ignores the file on start, only reacts to changes).

The driver **does follow** — the log shows the swivel reaching pick (181) and place (0), shafts and
covers tracking. But see below.

## What is NOT solved — the honest failures

1. **Grippers close but grip nothing.** The rig publishes only actuator *states*, never part
   positions. There is no bearing/shaft/cover part in the VC scene at the pick point, so the gripper
   closes on empty space. A real pick needs a **part-flow simulation** in VC (spawn part → convey →
   grasp on close → carry → release), which does not exist and is a separate build.
2. **Feeder is chronically flaky.** The PushJoint mover is **event-driven** (its script sleeps on a
   signal event); VC's mapping writes the boolean *value* but does not reliably fire the signal
   *event*, so the mover often never wakes. Checker/transfer work; feeder mostly doesn't.
3. **UR3e robot not driven** — no pose data (its `vc-positions` table is all zeros), so nothing to
   set; only the state (1→2→0) is known, not the six joint angles per state.
4. **Ejector not wired** in VC (publishes 1→2→3→4→0, no mapping).
5. **Motion is not smooth or "exact."** The rig sends **discrete waypoint states**, not a continuous
   trajectory, so the shadow can only ease between poses. Easing speed doesn't match the rig's
   per-move duration; fast cycles make it look jumpy.
6. **Two mechanisms (CSV mappings + file-fed joint driver) = fragile and inconsistent.** It is a
   patchwork, and end-to-end it does not convincingly shadow the rig.

## The user's verdict and proposed pivot

The user (a VC/automation engineer) considers the live-MQTT joint-driver a dead end: "there is no
such thing as a full shadow in our case," grippers don't work, actuators don't move smoothly. They
want to pivot to a **"teach file (via import)"** approach — teach/record the rig's motion into a
file, import it into VC, and have VC **play/mimic** it with native robot motion (grasping as part of
the taught program), instead of poking joint values live.

## Questions for review

1. Is the **teach-file / imported-program** approach the right VC-native architecture for a rig
   shadow, versus live joint-driving? What exactly should the imported artifact be — a taught robot
   program (RSL / points) per robot, a recorded joint trajectory played as an animation, a VC
   Process/Works task, or something else?
2. Given the rig cycle is **deterministic and fully captured (states + timing)**, is the best design
   a **canned playback** of one cycle, or **rig-state-triggered** taught steps (rig says "atPick" →
   VC plays the taught pick motion) so it stays in step with the real rig? How do you keep a
   triggered sequence in sync without the snapping problem?
3. How should **part flow + grasp** be modeled so grippers actually pick — VC Works/Process with
   product creation and grasp-on-signal, and is that drivable/triggerable from the rig states?
4. Is native MQTT mapping even the right transport for a triggered approach, given robot joints
   aren't mappable — or should the trigger come through a component signal (which IS mappable) that
   fires a taught VC program?
5. Anything fundamentally wrong with treating VC as a **playback mimic** rather than a live 1:1
   shadow, for a deterministic rig cycle?

Files: `statesync.py` (bridge), `vc_robot_driver.py` (joint driver), `sync-map.generated.json`,
`vc-positions.json` (per-state joint angles), `vc-live-positions.json` (live targets),
`VcMqttFeedMappings.csv` (working boolean mappings), `rig_watch.log` (the captured clean cycle).
