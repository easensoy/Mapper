"""Full-cycle integration replay: a REAL recorded rig cycle driven through the REAL
statesync translator into the REAL gateway source on a mock VC runtime.

Stimulus: tests/rig_cadence.log - one full recorded rig cycle of `smc/<component> state=N`
events with wall-clock timestamps. Rig events are injected at their true relative times, so
this reproduces the actual arrival pattern, including the 4 ms bursts.

The fixture is COMMITTED. It used to read MQTT/rig_cadence.log, a live file outside the
repo, and that capture was lost the moment the collector was restarted (it opened its log
"w"; it now appends). A test must not depend on a log someone can truncate.

    python tests/test_replay.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, ROOT)

import mock_vc as M                                        # noqa: E402
from test_shadow import Scene, Gateway, check, RESULTS      # noqa: E402
import statesync as S                                       # noqa: E402

CADENCE = os.path.join(HERE, "rig_cadence.log")
LINE = re.compile(r"(\d+):(\d+):([\d.]+)\s+(\S+)\s+state=(-?\d+)")


def load_events(limit_s=1e9):
    ev = []
    t0 = None
    for line in open(CADENCE, encoding="utf-8", errors="replace"):
        m = LINE.match(line)
        if not m:
            continue
        t = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
        if t0 is None:
            t0 = t
        if t - t0 > limit_s:
            break
        ev.append(((t - t0) * 1000.0, "smc/" + m.group(4), int(m.group(5))))
    return ev


def main():
    print("=" * 78)
    print("SMC shadow - full-cycle replay against recorded rig cadence")
    print("=" * 78)

    events = load_events()
    print("rig events replayed: {}  span: {:.1f}s".format(len(events), events[-1][0] / 1000.0))

    cfg = S.load_config(os.path.join(ROOT, "shadow-config.json"))
    cfg["site"]["vueone"]["enabled"] = False
    bridge = S.Bridge(cfg, dry=True)
    cmds = []
    bridge._pub = lambda t, o, retain: (
        cmds.append(o) if t == bridge.cmd_topic else None)

    scene = Scene()
    g = Gateway(scene, max_ticks=10 ** 7)
    g.pump(1)

    import contextlib
    import io as _io
    idx = 0
    quiet = _io.StringIO()
    with contextlib.redirect_stdout(quiet):
        while idx < len(events) or g.ns["pending"] or g.ns["active"]:
            while idx < len(events) and events[idx][0] <= M.CLOCK.ms:
                _, topic, state = events[idx]
                cmds[:] = []
                bridge.on_message(topic, "{state:%d}" % state)
                for c in cmds:
                    g.send(c)
                idx += 1
            g.pump(1)
            if M.CLOCK.ms > 900000:
                break

    enq = g.ev("enqueue")
    comp = g.ev("completed")
    fail = g.ev("failed")
    tmo = g.ev("timeout")
    rej = g.ev("reject")
    print("\ncommands: {} enqueued, {} completed, {} failed, {} timeout, {} rejected"
          .format(len(enq), len(comp), len(fail), len(tmo), len(rej)))

    check("every enqueued command completed",
          len(comp) == len(enq) and len(enq) > 50, "%d/%d" % (len(comp), len(enq)))
    check("zero failures across a full cycle", not fail,
          str([f.get("reason") for f in fail][:4]))
    check("zero routine timeouts", not tmo)
    check("nothing left pending at end of cycle",
          not g.ns["pending"] or all(not q for q in g.ns["pending"].values()))

    worst, worst_who = 0.0, ""
    starts = {e["commandId"]: e for e in g.ev("start")}
    for e in comp:
        st = starts.get(e.get("commandId"))
        if st and st.get("durMs") and e.get("durationMs"):
            err = abs(e["durationMs"] - st["durMs"])
            if err > worst:
                worst, worst_who = err, "%s %d/%d" % (e["vcId"], e["durationMs"], st["durMs"])
    check("no motion overruns its commanded duration by >250ms", worst <= 250,
          "worst=%dms %s" % (worst, worst_who))

    routines = [e for e in comp if e.get("execution") == "routine"]
    verified = [e for e in routines if e.get("verified")]
    check("every gripper routine verified by parent chain",
          verified and len(verified) == len([r for r in routines if r["vcId"] != "UR3e"]),
          "%d verified of %d non-UR3e routines"
          % (len(verified), len([r for r in routines if r["vcId"] != "UR3e"])))

    ur = [e for e in routines if e["vcId"] == "UR3e"]
    check("UR3e routines ran", bool(ur), "%d" % len(ur))

    # THE anti-freeze property, measured on real data: axis/signal motion that is IN FLIGHT
    # while a routine in another lane is executing must still finish on its commanded time.
    disp = {e["commandId"]: e["at"] for e in g.ev("dispatched")}
    rwin = [(disp[e["commandId"]], e["at"], e.get("lane"))
            for e in comp if e.get("execution") == "routine" and e["commandId"] in disp]
    overlapped, worst_o = 0, 0.0
    for e in comp:
        if e.get("execution") not in ("axis", "signal"):
            continue
        st = starts.get(e["commandId"])
        if not st or not st.get("durMs"):
            continue
        a, b = st["at"], e["at"]
        if any(a < re_ and b > rs and rl != e.get("lane") for rs, re_, rl in rwin):
            overlapped += 1
            worst_o = max(worst_o, abs(e["durationMs"] - st["durMs"]))
    # The count is a property of the recipe's natural overlap (~11% of cycle time), not of
    # the gateway; require it to be non-vacuous, and assert on the error. The bound is 3
    # because the fixture is a SINGLE recorded cycle - it scales with cycles, the error does
    # not.
    check("motion overlapping a cross-lane routine still hits its time",
          overlapped >= 3 and worst_o <= 250,
          "%d overlapping moves, worst error %dms" % (overlapped, worst_o))

    ids = [e["commandId"] for e in g.ev("start")] + [e["commandId"] for e in g.ev("dispatched")]
    check("no command executed twice", len(ids) == len(set(ids)),
          "%d starts, %d unique" % (len(ids), len(set(ids))))

    lanes = {}
    for e in g.events:
        if e["event"] in ("start", "dispatched"):
            lanes.setdefault(e.get("lane"), []).append((e["at"], e["commandId"]))
    check("all 9 mechanism lanes exercised", len(lanes) >= 9,
          "lanes=%s" % sorted(lanes))

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
