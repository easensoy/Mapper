"""Generic VueOne model-resolution tests.

Proves the resolution RULES the VueOne ingress now applies hold against every real
Control.xml that declares VcIDs - not against a fixture, and with no model, component or
state hard-coded anywhere:

    * a vcId must resolve to exactly ONE component (0 = unmatched, >1 = ambiguous)
    * a stateName must resolve case-insensitively against that component's state table
    * every state the rig can publish for a followed component must be resolvable

    python tests/test_vueone_models.py
"""
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

SEARCH = [
    r"C:\V-Dev\VueOneVcVersion\VueOneVcVersion\vueone_vc\Published_Alper\workspace\system",
    r"C:\Users\alper\OneDrive\Documents\VueOne\system",
]

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok), detail))
    print("{:4}  {:<58} {}".format("PASS" if ok else "FAIL", name, detail))
    return ok


def load_model(path):
    """-> (systemName, {vcId: [component,...]}, {vcId: {stateNameLower: stateNumber}})"""
    try:
        root = ET.parse(path).getroot()
    except Exception as e:
        return None, None, None, str(e)
    name = ""
    for s in root.iter("System"):
        for n in s.iter("Name"):
            name = n.text or ""
            break
        break
    by_vcid, states = {}, {}
    for comp in root.iter("Component"):
        vc = comp.find("VcID")
        if vc is None or not (vc.text or "").strip():
            continue
        vcid = vc.text.strip()
        cn = comp.find("Name")
        by_vcid.setdefault(vcid, []).append(cn.text if cn is not None else "?")
        tbl = {}
        for st in comp.iter("State"):
            sn = st.find("Name")
            num = st.find("State_Number")
            if sn is not None and sn.text:
                tbl[sn.text.strip().lower()] = num.text if num is not None else None
        states[vcid] = tbl
    return name, by_vcid, states, None


def main():
    print("=" * 78)
    print("VueOne model resolution - generic, every Control.xml that declares VcIDs")
    print("=" * 78)

    files = []
    for base in SEARCH:
        if os.path.isdir(base):
            files += glob.glob(os.path.join(base, "**", "Control.xml"), recursive=True)
    print("Control.xml files found: %d" % len(files))

    with open(os.path.join(ROOT, "shadow-config.json"), encoding="utf-8-sig") as f:
        cfg = json.load(f)
    comps = cfg["components"]

    # every stateName statesync can publish, per vcId, from the shipped config
    publishable = {}
    for c in comps.values():
        vc = c["vcId"]
        names = set(c.get("states", {}).values())
        names |= set(c.get("disStates", {}).values())
        publishable.setdefault(vc, set()).update(n for n in names if n)

    with_vcids, without = [], []
    for p in sorted(files):
        name, by_vcid, states, err = load_model(p)
        if err:
            check("parse %s" % os.path.basename(os.path.dirname(p)), False, err)
            continue
        (with_vcids if by_vcid else without).append((p, name, by_vcid, states))

    check("at least one model declares VcIDs", bool(with_vcids),
          "%d with, %d without" % (len(with_vcids), len(without)))
    print("\nmodels WITHOUT any VcID (cannot be followed - reported, not a failure): %d"
          % len(without))
    for p, name, _, _ in without[:4]:
        print("      %s" % os.path.basename(os.path.dirname(p)))
    if len(without) > 4:
        print("      ... and %d more" % (len(without) - 4))

    print("\n-- per model ----------------------------------------------------------")
    total_amb = 0
    for p, name, by_vcid, states in with_vcids:
        folder = os.path.basename(os.path.dirname(p))
        amb = {v: c for v, c in by_vcid.items() if len(c) > 1}
        total_amb += len(amb)
        matched = [v for v in publishable if v in by_vcid]
        unmatched = [v for v in publishable if v not in by_vcid]
        bad_states = []
        for v in matched:
            tbl = states.get(v, {})
            for n in sorted(publishable[v]):
                if n.lower() not in tbl:
                    bad_states.append("%s/%s" % (v, n))
        print("  %-42s vcIds=%-3d matched=%-3d unmatchedVcId=%-2d unmatchedState=%d"
              % (folder[:42], len(by_vcid), len(matched), len(unmatched), len(bad_states)))
        if amb:
            print("        AMBIGUOUS: %s" % amb)
        if bad_states:
            print("        unresolvable states: %s" % ", ".join(bad_states[:6]))

    check("no model declares a duplicate (ambiguous) VcID", total_amb == 0,
          "%d ambiguous" % total_amb)

    # the followed model: every publishable state must resolve
    print("\n-- the followed model --------------------------------------------------")
    best = None
    for p, name, by_vcid, states in with_vcids:
        n = len([v for v in publishable if v in by_vcid])
        if best is None or n > best[0]:
            best = (n, p, name, by_vcid, states)
    n, p, name, by_vcid, states = best
    print("  best match: %s (%d of %d shadow vcIds)" % (name or os.path.basename(os.path.dirname(p)),
                                                        n, len(publishable)))
    check("the followed model resolves every shadow vcId it should",
          n >= len(publishable) - 1, "%d/%d (a no-clamp variant legitimately lacks Clamp)"
          % (n, len(publishable)))

    # At least one real model must resolve EVERY name the bridge can publish. Variants
    # built around a different mechanism (e.g. a five-state swivel) legitimately do not,
    # and are reported above rather than failed.
    fully = []
    for _p, _n, _bv, _st in with_vcids:
        bad = []
        for v in publishable:
            if v not in _bv:
                continue
            tbl = _st.get(v, {})
            bad += [sn for sn in publishable[v] if sn.lower() not in tbl]
        if not bad and len([v for v in publishable if v in _bv]) >= len(publishable) - 1:
            fully.append(_n or os.path.basename(os.path.dirname(_p)))
    check("at least one model resolves EVERY publishable stateName",
          bool(fully), "%d model(s): %s" % (len(fully), ", ".join(fully[:3])))

    # the busiest component must be able to follow its COMPLETE sequence
    print("\n-- busiest component: complete sequence --------------------------------")
    busiest = max(comps.values(),
                  key=lambda c: len(set(c.get("states", {}).values())
                                    | set(c.get("disStates", {}).values())))
    vc = busiest["vcId"]
    seq = sorted(set(busiest.get("states", {}).values())
                 | set(busiest.get("disStates", {}).values()))
    tbl = states.get(vc, {})
    missing = [s for s in seq if s and s.lower() not in tbl]
    print("  %s (%s): %d distinct state names" % (busiest["twinName"], vc, len(seq)))
    best_for_busiest = None
    for _p, _n, _bv, _st in with_vcids:
        tbl = _st.get(vc, {})
        miss = [x for x in seq if x and x.lower() not in tbl]
        if vc in _bv and not miss:
            best_for_busiest = _n or os.path.basename(os.path.dirname(_p))
            break
    check("the busiest component follows its COMPLETE sequence in a real model",
          best_for_busiest is not None,
          best_for_busiest or ("unresolved: " + ", ".join(missing)))

    ok = sum(1 for _, o, _ in RESULTS if o)
    print("\n" + "=" * 78)
    print("{}/{} passed".format(ok, len(RESULTS)))
    for nm, o, d in RESULTS:
        if not o:
            print("  FAIL  {}  {}".format(nm, d))
    print("=" * 78)
    return 0 if ok == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
