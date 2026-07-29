"""Minimal Visual Components runtime stand-in, faithful to the API surface the gateway uses.

Models exactly what the api.xml evidence says:
  * callRoutine(routine, suspendScript) - suspendScript False returns immediately
  * Executor exposes CurrentStatement (None when not executing) and NO IsRunning/Busy
  * vcRoutine carries OnScopeExecuted (inherited from vcScope), fired on completion
  * moveImmediate drives every joint of a controller to its targets in zero sim time
  * a grasp reparents the part; Component.Parent is how attachment is observed

Time is virtual: delay() advances the clock, which is what releases routines. That makes
every assertion deterministic - no sleeps, no flakiness.
"""
from datetime import datetime, timedelta

BASE = datetime(2026, 7, 26, 12, 0, 0)
VC_STRING = "VC_STRING"


class Clock(object):
    def __init__(self):
        self.ms = 0.0

    def now(self):
        return BASE + timedelta(milliseconds=self.ms)


CLOCK = Clock()


class FakeDateTime(object):
    @staticmethod
    def now():
        return CLOCK.now()


class Prop(object):
    def __init__(self, name, value=None):
        self.Name = name
        self.Value = value


class Joint(object):
    def __init__(self, name):
        self.Name = name
        self.MaxSpeed = 0.0
        self.MaxAcceleration = 0.0
        self.MaxDeceleration = 0.0


class Controller(object):
    def __init__(self, joints):
        self.Joints = [Joint(j) for j in joints]
        self._values = [0.0] * len(joints)
        self._targets = [0.0] * len(joints)
        self.moveImmediate_calls = 0
        self.setJointTarget_calls = 0

    def getJointValue(self, j):
        return self._values[j]

    def setJointTarget(self, j, v):
        self._targets[j] = v
        self.setJointTarget_calls += 1

    def moveImmediate(self):
        self._values = list(self._targets)
        self.moveImmediate_calls += 1

    def moveJoint(self, j, v):                 # stock servo path (blocking in real VC)
        self._targets[j] = v
        self._values[j] = v


class Dof(object):
    def __init__(self, jointName, controller):
        self.Properties = [Prop("Name", jointName), Prop("Controller", controller)]


class Node(object):
    def __init__(self, name, jointName=None, controller=None):
        self.Name = name
        self.Dof = Dof(jointName, controller) if controller else None
        self.Parent = None


class Signal(object):
    """Stock PushJoint_ActionSignal + ServoController_Script, modelled faithfully:

      * EDGE-TRIGGERED. `OnSignal` fires only when the value CHANGES, so signalling the
        value the signal already holds moves nothing. This is the defect the gateway's
        edge guarantee exists to survive.
      * SWEPT. The servo interpolates over the trapezoid implied by the joint's
        MaxSpeed/MaxAcceleration/MaxDeceleration, which the gateway shapes beforehand.
      * `stuck=True` simulates a servo script that is dead: the signal is accepted but
        the joint never moves.
    """

    def __init__(self, comp, controller, j, openv, closedv, initial=False, stuck=False):
        self.comp, self.control, self.j = comp, controller, j
        self.openv, self.closedv = openv, closedv
        self.Value = bool(initial)
        self.stuck = stuck
        self.history = []
        self.edges = 0
        self._move = None

    def signal(self, value):
        v = bool(value)
        self.history.append((CLOCK.ms, v))
        if v == self.Value:
            return                              # no change -> no OnSignal -> no motion
        self.Value = v
        self.edges += 1
        if self.stuck:
            return
        start = self.control.getJointValue(self.j)
        target = self.closedv if v else self.openv
        joint = self.control.Joints[self.j]
        d = abs(target - start)
        sp = joint.MaxSpeed or 100.0
        a = joint.MaxAcceleration or sp
        de = joint.MaxDeceleration or a
        dur = (d / sp + sp / (2.0 * a) + sp / (2.0 * de)) * 1000.0 if d else 0.0
        self._move = (start, target, CLOCK.ms, max(dur, 1.0))

    def tick(self):
        if not self._move:
            return
        start, target, t0, dur = self._move
        frac = min(1.0, (CLOCK.ms - t0) / dur)
        self.control._values[self.j] = start + (target - start) * frac
        self.control._targets[self.j] = self.control._values[self.j]
        if frac >= 1.0:
            self._move = None


class Routine(object):
    def __init__(self, name):
        self.Name = name
        self.OnScopeExecuted = None


class Program(object):
    def __init__(self, routines):
        self._r = {n: Routine(n) for n in routines}

    def findRoutine(self, name):
        return self._r.get(name)


