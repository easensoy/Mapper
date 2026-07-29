"""Automated acceptance tests for the SMC rig -> Visual Components shadow.

Runs the REAL vc_gateway.py source against a mock VC runtime (tests/mock_vc.py) and the
REAL statesync.py Bridge in dry mode. No simulator, no broker, no sleeps - virtual time,
so every result is deterministic.

    python tests/test_shadow.py
"""
import json
import os
import sys
import types

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, ROOT)

import mock_vc as M                                          # noqa: E402

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    print("{:4}  {:<52} {}".format("PASS" if ok else "FAIL", name, detail))
    return ok


# ------------------------------------------------------------------ scene
class Scene(object):
    """Five signal cylinders, five axes (pnp shares one controller), four routine owners."""

    def __init__(self, fire_scope=True, report_busy=True, stuck=None, stuck_true=None,
                 ur_part=False):
        self.app = M.Application()
        self.gw = M.Component("0 #2")
        self.app.Components.append(self.gw)
        self.ctl = {}
        self.sig = {}

        for vcid, comp_name, openv, closedv in (
                ("Pusher", "Pusher #2", 0.0, 116.0),
                ("CheckerComp", "Checker", 0.0, 30.0),
                ("TransferComp", "Transfer", 0.0, 209.0),
                ("Ejector", "Transfer #3", 0.0, 90.0),
                ("Clamp", "WMG Clamp", 0.0, 32.4827386160209)):
            c = M.Controller(["PushJoint"])
            node = M.Node(vcid, "PushJoint", c)
            comp = M.Component(comp_name, [node], {},
                               {"PushSpeed": 100.0, "PushAcceleration": 1000.0})
            s = M.Signal(comp, c, 0, openv, closedv,
                         initial=(vcid in (stuck_true or ())),
                         stuck=(vcid in (stuck or ())))
            comp._beh["PushJoint_ActionSignal"] = s
            self.app.Components.append(comp)
            self.app.signals.append(s)
            self.ctl[vcid], self.sig[vcid] = c, s

        swivel_ctl = M.Controller(["J1"])
        self.app.Components.append(
            M.Component("BearingPnPComp", [M.Node("SviwelArmComp", "J1", swivel_ctl)]))
        self.ctl["SviwelArmComp"] = swivel_ctl

        pnp_ctl = M.Controller(["Z", "X"])              # shared controller, as in the real model
        self.app.Components.append(M.Component("PnPComp", [
            M.Node("pnp_vertical", "Z", pnp_ctl), M.Node("pnp_horizantal", "X", pnp_ctl)]))
        self.ctl["pnp_vertical"] = self.ctl["pnp_horizantal"] = pnp_ctl

        cov_ctl = M.Controller(["V", "H"])
        self.app.Components.append(M.Component("CoverComp", [
            M.Node("CoverPnp_Vertical", "V", cov_ctl), M.Node("CoverPnp_Horizantal", "H", cov_ctl)]))
        self.ctl["CoverPnp_Vertical"] = self.ctl["CoverPnp_Horizantal"] = cov_ctl

        # material + carriers, so parent-chain verification is real
        self.bearing = M.Component("Bearing")
        self.base = M.Component("bearing_base")
        self.carrier = M.Component("SviwelArmComp")
        self.arm = M.Component("Swivel Arm")
        self.carrier.Parent = self.arm
        self.bearing.Parent = self.base
        for c in (self.bearing, self.base, self.carrier, self.arm):
            self.app.Components.append(c)

        def take():
            self.bearing.Parent = self.carrier

        def release():
            self.bearing.Parent = self.base

        arm_ex = M.Executor(self.app, self.arm, ["OpenGripper", "CloseGripper"],
                            {"OpenGripper": 38.0, "CloseGripper": 4.0},
                            {"OpenGripper": take, "CloseGripper": release},
                            fire_scope=fire_scope, report_busy=report_busy)
        self.arm._beh["Executor"] = arm_ex

        self.ur3e = M.Component("UR3e")
        ur_ctl = M.Controller(["J1", "J2", "J3", "J4", "J5", "J6"])
        self.ctl["UR3e"] = ur_ctl
        # measured from the taught program: Partpick moves then dwells 2 s; Partplace has
        # NO motion at all - a gripper output and a ~5 s Delay; Home is pure motion.
        ur_durations = {"Partpick": 6679.0, "Partplace": 4979.0, "Home": 779.0}
        ur_motion = {"Partpick": 4679.0, "Partplace": 0.0, "Home": 779.0}
        ur_at = {}
        if ur_part:
            # The real taught program, to the millisecond it was observed running:
            #   Partpick   2299 ms motion . Delay 1.0 . GRIP . Delay 1.0 . retract  (4299)
            #   Partplace  2479 ms motion . Delay 1.0 . RELEASE . Delay 1.6         (5079)
            # The grip lands MID-routine with a retract still to come; the release is the
            # last physical act. That asymmetry is what the gateway has to respect.
            self.tool = M.Component("2F-85")
            self.tool.Parent = self.ur3e
            self.ur_part = M.Component("Part")
            self.hopper = M.Component("hopper_nest")
            self.ur_part.Parent = self.hopper
            for c in (self.tool, self.ur_part, self.hopper):
                self.app.Components.append(c)
            # Partpick's motion is modelled contiguous to 4299 because that is what the
            # gateway OBSERVES: its middle dwell never registers as 1500 ms of stillness,
            # so the window only opens after the retract - reproducing the real 5799 ms.
            ur_durations = {"Partpick": 4299.0, "Partplace": 5079.0, "Home": 779.0}
            ur_motion = {"Partpick": 4299.0, "Partplace": 2479.0, "Home": 779.0}
            ur_at = {"Partpick": (3299.0, lambda: setattr(self.ur_part, "Parent", self.tool)),
                     "Partplace": (3479.0, lambda: setattr(self.ur_part, "Parent", self.hopper))}
        ur_ex = M.Executor(self.app, self.ur3e, ["Partpick", "Partplace", "Home"],
                           ur_durations, motion=ur_motion, effects_at=ur_at,
                           controller=ur_ctl,
                           fire_scope=fire_scope, report_busy=report_busy)
        self.ur3e._beh["Executor"] = ur_ex
        self.app.Components.append(self.ur3e)

        # shaft + cover grippers, so a full-cycle replay exercises every routine owner
        self.parts = {"Bearing": self.bearing}
        self.grip_ex = {}
        for robot_name, carrier_name, part_name, take_r, rel_r, tms, rms in (
                ("pnp", "Shaft_Gripper", "Shaft", "CloseGripper", "OpenGripper", 1153.0, 1.0),
                ("coverpnp", "CoverPnp_Gripper", "TopCover", "grasp", "release", 1192.0, 2.0)):
            robot = M.Component(robot_name)
            carrier = M.Component(carrier_name)
            carrier.Parent = robot
            part = M.Component(part_name)
            rest = M.Component(part_name + "_rest")
            part.Parent = rest
            for c in (robot, carrier, part, rest):
                self.app.Components.append(c)
            self.parts[part_name] = part

            def mk(p, c):
                return lambda: setattr(p, "Parent", c)
            ex = M.Executor(self.app, robot, [take_r, rel_r], {take_r: tms, rel_r: rms},
                            {take_r: mk(part, carrier), rel_r: mk(part, rest)},
                            fire_scope=fire_scope, report_busy=report_busy)
            robot._beh["Executor"] = ex
            self.grip_ex[robot_name] = ex

        self.app.executors = [arm_ex, ur_ex] + list(self.grip_ex.values())
        self.arm_ex, self.ur_ex = arm_ex, ur_ex


