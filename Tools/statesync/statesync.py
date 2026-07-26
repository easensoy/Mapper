#!/usr/bin/env python3
"""StateSync - the rig-aware half of the SMC digital shadow.

The physical rig / EAE runtime is the single source of truth. This bridge is a read-only
follower: it subscribes to the EXISTING `smc/#` state stream and turns each `{state:N}`
into two things:

    uns/wmg/smc_rig/v1/<station>/<component>/state   retained  - the observation stream
    uns/wmg/smc_rig/v1/vc/command                    volatile  - the VC command envelope

It NEVER publishes to `smc/#` and never sends anything toward the PLCs.

ALL rig semantics live here and in shadow-config.json. The VC-side gateway holds none:
every fact it needs (lane, execution contract, target, duration, routine, signal value,
verification policy) travels in the envelope. That is what keeps the pasted VC script
stable across rig, twin and measurement changes.

Usage:
    python statesync.py [shadow-config.json]
    python statesync.py [config] --selftest      offline: no broker, no socket
    python statesync.py [config] --list-topics   print the UNS topics

Dependency: paho-mqtt
"""
import json
import os
import queue
import re
import socket
import sys
import threading
import time
from datetime import datetime, timezone

STATE_RE = re.compile(r"state\s*:\s*(-?\d+)")     # rig payload is not strict JSON
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = "shadow-config.json"


def utc_iso(ms=False):
    now = datetime.now(timezone.utc)
    if ms:
        return now.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    return now.strftime("%Y-%m-%dT%H:%M:%SZ")


class VueOneSocket:
    """Fire-and-forget TCP writer to the VueOne STD socket server.

    NON-BLOCKING by construction: emit() runs on paho's callback thread, which processes
    every rig message sequentially, so nothing that can block may run there. The blocking
    connect and the pacing sleep happen on the worker thread only.
    """

    def __init__(self, host, port, min_retry_s=5.0, min_send_ms=0.0):
        self.host, self.port, self.min_retry = host, port, min_retry_s
        self.sock = None
        self._last_try = 0.0
        self.min_send = float(min_send_ms) / 1000.0
        self._last_send = 0.0
        self.on_connect = None
        self._q = queue.Queue(maxsize=500)
        t = threading.Thread(target=self._worker)
        t.daemon = True
        t.start()

    def _ensure(self):
        if self.sock is not None:
            return True
        if time.time() - self._last_try < self.min_retry:
            return False
        self._last_try = time.time()
        try:
            s = socket.create_connection((self.host, self.port), timeout=3.0)
            s.settimeout(3.0)
            self.sock = s
            print("[vueone] connected {}:{}".format(self.host, self.port), flush=True)
            if self.on_connect is not None:
                self.on_connect()
            return True
        except OSError as e:
            print("[vueone] not connected ({}); MQTT/UNS keeps running, retry >= {:.0f}s"
                  .format(e, self.min_retry), flush=True)
            return False

    def send(self, obj):
        try:
            self._q.put_nowait(obj)
        except Exception:
            pass            # queue full -> drop a highlight; never stall the rig stream
        return True

    def _worker(self):
        while True:
            obj = self._q.get()
            try:
                self._send_blocking(obj)
            except Exception:
                pass

    def _send_blocking(self, obj):
        if not self._ensure():
            return False
        if self.min_send:
            dt = time.time() - self._last_send
            if dt < self.min_send:
                time.sleep(self.min_send - dt)
        line = json.dumps(obj, separators=(",", ":")) + "EOM"
        try:
            self.sock.sendall(line.encode("ascii", "replace"))
            self._last_send = time.time()
            return True
        except OSError as e:
            print("[vueone] send failed ({}); will reconnect".format(e), flush=True)
            try:
                self.sock.close()
            except OSError:
                pass
            self.sock = None
            return False


class _DryVueOne:
    def send(self, obj):
        print("VUEONE " + json.dumps(obj, separators=(",", ":")) + "EOM", flush=True)


