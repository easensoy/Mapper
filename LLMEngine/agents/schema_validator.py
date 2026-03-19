"""Step 4 — Validate generated FB JSON against IEC 61499 structural rules.

Performs structural checks in Python first; only calls the LLM to repair
issues that cannot be fixed deterministically.
"""
from __future__ import annotations

import json
import uuid

from langchain_anthropic import ChatAnthropic
from langchain_core.messages import HumanMessage, SystemMessage

REPAIR_SYSTEM = """\
You are an IEC 61499 validator.
The following function block JSON has structural errors listed below.
Return a CORRECTED version of the JSON that fixes all listed errors.
Return ONLY the corrected JSON object — no markdown, no explanation."""


async def run(state: dict, llm: ChatAnthropic) -> dict:
    raw = state.get("generated_fb_json", "[]")
    try:
        components = json.loads(raw)
    except json.JSONDecodeError:
        components = []

    validated = []
    for fb in components:
        fb, errors = _check(fb)
        if errors:
            fb = await _repair(fb, errors, llm)
            fb, _ = _check(fb)  # Re-check after repair
        validated.append(fb)

    return {"generated_fb_json": json.dumps(validated)}


def _check(fb: dict) -> tuple[dict, list[str]]:
    errors = []
    fb_type = fb.get("Type", "")

    # Ensure IDs exist
    if not fb.get("ComponentID"):
        fb["ComponentID"] = str(uuid.uuid4())

    states: list[dict] = fb.get("States", [])
    for i, s in enumerate(states):
        if not s.get("StateID"):
            s["StateID"] = str(uuid.uuid4())
        if s.get("StateNumber", 0) == 0:
            s["StateNumber"] = i + 1

    # State count rules
    if fb_type.lower() == "actuator" and len(states) != 5:
        errors.append(f"Actuator must have 5 states, found {len(states)}")
    if fb_type.lower() == "sensor" and len(states) != 2:
        errors.append(f"Sensor must have 2 states, found {len(states)}")

    # Exactly one InitialState
    initials = [s for s in states if s.get("InitialState")]
    if len(initials) == 0 and states:
        states[0]["InitialState"] = True
    elif len(initials) > 1:
        errors.append("Multiple states have InitialState=true")

    # Sequential state numbers
    for i, s in enumerate(states, 1):
        if s.get("StateNumber") != i:
            errors.append(f"StateNumber out of sequence at position {i}")
            break

    return fb, errors


async def _repair(fb: dict, errors: list[str], llm: ChatAnthropic) -> dict:
    error_text = "\n".join(f"- {e}" for e in errors)
    response = await llm.ainvoke([
        SystemMessage(content=REPAIR_SYSTEM),
        HumanMessage(
            content=f"ERRORS:\n{error_text}\n\nFB JSON:\n{json.dumps(fb, indent=2)}"
        ),
    ])
    raw = response.content.strip()
    if raw.startswith("```"):
        raw = raw.split("```")[1]
        if raw.startswith("json"):
            raw = raw[4:]
        raw = raw.strip()
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return fb  # Keep original if repair fails
