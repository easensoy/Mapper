#!/usr/bin/env python3
"""Generate the COMPLETE StateSync map from a VueOne Control.xml.

Discovers EVERY publishable runtime component (Actuator / Sensor / Robot - the
component types the Mapper gives an MQTT-publishing CAT) and emits a self-
contained runtime map = the non-Control.xml config (from statesync.config.json)
+ one entry per component. No component list is hardcoded.

    python gen_sync_map.py <Control.xml> [statesync.config.json] --out sync-map.generated.json

Prefer --out over shell `>` : it writes UTF-8 WITHOUT a BOM and LF newlines. A
PowerShell `>` redirect re-encodes to UTF-16 + BOM, which some consumers choke on
(statesync.py itself tolerates a BOM via utf-8-sig, but --out avoids the issue).

Derivation rules (single source of truth here, matching the Mapper):

  * MQTT source topic - the Mapper wires MqttPub.Topic1 from each CAT's name
    InputVar: actuators/robots use actuator_name = Name.ToLowerInvariant(),
    sensors use the sensor 'name' = display name as-is. So:
        Actuator / Robot -> smc/<name.lower()>   (smc/feeder, smc/bearing_pnp)
        Sensor           -> smc/<Name>           (smc/PartInHopper)
    (assumes no active Instance_Name_Overrides / RigAliases - empty on this rig.)

  * UNS station - the VueOne Process whose transition conditions reference the
    component (first Process in document order wins); else 'unassigned'.

  * State vocabulary - CRITICAL: the rig publishes current_state_to_process, the
    CAT RUNTIME state, which for two CAT types does NOT match the raw Control.xml
    state numbers. This generator uses the CAT runtime vocabulary, extracted from
    the CAT cores (SevenStateCentreHomeActuator.fbt / Robot_Task_Core.fbt):
        seven_state (Bearing_PnP)  -> 0..6, named with the TWIN VOStates so VueOne resolves
                                      them {ReturnedHome,ToWork1,AtPick,TurningPlace,Place,ToHome,AtHome}
        robot_task  (UR3e Robot)   -> 0..2  {HomeInitial,StartTask,Complete}
    For five_state and sensor CATs the Control.xml numbers ALREADY equal the
    runtime set (0..4 / 0..1), so their Control.xml (twin) names are kept - which
    also lets VueOne resolve them. Control.xml states outside the runtime set are
    dropped with a warning.

Stdlib only (xml.etree, json).
"""
import json
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
TOPIC_ROOT = "smc"                                # = MapperConfig.MqttTopicRoot default
PUBLISHABLE = ("actuator", "sensor", "robot")     # component Types that get an MQTT CAT

# CAT runtime vocabularies. KEYS = the runtime state number the rig publishes
# (current_state_to_process). VALUES = the TWIN's VOState.Name, because VueOne's
# ResolveIncomingState matches the incoming StateName against VOState.Name - so a
# generic runtime name (AtWork1) that the twin doesn't have resolves to null and the
# STD stalls. The seven-state swivel's runtime numbers 0/2/3/4 coincide with the twin
# Bearing_PnP State_Numbers, whose names are ReturnedHome/AtPick/TurningPlace/Place
# (Control.xml). 1/5/6 are transients not gated by any STD transition.
# NOTE: the twin distinguishes the disassembly pick (AtPick2, #12) from the assembly
# pick (AtPick, #2), but the runtime CAT collapses both to work1/work2 (2/4) - so these
# assembly names are correct for the Assembly STD; a disassembly-swivel step may need
# its own handling (the runtime cannot tell the two picks apart).
SEVEN_STATE_VOCAB = {"0": "ReturnedHome", "1": "ToWork1", "2": "AtPick",
                     "3": "TurningPlace", "4": "Place", "5": "ToHome", "6": "AtHome"}
ROBOT_TASK_VOCAB = {"0": "HomeInitial", "1": "StartTask", "2": "Complete"}
FIVE_STATE_NUMS = {"0", "1", "2", "3", "4"}
SENSOR_NUMS = {"0", "1"}

