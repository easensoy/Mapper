# SMC rig -> Visual Components shadow: the VC-side executor.
# Paste into the Python Script behaviour of component "0 #2". VC 5.0 embeds CPython 2.7.
#
#   uns/.../vc/command  --(whole message, Convert.ToString(Payload))-->  CommandJson
#   StatusJson          --(@Json(Value))-->                              uns/.../vc/event
#
# THIS FILE HAS NO RIG KNOWLEDGE. No component names, no positions, no durations, no lanes,
# no routine names, no grasp specs. Every one of those travels in the command envelope, so
# this pasted script does not change when the rig, the twin or a measurement changes.
# It implements three execution contracts and nothing else:
#
#   signal   shape the servo (speed/accel from distance+duration), then set the component's
#            own PushJoint_ActionSignal. The stock ServoController_Script sweeps the joint.
#            Genuinely swept motion - never moveImmediate for these.
#   axis     20 ms interpolation: setJointTarget per axis, then ONE moveImmediate per
#            controller so shared-controller axes stay coherent.
#   routine  callRoutine(r, False) - NON-BLOCKING, always. Completion is observed via the
#            routine's OnScopeExecuted event, or a start-latched idle transition, or
#            positive parent-chain evidence. Never assumed.
#
# WHY NON-BLOCKING MATTERS: blocking callRoutine suspends this coroutine, which also stops
# the axis interpolator in the same loop. Measured: a 6939 ms UR3e Partpick stretched an
# unrelated 2009 ms Ejector stroke to 8957 ms, then teleported it. Nothing here blocks.
#
# CurrentStatement is NOT a completion test on its own: it also reads None before a routine
# starts and when the executor is disabled (IsEnabled is False here). It is only consulted
# AFTER execution has been latched as started.
from vcScript import *
import json
from datetime import datetime

app = getApplication()
gw = getComponent()

LOG = r"C:\VueOneMapper\Tools\statesync\vc_gateway.log"
TICK_S = 0.02
MAX_PENDING = 30
ZERO_EPS = 0.05
START_GRACE_MS = 3000.0         # no start evidence within this -> complete as UNOBSERVED
DEFAULT_TIMEOUT_MS = 15000.0
RAMP_FRACTION = 0.10            # 10% accelerate, 80% cruise, 10% decelerate
SIGNAL_EDGE_GRACE_MS = 400.0    # no joint movement by now -> the servo missed the edge
SIGNAL_SLACK = 1.5              # endpoint deadline = durationMs * this + SIGNAL_SLACK_MS
SIGNAL_SLACK_MS = 600.0

# A taught routine is finished, as far as the shadow is concerned, once the robot has
# STOPPED MOVING - not when the executor finally falls idle. A routine can hold the
# executor busy long after the last motion, sitting in a taught Delay, and the rig has no
# equivalent: the physical robot reports one atomic task and is already parked. Waiting out
# those delays is what left the shadow ~5.8 s behind the rig on every cycle.
#
# MUST EXCEED THE LONGEST TAUGHT DWELL THAT PRECEDES MOTION OR AN OUTPUT ACTION in any
# routine the rig drives, or the routine is cut before its work is done. That bound is now
# MEASURED, not assumed: the UR3e's taught program (archived at
# Docs/statesync_archive/ur3e_taught_program.xml, extracted from the layout) is
#
#   Partpick   joint P4 (JointSpeed 0.2) . linear P5 . Delay 1.0 . DO103=1 . DO1=1 (GRIP)
#              . Delay 1.0 . linear P12 (retract)
#   Partplace  linear P2 . Delay 1.0 . DO103=0 . DO1=0 (RELEASE) . Delay 1.6
#   Home       joint P3 (JointSpeed 1.0)
#
# Partpick's two 1 s Delays are separated only by ZERO-TIME outputs, so the robot holds
# still for a full 2000 ms in the MIDDLE of the routine with the retract still to come.
# 1500 ms fired INSIDE that dwell and cancelled the lift on every single cycle - proven to
# 29 ms against VC's own recorded statement times:
#   joint P4 -> 3.387 | linear P5 -> 4.270 | Delay -> 5.270 | GRIP 5.270 | Delay -> 6.270
#   | linear P12 RETRACT -> 6.665
# stillness starts 4.270, +1500 = 5.770, and the observed completion was 5.799. The grip at
# 5.270 had happened (hence the part showed as carried) but P12 never ran, and the next
# chain step's clearCallStack cancelled it. 2200 ms clears the 2000 ms dwell by 10%, so the
# routine now reaches its own end and completes on the executor going idle instead.
# Override per command with "settleMs" on the wire.
ROUTINE_SETTLE_MS = 2200.0
JOINT_EPS = 1e-4                # joint units; below this the robot counts as stationary

# A PICK IS OVER ONCE THE ROBOT HAS TAKEN SOMETHING AND MOVED CLEAR OF IT. After the grasp
# the only taught motion left is the retract, so the still period that follows it is the end
# of the routine. RETRACT_EPS is what makes this safe: the post-grasp move must be a REAL
# joint excursion, not the sub-milliunit jitter the servo shows while holding position.
# ⚠ THIS IS CURRENTLY A BACKSTOP, NOT THE ACTIVE PATH. With the settle clearing the taught
# dwell the routine now runs to its own end, so idle_after_start - which is tested first -
# fires at the same instant the retract finishes and `picked` never gets its 250 ms. It
# earns its place if the executor ever stops reporting idle; do not delete it on the
# evidence of an empty log.
PICK_SETTLE_MS = 250.0
RETRACT_EPS = 0.5               # joint units the retract must exceed to count as motion

# ------------------------------------------------------------ SIMULATION CLOCK
# Elapsed MUST be measured on the simulation clock, not the wall clock.
#   * delay() advances simulation time, so the loop's own cadence is sim time.
#   * a stopped or paused simulation must not age an in-flight move. Measuring on
#     datetime.now() made a 1125 ms Pusher stroke report 227029 ms after the sim was
#     stopped mid-move and restarted 3m46s later - and timed out an innocent robot
#     routine at 227794 ms instead of its 15000 ms limit.
#   * it also keeps motion correct when VC runs at a non-realtime SimSpeed.
_simFallbackMs = [0.0]