class Gateway(object):
    """Loads the real gateway source with the mock runtime bound in."""

    def __init__(self, scene, max_ticks=4000):
        self.scene = scene
        self.ticks = 0
        self.max_ticks = max_ticks
        M.CLOCK.ms = 0.0

        class Stop(Exception):
            pass
        self.Stop = Stop

        def delay(sec):
            self.ticks += 1
            if self.ticks > self.max_ticks:
                raise Stop()
            M.CLOCK.ms += sec * 1000.0
            scene.app.tick()

        vcs = types.ModuleType("vcScript")
        vcs.getApplication = lambda: scene.app
        vcs.getComponent = lambda: scene.gw
        vcs.VC_STRING = M.VC_STRING
        vcs.delay = delay
        vcs.getSimulation = lambda: M.Simulation()
        sys.modules["vcScript"] = vcs

        src = open(os.path.join(ROOT, "vc_gateway.py")).read()
        self.ns = {"__name__": "vc_gateway"}
        exec(compile(src, "vc_gateway.py", "exec"), self.ns)
        self.ns["datetime"] = M.FakeDateTime          # virtual clock for elapsed-time maths
        self.ns["_log"] = None                        # keep the real log file untouched
        self.events = []
        _orig = self.ns["log"]

        def cap(event, **kw):
            rec = dict(kw); rec["event"] = event; rec["at"] = M.CLOCK.ms
            self.events.append(rec)
        self.ns["log"] = cap
        self.statuses = []
        _pub = self.ns["publish"]

        def cappub(env, status, **kw):
            self.statuses.append((env.get("commandId") if env else None, status, kw))
            _pub(env, status, **kw)
        self.ns["publish"] = cappub

    def send(self, env):
        self.scene.gw._props["CommandJson"].Value = json.dumps(env)
        self.ns["OnCommandChanged"](self.scene.gw._props["CommandJson"])

    def run(self, ticks):
        self.max_ticks = self.ticks + ticks
        try:
            self.ns["OnRun"]()
        except self.Stop:
            pass

    def pump(self, ticks):
        """Advance the loop body without re-running OnRun's reset/ready preamble."""
        self.max_ticks = self.ticks + ticks
        try:
            while True:
                self.ns["delay"](self.ns["TICK_S"])
                for lane in list(self.ns["pending"].keys()):
                    q = self.ns["pending"].get(lane)
                    if not q or lane in self.ns["active"]:
                        continue
                    env = q.pop(0)
                    self.ns["STARTERS"][env["execution"]](env, lane)
                self.ns["advance"]()
        except self.Stop:
            pass

    def ev(self, name):
        return [e for e in self.events if e["event"] == name]


