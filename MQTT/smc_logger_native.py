#!/usr/bin/env python3
"""
SMC rig MQTT event logger — paho-mqtt direct subscribe.

WHY THIS EXISTS (vs the .cmd + dual_logger.py pipe approach):

The original logger used `mosquitto_sub.exe -F "%J" | python dual_logger.py`.
That pipeline has a SHOWSTOPPER buffering bug on Windows: when mosquitto_sub
is not connected to a TTY (i.e. when its stdout is a pipe), the CRT switches
its stdout from line-buffered to BLOCK-BUFFERED (~4–8 KB). State events on
the rig are small (~150 bytes each), so dozens of events have to accumulate
before the buffer flushes and Python sees them. In a slow cycle (one cycle
every few seconds) it can take HOURS for the first event to reach the disk.
The broker correctly delivers every message — mosquitto.log shows the
`Sending PUBLISH to smc_logger_desktop` lines — but they sit in
mosquitto_sub's buffer instead of in the desktop files.

This script removes that pipe. paho-mqtt connects to the broker directly;
its on_message callback fires the moment the broker delivers a message,
and writes both files inline. No external mosquitto_sub.exe, no pipe, no
buffer.

DURABILITY (same guarantees as smc_logger_desktop.cmd):
  client_id      = smc_logger_desktop_py   (fixed; broker queues for it)
  clean_session  = False                   (durable session)
  protocol       = MQTT 3.1.1
  qos            = 1 on subscribe          (at-least-once)
  keepalive      = 60 s

OUTPUTS (Desktop, append-mode so sessions accumulate):
  smc_log.jsonl    raw NDJSON, pandas-friendly
  smc_log.txt      "tst | topic | payload" per line, eyeball-friendly

Both files are line-buffered (`buffering=1`) so every event hits disk
the instant it arrives — Ctrl-C never loses the last events of a cycle.
"""

import json
import os
import signal
import sys
from datetime import datetime

try:
    import paho.mqtt.client as mqtt
except ImportError:
    sys.stderr.write(
        "[smc_logger_native] paho-mqtt is not installed.\n"
        "Install it once with:  pip install paho-mqtt\n")
    sys.exit(1)

BROKER_HOST = "192.168.1.50"
BROKER_PORT = 1883
CLIENT_ID   = "smc_logger_desktop_py"
TOPIC       = "smc/#"
KEEPALIVE   = 60


def _open_outputs():
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    if not os.path.isdir(desktop):
        sys.stderr.write(
            f"[smc_logger_native] WARN Desktop not found at {desktop}; "
            f"writing to cwd instead.\n")
        desktop = os.getcwd()
    json_path = os.path.join(desktop, "smc_log.jsonl")
    txt_path  = os.path.join(desktop, "smc_log.txt")
    # buffering=1 = line buffered. Every event is on disk on the \n.
    jf = open(json_path, "a", buffering=1, encoding="utf-8")
    tf = open(txt_path,  "a", buffering=1, encoding="utf-8")
    return jf, tf, json_path, txt_path