def simNowMs():
    try:
        return getSimulation().SimTime * 1000.0
    except Exception:
        return _simFallbackMs[0]

# ---------------------------------------------------------------- logging
try:
    _log = open(LOG, "a")
except Exception:
    _log = None


def log(event, **kw):
    rec = {"t": datetime.now().strftime("%H:%M:%S.%f")[:-3], "event": event}
    rec.update(kw)
    line = json.dumps(rec)
    if _log:
        try:
            _log.write(line + "\n"); _log.flush()
        except Exception:
            pass
    print("[gw] " + line)


def ensure(name):
    p = gw.getProperty(name)
    if not p:
        p = gw.createProperty(VC_STRING, name)
        p.Value = ""
    return p

cmdProp = ensure('CommandJson')
staProp = ensure('StatusJson')

# ------------------------------------------------------------------ state
pending = {}        # lane -> [envelope]
active = {}         # lane -> in-flight item
seenIds = []
lastSeq = {}
epochSeen = [None]
_jointCache = {}
_execCache = {}
_scopeCount = {}    # (vcId, routine) -> monotonic count of observed OnScopeExecuted
_hooked = set()     # (vcId, routine) already carrying our handler
_shapedSeen = set() # (component, durationMs) whose servo shaping has been reported once


def reset_state(why):
    pending.clear(); active.clear()
    del seenIds[:]
    lastSeq.clear()
    epochSeen[0] = None
    _scopeCount.clear()
    _shapedSeen.clear()     # a new session re-announces what shaping it is running
    # AppliedScale is deliberately NOT cleared here. It records what the MODEL is carrying,
    # and a reset does not unwind those writes; clearing it would make the next run treat a
    # scaled model as taught. It is only ever rewritten by an actual scaling.
    log("state_cleared", why=why)


def publish(env, status, **kw):
    rec = {
        "schemaVersion": 2,
        "commandId": env.get("commandId") if env else None,
        "epoch": env.get("epoch") if env else None,
        "vcId": env.get("vcId") if env else None,
        "lane": env.get("lane") if env else None,
        "execution": env.get("execution") if env else None,
        "stateName": env.get("stateName") if env else None,
        "status": status,
        "ts": datetime.now().isoformat() + "Z",
    }
    rec.update(kw)
    staProp.Value = json.dumps(rec)


def publish_ready(why):
    staProp.Value = json.dumps({
        "schemaVersion": 2, "vcId": "*", "status": "ready",
        "detail": why, "ts": datetime.now().isoformat() + "Z"})
    log("ready", why=why)

# ------------------------------------------------- ingest (never blocks)
def OnCommandChanged(prop):
    raw = prop.Value
    if not raw:
        return
    try:
        env = json.loads(raw)
    except Exception as e:
        log("reject", reason="bad_json", err=str(e)); return

    cid = env.get("commandId")
    vcid = env.get("vcId")
    lane = env.get("lane")
    ex = env.get("execution")
    if not cid or not vcid or not lane or ex not in ("signal", "axis", "routine"):
        log("reject", commandId=cid, vcId=vcid, reason="malformed_envelope"); return
    if cid in seenIds:
        log("reject", commandId=cid, vcId=vcid, reason="duplicate"); return

    ep = env.get("epoch")
    if epochSeen[0] is None:
        epochSeen[0] = ep
    elif ep != epochSeen[0]:
        log("epoch_change", old=epochSeen[0], new=ep)
        epochSeen[0] = ep
        pending.clear(); active.clear(); lastSeq.clear()

    sq = env.get("seq")
    if sq is not None and vcid in lastSeq and sq <= lastSeq[vcid]:
        log("reject", commandId=cid, vcId=vcid, reason="stale_seq", seq=sq); return
    if sq is not None:
        lastSeq[vcid] = sq

    q = pending.setdefault(lane, [])
    if len(q) >= MAX_PENDING:
        log("reject", commandId=cid, vcId=vcid, reason="backlog", depth=len(q)); return

    seenIds.append(cid)
    if len(seenIds) > 1000:
        del seenIds[:500]
    env["_recv"] = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    q.append(env)
    log("enqueue", commandId=cid, vcId=vcid, lane=lane, execution=ex, depth=len(q))

cmdProp.OnChanged = OnCommandChanged

# ------------------------------------------------------------- resolution
def resolveJoint(vcid):
    if vcid in _jointCache:
        return _jointCache[vcid]
    for comp in app.Components:
        node = comp.findNode(vcid)
        if not node:
            continue
        props = node.Dof.Properties
        jointName, control = '', None
        for i in range(len(props)):
            if props[i].Name == "Name":
                jointName = props[i].Value
            if props[i].Name == "Controller":
                control = props[i].Value
        if control:
            for j in range(len(control.Joints)):
                if control.Joints[j].Name == jointName:
                    _jointCache[vcid] = (comp, control, j)
                    return _jointCache[vcid]
    return None


def resolveExecutor(vcid):
    if vcid in _execCache:
        return _execCache[vcid]
    robot = app.findComponent(vcid)
    if robot is None:
        return None
    ex = robot.findBehaviour("Executor")
    if ex is None:
        return None
    try:
        ex.IsEnabled = False        # True breaks the cover actuators in this model
    except Exception:
        pass
    _execCache[vcid] = ex
    return ex


# RETIMING IS ABSOLUTE: every write is taught_value * factor, never current_value * ratio.
# Scaling mutates live model values, and this script is re-pasted constantly - so anything
# that infers "the taught value" from what the model currently holds will eventually read
# its own output. That is not hypothetical: an earlier build wrote the joint limits x1.6 and
# recorded it nowhere, so a relative scheme starting fresh would have compounded to x2.56
# and run the robot ~850 ms FAST. Taught joint limits therefore come from the config, and
# taught statement values are captured ONCE into a property that lives beside the model, so
# a re-paste or a save cannot turn a scaled value into a baseline.
scaleProp = ensure('AppliedScale')


def _appliedScale():
    try:
        return json.loads(scaleProp.Value) if scaleProp.Value else {}
    except Exception:
        return {}


def _rememberScale(key, factor):
    st = _appliedScale()
    st[key] = factor
    try:
        scaleProp.Value = json.dumps(st)
    except Exception:
        pass