_ID = [0]


def env(vcId, lane, execution, **kw):
    _ID[0] += 1
    e = {"schemaVersion": 2, "epoch": "E1", "commandId": "E1#%d" % _ID[0], "seq": _ID[0],
         "vcId": vcId, "lane": lane, "execution": execution}
    e.update(kw)
    return e


# ============================================================== gateway tests
def t_routine_observed():
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("Swivel Arm", "bearing", "routine", routine="OpenGripper", stateName="open",
               verify={"part": "Bearing", "carriers": ["Swivel Arm", "SviwelArmComp"],
                       "attached": True}))
    g.pump(20)
    done = g.ev("completed")
    ok = bool(done) and done[0].get("via") in ("scope_event", "idle_after_start",
                                              "material_evidence")
    check("routine completion is OBSERVED not assumed", ok,
          "via=%s" % (done[0].get("via") if done else "none"))
    check("gateway never calls blocking callRoutine",
          s.arm_ex.blocking_calls == 0 and s.ur_ex.blocking_calls == 0,
          "blocking=%d" % (s.arm_ex.blocking_calls + s.ur_ex.blocking_calls))
    check("dispatched status published before completion",
          any(st == "dispatched" for _, st, _ in g.statuses))
    return g


def t_grasp_chain():
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("Swivel Arm", "bearing", "routine", routine="OpenGripper",
               verify={"part": "Bearing", "carriers": ["Swivel Arm", "SviwelArmComp"],
                       "attached": True}))
    g.pump(20)
    took = [e for e in g.ev("completed") if e.get("verified")]
    ok_take = bool(took) and took[0].get("attached") is True
    check("gripper ATTACH verified by parent chain", ok_take,
          "chain=%s" % (took[0].get("chainAfter") if took else None))

    g.send(env("Swivel Arm", "bearing", "routine", routine="CloseGripper",
               verify={"part": "Bearing", "carriers": ["Swivel Arm", "SviwelArmComp"],
                       "attached": False}))
    g.pump(20)
    rel = [e for e in g.ev("completed") if e.get("verified") and e.get("attached") is False]
    check("gripper RELEASE verified by parent chain", bool(rel),
          "chain=%s" % (rel[0].get("chainAfter") if rel else None))
    check("no failed/timeout during grasp cycle",
          not g.ev("failed") and not g.ev("timeout"))


def t_axis_during_ur3e():
    """The regression that motivated the rework: a 6939 ms UR3e routine must not stretch
    an unrelated axis. Baseline defect was +6948 ms."""
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("UR3e", "robot", "routine", routine="Partpick", stateName="PickPart"))
    g.pump(5)
    g.send(env("SviwelArmComp", "bearing", "axis", target=181.0, durationMs=441))
    g.send(env("pnp_vertical", "shaft", "axis", target=-34.19, durationMs=822))
    g.send(env("Pusher", "pusher", "signal", signalValue=True, strokeDistance=116.0,
               target=116.0, durationMs=716, signalBehaviour="PushJoint_ActionSignal"))
    g.pump(600)
    worst, rows = 0.0, []
    for e in g.ev("completed"):
        if e.get("execution") in ("axis", "signal") and e.get("durationMs") is not None:
            cmd = {"SviwelArmComp": 441, "pnp_vertical": 822, "Pusher": 716}.get(e["vcId"])
            if cmd:
                err = abs(e["durationMs"] - cmd)
                rows.append("%s %d/%d" % (e["vcId"], e["durationMs"], cmd))
                worst = max(worst, err)
    check("axis+signal complete within 250ms while UR3e runs", rows and worst <= 250,
          "worst=%dms  %s" % (worst, ", ".join(rows)))
    ur_done = [e for e in g.ev("completed") if e["vcId"] == "UR3e"]
    check("UR3e routine still completes", bool(ur_done),
          "%dms" % (ur_done[0]["durationMs"] if ur_done else -1))


