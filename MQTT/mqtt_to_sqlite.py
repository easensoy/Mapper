#!/usr/bin/env python3
"""Log SMC MQTT telemetry (smc/#) to JSONL + SQLite, printing each row live.

  python mqtt_to_sqlite.py                # subscribe + log forever (Ctrl+C to stop)
  python mqtt_to_sqlite.py show           # last 20 rows, any kind
  python mqtt_to_sqlite.py show 100       # last 100 rows
  python mqtt_to_sqlite.py show process   # last 20 PROCESS rows (recipe phases)
  python mqtt_to_sqlite.py show component 50
  python mqtt_to_sqlite.py steps          # process phase timeline, one line per change
  python mqtt_to_sqlite.py csv            # export everything to smc.csv for Excel

Files are written to the desktop Output folder (OUT_DIR below): smc.db (SQLite)
+ smc.jsonl (one JSON object per line). The rig payload is the loose form
{state:0} (not valid JSON), so the parsed integer goes in `state` and the
original text in `raw`.

TWO KINDS OF TELEMETRY share one broker, one table and one file:

  smc/<component>          e.g. smc/feeder {state:2}
      A device's own position. Published by the MqttFmt/MqttPub pair embedded
      in each actuator/sensor CAT, fired when that device changes state.

  smc/process/<Process>    e.g. smc/process/Feed_Station {state:5}
      Which PHASE of the recipe a process is in. Published by the pair embedded
      in Process1_Generic. The number identifies a state of the process in the
      twin (5 = FeederReturning), NOT the compiled recipe row index - the row
      index is a generator artefact and means nothing outside it.

`kind` tells the two apart, because a process and a component can legitimately
carry the same number while meaning different things.

PHASE NAMES: the wire payload is an integer, so the Mapper writes an
ordinal -> name map beside the generated project (process-phases.json). When it
is found, each process row is stored and printed with the twin's own state name
and the log reads "Unclamping" rather than "29". Without it the logger still
works, just in numbers.
"""
import sys, os, re, json, sqlite3, datetime

OUT_DIR = r"C:\Users\alper\OneDrive\Masaüstü\Output"
os.makedirs(OUT_DIR, exist_ok=True)
DB    = os.path.join(OUT_DIR, "smc.db")
JSONL = os.path.join(OUT_DIR, "smc.jsonl")
HOST, PORT = "127.0.0.1", 1883
STATE_RE = re.compile(r"state\s*:\s*(-?\d+)")

# The root the generator publishes under (MapperConfig.MqttTopicRoot) and the
# segment Process1_Generic inserts for a process (RootPath 'smc/process').
ROOT, PROCESS_SEG = "smc", "process"

# Written by the Mapper next to the generated project, deliberately outside the
# EAE solution. Override with SMC_PHASE_MAP when generating somewhere else.
PHASE_MAP_PATHS = [
    os.environ.get("SMC_PHASE_MAP", ""),
    r"C:\Demonstrator\process-phases.json",
]


def load_phase_map():
    """process -> {ordinal: state name}. Empty when the map has not been generated."""
    for p in PHASE_MAP_PATHS:
        if not p or not os.path.exists(p):
            continue
        try:
            with open(p, encoding="utf-8") as f:
                doc = json.load(f)
            m = {proc: {int(k): v for k, v in phases.items()}
                 for proc, phases in doc.get("processes", {}).items()}
            if m:
                return m, p
        except Exception as e:
            print(f"phase map at {p} could not be read ({e}); falling back to numbers")
    return {}, None


PHASES, PHASE_MAP_FILE = load_phase_map()


def phase_name(kind, name, state):
    """The twin's state name for a process reading, else None."""
    if kind != "process" or state is None:
        return None
    return PHASES.get(name, {}).get(state)