def scaleRobotSpeed(ex, vcid, factor, taught):
    """Run a taught routine faster or slower by scaling the robot's JOINT LIMITS.

    A taught statement carries its own JointSpeed (Partpick's first move asks for 0.2, i.e.
    20%) and vcServoController.Speed is documented as a percentage 0-100, so there is no
    way to ask the executor for more than it was taught. The joint limits underneath it are
    writable though - the same lever shapeServo already uses on every cylinder - and a
    statement asking for 20% of a raised limit moves proportionally faster.

    THIS ONLY REACHES JOINT MOTION. A joint statement asks for a FRACTION of the joint's
    maximum (Partpick's P4 asks 0.2), so raising the maximum speeds it up. A LINEAR
    statement asks for an absolute mm/s of its own and is untouched by this - measured:
    scaling the joints x1.6 left Partplace's single linear move at 2379 ms against a taught
    2368 ms. Linear statements are handled by scaleRoutineMotion below.

    Speed scales by the factor and acceleration by its SQUARE, so a move that is
    acceleration-limited (a short one, like Home) speeds up by the same factor as one that
    is speed-limited. With accel scaled linearly Home only reached x1.35.

    ONLY MOTION SCALES. Taught Delay statements are wall time and do not, which is exactly
    right: the rig's dwell is real and the shadow should keep it.

    `taught` is the model's own joint limits, supplied by the caller from the config. Every
    write is taught * factor, so running this twice, ten times, or after a re-paste lands on
    exactly the same numbers, and factor 1.0 restores the model."""
    if not factor or factor <= 0 or not taught:
        return None
    try:
        ctrl = ex.Controller
        if ctrl is None:
            return None
        n = 0
        for jt in ctrl.Joints:
            for attr, mult in (("MaxSpeed", factor), ("MaxAcceleration", factor * factor),
                               ("MaxDeceleration", factor * factor)):
                base = taught.get(attr)
                if base is None:
                    continue
                try:
                    setattr(jt, attr, float(base) * mult)
                    n += 1
                except Exception:
                    pass
        if not n:
            log("robot_speed_scale_failed", vcId=vcid, factor=factor,
                reason="no_joint_limit_written", taught=taught)
            return None
        _rememberScale("joints:" + str(vcid), factor)
        log("robot_speed_scaled", vcId=vcid, factor=factor, joints=len(ctrl.Joints),
            limitsWritten=n, taught=taught)
        return factor
    except Exception as e:
        log("robot_speed_scale_failed", vcId=vcid, err=str(e))
        return None


# A taught LINEAR statement carries its own absolute speed and acceleration, so the joint
# limits above do not reach it. vcStatement exposes getProperty(), so those taught values
# are writable at runtime and the taught program never has to be edited. Speeds scale by
# the factor, accelerations by its square, for the same reason as the joint limits.
LINEAR_SPEED_PROPS = ("MaxSpeed", "MaxAngularSpeed")
LINEAR_ACCEL_PROPS = ("Acceleration", "Deceleration",
                      "AngularAcceleration", "AngularDeceleration")


def _stmtProp(st, name):
    """These six are real runtime members of a statement, not saved-XML artefacts - VC's own
    vcHelpers/Robot.py updSpeeds() writes exactly this set, branching on JOINT_MOTION versus
    linear, which is the same dichotomy this file uses. api.xml does NOT document them on
    vcMotionStatement, so do not "discover" they are undocumented and remove them. Some
    statement classes expose them as attributes rather than through getProperty, so try
    both; a missing name must read as absent, never as an error."""
    try:
        pr = st.getProperty(name)
        if pr is not None:
            return pr
    except Exception:
        pass
    try:
        if hasattr(st, name):
            return _AttrProp(st, name)
    except Exception:
        pass
    return None


class _AttrProp(object):
    """Adapter so an attribute-style statement member reads and writes like a vcProperty."""

    def __init__(self, obj, name):
        self._o, self._n = obj, name

    def _get(self):
        return getattr(self._o, self._n)

    def _set(self, v):
        setattr(self._o, self._n, v)

    Value = property(_get, _set)


def scaleRoutineMotion(routine, vcid, name, factor, dwellFactor=None):
    """Retime one taught routine, in the model, at runtime.

    A statement carrying JointSpeed is deliberately SKIPPED: it asks for a fraction of the
    joint maximum, which scaleRobotSpeed already raised, and scaling both would compound to
    the factor squared.

    TAUGHT DELAY IS RETIMED TOO, and it has to be. The rig's own pick-and-place time
    INCLUDES its gripper dwell, so a shadow that shortens only the travel can never land the
    release where the rig lands it - the dwell is a fixed floor. Measured: with motion alone
    at x1.6 the release came 7413 ms into the task against the rig's 5012 ms, leaving the
    part 320 ms to settle in the hopper before the feeder stroked, where the rig allows
    2721 ms. That is why the part was unstable. dwellFactor DIVIDES each taught Delay, so
    the whole routine can be run at the rate the rig actually works at.

    ABSOLUTE, like the joint limits: the taught values are captured ONCE into a property
    that lives beside the model, and every write is taught * factor. A re-paste of this
    script, or a saved model, can therefore never turn a scaled value into a baseline.
    Passing factor 1.0 puts the taught program back exactly as it was."""
    if not factor or factor <= 0 or routine is None:
        return None
    dwellFactor = float(dwellFactor) if dwellFactor else 1.0
    key = "stmt:%s/%s" % (vcid, name)
    state = _appliedScale().get(key) or {}
    taught = state.get("taught")
    try:
        stmts = list(routine.Statements or [])
    except Exception as e:
        log("routine_motion_scale_failed", vcId=vcid, routine=name, err=str(e))
        return None

    # Capture the taught values, MERGING into whatever was recorded before rather than
    # replacing it. A stored baseline can be missing a property this build has only just
    # started collecting - that is exactly what happened when Delay was added: the row count
    # still matched, the stale rows were reused, and the dwell was silently never written
    # (delays=0 in the log while the routine still ran its full 2000 ms). Merging fixes that
    # WITHOUT re-reading a value we have already scaled: a property already in the baseline
    # is kept, and one that is missing has by definition never been written, so the model
    # still holds its taught value.
    if not taught or len(taught) != len(stmts):
        if taught:
            log("routine_baseline_reset", vcId=vcid, routine=name,
                had=len(taught), now=len(stmts))
        taught = [{} for _ in stmts]
    grew = False
    for st, row in zip(stmts, taught):
        if _stmtProp(st, "JointSpeed") is not None:   # joint move: the limits own it
            continue
        for pn in LINEAR_SPEED_PROPS + LINEAR_ACCEL_PROPS + ("Delay",):
            if pn in row:
                continue                             # already taught-recorded; never re-read
            pr = _stmtProp(st, pn)
            if pr is None:
                continue
            try:
                v = float(pr.Value)
            except Exception:
                continue
            if v > 0:
                row[pn] = v
                grew = True

    # Nothing to do only if the rate is unchanged AND the baseline did not just grow - a
    # newly captured property has never been written and would otherwise stay taught while
    # the log claimed the routine was retimed.
    if not grew and abs(float(state.get("factor", 0.0)) - factor) < 1e-6 \
            and abs(float(state.get("dwell", 0.0)) - dwellFactor) < 1e-6:
        return factor

    n = dwells = 0
    for st, row in zip(stmts, taught):
        for pn, base in row.items():
            pr = _stmtProp(st, pn)
            if pr is None:
                continue
            try:
                if pn == "Delay":
                    pr.Value = float(base) / dwellFactor
                    dwells += 1
                else:
                    pr.Value = float(base) * (factor * factor
                                              if pn in LINEAR_ACCEL_PROPS else factor)
                n += 1
            except Exception:
                pass
    _rememberScale(key, {"factor": factor, "dwell": dwellFactor, "taught": taught})
    log("routine_motion_scaled", vcId=vcid, routine=name, factor=factor,
        dwellFactor=dwellFactor, statements=len(stmts), properties=n, delays=dwells)
    return factor