def t_unobserved_routine():
    """Executor that never fires a scope event and never reports busy - the IsEnabled trap.
    A routine with no evidence either way is UNKNOWN: it must not be reported as verified,
    must not complete early, and must NOT strand the lane. Reporting `never_started` here
    is what left UR3e Home queued behind Partplace forever."""
    s = Scene(fire_scope=False, report_busy=False); g = Gateway(s); g.pump(2)
    s.ur_ex.Controller = None            # no joints to watch -> settle cannot apply
    g.send(env("UR3e", "robot", "routine", routine="Home"))
    g.pump(40)                                   # 800 ms - inside the 3000 ms grace
    check("no completion before the observation grace", not g.ev("completed"),
          "%d early completions" % len(g.ev("completed")))
    g.pump(200)
    done = g.ev("completed")
    ok = bool(done) and done[0].get("via") == "unobserved" and \
        done[0].get("verified") is False and done[0].get("observed") is False
    check("unobserved routine reported honestly, not as success",
          ok, "via=%s verified=%s observed=%s" % (
              done[0].get("via") if done else None,
              done[0].get("verified") if done else None,
              done[0].get("observed") if done else None))
    check("no routine completes in <1ms without evidence",
          not [e for e in done if (e.get("durationMs") or 999) < 1])

    g.send(env("UR3e", "robot", "routine", routine="Partpick"))
    g.pump(20)
    check("lane is released for the next robot command",
          any(e["event"] == "dispatched" and e.get("routine") == "Partpick" for e in g.events))


def t_stuck_routine_timeout():
    """A routine that starts and never finishes must time out on the SIMULATION clock at
    its configured limit, fail the lane explicitly, and release it."""
    s = Scene(); g = Gateway(s); g.pump(2)
    s.ur_ex._durations["Partplace"] = 10 ** 9         # never completes
    s.ur_ex._motion["Partplace"] = 10 ** 9            # and never stops moving
    g.send(env("UR3e", "robot", "routine", routine="Partplace", timeoutMs=2000))
    g.pump(150)
    tmo = g.ev("timeout")
    check("stuck routine times out at its configured limit",
          bool(tmo) and abs(tmo[0]["durationMs"] - 2000) < 100,
          "%sms (want 2000)" % (tmo[0]["durationMs"] if tmo else None))
    check("timed-out routine releases its lane", not g.ns["active"])


def t_motion_settled():
    """The robot defect: a taught routine that stops moving but holds the executor busy in
    a Delay. Partplace has NO motion at all - a gripper output and a ~5 s delay - and
    waiting it out left the shadow 5.8 s behind the rig on every cycle."""
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("UR3e", "robot", "routine", routine="Partplace"))
    g.pump(400)
    done = [e for e in g.ev("completed") if e["vcId"] == "UR3e"]
    ok = bool(done) and done[0].get("via") == "motion_settled"
    check("a stationary routine completes on motion settling", ok,
          "via=%s at %sms (taught 4979)" % (done[0].get("via") if done else None,
                                            done[0].get("durationMs") if done else None))
    check("...well before the taught delay would have ended",
          bool(done) and done[0]["durationMs"] < 2500,
          "%sms" % (done[0]["durationMs"] if done else None))

    # a routine that IS moving must never be cut short
    g2 = Gateway(Scene()); g2.pump(2)
    g2.send(env("UR3e", "robot", "routine", routine="Partpick"))
    g2.pump(500)
    pick = [e for e in g2.ev("completed") if e["vcId"] == "UR3e"]
    check("a moving routine runs to its natural end",
          bool(pick) and pick[0]["durationMs"] > 4679,
          "%sms (motion ends 4679, taught 6679)" % (pick[0]["durationMs"] if pick else None))
    check("carried part reported without any config",
          bool(pick) and "carrying" in pick[0], str(pick[0].get("carrying") if pick else None))


