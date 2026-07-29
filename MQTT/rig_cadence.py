#!/usr/bin/env python3
"""Rig cadence logger -- the ground truth for making the VC model track the rig.

Subscribes to the RAW rig topics (smc/#) and logs every publish with a millisecond
timestamp, the component, the state VALUE, and the gap since that component's previous
publish. The gap is the rig's REAL per-state timing: e.g. "transfer state=1 ... state=2
+2.13s" means the rig takes 2.13 s to advance. That is the number the VC actuator must
match to be synchronised -- not boosted (snaps ahead), not native-slow (crawls behind).

mosquitto.log already proves the rig->statesync->uns hop is same-second (instant); this
logger adds the state values + millisecond precision that mosquitto.log lacks.

    python rig_cadence.py     # log live to MQTT/rig_cadence.log until Ctrl+C
"""
import re, time, datetime
import paho.mqtt.client as mqtt

HOST, PORT = "127.0.0.1", 1883
OUT = r"C:\VueOneMapper\MQTT\rig_cadence.log"
STATE = re.compile(r"state\s*:\s*(-?\d+)")   # rig payload is the loose form {state:N}
_last = {}
_f = open(OUT, "a")   # APPEND: restarting the logger must never destroy a prior capture

def _log(line):
    print(line)
    _f.write(line + "\n")
    _f.flush()

def on_connect(c, u, flags, rc):
    c.subscribe("smc/#", qos=0)
    _log("# subscribed smc/#  rc=%s  (component  state  +gap-since-its-last-publish)" % rc)

def on_message(c, u, msg):
    now = time.time()
    comp = msg.topic.split("/", 1)[-1]
    m = STATE.search(msg.payload.decode("utf-8", "ignore"))
    state = m.group(1) if m else "?"
    gap = now - _last.get(comp, now)
    _last[comp] = now
    ts = datetime.datetime.fromtimestamp(now).strftime("%H:%M:%S.%f")[:-3]
    _log("%s  %-16s state=%-3s  +%6.3fs" % (ts, comp, state, gap))

def main():
    try:
        c = mqtt.Client(mqtt.CallbackAPIVersion.VERSION1)
    except (AttributeError, TypeError):
        c = mqtt.Client()  # paho 1.x fallback
    c.on_connect = on_connect
    c.on_message = on_message
    c.connect(HOST, PORT, 60)
    try:
        c.loop_forever()
    except KeyboardInterrupt:
        _log("# stopped")

if __name__ == "__main__":
    main()