def robotJoints(ex):
    """Current joint vector of the robot behind an executor, or None. vcRobotController
    inherits vcServoController, so the executor's Controller exposes Joints/getJointValue."""
    try:
        ctrl = ex.Controller
        if ctrl is None:
            return None
        return [float(ctrl.getJointValue(i)) for i in range(len(ctrl.Joints))]
    except Exception:
        return None


def jointsMoved(a, b):
    if a is None or b is None or len(a) != len(b):
        return True
    for i in range(len(a)):
        if abs(a[i] - b[i]) > JOINT_EPS:
            return True
    return False


def excursion(a, b):
    """Largest single-joint change between two readings - how far the robot actually went."""
    if a is None or b is None or len(a) != len(b):
        return 0.0
    return max(abs(a[i] - b[i]) for i in range(len(a))) if a else 0.0


def parentChain(comp):
    names, n = [], comp
    for _ in range(24):
        if n is None:
            break
        try:
            names.append(str(n.Name))
        except Exception:
            break
        n = getattr(n, "Parent", None)
    return names


def chainHasCarrier(partName, carriers):
    p = app.findComponent(partName)
    if p is None:
        return None, []
    chain = parentChain(p)
    return any(c in chain for c in carriers), chain


def carriedBy(robotName):
    """Every component currently parented under this robot. Generic discovery: it needs no
    config, so a robot with no verify spec still reports what it is actually holding."""
    out = []
    try:
        for c in app.Components:
            n = str(c.Name)
            if n == robotName:
                continue
            if robotName in parentChain(c)[1:]:
                out.append(n)
    except Exception:
        pass
    return out

# ---------------------------------------------------- signal executor
def stateSignals(comp, control, j):
    """The stock <joint>_OpenState / <joint>_ClosedState feedback signals, when present.
    Evidence only - they are unconnected in this model, so they are logged, not gated on."""
    try:
        name = control.Joints[j].Name
    except Exception:
        return None, None
    return (comp.findBehaviour(name + "_OpenState"),
            comp.findBehaviour(name + "_ClosedState"))


def signalValueOf(sig):
    try:
        return bool(sig.Value)
    except Exception:
        return None


def shapeServo(comp, control, j, distance, durationMs):
    """Speed/accel so the stock servo sweeps `distance` in `durationMs`:
    10% ramp up, 80% cruise, 10% ramp down -> total time == durationMs exactly.
    Written to the joint (authoritative) and to the component's Push* properties
    (so the stock changeSpeed lands in the same place if it re-runs).

    Reports the shaping once per (component, duration), so a new setting announces itself
    and a steady one stays silent. The figure that matters is mmPerStep - how far the
    pusher advances in one 20 ms simulation step. The part is carried by PHYSICS CONTACT
    (the components hold a PhysicsEntity, Physics Type "In Physics"), so that per-step
    advance is what decides whether the solver sees the contact at all; a cylinder can
    arrive perfectly and still fail to shift anything.

    DO NOT compare the shaped values against the PushSpeed/PushAcceleration stored in the
    layout and call the ratio an overdrive. Those stored values are THIS SCRIPT'S OWN
    LEFTOVERS: shapeServo writes them on every stroke, so whatever the file holds is
    simply the last stroke before the model was saved. Proven to 4 dp on all five
    cylinders - each equals the gateway's RETURN-stroke shaping (Pusher 114.5679,
    Checker 102.5641, Transfer 67.5850, Clamp 128.8998). There is no authored ceiling
    recorded anywhere in the model."""
    t = max(float(durationMs), 1.0) / 1000.0
    d = abs(float(distance))
    if d <= ZERO_EPS:
        return None
    v = d / ((1.0 - RAMP_FRACTION) * t)
    a = v / (RAMP_FRACTION * t)
    for pname, val in (("PushSpeed", v), ("PushAcceleration", a)):
        pr = comp.getProperty(pname)
        if pr:
            try:
                pr.Value = float(val)
            except Exception:
                pass
    key = (str(getattr(comp, "Name", "?")), int(durationMs))
    if key not in _shapedSeen:
        _shapedSeen.add(key)
        log("servo_shaped", comp=key[0], durMs=key[1], strokeMm=round(d, 2),
            speed=round(v, 2), accel=round(a, 1), mmPerStep=round(v * TICK_S, 2))
    try:
        joint = control.Joints[j]
        for attr, val in (("MaxSpeed", v), ("MaxAcceleration", a), ("MaxDeceleration", a)):
            try:
                setattr(joint, attr, float(val))
            except Exception:
                pass
    except Exception:
        pass
    return v


