#!/usr/bin/env python3
"""StateSync - one-way MQTT->UNS + VueOne STD bridge for the SMC rig.

The physical rig / EAE runtime is the single source of truth. This bridge is a
read-only follower feeder: it subscribes to the EXISTING `smc/#` state stream,
normalizes each `{state:N}` message into valid UNS JSON (retained), and pushes
the same state into VueOne STD over its localhost socket. It NEVER publishes to
`smc/#` and never sends anything toward the PLCs.

    smc/<component> {state:N}
      -> uns/wmg/smc_rig/v1/<station>/<component>/state   (retained JSON, for Visual Components)
      -> VueOne STD socket 127.0.0.1:51000   ({...}EOM, for the STD highlight)

The runtime map (default sync-map.generated.json) is produced by gen_sync_map.py
from Control.xml; this bridge is fully generic - it knows no component names. The
only rig-independent facts baked in are the protocol details (the state regex,
msgType=1, and the two wire conventions confirmed against the VueOne source:
ComponentName carries the VcID, and JSON keys are PascalCase).

Usage:
    python statesync.py [map.json]              # run the bridge (default: sync-map.generated.json)
    python statesync.py [map.json] --selftest   # offline: exercise the map, no broker/socket
    python statesync.py [map.json] --list-topics # print the UNS topics (for the VC subscribe list)

Dependency: paho-mqtt   (pip install paho-mqtt)
"""
import json
import os
import re
import socket
import sys
import time
from datetime import datetime, timezone

# Tolerant of `{state:3}`, `{ state : 3 }`, `state:-1` - matches the proven parser
# in MQTT/mqtt_to_sqlite.py. The rig payload is NOT strict JSON (unquoted key).
STATE_RE = re.compile(r"state\s*:\s*(-?\d+)")
HERE = os.path.dirname(os.path.abspath(__file__))


def utc_iso(ms=False):
    now = datetime.now(timezone.utc)
    if ms:
        return now.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    return now.strftime("%Y-%m-%dT%H:%M:%SZ")


class VueOneSocket:
    """Fire-and-forget TCP writer to the VueOne STD socket server (lazy reconnect).

    VueOne's AsyncSocketServer reads ASCII, splits the stream on the literal
    "EOM", ignores anything not ending in "}", then deserializes with a
    case-SENSITIVE System.Text.Json into VcComponentArg (PascalCase properties)
    and acts only when ClientId == "VC". So: PascalCase keys, one compact JSON
    object, "}EOM", no trailing newline.
    """

    def __init__(self, host, port, min_retry_s=5.0):
        self.host, self.port, self.min_retry = host, port, min_retry_s
        self.sock = None
        self._last_try = 0.0
        self.on_connect = None   # called once per successful (re)connect -> snapshot replay

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
                self.on_connect()   # snapshot replay; self.sock is set, so nested sends won't re-connect
            return True
        except OSError as e:
            print("[vueone] not connected ({}); MQTT/UNS keeps running, retry >= {:.0f}s"
                  .format(e, self.min_retry), flush=True)
            return False

    def send(self, obj):
        if not self._ensure():
            return False
        line = json.dumps(obj, separators=(",", ":")) + "EOM"
        try:
            self.sock.sendall(line.encode("ascii", "replace"))
            return True
        except OSError as e:
            print("[vueone] send failed ({}); dropping socket, will reconnect".format(e), flush=True)
            try:
                self.sock.close()
            except OSError:
                pass
            self.sock = None
            return False


class _DryVueOne:
    """Selftest stand-in: prints the socket line instead of opening a socket."""

    def send(self, obj):
        print("VUEONE " + json.dumps(obj, separators=(",", ":")) + "EOM", flush=True)


