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
# routine the rig drives, or the routine is cut before its work is done. In this program
# the longest such dwell is 1 s (Partpick delays either side of its gripper output), so
# 1500 ms leaves 50% margin. Override per command with "settleMs" on the wire.
ROUTINE_SETTLE_MS = 1500.0
JOINT_EPS = 1e-4                # joint units; below this the robot counts as stationary

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


def reset_state(why):
    pending.clear(); active.clear()
    del seenIds[:]
    lastSeq.clear()
    epochSeen[0] = None
    _scopeCount.clear()
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
    (so the stock changeSpeed lands in the same place if it re-runs)."""
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
    chainBefore = configureGrasp(vcid, verify.get("part")) if verify else None

    item = {"kind": "routine", "env": env, "vcId": vcid, "routine": name, "key": key,
            "verify": verify, "t0": simNowMs(),
            "scopeBase": _scopeCount.get(key, 0), "started": False,
            "timeoutMs": float(env.get("timeoutMs") or DEFAULT_TIMEOUT_MS),
            "settleMs": float(env.get("settleMs") or ROUTINE_SETTLE_MS),
            "lastJv": None, "stillSince": simNowMs(),
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
    if chainIdx == 0:
        publish(env, "dispatched")
    log("dispatched", commandId=env.get("commandId"), vcId=vcid, lane=lane, routine=name,
        chainStep=("%d/%d" % (chainIdx + 1, len(chain))) if chain else None,
        chainBefore=chainBefore, recv=env.get("_recv"))


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
                if jointsMoved(jv, s.get("lastJv")):
                    s["lastJv"] = jv
                    s["stillSince"] = now
                elif now - s.get("stillSince", now) >= s["settleMs"]:
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


def OnRun():
    reset_state("run")
    _simFallbackMs[0] = 0.0
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
    publish_ready("reset")


def OnStop():
    n = len(active)
    log("gateway_down", pending=sum(len(q) for q in pending.values()), active=n)
    abort_inflight("simulation_stopped")