def t_release_completes_step():
    """Letting go is the last physical act of a place, so the shadow moves on the moment it
    happens. Partplace then dwells another 1.6 s with nothing left to do - pure lateness
    against a rig that has already parked. A GRASP is the opposite: Partpick grips at
    statement 5 of 7 and still has to retract, so completing on it would drag the part."""
    s = Scene(ur_part=True); g = Gateway(s); g.pump(2)
    s.ur_part.Parent = s.tool            # the robot arrives at Partplace holding the part
    g.send(env("UR3e", "robot", "routine", routine="Partplace"))
    g.pump(400)
    done = [e for e in g.ev("completed") if e["vcId"] == "UR3e"]
    check("a place completes on the release, not on a still-window",
          bool(done) and done[0].get("via") == "released",
          "via=%s" % (done[0].get("via") if done else None))
    check("...at the release, ahead of the settle deadline and the taught 5079 ms",
          bool(done) and 3400 <= done[0]["durationMs"] < 3979,
          "%sms (release 3479, settle would fire 3979)" % (done[0]["durationMs"] if done else None))
    check("the part really did leave the gripper",
          "Part" not in (done[0].get("carrying") or []) if done else False,
          str(done[0].get("carrying") if done else None))

    g2 = Gateway(Scene(ur_part=True)); g2.pump(2)
    g2.send(env("UR3e", "robot", "routine", routine="Partpick"))
    g2.pump(400)
    pick = [e for e in g2.ev("completed") if e["vcId"] == "UR3e"]
    check("a grasp does NOT end the step - the retract still has to run",
          bool(pick) and pick[0]["durationMs"] > 4299,
          "%sms (grip 3299, retract ends 4299, observed 5799)" % (
              pick[0]["durationMs"] if pick else None))
    check("...and the part is still held when the step ends",
          "Part" in (pick[0].get("carrying") or []) if pick else False,
          str(pick[0].get("carrying") if pick else None))


def t_routine_chain():
    """The rig's robot is ONE atomic task: it reports start, then done+ready together at the
    end. Every routine it performs therefore belongs to the start state, and the lane must be
    held for the whole chain."""
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("UR3e", "robot", "routine", routine="Partpick",
               chain=["Partpick", "Partplace", "Home"]))
    g.pump(700)
    order = [d[1] for d in s.ur_ex.dispatches]
    check("the whole chain runs, in order", order == ["Partpick", "Partplace", "Home"],
          str(order))
    done = [e for e in g.ev("completed") if e["vcId"] == "UR3e"]
    check("the chain reports ONE completion for the command", len(done) == 1,
          "%d completions" % len(done))
    steps = [e for e in g.ev("chain_step_start") if e["vcId"] == "UR3e"]
    check("intermediate steps logged, not published", len(steps) == 2, "%d steps" % len(steps))
    task = done[0].get("taskMs") if done else None
    check("task time is the whole chain, and matches the rig's 7827 ms",
          task is not None and abs(task - 7827) <= 900, "%sms vs rig 7827" % task)
    check("no command left the lane early",
          not g.ns["active"] and not g.ns["pending"].get("robot"))


def t_duplicate():
    s = Scene(); g = Gateway(s); g.pump(2)
    e = env("Pusher", "pusher", "signal", signalValue=True, strokeDistance=116.0,
            target=116.0, durationMs=716, signalBehaviour="PushJoint_ActionSignal")
    g.send(e); g.send(dict(e))                    # identical commandId twice
    g.pump(60)
    check("duplicate commandId executed exactly once",
          len(g.ev("enqueue")) == 1 and len(g.ev("start")) == 1,
          "enqueue=%d start=%d" % (len(g.ev("enqueue")), len(g.ev("start"))))
    dups = [x for x in g.ev("reject") if x.get("reason") == "duplicate"]
    check("duplicate rejected explicitly", len(dups) == 1)


def t_signal_executor():
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("Clamp", "clamp", "signal", signalValue=True, strokeDistance=32.4827386160209,
               target=32.4827386160209, durationMs=166, signalBehaviour="PushJoint_ActionSignal"))
    g.pump(30)
    g.send(env("Clamp", "clamp", "signal", signalValue=False, strokeDistance=32.4827386160209,
               target=0.0, durationMs=280, signalBehaviour="PushJoint_ActionSignal"))
    g.pump(40)
    starts = g.ev("start")
    check("signal executor sets the ActionSignal", len(s.sig["Clamp"].history) == 2,
          str([(int(t), v) for t, v in s.sig["Clamp"].history]))
    speeds = [x.get("speed") for x in starts if x["vcId"] == "Clamp"]
    check("asymmetric advance/return speeds preserved",
          len(speeds) == 2 and speeds[0] != speeds[1], "speeds=%s" % speeds)
    # v = D / (0.9 t) ; a = v / (0.1 t)  -> total trapezoid time == t
    want = 32.4827386160209 / (0.9 * 0.166)
    check("PushSpeed computed from stroke and duration",
          abs(speeds[0] - want) < 0.01, "%.2f vs %.2f mm/s" % (speeds[0], want))
    j = s.ctl["Clamp"].Joints[0]
    check("joint MaxSpeed/Accel shaped before signalling",
          j.MaxSpeed > 0 and j.MaxAcceleration > 0,
          "v=%.1f a=%.1f" % (j.MaxSpeed, j.MaxAcceleration))
    check("signal cylinders never driven by moveImmediate",
          s.ctl["Clamp"].moveImmediate_calls == 0,
          "moveImmediate=%d" % s.ctl["Clamp"].moveImmediate_calls)
    durs = [e["durationMs"] for e in g.ev("completed") if e["vcId"] == "Clamp"]
    check("signal durations honour the rig strokes",
          len(durs) == 2 and abs(durs[0] - 166) <= 40 and abs(durs[1] - 280) <= 40,
          "actual=%s want=[166,280]" % durs)