class Bridge:
    """smc/# -> retained UNS observation + volatile VC command envelopes. One-way only."""

    def __init__(self, cfg, dry=False):
        self.cfg = cfg
        self.dry = dry
        self.comps = cfg["components"]
        proto = cfg.get("protocol", {})
        site = cfg.get("site", {})
        self.prefix = site.get("unsPrefix", "uns/wmg/smc_rig/v1").rstrip("/")
        self.qos = int(site.get("unsQos", 1))
        self.schema = int(proto.get("schemaVersion", 2))
        self.start2dest = {int(k): int(v) for k, v in proto.get("motionStartToDest", {}).items()}
        self.cmd_topic = proto.get("commandTopic", self.prefix + "/vc/command")
        self.evt_topic = proto.get("eventTopic", self.prefix + "/vc/event")
        self.routine_timeout = int(proto.get("routineTimeoutMs", 15000))
        self.resync_on_ready = bool(proto.get("resyncOnReady", True))
        self.status_topic = self.prefix + "/_bridge/status"

        # topic -> canonical key, including declared aliases
        self.index = {}
        for key, c in self.comps.items():
            self.index[key] = key
            for a in c.get("aliases", []):
                self.index[a] = key
        self.ci = {k.lower(): k for k in self.index}

        self.epoch = utc_iso()
        self.seq = 0
        self._cmd_seq = 0
        self.last_state = {}
        self._last_routine = {}     # key -> last state that actually commanded a routine
        self._sw_prev = {}
        self._sw_phase = {}
        self.warned = set()
        self.msgs_in = self.msgs_out = 0
        self.mqtt = None
        self.resyncs = 0

        vc = site.get("vueone", {})
        self.vc_clientid = vc.get("clientId", "VC")
        if not vc.get("enabled"):
            self.vueone = None
        elif dry:
            self.vueone = _DryVueOne()
        else:
            self.vueone = VueOneSocket(vc.get("host", "127.0.0.1"), int(vc.get("port", 51000)),
                                       min_send_ms=float(vc.get("sendMinIntervalMs", 20)))
            self.vueone.on_connect = self.replay_vueone

    # ------------------------------------------------------------ resolve
    def resolve(self, topic):
        k = self.index.get(topic)
        if k is None:
            k = self.index.get(self.ci.get(topic.lower(), ""))
        return (k, self.comps[k]) if k else (None, None)

    def phase_of(self, key, comp, state, track=True):
        """The centre-home swivel visits one physical place under two twin names, and its
        assembly and disassembly strokes have non-overlapping durations. Phase is stateful
        runtime history: leaving home toward work1 = assembly, toward work2 = disassembly.
        Held until it next departs home."""
        if "disStates" not in comp and not isinstance(comp.get("durationsMs", {}).get("asm"), dict):
            return None
        if track:
            if self._sw_prev.get(key) in (0, 6):
                if state == 1:
                    self._sw_phase[key] = "asm"
                elif state == 3:
                    self._sw_phase[key] = "dis"
            self._sw_prev[key] = state
        return self._sw_phase.get(key, "asm")

    def state_name(self, comp, state, phase):
        if phase == "dis":
            alt = comp.get("disStates", {}).get(str(state))
            if alt is not None:
                return alt
        return comp.get("states", {}).get(str(state))

    def duration_for(self, comp, state, phase):
        d = comp.get("durationsMs", {})
        if phase and isinstance(d.get(phase), dict):
            d = d[phase]
        return d.get(str(state))

    # ------------------------------------------------------------ ingest
    def on_message(self, topic, payload):
        self.msgs_in += 1
        if topic.startswith(self.prefix):
            return
        key, comp = self.resolve(topic)
        if comp is None:
            if topic not in self.warned:
                print("[warn] unknown topic (not in shadow-config, ignoring): {}".format(topic),
                      flush=True)
                self.warned.add(topic)
            return
        m = STATE_RE.search(payload or "")
        if not m:
            print("[warn] no 'state:N' in payload for {}: {!r}".format(topic, payload), flush=True)
            return
        state = int(m.group(1))
        if self.last_state.get(key) == state:
            return                                   # consecutive duplicate is not an event
        self.last_state[key] = state
        self.seq += 1
        phase = self.phase_of(key, comp, state)
        self.emit(key, comp, state, phase)

    def uns_topic_for(self, comp):
        return "{}/{}/{}/state".format(self.prefix, comp.get("station", "unassigned"),
                                       comp.get("component", ""))

    def emit(self, key, comp, state, phase):
        name = self.state_name(comp, state, phase)
        payload = {
            "seq": self.seq, "epoch": self.epoch, "ts": utc_iso(ms=True),
            "sourceTopic": key, "station": comp.get("station", "unassigned"),
            "component": comp.get("component", ""), "twinName": comp.get("twinName"),
            "vcId": comp.get("vcId"), "state": state, "stateName": name,
            "quality": "GOOD",
        }
        if comp.get("execution") == "sensor":
            payload["present"] = (state == comp.get("presentState", 1))
        self._pub(self.uns_topic_for(comp), payload, retain=True)
        self.msgs_out += 1

        env = self.build_envelope(comp, state, name, phase)
        if env is not None:
            if env["execution"] == "routine":
                self._last_routine[key] = state       # what parentage a resync should expect
            self._pub(self.cmd_topic, env, retain=False)
            detail = env.get("routine")
            if detail is None:
                detail = "{}->{}".format(env.get("signalValue"), env.get("target")) \
                    if env["execution"] == "signal" else "pos={}".format(env.get("target"))
            print("[cmd] {} {} state={} {} {}ms id={}".format(
                comp["vcId"], env["execution"], state, detail,
                env.get("durationMs"), env["commandId"]), flush=True)

        if self.vueone is not None and name is not None:
            self.vueone.send(self._vueone_msg(comp, name))
        self.publish_status()

    # -------------------------------------------------- envelope v2
    def build_envelope(self, comp, state, name, phase, snapshot=False):
        """One envelope shape, three execution contracts. Returns None when this state is
        not a command (arrivals, sensors, and states with no configured motion)."""
        ex = comp.get("execution")
        if ex == "sensor":
            return None

        # What makes a state "commandable" differs by contract: a stroke needs a measured
        # duration, a routine needs a taught routine name. Routines are timeout-bounded,
        # not duration-driven, so they carry no durationMs.
        if ex == "routine":
            if comp.get("routines", {}).get(str(state)) is None:
                return None
            dur = 0
        elif snapshot:
            dur = 0
        else:
            dur = self.duration_for(comp, state, phase)
            if dur is None:
                return None                          # arrival / idle state - nothing to command

        self._cmd_seq += 1
        env = {
            "schemaVersion": self.schema,
            "epoch": self.epoch,
            "commandId": "{}#{}".format(self.epoch, self._cmd_seq),
            "seq": self.seq,
            "sourceTs": utc_iso(ms=True),
            "vcId": comp["vcId"],
            "state": state,
            "stateName": name,
            "phase": phase,
            "lane": comp["lane"],
            "execution": ex,
            "durationMs": None if ex == "routine" else dur,   # routines are timeout-bounded
        }
        if snapshot:
            env["snapshot"] = True

        if ex == "signal":
            sig = comp["signal"]
            env["signalBehaviour"] = sig.get("behaviour", "PushJoint_ActionSignal")
            env["signalValue"] = state in (1, 2)
            env["strokeDistance"] = abs(float(sig["closed"]) - float(sig["open"]))
            env["target"] = sig["closed"] if env["signalValue"] else sig["open"]
            return env

        if ex == "axis":
            dest = state if snapshot else self.start2dest.get(state)
            if dest is None:
                return None
            pos = comp.get("positions", {}).get(str(dest))
            if pos is None and snapshot:
                pos = comp.get("positions", {}).get(str(self.start2dest.get(state, dest)))
            if pos is None:
                print("[warn] no position for {} dest-state {}; no command".format(
                    comp["vcId"], dest), flush=True)
                return None
            env["target"] = pos
            return env

        # routine
        routine = comp.get("routines", {}).get(str(state))
        if routine is None:
            return None
        env["routine"] = routine
        env["timeoutMs"] = self.routine_timeout
        v = comp.get("verify")
        if v:
            env["verify"] = {"part": v["part"], "carriers": list(v["carriers"]),
                             "attached": state in v.get("attachedOn", [])}
        return env

    # ------------------------------------------------------ resync
    def resync_vc(self, why):
        """Mechanism-specific. Restore absolute state where it is safe; never fabricate an
        attachment; never replay a grasp or a robot move."""
        sent = skipped = 0
        for key, state in list(self.last_state.items()):
            comp = self.comps.get(key)
            if comp is None:
                continue
            ex = comp.get("execution")
            if ex == "sensor":
                continue
            if ex == "routine":
                if not comp.get("verify"):
                    skipped += 1                      # robot: never replay a pose
                    continue
                # A gripper resyncs against the last grasp/release actually COMMANDED, so
                # the expected parentage is known exactly. Never inferred from a rest state.
                state = self._last_routine.get(key)
                if state is None:
                    skipped += 1
                    continue
            phase = self._sw_phase.get(key)
            name = self.state_name(comp, state, phase)
            env = self.build_envelope(comp, state, name, phase, snapshot=True)
            if env is None:
                skipped += 1
                continue
            self._pub(self.cmd_topic, env, retain=False)
            sent += 1
        self.resyncs += 1
        print("[resync] {} -> {} snapshot command(s), {} skipped (robots/unknown)".format(
            why, sent, skipped), flush=True)

    def on_vc_event(self, payload):
        try:
            evt = json.loads(payload)
        except Exception:
            return
        status = evt.get("status")
        if status == "ready":
            if self.resync_on_ready:
                self.resync_vc("vc " + str(evt.get("detail")))
        elif status in ("failed", "timeout", "desync"):
            print("[vc:{}] {} {} {}".format(status, evt.get("vcId"), evt.get("stateName") or "",
                                            evt.get("detail") or ""), flush=True)

    # ------------------------------------------------------ vueone
    def _vueone_msg(self, comp, name):
        return {"ClientId": self.vc_clientid, "ComponentName": comp.get("vcId"),
                "StateName": name,
                "Value": "true" if str(name).upper() in ("ON", "TRUE") else "false",
                "MsgType": 1}

    def replay_vueone(self):
        if self.vueone is None:
            return
        n = 0
        for key, state in list(self.last_state.items()):
            comp = self.comps.get(key)
            if comp is None:
                continue
            name = self.state_name(comp, state, self._sw_phase.get(key))
            if name is None:
                continue
            self.vueone.send(self._vueone_msg(comp, name))
            n += 1
        if n:
            print("[vueone] snapshot replayed {} component state(s)".format(n), flush=True)

    # ------------------------------------------------------ plumbing
    def publish_status(self):
        self._pub(self.status_topic, {
            "online": True, "epoch": self.epoch, "seq": self.seq,
            "messagesIn": self.msgs_in, "messagesOut": self.msgs_out,
            "resyncs": self.resyncs, "unknownTopics": len(self.warned)}, retain=True)

    def _pub(self, topic, obj, retain):
        line = json.dumps(obj, separators=(",", ":"))
        if self.dry:
            print("PUB {}{}  {}".format("(retained) " if retain else "", topic, line), flush=True)
        elif self.mqtt is not None:
            self.mqtt.publish(topic, line, qos=self.qos, retain=retain)