def classify(topic):
    """Split a rig topic into (kind, name).

    smc/feeder              -> ("component", "feeder")
    smc/process/Feed_Station-> ("process",   "Feed_Station")

    Anything unexpected is kept rather than dropped, tagged "other", so a new
    topic shape shows up in the log instead of silently vanishing.
    """
    parts = [p for p in (topic or "").split("/") if p]
    if parts and parts[0] == ROOT:
        parts = parts[1:]
    if not parts:
        return "other", topic or ""
    if parts[0] == PROCESS_SEG:
        return ("process", "/".join(parts[1:])) if len(parts) > 1 else ("other", topic)
    return ("component", "/".join(parts)) if len(parts) == 1 else ("other", topic)


def db_connect():
    db = sqlite3.connect(DB)
    db.execute("""CREATE TABLE IF NOT EXISTS mqtt_log(
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        ts TEXT, topic TEXT, component TEXT, state INTEGER, raw TEXT)""")
    # Additive migration: older captures have no `kind`. Add it, then label the
    # existing rows from their own topic so history stays queryable rather than
    # being split across two schemas.
    cols = [c[1] for c in db.execute("PRAGMA table_info(mqtt_log)")]
    if "kind" not in cols:
        db.execute("ALTER TABLE mqtt_log ADD COLUMN kind TEXT")
        for rowid, topic in db.execute(
                "SELECT id, topic FROM mqtt_log WHERE kind IS NULL").fetchall():
            db.execute("UPDATE mqtt_log SET kind=? WHERE id=?", (classify(topic)[0], rowid))
        print(f"migrated: added `kind` and labelled "
              f"{db.execute('SELECT COUNT(*) FROM mqtt_log').fetchone()[0]} existing rows")
    if "phase" not in cols and "phase" not in [c[1] for c in db.execute("PRAGMA table_info(mqtt_log)")]:
        # Resolved at capture time rather than on read: the map belongs to the build that was
        # running, so a later regeneration cannot silently relabel history.
        db.execute("ALTER TABLE mqtt_log ADD COLUMN phase TEXT")
        print("migrated: added `phase` (earlier rows keep NULL - they predate the name map)")
    db.commit()
    return db


def show(kind=None, n=20):
    db = db_connect()
    sql = "SELECT ts, kind, component, state, phase FROM mqtt_log"
    args = []
    if kind:
        sql += " WHERE kind=?"
        args.append(kind)
    sql += " ORDER BY id DESC LIMIT ?"
    args.append(n)
    rows = db.execute(sql, args).fetchall()
    print(f"{'ts':<26}{'kind':<11}{'name':<20}{'state':>6}  phase")
    print("-" * 90)
    for ts, k, comp, state, ph in reversed(rows):
        print(f"{ts:<26}{(k or '?'):<11}{comp:<20}{str(state):>6}  {ph or ''}")
    counts = dict(db.execute("SELECT kind, COUNT(*) FROM mqtt_log GROUP BY kind").fetchall())
    total = sum(counts.values())
    breakdown = "  ".join(f"{k or '?'}={v}" for k, v in sorted(counts.items(), key=lambda x: str(x[0])))
    print(f"\n{total} rows total  ({breakdown})   db={DB}   jsonl={JSONL}")
    print(f"phase names: {PHASE_MAP_FILE or 'NOT FOUND - showing numbers only'}")


def steps(n=40):
    """Process phase timeline: one line per phase CHANGE, per process.

    A process republishes on every recipe row, so consecutive rows repeat the
    same state while one phase spans several rows. Collapsing the repeats is
    what turns the raw log into a readable sequence of phases.
    """
    db = db_connect()
    rows = db.execute(
        "SELECT ts, component, state, phase FROM mqtt_log WHERE kind='process' ORDER BY id").fetchall()
    if not rows:
        print("no process rows yet -- is a process publishing on smc/process/#?")
        return
    last, timeline = {}, []
    for ts, proc, state, ph in rows:
        if last.get(proc) != state:
            timeline.append((ts, proc, state, ph))
            last[proc] = state
    print(f"{'ts':<26}{'process':<20}{'state':>6}  phase")
    print("-" * 86)
    for ts, proc, state, ph in timeline[-n:]:
        print(f"{ts:<26}{proc:<20}{str(state):>6}  {ph or ''}")
    print(f"\n{len(timeline)} phase changes from {len(rows)} process messages")
    if not PHASE_MAP_FILE:
        print("phase names unavailable (no process-phases.json) - regenerate to produce it")


