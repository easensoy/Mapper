#!/usr/bin/env python3
"""
SMC rig — dual-output MQTT event logger.

Reads MQTT messages as NDJSON (one JSON object per line, the format
produced by `mosquitto_sub -F "%J"`) on stdin and tees every event
into two files on the operator's Desktop:

  smc_log.jsonl  — raw NDJSON, one event per line. Machine-friendly,
                   safe to load with `pandas.read_json(..., lines=True)`
                   or stream with `jq -c '.' < smc_log.jsonl`.
  smc_log.txt    — human-readable "tst | topic | payload" per line,
                   easy to `grep`/`awk`/eyeball.

Both files are append-mode so consecutive sessions accumulate. Delete
or rename them between rig runs if you want a clean session per test.
A session-start banner is written to each file at process start so
session boundaries are easy to spot in the accumulated logs.

Each file is line-buffered (`buffering=1`), so every event is on disk
the instant it arrives — Ctrl-C never loses the last few messages.

Usage (typically piped from mosquitto_sub via smc_logger_desktop.cmd):
  mosquitto_sub -h <broker> -t "smc/#" -F "%J" | python dual_logger.py

The timestamp on every event comes from mosquitto's own `%J` format
(the broker-side wall-clock at the moment the message was received),
so the order in the log reflects broker arrival, not Python's
processing time.
"""

import json
import os
import sys
from datetime import datetime


def main() -> int:
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    if not os.path.isdir(desktop):
        # Headless / unusual user profile — fall back to cwd and warn.
        print(f"[dual_logger] WARN Desktop not found at {desktop}; "
              f"writing to cwd instead.", file=sys.stderr)
        desktop = os.getcwd()

    json_path = os.path.join(desktop, "smc_log.jsonl")
    txt_path  = os.path.join(desktop, "smc_log.txt")

    # Line-buffered append. buffering=1 (line buffering) is what keeps
    # each event on disk the instant it arrives — without this, Ctrl-C
    # mid-cycle would lose the last buffered block of events.
    with open(json_path, "a", buffering=1, encoding="utf-8") as jf, \
         open(txt_path,  "a", buffering=1, encoding="utf-8") as tf:

        now = datetime.now().isoformat(timespec="milliseconds")
        banner_txt = f"--- session start {now} PID={os.getpid()} ---"
        banner_json = {"event": "session_start",
                       "tst_local": now,
                       "pid": os.getpid()}

        # Same banner in both files so a session can be located in either.
        jf.write(json.dumps(banner_json) + "\n")
        tf.write(banner_txt + "\n")

        # Mirror to stderr so the operator sees the logger has started
        # before the first MQTT event lands.
        print(f"[dual_logger] writing to:\n  {json_path}\n  {txt_path}",
              file=sys.stderr)
        print(f"[dual_logger] {banner_txt}", file=sys.stderr)

        for raw in sys.stdin:
            line = raw.rstrip("\r\n")
            if not line:
                continue

            # ALWAYS preserve the raw NDJSON line first — even if reformatting
            # fails on a malformed event, the JSONL file stays complete.
            jf.write(line + "\n")

            try:
                obj = json.loads(line)
                tst     = obj.get("tst", "?")
                topic   = obj.get("topic", "?")
                # mosquitto_sub's %J format emits `payload` as the message
                # body string; if the message was binary it falls back to
                # `payloadlen` only. Handle both gracefully.
                if "payload" in obj:
                    payload = obj["payload"]
                else:
                    payload = f"<{obj.get('payloadlen', '?')} bytes binary>"
                tf.write(f"{tst} | {topic} | {payload}\n")
            except json.JSONDecodeError as ex:
                # Don't crash on the rare malformed line; record it as a
                # comment in the TXT for forensics and keep going.
                tf.write(f"# PARSE_FAIL: {ex.msg} | raw={line}\n")

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        # Clean exit on Ctrl-C — the `with` blocks have already flushed
        # everything because the files are line-buffered.
        print("\n[dual_logger] stopped on Ctrl-C", file=sys.stderr)
        sys.exit(0)