class Bridge:
    """Normalize smc/# -> retained UNS JSON + VueOne socket updates. One-way only."""

    def __init__(self, cfg, comps, ci, dry=False):
        self.cfg, self.comps, self.ci, self.dry = cfg, comps, ci, dry
        self.prefix = cfg.get("unsPrefix", "uns/wmg/smc_rig/v1").rstrip("/")
        self.status_topic = self.prefix + "/_bridge/status"
        self.qos = int(cfg.get("unsQos", 1))   # UNS state + status publish QoS (retain stays on)
        self.epoch = utc_iso()          # bridge start -> followers detect a restart
        self.seq = 0                    # monotonic over ALL emitted events
        self.last_state = {}            # source topic -> last int state (dup drop)
        self.warned_unknown = set()
        self.warned_case = set()
        self.warned_badstate = set()
        self.msgs_in = 0
        self.msgs_out = 0
        self.mqtt = None                # set by run()
        vc = cfg.get("vueone", {})
        self.vc_clientid = vc.get("clientId", "VC")
        if not vc.get("enabled"):
            self.vueone = None
        elif dry:
            self.vueone = _DryVueOne()
        else:
            self.vueone = VueOneSocket(vc.get("host", "127.0.0.1"), int(vc.get("port", 51000)))
            self.vueone.on_connect = self.replay_snapshot   # catch VueOne up on every (re)connect

    def resolve(self, topic):
        """Exact match, then a case-only fallback (sensor topics keep their <Name> case)."""
        c = self.comps.get(topic)
        if c is not None:
            return topic, c
        k = self.ci.get(topic.lower())
        if k is not None:
            if topic not in self.warned_case:
                print("[warn] topic '{}' matched sync-map key '{}' by case only; "
                      "align the map key to the wire topic".format(topic, k), flush=True)
                self.warned_case.add(topic)
            return k, self.comps[k]
        return None, None

    def on_message(self, topic, payload):
        self.msgs_in += 1
        if topic.startswith(self.prefix):
            return  # never re-ingest our own UNS output (belt-and-braces; we only subscribe smc/#)
        key, comp = self.resolve(topic)
        if comp is None:
            if topic not in self.warned_unknown:
                print("[warn] unknown topic (not in sync-map, ignoring): {}".format(topic), flush=True)
                self.warned_unknown.add(topic)
            self.publish_status()
            return
        m = STATE_RE.search(payload or "")
        if not m:
            print("[warn] no 'state:N' in payload for {}: {!r}".format(topic, payload), flush=True)
            return
        state = int(m.group(1))
        if self.last_state.get(key) == state:
            return  # consecutive duplicate -> not an event
        self.last_state[key] = state
        self.seq += 1
        name = comp.get("states", {}).get(str(state))
        if name is None and (key, state) not in self.warned_badstate:
            print("[warn] {} state {} not in sync-map; publishing stateName=null".format(key, state), flush=True)
            self.warned_badstate.add((key, state))
        self.emit(key, comp, state, name)

    def uns_topic_for(self, comp):
        return "{}/{}/{}/state".format(self.prefix, comp.get("station", "unassigned"), comp.get("component", ""))

    def emit(self, topic, comp, state, name):
        uns_topic = self.uns_topic_for(comp)
        assert uns_topic.startswith(self.prefix), "refusing to publish outside the UNS namespace"
        self._pub(uns_topic, {
            "seq": self.seq,
            "epoch": self.epoch,
            "ts": utc_iso(ms=True),
            "sourceTopic": topic,
            "station": comp.get("station", "unassigned"),
            "component": comp.get("component", ""),
            "twinName": comp.get("twinName"),
            "vcId": comp.get("vcId"),
            "state": state,
            "stateName": name,
            "quality": "GOOD",
        }, retain=True)
        self.msgs_out += 1
        if self.vueone is not None and name is not None:
            self.vueone.send(self._vueone_msg(comp, name))
        self.publish_status()

    def _vueone_msg(self, comp, name):
        # ComponentName carries the VcID: VueOne matches it against aComponent.VCID
        # (FormSystemEditor.OnEventFromVc). Actuators read StateName; sensors read
        # Value via Convert.ToBoolean, so it must be "true"/"false".
        return {
            "ClientId": self.vc_clientid,
            "ComponentName": comp.get("vcId"),
            "StateName": name,
            "Value": "true" if str(name).upper() in ("ON", "TRUE") else "false",
            "MsgType": 1,
        }

    def replay_snapshot(self):
        """On VueOne (re)connect: push the latest known state of every seen component so
        the STD catches up at once, without waiting for each component's next event."""
        if self.vueone is None:
            return
        n = 0
        for topic, state in list(self.last_state.items()):
            comp = self.comps.get(topic)
            if comp is None:
                continue
            name = comp.get("states", {}).get(str(state))
            if name is None:
                continue
            self.vueone.send(self._vueone_msg(comp, name))
            n += 1
        if n:
            print("[vueone] snapshot replayed {} component state(s)".format(n), flush=True)

    def publish_status(self):
        self._pub(self.status_topic, {
            "online": True,
            "epoch": self.epoch,
            "seq": self.seq,
            "messagesIn": self.msgs_in,
            "messagesOut": self.msgs_out,
            "unknownTopics": len(self.warned_unknown),
        }, retain=True)

    def _pub(self, topic, obj, retain):
        line = json.dumps(obj, separators=(",", ":"))
        if self.dry:
            print("PUB {}{}  {}".format("(retained) " if retain else "", topic, line), flush=True)
        elif self.mqtt is not None:
            self.mqtt.publish(topic, line, qos=self.qos, retain=retain)