def t_signal_no_motion():
    """A cylinder whose servo never moves it must be reported as signal_no_motion, NOT
    declared complete because durationMs elapsed. This is the Ejector defect."""
    s = Scene(stuck=("Ejector",)); g = Gateway(s); g.pump(2)
    g.send(env("Ejector", "ejector", "signal", signalValue=True, strokeDistance=90.0,
               target=90.0, durationMs=2011, signalBehaviour="PushJoint_ActionSignal"))
    g.pump(400)
    fail = [e for e in g.ev("failed") if e["vcId"] == "Ejector"]
    check("a cylinder that never moves reports signal_no_motion",
          bool(fail) and fail[0].get("reason") == "signal_no_motion",
          "reason=%s" % (fail[0].get("reason") if fail else None))
    check("no time-based completion for a dead cylinder",
          not [e for e in g.ev("completed") if e["vcId"] == "Ejector"])
    check("failure records the endpoint evidence",
          bool(fail) and fail[0].get("jointAfter") is not None
          and fail[0].get("endpointError") is not None,
          "after=%s err=%s" % (fail[0].get("jointAfter") if fail else None,
                               fail[0].get("endpointError") if fail else None))


def t_signal_edge_guarantee():
    """A VC boolean signal fires OnSignal only on a CHANGE. If the signal already holds the
    requested value the stock servo never sees it and the cylinder silently does not move -
    forward dead, return fine. The gateway must force the edge and still reach the endpoint."""
    s = Scene(stuck_true=("Ejector",)); g = Gateway(s); g.pump(2)
    check("signal starts already at the value we are about to command",
          s.sig["Ejector"].Value is True)
    g.send(env("Ejector", "ejector", "signal", signalValue=True, strokeDistance=90.0,
               target=90.0, durationMs=2011, signalBehaviour="PushJoint_ActionSignal"))
    g.pump(300)
    done = [e for e in g.ev("completed") if e["vcId"] == "Ejector"]
    check("forward stroke still reaches the endpoint", bool(done),
          "after=%s err=%s" % (done[0].get("jointAfter") if done else None,
                               done[0].get("endpointError") if done else None))
    check("the edge was forced exactly once",
          s.sig["Ejector"].edges >= 1 and (done and done[0].get("forcedEdge") is True),
          "edges=%d forced=%s" % (s.sig["Ejector"].edges,
                                  done[0].get("forcedEdge") if done else None))
    check("endpoint reached within tolerance",
          bool(done) and done[0]["endpointError"] <= 0.9,
          str(done[0]["endpointError"] if done else None))


def t_stop_safety():
    """A stopped simulation must not age an in-flight move. Wall-clock elapsed turned a
    1125 ms Pusher stroke into 227029 ms and timed out an innocent robot routine."""
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("Pusher", "pusher", "signal", signalValue=True, strokeDistance=116.0,
               target=116.0, durationMs=716, signalBehaviour="PushJoint_ActionSignal"))
    g.send(env("UR3e", "robot", "routine", routine="Partpick"))
    g.pump(5)
    check("both lanes in flight before the stop", len(g.ns["active"]) == 2)
    g.ns["OnStop"]()
    check("stop fails every in-flight item explicitly",
          not g.ns["active"] and
          len([e for e in g.ev("failed") if e.get("reason") == "simulation_stopped"]) == 2,
          "%d failed" % len([e for e in g.ev("failed")
                             if e.get("reason") == "simulation_stopped"]))
    M.CLOCK.ms += 227000.0                       # the sim sat stopped for 3m47s
    g.pump(5)
    check("no phantom duration survives the stop",
          not [e for e in g.ev("completed") if (e.get("durationMs") or 0) > 10000],
          str([e.get("durationMs") for e in g.ev("completed")]))
    check("gateway uses the simulation clock, not the wall clock",
          g.ns["_clockKind"]() == "SimTime", g.ns["_clockKind"]())