def startSignal(env, lane):
    vcid = env.get("vcId")
    r = resolveJoint(vcid)
    if r is None:
        publish(env, "failed", detail="joint_not_found")
        log("failed", commandId=env.get("commandId"), vcId=vcid, reason="joint_not_found"); return
    comp, control, j = r
    sig = comp.findBehaviour(env.get("signalBehaviour") or "PushJoint_ActionSignal")
    if sig is None:
        publish(env, "failed", detail="signal_not_found")
        log("failed", commandId=env.get("commandId"), vcId=vcid, reason="signal_not_found"); return

    value = bool(env.get("signalValue"))
    durMs = float(env.get("durationMs") or 0)
    dist = float(env.get("strokeDistance") or 0)
    target = float(env.get("target"))
    tol = max(0.5, 0.01 * dist) if dist else 0.5
    before = float(control.getJointValue(j))
    openSig, closedSig = stateSignals(comp, control, j)
    sigBefore = signalValueOf(sig)

    if abs(before - target) <= tol:
        try:
            sig.signal(value)            # keep the servo's own view consistent
        except Exception:
            pass
        publish(env, "completed", position=round(before, 3), durationMs=0, atTarget=True)
        log("completed", commandId=env.get("commandId"), vcId=vcid, lane=lane,
            execution="signal", durationMs=0, cur=round(before, 3), alreadyAtTarget=True)
        return

    speed = shapeServo(comp, control, j, dist, durMs) if durMs > 0 else None

    # EDGE GUARANTEE: a VC boolean signal fires OnSignal only on a CHANGE, and the stock
    # ServoController_Script moves only on that event. If the signal already holds the
    # requested value the servo will never see it and the cylinder silently does not move.
    # Forcing the edge is safe here precisely because the joint is provably NOT at target.
    forced = False
    try:
        if sigBefore is not None and sigBefore == value:
            sig.signal(not value)
            forced = True
        sig.signal(value)
    except Exception as e:
        publish(env, "failed", detail="signal:" + str(e))
        log("failed", commandId=env.get("commandId"), vcId=vcid, err=str(e)); return

    if durMs <= 0:                       # snapshot: snap, no animation expected
        publish(env, "completed", durationMs=0, snapshot=True)
        log("completed", commandId=env.get("commandId"), vcId=vcid, lane=lane,
            execution="signal", durationMs=0, snapshot=True)
        return

    active[lane] = {"kind": "signal", "env": env, "vcId": vcid, "control": control, "j": j,
                    "sig": sig, "value": value, "start": before, "target": target, "tol": tol,
                    "t0": simNowMs(), "durMs": durMs, "moved": False, "forced": forced,
                    "openSig": openSig, "closedSig": closedSig,
                    "deadline": durMs * SIGNAL_SLACK + SIGNAL_SLACK_MS}
    publish(env, "started", position=round(before, 3), speed=speed)
    log("start", commandId=env.get("commandId"), vcId=vcid, lane=lane, execution="signal",
        value=value, sigBefore=sigBefore, forcedEdge=forced, jointBefore=round(before, 3),
        target=round(target, 3), tol=round(tol, 3), durMs=int(durMs),
        speed=round(speed, 2) if speed else None, recv=env.get("_recv"))

# ------------------------------------------------------ axis executor
def startAxis(env, lane):
    vcid = env.get("vcId")
    r = resolveJoint(vcid)
    if r is None:
        publish(env, "failed", detail="joint_not_found")
        log("failed", commandId=env.get("commandId"), vcId=vcid, reason="joint_not_found"); return
    comp, control, j = r
    start = float(control.getJointValue(j))
    target = float(env.get("target"))
    durMs = float(env.get("durationMs") or 0)

    if abs(target - start) <= ZERO_EPS or durMs <= 0:
        try:
            control.setJointTarget(j, target); control.moveImmediate()
        except Exception as e:
            publish(env, "failed", detail="snap:" + str(e)); return
        publish(env, "completed", position=round(target, 3), durationMs=0)
        log("completed", commandId=env.get("commandId"), vcId=vcid, lane=lane, execution="axis",
            durationMs=0, cur=round(target, 2), snapshot=(durMs <= 0))
        return

    active[lane] = {"kind": "axis", "env": env, "vcId": vcid, "control": control, "j": j,
                    "start": start, "target": target, "t0": simNowMs(), "durMs": durMs}
    publish(env, "started", position=round(start, 3))
    log("start", commandId=env.get("commandId"), vcId=vcid, lane=lane, execution="axis",
        fromPos=round(start, 2), toPos=round(target, 2), durMs=int(durMs), recv=env.get("_recv"))

# --------------------------------------------------- routine executor
def hookRoutine(vcid, routineName, routine):
    key = (vcid, routineName)
    if key in _hooked:
        return
    def onScope(*a):
        _scopeCount[key] = _scopeCount.get(key, 0) + 1
    try:
        routine.OnScopeExecuted = onScope   # assignment replaces; cannot accumulate
        _hooked.add(key)
    except Exception as e:
        log("scope_hook_unavailable", vcId=vcid, routine=routineName, err=str(e))


def configureGrasp(robotName, partName):
    """VC cannot grasp through an EXCLUDED owner, and the part's owner changes between pick
    and place - so the filter is rebuilt from the part's CURRENT parent chain every time."""
    robot = app.findComponent(robotName)
    part = app.findComponent(partName)
    if robot is None or part is None:
        return None
    chain = parentChain(part)
    allow = set(chain)
    ex = robot.getProperty("SignalActions::ExcludeGrasping")
    if ex is not None:
        try:
            ex.Value = [c for c in app.Components if str(c.Name) not in allow]
        except Exception:
            pass
    inc = robot.getProperty("SignalActions::GraspIncludeEmptyAssemblies")
    if inc is not None:
        for candidate in ("Yes -Take", "Yes"):
            try:
                inc.Value = candidate; break
            except Exception:
                continue
    multi = robot.getProperty("SignalActions::MultiGrasp")
    if multi is not None:
        try:
            multi.Value = False
        except Exception:
            pass
    return chain


