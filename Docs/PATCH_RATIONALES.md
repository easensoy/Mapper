# Patch and compile rationales

Why some deploy-time patches and recipe-compile rules exist, in the detail that does not belong inside
a production method. The code cites these by name; each entry states the failure that produced the rule,
so a future edit can see what re-breaks.

Load-bearing FACTS live in `INVARIANTS.md`. This file is the reasoning behind them.

## P-1. Announce a phase after its own work, before its conditions

A phase announcement is a claim about the plant — `bearing_pnp_home_pos` asserts the arm is home — and a
peer acts on it. Publishing it before the movement that makes it true releases that peer into a machine
that is still moving.

On the no-clamp model that was a collision: Disassembly announced its bearing-home phase BEFORE homing
the arm, Feed read that as "Disassembly finished" and re-advanced the Transfer, and Assembly began
commanding the same swivel Disassembly was still carrying a bearing on. The actuator interlocks cannot
catch this — they guard the Work1↔Work2 crossing, never the move out of home.

So the order is: own work, THEN announce, THEN wait on the conditions.

The ENTRY phase additionally announces after its entry CONDITIONS. A process has not begun a cycle until
it has been authorised to; without that, every process publishes "I have started" the moment the
controllers are deployed, before any material has arrived.

## P-2. Arm an entry gate that observes an actuator the process never moves

A process re-enters its first phase the moment its entry gate reads true. An actuator it does not drive
is wherever the last cycle left it, so if that is the observed stop the gate is already true and the
process restarts immediately, re-doing finished work.

That is how a no-clamp Assembly — whose entry gate is "Transfer advanced", and which never touches the
Transfer — looped straight back and drove the swivel to Work1 to pick up the bearing Disassembly had
just released there.

A process that DOES own a movement of that actuator is not exposed: its own cycle moves the actuator off
the observed stop. Arming it there would be actively wrong — the no-clamp Disassembly observes a Transfer
that is still advanced *because* it is holding the part Disassembly is about to take, and demanding a
departure first would deadlock it against its own return command.

The arming value is the stop the actuator's own graph says it arrives FROM, so where that settles to the
same value the arming collapses to a single WAIT and a gate confirming a resting position is untouched.

## P-3. Address a sensor before waiting on it

A sensor announces a level only on the edge that produced it. A level that was already true before this
PLC started produces no edge at all, and the single frame emitted at INIT is lost if the consuming ring
is not up yet — after which nothing can re-announce it and the consumer's WAIT is dead until the sensor
is physically toggled.

Addressing the sensor drives its CAT through sample-then-report, so the WAIT always evaluates a freshly
read input. The state value carries no meaning: nothing on a sensor consumes `state_cmd`, the frame is
only a request to report.

**Not `RingKey` for the target name.** `updateComponentState.BREQ` tests
`component_state_in.dest_name = name` with case-sensitive ST string equality. Actuators are claimable in
lower case because the injector lower-cases `actuator_name`; a sensor is parameterised with its component
name verbatim, so a lower-cased target would circle the ring unclaimed and the sensor would never answer.

## P-4. The addressed-refresh wiring inside `Sensor_Bool_CAT`

`StateHandling.CNF` fires ONLY from `updateComponentState`'s BREQ state, which is entered only when
`component_state_in.dest_name = name`. Reports set `dest_name := ''`, so no report can trigger any
sensor: only a frame that names this component does. The refresh is therefore strictly bounded — one
request in, one report out, nothing at all while the line is idle.

The order matters and must be SERIAL, not fanned out. Driving `StateHandling.REQ` from the same event as
the sample would publish the CACHED `state_sts`, which for an active-low input can briefly report the
wrong level and release a gate early. So:

```
StateHandling.CNF -> FB1.RPT -> FB1.SMP -> FB2.REQ -> FB2.CNF -> FB1.REQ -> FB1.CNF -> publish
```

`RPT` parks FB1's ECC in START, where both level transitions are unconditionally available, so the
following sample always emits CNF — that is what makes an unchanged level report at all.

## P-5. Fire the cover-sensor publisher from the change detector, never from the scan

`BX1_IO.CoverSensorEvent` comes from FB2 (`changeEventM262_2`), clocked on every broker read
(`EIPInputs_Bool.CNF -> FB2.Scan_Event`) but emitting only when the bit differs from its retained
previous value. That retained value is a plain BOOL with no InitialValue and FB2's INIT algorithm is
empty, so at power-on it is FALSE: a cover ALREADY in place reads `TRUE <> FALSE` on the very first scan
and DOES produce the event. The detector therefore covers boot establishment as well as every later
change, and does so inside the broker where it costs nothing.

Driving the publisher from the cyclic scan as well re-fires it at the scan rate forever, and because
`SRC.CNF` drives the sensor CAT's `RD`, every one of those fires re-reads the CAT
(`RD -> FB2.REQ -> FB2.CNF -> FB1.REQ`) — so the sensor's event counters climb without bound while the
rig is idle. The wire is reconciled rather than merely not-added, so a tree already deployed with it
self-heals on the next deploy.

## P-6. Drop relocated components on the FINAL sysres write

The relocated Feed components belong ONLY on the resource that receives them. The wire pass is the LAST
writer of every sysres and it PRESERVES the FB elements it reads, so a stale or EAE-locked tree that
still lists them on the original resource would get them re-written — a duplicate instance across two
resources, which EAE reports as "Repair Instances".

Dropping them (and their connections) on that final write, for every resource that does not receive
relocated components, yields the clean partition instead. It is a no-op on the receiving resource, on
resources that never carry those components, and when nothing is relocated. The origin's ring then wires
around the remaining components, leaving the cross-device seam open for EAE to bridge.

## P-7. Keep the cross-controller segment off the locally-closed ring

Everything on the segment is driven BY that segment, so it must stay off the locally-closed ring or its
`stateRprtCmd_in` has two sources — which EAE resolves in arbitrary order, so the report a process reads
is whichever wire happened to win.

Membership comes from the segment itself rather than being re-listed, and applies to sensors as well as
actuators: a twin that DECLARES the part-present sensor (rather than leaving it to the injection) puts it
in the station's sensors, and a rule that filtered only actuators spliced it onto the ring while the
segment was already driving it. The sysres emitter excludes the same set, which is what keeps the two
halves agreeing.