def t_lane_serialisation():
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("SviwelArmComp", "bearing", "axis", target=181.0, durationMs=441))
    g.send(env("Swivel Arm", "bearing", "routine", routine="OpenGripper",
               verify={"part": "Bearing", "carriers": ["Swivel Arm", "SviwelArmComp"],
                       "attached": True}))
    g.pump(80)
    order = [(e["event"], e["vcId"], e["at"]) for e in g.events
             if e["event"] in ("start", "dispatched", "completed")]
    axis_done = [a for ev_, v, a in order if ev_ == "completed" and v == "SviwelArmComp"]
    grip_disp = [a for ev_, v, a in order if ev_ == "dispatched" and v == "Swivel Arm"]
    check("same-lane work is strictly serialised",
          axis_done and grip_disp and grip_disp[0] >= axis_done[0],
          "axis done @%s, grasp dispatched @%s" % (axis_done[:1], grip_disp[:1]))


def t_shared_controller():
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("pnp_vertical", "shaft", "axis", target=-34.19, durationMs=822))
    g.send(env("CoverPnp_Vertical", "cover", "axis", target=25.0, durationMs=515))
    g.pump(10)
    before = s.ctl["pnp_vertical"].moveImmediate_calls
    g.pump(1)
    check("one moveImmediate per controller per tick",
          s.ctl["pnp_vertical"].moveImmediate_calls - before == 1,
          "delta=%d" % (s.ctl["pnp_vertical"].moveImmediate_calls - before))


def t_reset():
    s = Scene(); g = Gateway(s); g.pump(2)
    g.send(env("SviwelArmComp", "bearing", "axis", target=181.0, durationMs=441))
    g.pump(3)
    g.ns["OnReset"]()
    check("reset clears pending and active",
          not g.ns["pending"] and not g.ns["active"] and not g.ns["seenIds"])
    check("reset republishes ready",
          json.loads(s.gw._props["StatusJson"].Value).get("status") == "ready")
    check("reset clears routine handler registry", not g.ns["_hooked"])


def t_snapshot_policy():
    s = Scene(); g = Gateway(s); g.pump(2)
    s.bearing.Parent = s.base                        # part is NOT held
    g.send(env("Swivel Arm", "bearing", "routine", routine="OpenGripper", snapshot=True,
               verify={"part": "Bearing", "carriers": ["Swivel Arm", "SviwelArmComp"],
                       "attached": True}))
    g.pump(10)
    check("snapshot never replays a grasp routine",
          len(s.arm_ex.dispatches) == 0, "dispatches=%d" % len(s.arm_ex.dispatches))
    check("snapshot publishes desync on parentage mismatch", bool(g.ev("desync")))