# Current Mapper build-time decisions (CodeGen). If these flags change, update here:
#   MapperConfig.StubSevenStateActuatorsAsFiveState = false  -> Bearing_PnP is Seven_State
#   HandoffPlanner.DischargeActive = true (=> EnableRobotTaskTail) -> UR3e is Robot_Task
STUB_SEVEN_AS_FIVE = False
ROBOT_TASK_ENABLED = True
ROBOT_ARM_VCID = "UR3e"                            # TemplateMap.IsRobotTaskArm identifiers
ROBOT_ARM_ID = "C-c4ebfd68-0a5b-4512-889e-f6ab61bccecb"


def source_topic(name, ctype):
    base = name.lower() if ctype.lower() in ("actuator", "robot") else name
    return "{}/{}".format(TOPIC_ROOT, base)


def is_robot_task_arm(name, vcid, cid):
    if "gripper" in name.lower() or "grasp" in name.lower():
        return False
    return (name.lower() == "robot"
            or (vcid or "").lower() == ROBOT_ARM_VCID.lower()
            or (cid or "").lower() == ROBOT_ARM_ID.lower())


def is_branched_seven(comp):
    for st in comp.findall("State"):
        types = {(tr.findtext("Type") or "").strip().upper() for tr in st.findall("Transition")}
        if "PARALLEL" in types and "ALTERNATIVE" in types:
            return True
    return False


def control_states(comp, allowed, name, kind):
    """Control.xml number->name, restricted to the CAT runtime number set (twin names)."""
    out = {}
    for st in comp.findall("State"):
        num, snm = st.findtext("State_Number"), st.findtext("Name")
        if num is None or snm is None:
            continue
        k = str(int(num))
        if k not in allowed:
            sys.stderr.write("[warn] {} ({}): Control.xml state {} outside runtime set {} - dropped\n"
                             .format(name, kind, k, sorted(allowed, key=int)))
            continue
        out[k] = snm.strip()
    return out


def classify(comp, name, ctype, vcid, cid):
    """Return (catKind, states) using the Mapper's CAT runtime vocabulary."""
    if ctype.lower() == "sensor":
        return "sensor", control_states(comp, SENSOR_NUMS, name, "sensor")
    if ROBOT_TASK_ENABLED and is_robot_task_arm(name, vcid, cid):
        return "robot_task", dict(ROBOT_TASK_VOCAB)
    if not STUB_SEVEN_AS_FIVE and (len(comp.findall("State")) == 7 or is_branched_seven(comp)):
        return "seven_state", dict(SEVEN_STATE_VOCAB)
    return "five_state", control_states(comp, FIVE_STATE_NUMS, name, "five_state")


def hold_transient_terminal(states, gated_names):
    """VueOne is level-based; some actuators only FLASH through their terminal
    'finished' state (runtime 4, e.g. ReturnedFinished) on the way to rest (runtime
    0) - the rig remaps 4->0. A twin gate that ANDs several such finished-states
    (Feed's HandShake: Feeder/ReturnedFinished AND Transfer/ReturnedFinished AND
    Checker/ReturnedHome) can never see them coincide, because each component
    flashes 4 at a different time, so the follower deadlocks there. If a component
    is gated on its terminal (state-4) name but NEVER on its rest (state-0) name,
    name state 0 with the terminal name too -> VueOne holds 'finished' at rest,
    exactly like the rig's 4->0 remap. Skipped when the state-0 name is itself used
    by a gate (Checker/ReturnedHome, Shaft_*, CoverPNP_*), so those keep both."""
    if "4" in states and "0" in states:
        term, rest = states["4"], states["0"]
        if term in gated_names and rest not in gated_names:
            s = dict(states)
            s["0"] = term
            return s
    return states