def startRoutine(env, lane, chainIdx=0):
    """A command may name one routine or an ORDERED CHAIN. A chain is one atomic task: the
    lane is held for the whole of it, so the shadow performs the entire task from the rig's
    task-start state rather than replaying its tail after the rig has already finished."""
    vcid = env.get("vcId")
    chain = env.get("chain")
    name = chain[chainIdx] if chain else env.get("routine")
    verify = env.get("verify")
    ex = resolveExecutor(vcid)
    if ex is None:
        publish(env, "failed", detail="executor_not_found")
        log("failed", commandId=env.get("commandId"), vcId=vcid, reason="executor_not_found"); return
    try:
        routine = ex.Program.findRoutine(name)
    except Exception as e:
        publish(env, "failed", detail="findRoutine:" + str(e)); return
    if routine is None:
        publish(env, "failed", detail="routine_not_found:" + str(name))
        log("failed", commandId=env.get("commandId"), vcId=vcid, reason="routine_not_found",
            routine=name); return

    # SNAPSHOT: never replay a grasp. Report parentage truthfully instead.
    if env.get("snapshot"):
        if verify:
            attached, chain = chainHasCarrier(verify.get("part"), verify.get("carriers") or [])
            want = bool(verify.get("attached"))
            if attached is not None and attached != want:
                publish(env, "desync", detail="material_parentage", attached=attached,
                        expected=want, chain=chain)
                log("desync", commandId=env.get("commandId"), vcId=vcid, lane=lane,
                    attached=attached, expected=want, chain=chain)
                return
        publish(env, "completed", snapshot=True, replayed=False)
        log("completed", commandId=env.get("commandId"), vcId=vcid, lane=lane,
            execution="routine", snapshot=True, replayed=False)
        return

    key = (vcid, name)
    hookRoutine(vcid, name, routine)
    # No factor on the wire means "run it as taught". Restoring rather than doing nothing is
    # what makes removing the setting from the config actually put the model back.
    # Retime only where the config asks for it. Without a factor there is nothing to undo
    # unless this routine was scaled before, so an untouched robot is never written to.
    sf = env.get("speedFactor")
    if sf or _appliedScale().get("stmt:%s/%s" % (vcid, name)):
        sf = float(sf) if sf else 1.0
        scaleRobotSpeed(ex, vcid, sf, env.get("taughtJointLimits"))
        scaleRoutineMotion(routine, vcid, name, sf, env.get("dwellFactor"))
    chainBefore = configureGrasp(vcid, verify.get("part")) if verify else None

    item = {"kind": "routine", "env": env, "vcId": vcid, "routine": name, "key": key,
            "verify": verify, "t0": simNowMs(),
            "scopeBase": _scopeCount.get(key, 0), "started": False,
            "timeoutMs": float(env.get("timeoutMs") or DEFAULT_TIMEOUT_MS),
            "settleMs": float(env.get("settleMs") or ROUTINE_SETTLE_MS),
            "lastJv": None, "stillSince": simNowMs(), "carried0": carriedBy(vcid),
            "chain": chain, "chainIdx": chainIdx, "t0chain": simNowMs(),
            "chainBefore": chainBefore, "ex": ex}
    if chain and chainIdx > 0:
        item["t0chain"] = env.get("_t0chain", item["t0chain"])
    try:
        ex.callRoutine(routine, False)      # NON-BLOCKING. Never callRoutine(routine).
    except Exception as e:
        publish(env, "failed", detail="callRoutine:" + str(e))
        log("failed", commandId=env.get("commandId"), vcId=vcid, err=str(e)); return

    try:                                    # a short routine can start and finish between
        if ex.CurrentStatement is not None:  # ticks - latch the start immediately
            item["started"] = True
    except Exception:
        pass

    active[lane] = item
    # Only the first routine of a chain is a dispatch of the COMMAND; the rest are steps
    # within the same atomic task, so they never re-publish and never re-count.
    if chainIdx == 0:
        publish(env, "dispatched")
    log("dispatched" if chainIdx == 0 else "chain_step_start",
        commandId=env.get("commandId"), vcId=vcid, lane=lane, routine=name,
        chainStep=("%d/%d" % (chainIdx + 1, len(chain))) if chain else None,
        chainBefore=chainBefore, recv=env.get("_recv"))


def reportTaskError(vcid, env, measuredMs):
    """How far the shadow's whole robot task is from the rig's, and the factor that
    would close it.

    speedFactor is only correct for the taught program it was derived against. Re-teach
    the robot - add a descent, change a pose, delete a dwell - and the taught duration
    moves, so a fixed factor silently drifts. That is not visible in any single line of
    the log today: taskMs is there, the rig's number is not. This prints both together
    with the corrected factor, so a re-teach costs one number instead of an evening.

    duration = taught / k, so k_new = k_now * measured / target. Pure arithmetic on a
    measurement, not a guess.
    """
    target = env.get("taskTargetMs")
    if not target:
        return
    try:
        target = float(target)
        if target <= 0:
            return
        k = float(env.get("speedFactor") or 1.0)
        err = measuredMs - target
        log("robot_task_error", vcId=vcid, taskMs=int(measuredMs), targetMs=int(target),
            errorMs=int(err), errorPct=round(100.0 * err / target, 1),
            factor=k, suggestedFactor=round(k * measuredMs / target, 3))
    except Exception as exc:
        log("robot_task_error_failed", vcId=vcid, error=str(exc))


def finishRoutine(lane, item, how, elapsedMs, quiet=False):
    env, verify = item["env"], item["verify"]
    total = int(simNowMs() - item["t0chain"]) if item.get("chain") else int(elapsedMs)
    if not verify:
        # No verify spec: report what the robot is actually holding, so the carried part
        # can be identified without anyone having to configure it first.
        carrying = carriedBy(item["vcId"])
        if not quiet:
            publish(env, "completed", durationMs=total, verified=False, via=how)
        log("completed" if not quiet else "chain_step",
            commandId=env.get("commandId"), vcId=item["vcId"], lane=lane,
            execution="routine", routine=item["routine"], durationMs=int(elapsedMs),
            taskMs=total, verified=False, via=how, carrying=carrying)
        if not quiet:
            reportTaskError(item["vcId"], env, total)
        return True
    attached, chain = chainHasCarrier(verify.get("part"), verify.get("carriers") or [])
    want = bool(verify.get("attached"))
    if attached == want:
        publish(env, "completed", durationMs=int(elapsedMs), verified=True,
                attached=attached, via=how)
        log("completed", commandId=env.get("commandId"), vcId=item["vcId"], lane=lane,
            execution="routine", routine=item["routine"], durationMs=int(elapsedMs),
            verified=True, attached=attached, chainAfter=chain, via=how)
        return True
    reason = "grasp_not_observed" if want else "release_not_observed"
    publish(env, "failed", detail=reason, durationMs=int(elapsedMs), attached=attached)
    log("failed", commandId=env.get("commandId"), vcId=item["vcId"], lane=lane,
        execution="routine", routine=item["routine"], reason=reason,
        durationMs=int(elapsedMs), chainBefore=item["chainBefore"], chainAfter=chain, via=how)
    return False

