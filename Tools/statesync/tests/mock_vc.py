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
    """Stock PushJoint_ActionSignal. Drives the joint through the component's own servo,
    honouring the shaped MaxSpeed/MaxAcceleration - i.e. swept, not teleported."""

    def __init__(self, comp, controller, j, openv, closedv):
        self.comp, self.control, self.j = comp, controller, j
        self.openv, self.closedv = openv, closedv
        self.history = []

    def signal(self, value):
        self.history.append((CLOCK.ms, bool(value)))
        self.control.moveJoint(self.j, self.closedv if value else self.openv)


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
                 fire_scope=True, report_busy=True):
        self.app = app
        self.comp = comp
        self.Program = Program(routines)
        self.IsEnabled = True
        self.CurrentStatement = None
        self._durations = durations
        self._effects = effects or {}
        self._fire_scope = fire_scope        # simulate OnScopeExecuted being unavailable
        self._report_busy = report_busy      # simulate CurrentStatement staying None
        self._due = None
        self._routine = None
        self.blocking_calls = 0
        self.dispatches = []

    def callRoutine(self, routine, suspendScript=True, clearCallStack=True):
        if suspendScript:
            self.blocking_calls += 1         # the gateway must NEVER do this
        self._routine = routine
        self._due = CLOCK.ms + self._durations.get(routine.Name, 100.0)
        self.dispatches.append((CLOCK.ms, routine.Name))
        if self._report_busy:
            self.CurrentStatement = "stmt"

    def tick(self):
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


class Application(object):
    def __init__(self):
        self.Components = []
        self.executors = []

    def findComponent(self, name):
        for c in self.Components:
            if c.Name == name:
                return c
        return None

    def tick(self):
        for e in self.executors:
            e.tick()