def load_config(path):
    if not os.path.exists(path):
        sys.exit("config not found: {}".format(path))
    with open(path, "r", encoding="utf-8-sig") as f:
        cfg = json.load(f)
    if "components" not in cfg:
        sys.exit("config has no 'components': {}".format(path))
    return cfg


def run(bridge):
    import paho.mqtt.client as mqtt
    broker = bridge.cfg.get("site", {}).get("broker", {})
    host, port = broker.get("host", "127.0.0.1"), int(broker.get("port", 1883))
    try:
        client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION1)
    except (AttributeError, TypeError):
        client = mqtt.Client()
    bridge.mqtt = client

    def on_connect(c, u, flags, rc, *a):
        print("[mqtt] connected rc={}; subscribing smc/# and {}".format(rc, bridge.evt_topic),
              flush=True)
        c.subscribe("smc/#")
        c.subscribe(bridge.evt_topic)      # gateway -> ready / failed / timeout / desync
        bridge.publish_status()

    def on_message(c, u, msg):
        try:
            if msg.topic == bridge.evt_topic:
                bridge.on_vc_event(msg.payload.decode("utf-8", "replace"))
            else:
                bridge.on_message(msg.topic, msg.payload.decode("utf-8", "replace"))
        except Exception as e:
            print("[err] on_message {}: {}".format(msg.topic, e), flush=True)

    client.on_connect = on_connect
    client.on_message = on_message
    client.will_set(bridge.status_topic,
                    json.dumps({"online": False, "epoch": bridge.epoch}, separators=(",", ":")),
                    qos=bridge.qos, retain=True)
    for attempt in range(1, 21):
        try:
            client.connect(host, port, keepalive=60)
            break
        except OSError as e:
            print("[mqtt] broker {}:{} unreachable ({}); retry {}/20 in 2s"
                  .format(host, port, e, attempt), flush=True)
            time.sleep(2)
    else:
        print("[mqtt] giving up connecting to {}:{}".format(host, port), flush=True)
        return
    print("[statesync] up. epoch={}  prefix={}  components={}  vueone={}".format(
        bridge.epoch, bridge.prefix, len(bridge.comps), "on" if bridge.vueone else "off"),
        flush=True)
    if bridge.vueone is not None:
        bridge.vueone._ensure()
    client.loop_forever()


