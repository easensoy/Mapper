# VC execution driver -- rig -> Visual Components digital shadow.
# Runs in component "0 #2" Python Script behaviour (IronPython 2.7).
#
#   uns/.../vc/command  --(whole message, Convert.ToString(Payload))-->  CommandGateway.CommandJson
#   CommandGateway.StatusJson  --(@Json(Value))-->  uns/.../vc/event
#
# MECHANISM LANES: one physical mechanism = one ordered execution lane. The swivel and its
# gripper are the same mechanism, so a move must never start while its grasp is still running.
# Unrelated lanes stay independent.
#
# VC API constraints:
#   * callRoutine(r) BLOCKS until the taught routine finishes -- that is what actually executes
#     the routine and its SignalActions grasp. callRoutine(r, False) returns immediately and,
#     with Executor.IsEnabled False, the grasp was never observed to fire. Gripper routines are
#     milliseconds, so blocking is cheap; only the UR3e pick is long, and it runs alone.
#   * Executor.IsEnabled must stay False -- True breaks the cover actuators.
#   * setJointTarget only sets a target; moveImmediate drives EVERY joint of that controller,
#     so shared-controller axes (pnp Z/X) set their targets first and share one drive per tick.
#   * Component.Parent is read-only: attach happens only via the grasp signal action, so a
#     grasp is verified by the part's parent chain, never assumed.
from vcScript import *
from vcHelpers.Application import *
import json
from datetime import datetime

app = getApplication()
gw = getComponent()

# ----------------------------------------------------------------- CONFIG
LOG = r"C:\VueOneMapper\Tools\statesync\vc_gateway.log"
TICK_S = 0.02
MAX_PENDING = 30
ZERO_DIST_EPS = 0.05

LANES = {
    "bearing": ("SviwelArmComp", "Swivel Arm"),
    "shaft":   ("pnp_vertical", "pnp_horizantal", "pnp"),
    "cover":   ("CoverPnp_Vertical", "CoverPnp_Horizantal", "coverpnp"),
}
SOLO = ("Pusher", "Ejector", "UR3e")          # independent mechanisms, one lane each
LANE_OF = {}
for _lane, _ids in LANES.items():
    for _v in _ids:
        LANE_OF[_v] = _lane
GATEWAY_VCIDS = tuple(LANE_OF) + SOLO
ROBOT_VCIDS = ("Swivel Arm", "pnp", "coverpnp", "UR3e")

# (vcId, routine) -> (part, carrier names, expect_attached)
# Bearing polarity is inverted in this model: OpenGripper TAKES, CloseGripper RELEASES.
GRASP_SPEC = {
    ("Swivel Arm", "OpenGripper"):  ("Bearing", ("Swivel Arm", "SviwelArmComp"), True),
    ("Swivel Arm", "CloseGripper"): ("Bearing", ("Swivel Arm", "SviwelArmComp"), False),
    ("pnp", "CloseGripper"):        ("Shaft", ("pnp", "Shaft_Gripper"), True),
    ("pnp", "OpenGripper"):         ("Shaft", ("pnp", "Shaft_Gripper"), False),
    ("coverpnp", "grasp"):          ("TopCover", ("coverpnp", "CoverPnp_Gripper"), True),
    ("coverpnp", "release"):        ("TopCover", ("coverpnp", "CoverPnp_Gripper"), False),
}
GRASP_PART = {"Swivel Arm": "Bearing", "pnp": "Shaft", "coverpnp": "TopCover"}

# ---------------------------------------------------------------- LOGGING
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

# ------------------------------------------------------------- PROPERTIES
def ensure(name):
    p = gw.getProperty(name)
    if not p:
        p = gw.createProperty(VC_STRING, name)
        p.Value = ""
    return p

cmdProp = ensure('CommandJson')
staProp = ensure('StatusJson')

# ------------------------------------------------------------------ STATE
pending = {}          # lane -> [cmd, ...]
active = {}           # lane -> in-flight axis move
seenIds = []
lastSeq = {}
epochSeen = [None]
_jointCache = {}
_execCache = {}

def laneOf(vcid):
    return LANE_OF.get(vcid, vcid)

# ------------------------------------------------------- STATUS PUBLISHING
def publishStatus(cmd, status, position=None, detail=""):
    staProp.Value = json.dumps({
        "schemaVersion": 1,
        "commandId": cmd.get("commandId"),
        "epoch": cmd.get("epoch"),
        "vcId": cmd.get("ComponentName") or cmd.get("vcId"),
        "ComponentType": cmd.get("ComponentType"),
        "StateName": cmd.get("StateName"),
        "status": status,
        "position": position,
        "detail": detail,
        "ts": datetime.now().isoformat() + "Z",
    })

