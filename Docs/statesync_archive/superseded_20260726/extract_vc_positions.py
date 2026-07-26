"""Extract per-component, per-state joint positions from a VueOne Control.xml.

VC actuators need a POSITION per state (a bare state number can't place a joint).
The twin stores it as <Position> inside each <State> (Operator=equal => absolute),
which is exactly what VueOne feeds VC. This writes {vcId: {stateNum: position}} so
the VC shadow can drive each joint to the real home/work value instead of guessing
min/max. Regenerate whenever Control.xml changes.

    python extract_vc_positions.py [Control.xml]  ->  vc-positions.json
"""
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONTROL = r"C:\VueOneMapper\MapperUI\MapperUI\bin\Debug\net10.0-windows\Input\Control.xml"

def main():
    control = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CONTROL
    out_path = os.path.join(HERE, "vc-positions.json")
    root = ET.parse(control).getroot()
    out = {}
    nonequal = []
    for c in root.iter("Component"):
        vcid = (c.findtext("VcID") or "").strip()
        if not vcid:
            continue
        states = {}
        for st in c.findall("State"):
            num = st.findtext("State_Number")
            pos = st.findtext("Position")
            op = (st.findtext("Operator") or "").strip().lower()
            if num is None or pos is None:
                continue
            try:
                states[str(int(num))] = float(pos)
            except Exception:
                continue
            if op and op != "equal":
                nonequal.append((vcid, num, op))
        if states:
            out[vcid] = states
    with open(out_path, "w") as f:
        json.dump(out, f, indent=2)
    print("wrote %s : %d components" % (out_path, len(out)))
    for vc in sorted(out):
        print("  %-22s %s" % (vc, out[vc]))
    if nonequal:
        print("NON-EQUAL operators (need relative handling):", nonequal)
    else:
        print("all operators = equal (absolute positions)")

if __name__ == "__main__":
    main()