class Executor(object):
    """Non-blocking dispatch: callRoutine(r, False) schedules completion at now+duration.
    Until then CurrentStatement is a truthy token; afterwards None and OnScopeExecuted fires."""

    def __init__(self, app, comp, routines, durations, effects=None,
                 fire_scope=True, report_busy=True, motion=None, controller=None,
                 effects_at=None, jitter=0.0):
        self.app = app
        self.comp = comp
        self.Program = Program(routines)
        self.IsEnabled = True
        self.CurrentStatement = None
        self._durations = durations
        self._effects = effects or {}
        self._fire_scope = fire_scope        # simulate OnScopeExecuted being unavailable
        self._report_busy = report_busy      # simulate CurrentStatement staying None
        # motion[routine] is either ms of joint movement at the START of the routine, or a
        # list of (from_ms, to_ms) segments when the motion is not contiguous. The remainder
        # of _durations[routine] is taught Delay with the robot stationary. Partpick is the
        # case that needs segments: it moves, dwells 1 s, grips, dwells 1 s, then RETRACTS -
        # so its last motion is at the end, and a still-window cannot open before it.
        self._motion = motion or {}
        # effects_at[routine] = (ms_after_start, fn). A taught SetDigitalOutput does not sit
        # at the end of a routine - Partplace opens its gripper 1 s after the last motion and
        # then dwells another 1.6 s - so the grasp/release must be able to land mid-routine.
        self._effects_at = effects_at or {}
        # Servo dither while the robot HOLDS position through a taught Delay. Tiny, but
        # above the gateway's stationary threshold - which is why the middle of Partpick
        # never registers as a still window, and why a retract has to be told apart from
        # this by the SIZE of the excursion rather than by its presence.
        self._jitter = jitter
        self._sign = 1.0
        self.Controller = controller
        self._due = None
        self._effect_due = None
        self._motion_until = None
        self._routine = None
        self.blocking_calls = 0
        self.dispatches = []

    def callRoutine(self, routine, suspendScript=True, clearCallStack=True):
        if suspendScript:
            self.blocking_calls += 1         # the gateway must NEVER do this
        if clearCallStack:                   # cancels whatever was still running
            self._due = None
            self._effect_due = None
            self._motion_until = None
        self._routine = routine
        self._due = CLOCK.ms + self._durations.get(routine.Name, 100.0)
        at = self._effects_at.get(routine.Name)
        self._effect_due = (CLOCK.ms + at[0], at[1]) if at else None
        m = self._motion.get(routine.Name, self._durations.get(routine.Name, 100.0))
        segs = m if isinstance(m, (list, tuple)) else [(0.0, m)]
        self._motion_until = [(CLOCK.ms + a, CLOCK.ms + b) for a, b in segs]
        self.dispatches.append((CLOCK.ms, routine.Name))
        if self._report_busy:
            self.CurrentStatement = "stmt"

    def tick(self):
        # drive the joints only while the routine is actually moving
        if self.Controller is not None and self._motion_until:
            if any(a <= CLOCK.ms < b for a, b in self._motion_until):
                for i in range(len(self.Controller.Joints)):
                    self.Controller._values[i] += 0.5
            else:
                # dither only while taught statements are still running; once the last
                # motion is done the servo parks and the robot is genuinely still
                if self._jitter and CLOCK.ms < max(b for _, b in self._motion_until):
                    self._sign = -self._sign
                    for i in range(len(self.Controller.Joints)):
                        self.Controller._values[i] += self._jitter * self._sign
                if CLOCK.ms >= max(b for _, b in self._motion_until):
                    self._motion_until = None
        if self._effect_due is not None and CLOCK.ms >= self._effect_due[0]:
            fn = self._effect_due[1]
            self._effect_due = None
            fn()
        if self._due is not None and CLOCK.ms >= self._due:
            r, self._due = self._routine, None
            self.CurrentStatement = None
            eff = self._effects.get(r.Name)
            if eff:
                eff()
            if self._fire_scope and r.OnScopeExecuted:
                r.OnScopeExecuted()


class Component(object):
    def __init__(self, name, nodes=None, behaviours=None, props=None):
        self.Name = name
        self.Parent = None
        self._nodes = {n.Name: n for n in (nodes or [])}
        self._beh = dict(behaviours or {})
        self._props = {k: Prop(k, v) for k, v in (props or {}).items()}

    def findNode(self, name):
        return self._nodes.get(name)

    def findBehaviour(self, name):
        return self._beh.get(name)

    def getProperty(self, name):
        return self._props.get(name)

    def createProperty(self, _t, name):
        p = Prop(name, "")
        self._props[name] = p
        return p


class Simulation(object):
    """vcSimulation.SimTime - seconds. This is the clock the gateway must use: it stops
    when the simulation stops, so a paused sim cannot age an in-flight move."""

    @property
    def SimTime(self):
        return CLOCK.ms / 1000.0


class Application(object):
    def __init__(self):
        self.Components = []
        self.executors = []
        self.signals = []

    def findComponent(self, name):
        for c in self.Components:
            if c.Name == name:
                return c
        return None

    def tick(self):
        for e in self.executors:
            e.tick()
        for s in self.signals:
            s.tick()