# --------------------------------------------- COMMAND VALIDATION + ENQUEUE
def OnCommandChanged(prop):
    t_recv = datetime.now()
    raw = prop.Value
    if not raw:
        return
    try:
        m = json.loads(raw)
    except Exception as e:
        log("reject", reason="bad_json", err=str(e)); return

    cid = m.get("commandId")
    vcid = m.get("ComponentName") or m.get("vcId")
    if m.get("MsgTo") != "VC" or vcid not in GATEWAY_VCIDS:
        return
    if not cid or cid in seenIds:
        return

    ep = m.get("epoch")
    if epochSeen[0] is None:
        epochSeen[0] = ep
    elif ep != epochSeen[0]:
        log("epoch_change", old=epochSeen[0], new=ep)
        epochSeen[0] = ep
        lastSeq.clear()

    sq = m.get("sourceSeq")
    if sq is not None and vcid in lastSeq and sq <= lastSeq[vcid]:
        return
    if sq is not None:
        lastSeq[vcid] = sq

    lane = laneOf(vcid)
    q = pending.setdefault(lane, [])
    if len(q) >= MAX_PENDING:
        log("reject", commandId=cid, vcId=vcid, reason="backlog", depth=len(q)); return

    seenIds.append(cid)
    if len(seenIds) > 1000:
        del seenIds[:500]
    m["_t_recv"] = t_recv.strftime("%H:%M:%S.%f")[:-3]
    q.append(m)
    log("enqueue", commandId=cid, vcId=vcid, lane=lane, depth=len(q))

cmdProp.OnChanged = OnCommandChanged

# --------------------------------------------------- ACTUATOR RESOLUTION
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
                    _jointCache[vcid] = (control, j)
                    return _jointCache[vcid]
    return None


def targetPosition(newPos, currentPos, operatorType):
    if operatorType == 'plus':
        return currentPos + newPos
    if operatorType == 'minus':
        return currentPos - newPos
    return newPos

# ------------------------------------------------- ACTUATOR MOTION UPDATE
def startMove(m, lane):
    vcid = m.get("ComponentName")
    r = resolveJoint(vcid)
    if r is None:
        publishStatus(m, "failed", detail="joint_not_found")
        log("failed", commandId=m.get("commandId"), vcId=vcid, reason="joint_not_found")
        return
    control, j = r
    startVal = float(control.getJointValue(j))
    target = float(targetPosition(m.get("ComponentPos"), startVal, m.get("OperatorType")))
    durMs = float(m.get("ExpectedDurationMs") or 300)
    if abs(target - startVal) <= ZERO_DIST_EPS:
        publishStatus(m, "completed", position=startVal)
        log("completed", commandId=m.get("commandId"), vcId=vcid, lane=lane,
            durationMs=0, cur=round(startVal, 2), zeroDistance=True)
        return
    active[lane] = {"cmd": m, "vcId": vcid, "control": control, "j": j,
                    "startVal": startVal, "target": target,
                    "t0": datetime.now(), "durMs": durMs}
    publishStatus(m, "started", position=startVal)
    log("start", commandId=m.get("commandId"), vcId=vcid, lane=lane,
        fromPos=round(startVal, 2), toPos=round(target, 2), durMs=int(durMs),
        t_recv=m.get("_t_recv"))


def stepAxes():
    """Interpolate every in-flight axis one tick, then drive each controller ONCE so
    shared-controller axes stay coherent."""
    now = datetime.now()
    controllers = []
    done = []
    for lane, s in list(active.items()):
        elapsedMs = (now - s["t0"]).total_seconds() * 1000.0
        frac = 1.0 if s["durMs"] <= 0 else min(1.0, elapsedMs / s["durMs"])
        val = s["startVal"] + (s["target"] - s["startVal"]) * frac
        try:
            s["control"].setJointTarget(s["j"], val)
        except Exception as e:
            log("failed", commandId=s["cmd"].get("commandId"), vcId=s["vcId"], err=str(e))
            done.append(lane); continue
        if s["control"] not in controllers:
            controllers.append(s["control"])
        if frac >= 1.0:
            s["_elapsed"] = elapsedMs
            done.append(lane)
    for c in controllers:
        try:
            c.moveImmediate()
        except Exception as e:
            log("failed", vcId="(controller)", err=str(e))
    for lane in done:
        s = active.pop(lane, None)
        if s and "_elapsed" in s:
            publishStatus(s["cmd"], "completed", position=round(s["target"], 2))
            log("completed", commandId=s["cmd"].get("commandId"), vcId=s["vcId"], lane=lane,
                durationMs=int(s["_elapsed"]), cur=round(s["target"], 2))