# ---------------------------------------------------------------- advance
def advance():
    now = simNowMs()
    controllers, done = [], []

    for lane, s in list(active.items()):
        elapsed = now - s["t0"]

        if s["kind"] == "axis":
            frac = 1.0 if s["durMs"] <= 0 else min(1.0, elapsed / s["durMs"])
            val = s["start"] + (s["target"] - s["start"]) * frac
            try:
                s["control"].setJointTarget(s["j"], val)
            except Exception as e:
                log("failed", commandId=s["env"].get("commandId"), vcId=s["vcId"], err=str(e))
                done.append((lane, "error", elapsed)); continue
            if s["control"] not in controllers:
                controllers.append(s["control"])
            if frac >= 1.0:
                done.append((lane, "time", elapsed))

        elif s["kind"] == "signal":
            # Completion is ENDPOINT evidence, never elapsed time. A cylinder that did not
            # move is reported as such instead of being declared finished by the clock.
            try:
                cur = float(s["control"].getJointValue(s["j"]))
            except Exception as e:
                log("failed", commandId=s["env"].get("commandId"), vcId=s["vcId"], err=str(e))
                done.append((lane, "error", elapsed)); continue
            s["cur"] = cur
            if abs(cur - s["start"]) > s["tol"]:
                s["moved"] = True
            if abs(cur - s["target"]) <= s["tol"]:
                done.append((lane, "endpoint", elapsed))
            elif not s["moved"] and not s["forced"] and elapsed >= SIGNAL_EDGE_GRACE_MS:
                # nothing moved: the servo never saw an edge. Force one, once.
                try:
                    s["sig"].signal(not s["value"]); s["sig"].signal(s["value"])
                    s["forced"] = True
                    log("signal_edge_forced", commandId=s["env"].get("commandId"),
                        vcId=s["vcId"], lane=lane, cur=round(cur, 3),
                        target=round(s["target"], 3))
                except Exception as e:
                    log("failed", commandId=s["env"].get("commandId"), vcId=s["vcId"],
                        err=str(e))
            elif elapsed >= s["deadline"]:
                done.append((lane, "no_motion" if not s["moved"] else "not_reached", elapsed))

        else:  # routine
            fired = _scopeCount.get(s["key"], 0) > s["scopeBase"]
            if fired:
                done.append((lane, "scope_event", elapsed)); continue
            busy = None
            try:
                busy = s["ex"].CurrentStatement is not None
            except Exception:
                busy = None
            if busy:
                s["started"] = True
            elif s["started"]:
                done.append((lane, "idle_after_start", elapsed)); continue
            # Positive material evidence completes a routine we may never have seen busy
            # (the fast release routines run in 1-6 ms).
            v = s["verify"]
            if v and not s["started"]:
                attached, _ = chainHasCarrier(v.get("part"), v.get("carriers") or [])
                if attached is not None and attached == bool(v.get("attached")):
                    done.append((lane, "material_evidence", elapsed)); continue

            # MOTION SETTLED: the robot has stopped. Whatever the executor is still doing
            # is a taught delay with no counterpart on the rig, so the shadow moves on and
            # the next dispatch (clearCallStack) cancels the remainder.
            jv = robotJoints(s["ex"])
            if jv is not None:
                # What the robot is holding is read EVERY tick, not only while it is still:
                # the taught grasp lands inside a dwell that the servo's own jitter still
                # reports as motion, so a grasp seen only when stationary is never seen.
                n0 = s.get("carried0")
                held = len(carriedBy(s["vcId"])) if n0 is not None else None
                if held is not None and held > len(n0) and not s.get("jvAtGrasp"):
                    s["jvAtGrasp"] = jv
                if s.get("jvAtGrasp") and not s.get("retracted") \
                        and excursion(jv, s["jvAtGrasp"]) > RETRACT_EPS:
                    s["retracted"] = True

                if jointsMoved(jv, s.get("lastJv")):
                    s["lastJv"] = jv
                    s["stillSince"] = now
                else:
                    # THE TAUGHT DWELL AFTER A RELEASE IS THE PART SETTLING, AND IT IS KEPT.
                    # This used to complete the step the instant the part detached, on the
                    # grounds that Partplace's trailing Delay 1.6 was pure lateness with no
                    # rig counterpart. dwellFactor superseded that: the dwell is now run at
                    # the rig's own rate, so it IS the rig's settle time, not lateness - and
                    # cutting it withdrew the gripper while the part was still falling.
                    # A GRASP is still deliberately not enough to end a step: it is always
                    # followed by a retract (Partpick grips at statement 5 of 7), so
                    # completing there would cancel the lift and drag the part. Grasp THEN a
                    # real retract THEN stillness is - there is nothing taught after a lift.
                    still = now - s.get("stillSince", now)
                    if s.get("retracted") and still >= PICK_SETTLE_MS:
                        done.append((lane, "picked", elapsed)); continue
                    if still >= s["settleMs"]:
                        done.append((lane, "motion_settled", elapsed)); continue

            if elapsed >= s["timeoutMs"]:
                done.append((lane, "timeout", elapsed)); continue
            if not s["started"] and elapsed >= START_GRACE_MS:
                # No scope event, never seen busy, no material evidence. A routine that
                # ran and finished between polls is indistinguishable from one that never
                # ran, so this is UNKNOWN - not a failure. Report it honestly as
                # unverified and release the lane; blocking the robot forever on a
                # heuristic is what stranded Home behind Partplace.
                done.append((lane, "unobserved", elapsed)); continue

    for c in controllers:
        try:
            c.moveImmediate()
        except Exception as e:
            log("failed", vcId="(controller)", err=str(e))

    for lane, how, elapsed in done:
        s = active.pop(lane, None)
        if s is None:
            continue
        env = s["env"]
        if s["kind"] == "axis":
            if how == "error":
                continue
            publish(env, "completed", position=round(s["target"], 3), durationMs=int(elapsed))
            log("completed", commandId=env.get("commandId"), vcId=s["vcId"], lane=lane,
                execution="axis", durationMs=int(elapsed), cur=round(s["target"], 2))
        elif s["kind"] == "signal":
            cur = s.get("cur", s["start"])
            err = abs(cur - s["target"])
            ev = {"openState": signalValueOf(s["openSig"]) if s["openSig"] else None,
                  "closedState": signalValueOf(s["closedSig"]) if s["closedSig"] else None}
            if how == "endpoint":
                publish(env, "completed", position=round(cur, 3), durationMs=int(elapsed),
                        endpointError=round(err, 3), **ev)
                log("completed", commandId=env.get("commandId"), vcId=s["vcId"], lane=lane,
                    execution="signal", durationMs=int(elapsed), jointBefore=round(s["start"], 3),
                    jointAfter=round(cur, 3), target=round(s["target"], 3),
                    endpointError=round(err, 3), forcedEdge=s["forced"], **ev)
            elif how != "error":
                reason = "signal_no_motion" if how == "no_motion" else "target_not_reached"
                publish(env, "failed", detail=reason, position=round(cur, 3),
                        durationMs=int(elapsed), endpointError=round(err, 3), **ev)
                log("failed", commandId=env.get("commandId"), vcId=s["vcId"], lane=lane,
                    execution="signal", reason=reason, durationMs=int(elapsed),
                    jointBefore=round(s["start"], 3), jointAfter=round(cur, 3),
                    target=round(s["target"], 3), endpointError=round(err, 3),
                    forcedEdge=s["forced"], signalValue=s["value"], **ev)
        else:
            if how == "timeout":
                publish(env, "timeout", durationMs=int(elapsed), detail="routine_timeout")
                log("timeout", commandId=env.get("commandId"), vcId=s["vcId"], lane=lane,
                    routine=s["routine"], durationMs=int(elapsed))
                flush(lane, "routine_timeout")
            elif how == "unobserved":
                publish(env, "completed", durationMs=int(elapsed), verified=False,
                        observed=False, via="unobserved")
                log("completed", commandId=env.get("commandId"), vcId=s["vcId"], lane=lane,
                    execution="routine", routine=s["routine"], durationMs=int(elapsed),
                    verified=False, observed=False, via="unobserved")
            else:
                nxt = s["chainIdx"] + 1
                more = s["chain"] and nxt < len(s["chain"])
                if not finishRoutine(lane, s, how, elapsed, quiet=bool(more)):
                    flush(lane, "verify_failed")
                elif more:
                    # same atomic task: hold the lane and run the next routine. The fresh
                    # callRoutine clears the call stack, cancelling any residual dwell.
                    env["_t0chain"] = s["t0chain"]
                    startRoutine(env, lane, nxt)