def export_csv():
    import csv
    out = os.path.join(OUT_DIR, "smc.csv")
    db = db_connect()
    with open(out, "w", newline="", encoding="utf-8-sig") as f:   # utf-8-sig so Excel reads it
        w = csv.writer(f)
        w.writerow(["id", "ts", "topic", "kind", "component", "state", "phase", "raw"])
        w.writerows(db.execute(
            "SELECT id,ts,topic,kind,component,state,phase,raw FROM mqtt_log ORDER BY id"))
    n = db.execute("SELECT COUNT(*) FROM mqtt_log").fetchone()[0]
    print(f"wrote {n} rows -> {out}  (double-click to open in Excel)")


def record(db, jf, topic, payload):
    m = STATE_RE.search(payload or "")
    state = int(m.group(1)) if m else None
    kind, name = classify(topic)
    phase = phase_name(kind, name, state)
    ts = datetime.datetime.now().isoformat(timespec="milliseconds")
    jf.write(json.dumps({"ts": ts, "topic": topic, "kind": kind, "component": name,
                         "state": state, "phase": phase, "raw": payload}) + "\n")
    jf.flush()
    db.execute(
        "INSERT INTO mqtt_log(ts,topic,kind,component,state,phase,raw) VALUES(?,?,?,?,?,?,?)",
        (ts, topic, kind, name, state, phase, payload))
    db.commit()
    # Print like `mosquitto_sub -v`: "smc/<topic> {state:N}", plus the phase name
    # when one is known, which is the whole point of watching a process.
    print(f"{topic} {payload}" + (f"   {phase}" if phase else ""), flush=True)


def run():
    db = db_connect()
    jf = open(JSONL, "a", encoding="utf-8")
    try:
        import paho.mqtt.client as mqtt
    except ImportError:
        # No paho-mqtt: read "topic payload" lines from stdin.
        #   mosquitto_sub -h 127.0.0.1 -t "smc/#" -v | python mqtt_to_sqlite.py
        print("paho-mqtt not installed -> reading stdin "
              "(pipe: mosquitto_sub -t \"smc/#\" -v | python mqtt_to_sqlite.py)")
        for line in sys.stdin:
            line = line.rstrip("\n")
            if line:
                topic, _, payload = line.partition(" ")
                record(db, jf, topic, payload)
        return
    try:
        client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION1)   # paho-mqtt v2.x
    except (AttributeError, TypeError):
        client = mqtt.Client()                                   # paho-mqtt v1.x
    # smc/# covers BOTH kinds -- components at smc/<name> and processes at
    # smc/process/<Name> -- so one subscription captures the whole rig.
    client.on_connect = lambda c, u, f, rc, *a: (print(f"connected rc={rc}"), c.subscribe("smc/#"))
    client.on_message = lambda c, u, msg: record(db, jf, msg.topic, msg.payload.decode("utf-8", "replace"))
    import time
    for attempt in range(20):
        try:
            client.connect(HOST, PORT, 60)
            break
        except OSError as e:
            print(f"broker not reachable yet ({e}); retry {attempt + 1}/20 in 1s...")
            time.sleep(1)
    else:
        print(f"could not connect to broker at {HOST}:{PORT} -- is mosquitto running?")
        return
    print(f"logging smc/# (components + processes) from {HOST}:{PORT} "
          f"-> {JSONL} + {DB}   (Ctrl+C to stop)")
    client.loop_forever()


if __name__ == "__main__":
    args = sys.argv[1:]
    cmd = args[0] if args else ""
    if cmd == "show":
        rest = args[1:]
        kind = rest[0] if rest and not rest[0].isdigit() else None
        num = next((int(a) for a in rest if a.isdigit()), 20)
        show(kind, num)
    elif cmd == "steps":
        steps(int(args[1]) if len(args) > 1 and args[1].isdigit() else 40)
    elif cmd == "csv":
        export_csv()
    else:
        run()