# ------------------------------------------- ROUTINE / EXECUTOR RESOLUTION
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
        ex.IsEnabled = False
    except Exception:
        pass
    _execCache[vcid] = ex
    return ex


def parentChain(comp):
    names = []
    n = comp
    for _ in range(24):
        if n is None:
            break
        try:
            names.append(str(n.Name))
        except Exception:
            break
        n = getattr(n, "Parent", None)
    return names

# ----------------------------------------------------- GRASP CONFIGURATION
def configureGrasp(robotName, partName):
    """VC cannot grasp a part through an EXCLUDED owner, and the part's owner changes between
    pick and place -- so the filter is rebuilt from the part's CURRENT parent chain every time."""
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
        try:
            inc.Value = "Yes -Take"
        except Exception:
            pass
    multi = robot.getProperty("SignalActions::MultiGrasp")
    if multi is not None:
        try:
            multi.Value = False
        except Exception:
            pass
    return chain

# ------------------------------------------ ROUTINE DISPATCH + VERIFICATION
def runRoutine(m, lane):
    """Blocking: callRoutine returns only when the taught routine (and its grasp action) has
    run, so the parent chain can be checked immediately after. Returns True on success; on a
    material failure the lane is flushed so the mechanism cannot carry on empty."""
    vcid = m.get("ComponentName")
    routine = m.get("StateName")
    ex = resolveExecutor(vcid)
    if ex is None:
        publishStatus(m, "failed", detail="executor_not_found")
        log("failed", commandId=m.get("commandId"), vcId=vcid, reason="executor_not_found")
        return False
    try:
        r = ex.Program.findRoutine(routine)
    except Exception as e:
        publishStatus(m, "failed", detail="findRoutine:" + str(e)); return False
    if r is None:
        publishStatus(m, "failed", detail="routine_not_found:" + str(routine))
        log("failed", commandId=m.get("commandId"), vcId=vcid,
            reason="routine_not_found", routine=routine)
        return False

    part = GRASP_PART.get(vcid)
    chainBefore = configureGrasp(vcid, part) if part else None
    publishStatus(m, "started")
    log("routine", commandId=m.get("commandId"), vcId=vcid, lane=lane, routine=routine,
        chainBefore=chainBefore, t_recv=m.get("_t_recv"))

    t0 = datetime.now()
    try:
        ex.callRoutine(r)              # BLOCKS until the routine and its grasp have executed
    except Exception as e:
        publishStatus(m, "failed", detail="callRoutine:" + str(e))
        log("failed", commandId=m.get("commandId"), vcId=vcid, err=str(e))
        return False
    ms = int((datetime.now() - t0).total_seconds() * 1000)

    spec = GRASP_SPEC.get((vcid, routine))
    if spec is None:
        publishStatus(m, "completed")
        log("completed", commandId=m.get("commandId"), vcId=vcid, lane=lane,
            routine=routine, durationMs=ms, verified=False)
        return True

    partName, carriers, expectAttached = spec
    p = app.findComponent(partName)
    chainAfter = parentChain(p) if p is not None else []
    attached = any(c in chainAfter for c in carriers)
    if attached == expectAttached:
        publishStatus(m, "completed")
        log("completed", commandId=m.get("commandId"), vcId=vcid, lane=lane, routine=routine,
            durationMs=ms, verified=True, attached=attached, chainAfter=chainAfter)
        return True

    reason = "grasp_not_observed" if expectAttached else "release_not_observed"
    publishStatus(m, "failed", detail=reason)
    log("failed", commandId=m.get("commandId"), vcId=vcid, lane=lane, routine=routine,
        reason=reason, durationMs=ms, chainBefore=chainBefore, chainAfter=chainAfter)
    return False


def flushLane(lane, reason):
    q = pending.get(lane) or []
    if q:
        log("lane_flush", lane=lane, dropped=len(q), reason=reason)
        del q[:]

# ------------------------------------------------------------------ OnRun
def OnRun():
    log("gateway_up", lanes=sorted(set(list(LANE_OF.values()) + list(SOLO))))
    while True:
        delay(TICK_S)
        for lane in list(pending.keys()):
            q = pending.get(lane)
            if not q or lane in active:
                continue                      # lane busy: preserves mechanism order
            m = q.pop(0)
            if m.get("ComponentType") == "Robot":
                if not runRoutine(m, lane):
                    flushLane(lane, "routine_failed")
            else:
                startMove(m, lane)
        stepAxes()


def OnStop():
    log("gateway_down", pending=sum(len(q) for q in pending.values()),
        activeAxes=len(active), activeRoutines=0)