def parse(xml_path):
    """Return (publishable components in doc order, processes[(name, referenced_ids)])."""
    root = ET.parse(xml_path).getroot()
    # component-id -> set of state names any process transition gates on (part after '/')
    gated = {}
    for cond in root.iter("Condition"):
        gcid = (cond.get("ComponentID") or "").strip()
        gnm = cond.get("Name") or ""
        if gcid and "/" in gnm:
            gated.setdefault(gcid, set()).add(gnm.split("/", 1)[1].strip())
    comps, processes = [], []
    for c in root.iter("Component"):
        cid = (c.findtext("ComponentID") or "").strip()
        name = (c.findtext("Name") or "").strip()
        ctype = (c.findtext("Type") or "").strip()
        vcid = (c.findtext("VcID") or "").strip()
        if ctype.lower() == "process":
            refs = set(rid for cond in c.iter("Condition")
                       for rid in [(cond.get("ComponentID") or "").strip()] if rid)
            processes.append((name, refs))
            continue
        if ctype.lower() not in PUBLISHABLE or not c.findall("State") or not name:
            continue
        kind, smap = classify(c, name, ctype, vcid, cid)
        smap = hold_transient_terminal(smap, gated.get(cid, set()))
        comps.append({"id": cid, "name": name, "vcId": vcid, "type": ctype,
                      "catKind": kind, "states": smap})
    model = root.findtext("System/Name") or root.findtext("Name") or "?"
    has_clamp = any(x["name"].lower() == "clamp" for x in comps)
    return comps, processes, model, has_clamp


def station_for(cid, processes):
    for pname, refs in processes:        # document order -> first owner wins
        if cid in refs:
            return pname.lower()
    return "unassigned"


def main():
    argv = [a for a in sys.argv[1:]]
    out_path = None
    if "--out" in argv:
        i = argv.index("--out")
        out_path = argv[i + 1] if i + 1 < len(argv) else None
        del argv[i:i + 2]
    if not argv:
        sys.exit("usage: gen_sync_map.py <Control.xml> [statesync.config.json] --out <file>")
    xml_path = argv[0]
    cfg_path = argv[1] if len(argv) > 1 else os.path.join(HERE, "statesync.config.json")
    with open(cfg_path, "r", encoding="utf-8-sig") as f:
        cfg = json.load(f)
    comps, processes, model, has_clamp = parse(xml_path)

    out = {
        "_comment": "GENERATED by gen_sync_map.py from Control.xml - do not hand-edit. Runtime config "
                    "from statesync.config.json; components from Control.xml; state tables are the CAT "
                    "runtime vocabulary. Regenerate whenever Control.xml changes.",
        "source": {"model": model, "clamp": has_clamp},
        "broker": cfg.get("broker", {"host": "192.168.1.50", "port": 1883}),
        "unsPrefix": cfg.get("unsPrefix", "uns/wmg/smc_rig/v1"),
        "unsQos": cfg.get("unsQos", 1),
        "vueone": cfg.get("vueone", {"enabled": True, "host": "127.0.0.1", "port": 51000, "clientId": "VC"}),
        "components": {},
    }
    kinds = {}
    for c in comps:
        topic = source_topic(c["name"], c["type"])
        if topic in out["components"]:
            sys.stderr.write("[warn] topic collision on {} ({} vs {})\n".format(
                topic, out["components"][topic]["twinName"], c["name"]))
        out["components"][topic] = {
            "station": station_for(c["id"], processes),
            "component": c["name"].lower(),
            "twinName": c["name"],
            "vcId": c["vcId"],
            "type": c["type"],
            "catKind": c["catKind"],
            "states": c["states"],
        }
        kinds[c["catKind"]] = kinds.get(c["catKind"], 0) + 1

    text = json.dumps(out, indent=2) + "\n"
    if out_path:
        with open(out_path, "w", encoding="utf-8", newline="\n") as f:   # UTF-8, no BOM, LF
            f.write(text)
    else:
        try:
            sys.stdout.reconfigure(encoding="utf-8", newline="\n")       # best effort; shell may still re-encode
        except Exception:
            pass
        sys.stdout.write(text)
    sys.stderr.write("[gen] model={!r} clamp={} : {} components ({}) across {} processes\n".format(
        model, "yes" if has_clamp else "no", len(comps),
        ", ".join("{} {}".format(v, k) for k, v in sorted(kinds.items())), len(processes)))


if __name__ == "__main__":
    main()