def list_topics(cfg):
    b = Bridge(cfg, dry=True)
    for t in sorted({b.uns_topic_for(c) for c in b.comps.values()}):
        print(t)
    print(b.cmd_topic)
    print(b.evt_topic)
    print(b.status_topic)


def selftest(cfg):
    b = Bridge(cfg, dry=True)
    print("--- selftest epoch={} components={} ---".format(b.epoch, len(b.comps)), flush=True)
    for topic, comp in list(b.comps.items()):
        for s in sorted(comp.get("states", {}), key=lambda x: int(x)):
            b.on_message(topic, "{state:%s}" % s)
    b.on_message("smc/__unmapped__", "{state:9}")
    print("--- resync ---", flush=True)
    b.resync_vc("selftest")
    print("--- in={} out={} unknown={} ---".format(b.msgs_in, b.msgs_out, len(b.warned)),
          flush=True)


_GUARD = None


def _single_instance(port=51999):
    global _GUARD
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.bind(("127.0.0.1", port)); s.listen(1); _GUARD = s
        return True
    except OSError:
        s.close()
        return False


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    flags = {a for a in sys.argv[1:] if a.startswith("--")}
    cfg = load_config(args[0] if args else os.path.join(HERE, DEFAULT_CONFIG))
    if "--list-topics" in flags:
        list_topics(cfg)
    elif "--selftest" in flags:
        selftest(cfg)
    else:
        if not _single_instance():
            print("[statesync] another instance is already running; exiting", flush=True)
            sys.exit(0)
        if os.environ.get("STATESYNC_NO_VUEONE_SOCKET") == "1":
            cfg.setdefault("site", {}).setdefault("vueone", {})["enabled"] = False
        run(Bridge(cfg))