def load_map(path):
    if not os.path.exists(path):
        sys.exit("map not found: {}\n  generate it first:\n"
                 "    python gen_sync_map.py <Control.xml> --out {}".format(path, path))
    with open(path, "r", encoding="utf-8-sig") as f:   # tolerate a UTF-8 BOM
        cfg = json.load(f)
    comps = cfg.get("components", {})
    ci = {k.lower(): k for k in comps}   # case-insensitive fallback index
    return cfg, comps, ci


def run(cfg, bridge):
    import paho.mqtt.client as mqtt
    broker = cfg.get("broker", {})
    host, port = broker.get("host", "192.168.1.50"), int(broker.get("port", 1883))
    try:
        client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION1)  # paho-mqtt v2.x
    except (AttributeError, TypeError):
        client = mqtt.Client()                                  # paho-mqtt v1.x
    bridge.mqtt = client

    def on_connect(c, u, flags, rc, *a):
        print("[mqtt] connected rc={}; subscribing smc/#".format(rc), flush=True)
        c.subscribe("smc/#")
        bridge.publish_status()

    def on_message(c, u, msg):
        try:
            bridge.on_message(msg.topic, msg.payload.decode("utf-8", "replace"))
        except Exception as e:  # never let one bad message kill the bridge
            print("[err] on_message {}: {}".format(msg.topic, e), flush=True)

    client.on_connect = on_connect
    client.on_message = on_message
    # Last-Will so late subscribers see the bridge go offline (retained).
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
    print("[statesync] up. epoch={}  UNS prefix={}  vueone={}"
          .format(bridge.epoch, bridge.prefix, "on" if bridge.vueone else "off"), flush=True)
    client.loop_forever()


def list_topics(cfg, comps):
    """Print every UNS topic the bridge would publish - the Visual Components subscribe list."""
    bridge = Bridge(cfg, comps, {}, dry=True)
    for t in sorted({bridge.uns_topic_for(c) for c in comps.values()}):
        print(t)
    print(bridge.status_topic)


def selftest(cfg, comps, ci):
    # Fully generic: samples are derived from the loaded map, so no component name
    # is hardcoded in the bridge or its test.
    bridge = Bridge(cfg, comps, ci, dry=True)
    print("--- selftest  epoch={}  prefix={}  unsQos={}  components={} ---".format(
        bridge.epoch, bridge.prefix, bridge.qos, len(comps)), flush=True)
    for topic, comp in list(comps.items())[:3]:
        nums = sorted(comp.get("states", {}), key=lambda s: int(s))
        # first state, same again (duplicate -> dropped), then the next state
        for num in (nums[:1] + nums[:1] + nums[1:2]):
            bridge.on_message(topic, "{state:%s}" % num)
    bridge.on_message("smc/__unmapped__", "{state:9}")   # unknown topic -> one warning
    if comps:
        t0, c0 = next(iter(comps.items()))
        bad = max([int(x) for x in c0.get("states", {})] + [0]) + 5
        bridge.on_message(t0, "{ state : %d }" % bad)     # tolerant parse + out-of-range -> stateName null
    print("--- simulating a VueOne (re)connect: snapshot replay of last-known states ---", flush=True)
    bridge.replay_snapshot()
    print("--- summary in={} out={} unknownTopics={} ---"
          .format(bridge.msgs_in, bridge.msgs_out, len(bridge.warned_unknown)), flush=True)


DEFAULT_MAP = "sync-map.generated.json"

if __name__ == "__main__":
    args = sys.argv[1:]
    dry = "--selftest" in args
    listt = "--list-topics" in args
    args = [a for a in args if a not in ("--selftest", "--list-topics")]
    map_path = args[0] if args else os.path.join(HERE, DEFAULT_MAP)
    cfg, comps, ci = load_map(map_path)
    if listt:
        list_topics(cfg, comps)
    elif dry:
        selftest(cfg, comps, ci)
    else:
        run(cfg, Bridge(cfg, comps, ci))