def main() -> int:
    jf, tf, json_path, txt_path = _open_outputs()

    now = datetime.now().isoformat(timespec="milliseconds")
    banner_txt  = f"--- session start {now} PID={os.getpid()} (paho-mqtt direct) ---"
    banner_json = {"event": "session_start",
                   "tst_local": now,
                   "pid": os.getpid(),
                   "engine": "paho-mqtt"}
    jf.write(json.dumps(banner_json) + "\n")
    tf.write(banner_txt + "\n")
    sys.stderr.write(
        f"[smc_logger_native] writing to:\n"
        f"  {json_path}\n  {txt_path}\n"
        f"[smc_logger_native] {banner_txt}\n")

    counter = {"n": 0}

    # ─── paho-mqtt VERSION1 callbacks (proven on paho 2.x) ──────────────
    # VERSION2 callbacks were tried first but `connect_flags` is a NamedTuple
    # not a dict, and `reason_code` is a ReasonCode object — handlers that
    # touch them crash inside paho's _handle_connack with an opaque trace.
    # VERSION1 emits a deprecation warning at construction time but the
    # signatures are dead-simple ints, work on every paho release ≥ 1.6,
    # and are documented in `paho.mqtt.client.Client.connect_callback`.

    def on_connect(client, userdata, flags, rc):
        sys.stderr.write(
            f"[smc_logger_native] CONNECT rc={rc} session_present={flags.get('session present', flags)}\n")
        # qos=1 → at-least-once delivery, same as smc_logger_desktop.cmd.
        client.subscribe(TOPIC, qos=1)
        sys.stderr.write(f"[smc_logger_native] subscribed to {TOPIC} qos=1\n")

    def on_subscribe(client, userdata, mid, granted_qos):
        sys.stderr.write(
            f"[smc_logger_native] SUBACK mid={mid} granted_qos={granted_qos}\n")

    def on_disconnect(client, userdata, rc):
        sys.stderr.write(f"[smc_logger_native] DISCONNECT rc={rc}\n")

    def on_message(client, userdata, msg):
        # ISO 8601 local wall-clock at the moment paho hands us the message.
        # The mosquitto broker's own timestamp is not available here (paho's
        # MQTT 3.1.1 client doesn't expose the broker arrival time), so this
        # is the next-best monotonic ordering anchor on the desktop side.
        tst = datetime.now().isoformat(timespec="milliseconds")
        try:
            payload = msg.payload.decode("utf-8", errors="replace")
        except Exception as ex:
            payload = f"<decode-fail {type(ex).__name__}: {ex}>"
        entry = {
            "tst":    tst,
            "topic":  msg.topic,
            "qos":    msg.qos,
            "retain": bool(msg.retain),
            "payload": payload,
        }
        jf.write(json.dumps(entry) + "\n")
        tf.write(f"{tst} | {msg.topic} | {payload}\n")
        counter["n"] += 1
        sys.stderr.write(f"[#{counter['n']:05d}] {msg.topic} = {payload}\n")

    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION1,
        client_id=CLIENT_ID,
        clean_session=False,
        protocol=mqtt.MQTTv311,
    )
    client.on_connect    = on_connect
    client.on_subscribe  = on_subscribe
    client.on_disconnect = on_disconnect
    client.on_message    = on_message

    # Make Ctrl-C tear the loop down cleanly so we get a session-end banner.
    stopped = {"flag": False}

    def _shutdown(signum, frame):
        if stopped["flag"]:
            return
        stopped["flag"] = True
        sys.stderr.write("\n[smc_logger_native] Ctrl-C — disconnecting...\n")
        try:
            client.disconnect()
        except Exception:
            pass

    signal.signal(signal.SIGINT, _shutdown)
    try:
        signal.signal(signal.SIGTERM, _shutdown)
    except (AttributeError, ValueError):
        # SIGTERM not always supported on Windows-Python; SIGINT alone is enough.
        pass

    try:
        client.connect(BROKER_HOST, BROKER_PORT, keepalive=KEEPALIVE)
    except Exception as ex:
        sys.stderr.write(
            f"[smc_logger_native] connect to {BROKER_HOST}:{BROKER_PORT} "
            f"failed: {type(ex).__name__}: {ex}\n")
        return 1

    # loop_forever blocks here; auto-reconnects on broker outage so the
    # durable session keeps catching backlog when the broker comes back.
    try:
        client.loop_forever(retry_first_connection=False)
    except Exception as ex:
        sys.stderr.write(
            f"[smc_logger_native] loop_forever crashed: "
            f"{type(ex).__name__}: {ex}\n")
        import traceback
        traceback.print_exc(file=sys.stderr)
        return 2

    end = datetime.now().isoformat(timespec="milliseconds")
    end_txt = f"--- session end {end} events_received={counter['n']} ---"
    jf.write(json.dumps({"event": "session_end",
                          "tst_local": end,
                          "events_received": counter["n"]}) + "\n")
    tf.write(end_txt + "\n")
    sys.stderr.write(f"[smc_logger_native] {end_txt}\n")

    jf.close()
    tf.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
