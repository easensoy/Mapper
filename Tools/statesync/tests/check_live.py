"""Live-broker acceptance. Run AFTER a rig cycle with the new gateway deployed.

Verifies the properties that only the broker can prove - subscriptions and publishers,
not CSV rows.

    python tests/check_live.py [since_minutes]      default 30
"""
import collections
import io
import re
import sys
import time

LOG = r"C:\VueOneMapper\MQTT\mosquitto.log"
STATE_RE = re.compile(r"(\d+): Received SUBSCRIBE from (\S+)")
TOPIC_RE = re.compile(r"\d+: \t(\S+) \(QoS (\d)\)")
PUB_RE = re.compile(r"(\d+): Received PUBLISH from (\S+) \(d\d, q(\d), r(\d),[^']*'([^']+)'")

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    print("{:4}  {:<54} {}".format("PASS" if ok else "FAIL", name, detail))


def main():
    since = time.time() - float(sys.argv[1] if len(sys.argv) > 1 else 30) * 60
    subs, cur = [], None
    pubs = collections.Counter()
    with io.open(LOG, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            m = STATE_RE.match(line)
            if m:
                cur = [int(m.group(1)), m.group(2), []]
                subs.append(cur)
                continue
            if cur is not None:
                t = TOPIC_RE.match(line)
                if t:
                    cur[2].append(t.group(1))
                elif "SUBACK" in line:
                    cur = None
            p = PUB_RE.match(line)
            if p and int(p.group(1)) >= since:
                pubs[(p.group(2), p.group(5))] += 1

    recent = [s for s in subs if s[0] >= since]
    # the VC client is whichever recent subscriber took vc/command
    vc = None
    for ts, cl, tops in recent:
        if any(t.endswith("/vc/command") for t in tops):
            vc = cl
    if vc is None:
        print("No client subscribed to .../vc/command in the window. Is VC connected?")
        return 1
    vc_topics = sorted({t for ts, cl, tops in recent if cl == vc for t in tops})
    print("VC client: {}\nsubscribed: {}\n".format(vc, vc_topics))

    check("VC has exactly ONE subscription", len(vc_topics) == 1, str(vc_topics))
    check("that subscription is vc/command",
          len(vc_topics) == 1 and vc_topics[0].endswith("/vc/command"))
    check("VC does NOT subscribe to vc/event",
          not any(t.endswith("/vc/event") for t in vc_topics))
    stale = [t for t in vc_topics if "/state" in t]
    check("no stale per-actuator state subscriptions", not stale, str(stale))

    vc_pubs = {t: n for (c, t), n in pubs.items() if c == vc}
    check("VC publishes only vc/event",
          vc_pubs and all(t.endswith("/vc/event") for t in vc_pubs), str(sorted(vc_pubs)))
    check("VC never publishes to a rig observation topic",
          not any("/state" in t or t.startswith("smc/") for t in vc_pubs))

    # VueOne must be a pure observation subscriber: state topics only, never vc/*
    vue = [cl for ts, cl, tops in recent
           if cl != vc and any(t.endswith("/state") for t in tops)]
    for cl in set(vue):
        tops = sorted({t for ts, c, tt in recent if c == cl for t in tt})
        bad = [t for t in tops if "/vc/" in t or "_bridge" in t]
        check("VueOne client %s subscribes to observations only" % cl[:24], not bad, str(bad))

    cmds = sum(n for (c, t), n in pubs.items() if t.endswith("/vc/command"))
    evts = sum(vc_pubs.get(t, 0) for t in vc_pubs)
    check("commands flowed", cmds > 20, "%d commands" % cmds)
    check("events flowed back", evts > 20, "%d events" % evts)

    ok = sum(1 for _, o, _ in RESULTS if o)
    print("\n{}/{} live checks passed".format(ok, len(RESULTS)))
    return 0 if ok == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