def flush(lane, reason):
    q = pending.get(lane) or []
    if q:
        log("lane_flush", lane=lane, dropped=len(q), reason=reason)
        del q[:]

# ------------------------------------------------------------------ loop
STARTERS = {"signal": startSignal, "axis": startAxis, "routine": startRoutine}


def silenceExecutors():
    """Stop every robot in the layout from running its own program.

    A VC executor with IsEnabled True runs its taught program the moment the simulation
    starts - so a robot moves on its own, on its own schedule, with nothing on the rig
    asking it to. resolveExecutor disables one, but only when that robot's FIRST command
    arrives: measured, the UR3e ran unattended for 46 s between the simulation starting and
    its first Partpick. In a shadow NOTHING may move except in answer to the rig, so every
    executor is silenced up front. Generic - it needs no names and no config, because the
    rule is true of every robot here."""
    n = 0
    try:
        for c in app.Components:
            try:
                ex = c.findBehaviour("Executor")
            except Exception:
                continue
            if ex is None:
                continue
            try:
                if ex.IsEnabled:
                    ex.IsEnabled = False
                    n += 1
            except Exception:
                pass
    except Exception as e:
        log("silence_executors_failed", err=str(e))
    if n:
        log("executors_silenced", count=n)
    return n


def OnRun():
    reset_state("run")
    _simFallbackMs[0] = 0.0
    silenceExecutors()
    log("gateway_up", tick_ms=int(TICK_S * 1000), simClock=_clockKind())
    publish_ready("run")
    while True:
        delay(TICK_S)
        _simFallbackMs[0] += TICK_S * 1000.0
        for lane in list(pending.keys()):
            q = pending.get(lane)
            if not q or lane in active:
                continue                      # lane busy: preserves mechanism order
            env = q.pop(0)
            STARTERS[env["execution"]](env, lane)
        advance()


def _clockKind():
    try:
        getSimulation().SimTime
        return "SimTime"
    except Exception:
        return "tick-accumulator"


def abort_inflight(why):
    """A stop or reset invalidates every in-flight item: the simulation clock will not
    advance, the servos are frozen mid-stroke and the executors' state is unknown.
    Fail them explicitly so no lane is left holding a command that can never finish, and
    so the next Run/Reset resync starts from a clean slate."""
    for lane, s in list(active.items()):
        publish(s["env"], "failed", detail=why)
        log("failed", commandId=s["env"].get("commandId"), vcId=s.get("vcId"), lane=lane,
            execution=s.get("kind"), reason=why)
    active.clear()


def OnReset():
    abort_inflight("simulation_reset")
    reset_state("reset")
    _hooked.clear()                 # handlers are re-assigned (not accumulated) on next use
    _simFallbackMs[0] = 0.0
    silenceExecutors()              # a reset re-enables them; nothing may free-run
    publish_ready("reset")


def OnStop():
    n = len(active)
    log("gateway_down", pending=sum(len(q) for q in pending.values()), active=n)
    abort_inflight("simulation_stopped")
