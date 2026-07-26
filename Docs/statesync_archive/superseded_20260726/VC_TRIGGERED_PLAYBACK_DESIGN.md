# VC-native triggered-playback shadow — design

Rig is source of truth; VC is a follower that PLAYS taught motion, TRIGGERED by rig MQTT state
changes. No live joint-poking, no file polling. This document is design + the feeder proof; nothing
is implemented until approved.

---

## 1. Feeder failure — layer proof

Three layers in the feed-cylinder chain. Locate the break.

**Layer A — broker payload — PROVEN CLEAN.**
One live cycle captured (rig_watch.log): `smc/feeder {state:1}→{2}→{3}→{4}→{0}`. The rig publishes
retain=false, so nothing is retained at rest (an empty read at idle is expected, not a fault). The
rig feeder is fine.

**Layer B — VC connected variable — PROVEN CLEAN.**
statesync republishes retained `uns/.../feeder/state` with a `cmd` boolean (true on state 1/2 =
advancing, false otherwise). Live payload now: `{state:0, cmd:false}` at rest; during a cycle `cmd`
goes true→false. You have already seen VC's MQTT Messages show feeder `cmd:true`/`cmd:false`. So the
value reaches VC's mapped signal.

**Layer C — PusherNode `ServoController_Script` — THE BREAK.**
The mover is **event-driven**: `OnSignal()` appends to a queue, `OnRun()` sleeps on
`condition(lambda: queue)`. VC's MQTT mapping writes the signal's *value* but does not reliably fire
the signal *event*, so `OnSignal` never runs, the queue stays empty, the mover sleeps — the feeder
doesn't advance even though the value is correct. Grippers "work" because their logic is value-driven.

**The one check that confirms Layer C (do this in VC):**
1. PusherNode → `ServoController_Script` behaviour → confirm ENABLED and that the sim is running.
2. Sim playing, rig cycling: watch `PushJoint_ActionSignal.Value` — it flips true then false
   (re-confirms Layer B into the signal).
3. Watch the pusher joint: **value flips true but joint doesn't move ⇒ Layer C confirmed** (mover not
   waking on a value-write).

**Fix (mover only, not the mapping):** make the mover **poll** — re-read `PushJoint_ActionSignal.Value`
every ~50 ms and move on change (value-driven, like the grippers). This is a one-time per-component
behaviour fix; the mapping is untouched.

---

## 2. Freeze the CSV — simple cylinders only

Frozen set (1-DOF, home/extend, one boolean each): **feeder, checker, transfer, clamp, ejector.**
One mapping row per component: topic `uns/.../<comp>/state`, field `cmd` (Boolean, ServerToSimulation)
→ `PushJoint_ActionSignal`.

Rules:
- `VcMqttFeedMappings.csv` is the single source; version it; **never re-import over a live setup**
  (re-import half-binds PusherNode — proven). Edit by re-pairing in the dialog, not by re-import.
- Grippers and robots are NOT in this file.
- The feeder Layer-C poll fix is applied to the component once; independent of the mapping.

---

## 3. Robots are NOT booleans

Swivel Arm (bearing_pnp), pnp (shaft_hr/vr), coverpnp (cover_hr/vr), UR3e (robot) are multi-position
axes (home / pick / place / …). A boolean can't encode 3+ positions, and VC does not expose their
joints to mapping. They run taught segments (§4) triggered by state (§5). No boolean/joint mapping.

---

## 4. Taught motion segments (native VC robot programs)

For each robot: teach named positions in VC, and a program that moves between them with native motion
(PTP/LIN, real speed/accel), executed by the RobotExecutor — smooth, collision-aware. No external joint
setting. Reference angles from vc-positions.json; **UR3e poses must be taught in VC (unknown today).**

- **Swivel Arm (bearing_pnp)** — HOME(J1≈90°), PICK(181°), PLACE(0°). Segs: HOME→PICK, PICK→PLACE, PLACE→HOME.
- **pnp (shaft)** — Z(horizontal), X(vertical). HOME, WORK (from shaft_hr/vr). Segs per shaft cycle.
- **coverpnp (cover)** — X, Z. HOME, cover PICK/PLACE. Segs per cover cycle.
- **UR3e (robot)** — J1–J6. HOME, PICK, PLACE — **teach in VC.** Segs HOME→PICK→PLACE→HOME.

---

## 5. State changes as triggers / sync points

Transport the trigger through a **signal** (mappable), never the joint (not mappable). This is the
canonical robot-cell pattern: a PLC drives the robot's input signals; the robot program waits on them.

- Add input signals to each robot's `Inputs` signal map (its map is currently empty — this is a VC UI
  action, not scripting). Either a per-state boolean set, or one Integer `RigState`.
- Map the rig's `state` field → those input signals via MQTT (VC mapping supports Integer; same dialog).
- The robot program is wait-and-play: `WaitSignal(state change)` → run the taught segment whose target
  pose matches the new state (1/2→PICK, 3/4→PLACE, 0/5/6→HOME).
- **Sync points:** gate on the rig's ARRIVED states so VC never runs ahead — the swivel treats the part
  as picked only at state=2 (AtPick); the gripper grasp (§6) is gated on the same arrival. VC stays in
  step with the rig at each waypoint instead of racing.
- Inputs are mapped MQTT signals only; motion is the robot's own planner. No file polling.

---

## 6. Part flow + attach/release (VC Process, native)

So grippers actually pick:

- **Create** — a ProductType "Bearing" (+ shaft/cover). A part is created at the hopper when
  PartInHopper→On (mapped sensor signal triggers a Source/Create).
- **Transfer** — the feed cylinders carry the part from hopper to the assembly pick point (transport
  nodes / attach to the transfer body).
- **Grasp** — the gripper close (mapped boolean) fires the tool's **Attach** (grab nearest part), gated
  on the swivel arrival at PICK (§5 sync). The part then follows the robot tool through the carry segment.
- **Release** — the gripper open fires **Detach** at PLACE. Continue through disassembly → UR3e picks →
  returns the part to the hopper.
- The gripper boolean drives BOTH the jaw and the grasp — one signal, no second writer.

---

## 7. Acceptance criteria

- **No live file polling** — `vc_robot_driver.py` + `vc-live-positions.json` retired; robots run on taught
  programs + mapped signals only.
- **No teleporting** — all robot motion is native taught motion; cylinders move via their poll-fixed
  mover, never snapped.
- **No dual writers** — exactly one driver per actuator: cylinder = CSV boolean → PushJoint; robot =
  mapped state signal → taught program; gripper boolean → jaw + grasp. No joint/position is written from
  two sources.
- **Reconnect after CSV import** — after any mapping import/edit: Disconnect → Reconnect the MQTT
  connection; never re-import over a working setup; re-pair any binding the import drops (PusherNode);
  then Stop → Play.
- **Visible part transfer end-to-end** — a bearing is created at the hopper, moves feed → assembly, is
  gripped, carried, placed, disassembled, and returned by the UR3e — visibly, in step with the rig.

---

## What gets retired

`vc_robot_driver.py`, `vc-live-positions.json`, and the `position` field + live-file writer in
statesync. statesync keeps: `smc/#` → retained UNS, `cmd` (cylinders/grippers), `present` (sensors),
and adds the `state` int passthrough for the robot triggers (already present as `state`).