# ============================================================ statesync tests
def t_statesync():
    import statesync as S
    cfg = S.load_config(os.path.join(ROOT, "shadow-config.json"))
    b = S.Bridge(cfg, dry=True)

    src = open(os.path.join(ROOT, "statesync.py")).read()
    check("no FEED_CMD_VCIDS / direct-mapping logic remains",
          "FEED_CMD_VCIDS" not in src and "GRIP_CMD_STATES" not in src)

    envs = []
    b._pub = lambda t, o, retain: envs.append((t, o)) if t == b.cmd_topic else None

    b.on_message("smc/feeder", "{state:1}")
    e = envs[-1][1]
    need = ("schemaVersion", "epoch", "commandId", "seq", "vcId", "state", "stateName",
            "phase", "lane", "execution", "target", "durationMs")
    check("envelope v2 carries every required field",
          all(k in e for k in need), "missing=%s" % [k for k in need if k not in e])
    check("feeder is a SIGNAL contract",
          e["execution"] == "signal" and e["signalValue"] is True and e["durationMs"] == 716,
          "%s v=%s d=%s" % (e["execution"], e["signalValue"], e["durationMs"]))

    for t in ("smc/checker", "smc/transfer", "smc/clamp", "smc/ejector"):
        envs[:] = []
        b.on_message(t, "{state:1}")
        got = envs[-1][1]["execution"] if envs else None
        if not check("%s -> signal contract" % t.split("/")[1], got == "signal", str(got)):
            break

    envs[:] = []
    b.on_message("smc/bearing_pnp", "{state:1}")
    asm = envs[-1][1]
    b.on_message("smc/bearing_pnp", "{state:2}"); b.on_message("smc/bearing_pnp", "{state:0}")
    envs[:] = []
    b.on_message("smc/bearing_pnp", "{state:3}")
    dis_start = envs[-1][1]
    check("swivel duration is phase-keyed",
          asm["phase"] == "asm" and asm["durationMs"] == 441 and
          dis_start["phase"] == "dis" and dis_start["durationMs"] == 620,
          "asm1=%s dis3=%s" % (asm["durationMs"], dis_start["durationMs"]))

    envs[:] = []
    b.on_message("smc/bearing_gripper", "{state:1}")
    gr = envs[-1][1]
    check("bearing gripper keeps INVERTED routine polarity",
          gr["routine"] == "OpenGripper" and gr["verify"]["attached"] is True, gr["routine"])
    envs[:] = []
    b.on_message("smc/robot", "{state:1}")
    check("robot routine override PickPart -> Partpick",
          envs[-1][1]["routine"] == "Partpick", envs[-1][1]["routine"])

    envs[:] = []
    b.on_message("smc/TopCoverSensor", "{state:1}")
    check("misspelled rig topic alias is no longer discarded", not b.warned, str(b.warned))

    for topic, comp in b.comps.items():              # observe every mechanism at least once
        if comp.get("execution") == "sensor":
            continue
        b.on_message(topic, "{state:1}")             # a commandable motion-start state
        b.on_message(topic, "{state:2}")
    envs[:] = []
    b.resync_vc("test")
    kinds = {}
    for _, o in envs:
        kinds.setdefault(o["execution"], []).append(o)
    check("resync: no robot routine replayed",
          not any(o.get("vcId") == "UR3e" for _, o in envs))
    check("resync: gripper snapshots are verify-only",
          all(o.get("snapshot") for o in kinds.get("routine", [])),
          "%d gripper snapshot(s)" % len(kinds.get("routine", [])))
    movers = kinds.get("signal", []) + kinds.get("axis", [])
    check("resync: axes/cylinders restore absolute state",
          bool(movers) and all(o["durationMs"] == 0 for o in movers) and
          all("target" in o and o["target"] is not None for o in movers),
          "%d absolute snapshot(s)" % len(movers))
    check("resync: every gateway mechanism is covered",
          len(movers) + len(kinds.get("routine", [])) == 13,
          "%d of 13 (UR3e deliberately excluded)" % (len(movers) + len(kinds.get("routine", []))))

    envs[:] = []
    b.on_message("smc/BearingSensor", "{state:0}")
    b._pub = lambda t, o, retain: envs.append((t, o))
    b.last_state.pop("smc/BearingSensor", None)
    b.on_message("smc/BearingSensor", "{state:0}")
    st = [o for t, o in envs if t.endswith("/bearingsensor/state")]
    check("bearing sensor polarity is ACTIVE-LOW",
          bool(st) and st[0]["present"] is True, str(st[0]["present"] if st else None))


def t_csv():
    p = os.path.join(ROOT, "VcMqttMappings.csv")
    rows = [r for r in open(p).read().strip().split("\n")[1:] if r.strip()]
    check("CSV has exactly two mapping rows", len(rows) == 2, "%d rows" % len(rows))
    check("row 1 = command IN (ServerToSimulation -> CommandJson)",
          "ServerToSimulation" in rows[0] and "CommandJson" in rows[0]
          and "vc/command" in rows[0])
    check("row 2 = event OUT (SimulationToServer -> vc/event)",
          "SimulationToServer" in rows[1] and "StatusJson" in rows[1] and "vc/event" in rows[1])
    check("no per-actuator rows remain",
          not any(k in open(p).read() for k in
                  ("PushJoint_ActionSignal", "IN_J1_Action", "/feeder/state",
                   "/checker/state", "/transfer/state", "/clamp/state")))


def main():
    print("=" * 78)
    print("SMC shadow - automated acceptance")
    print("=" * 78)
    print("\n-- gateway: routine execution ------------------------------------------")
    t_routine_observed(); t_grasp_chain()
    print("\n-- gateway: concurrency ------------------------------------------------")
    t_axis_during_ur3e(); t_lane_serialisation(); t_shared_controller()
    print("\n-- gateway: honesty ----------------------------------------------------")
    t_unobserved_routine(); t_stuck_routine_timeout(); t_motion_settled()
    t_release_completes_step(); t_routine_chain(); t_duplicate()
    print("\n-- gateway: signal contract --------------------------------------------")
    t_signal_executor(); t_signal_no_motion(); t_signal_edge_guarantee()
    print("\n-- gateway: stop / clock safety ----------------------------------------")
    t_stop_safety()
    print("\n-- gateway: reset / snapshot -------------------------------------------")
    t_reset(); t_snapshot_policy()
    print("\n-- statesync + wiring --------------------------------------------------")
    t_statesync(); t_csv()

    ok = sum(1 for _, o, _ in RESULTS if o)
    print("\n" + "=" * 78)
    print("{}/{} passed".format(ok, len(RESULTS)))
    for n, o, d in RESULTS:
        if not o:
            print("  FAIL  {}  {}".format(n, d))
    print("=" * 78)
    return 0 if ok == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
