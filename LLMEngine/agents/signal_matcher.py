"""Step 5 — Match signal names to VueOne / EAE naming conventions."""
from __future__ import annotations

import json
import re

from langchain_anthropic import ChatAnthropic
from langchain_core.messages import HumanMessage, SystemMessage

SYSTEM = """\
You are an IEC 61499 naming specialist.
Given a list of VueOneComponent objects, ensure all state names conform to the
VueOne / EAE naming convention:
- State names must be lowercase_underscore (e.g. "retracted", "extended", "idle").
- Remove spaces, hyphens; replace with underscores.
- Keep names concise (1-3 words max).

Return the SAME JSON array with corrected state names ONLY.
Do not change ComponentID, StateID, StateNumber, InitialState, Time, Position, Counter, StaticState.
Return ONLY the JSON array — no markdown, no explanation."""


async def run(state: dict, llm: ChatAnthropic) -> dict:
    raw = state.get("generated_fb_json", "[]")
    rag = state.get("rag_context", "").strip()

    content = f"COMPONENTS:\n{raw}"
    if rag:
        content += f"\n\nNAMING REFERENCE (from existing templates):\n{rag[:1500]}"

    response = await llm.ainvoke([
        SystemMessage(content=SYSTEM),
        HumanMessage(content=content),
    ])

    text = response.content.strip()
    if text.startswith("```"):
        text = text.split("```")[1]
        if text.startswith("json"):
            text = text[4:]
        text = text.strip()

    try:
        components = json.loads(text)
        # Final normalisation pass in Python
        for comp in components:
            for s in comp.get("States", []):
                s["Name"] = _normalise(s.get("Name", ""))
        return {"result": components}
    except json.JSONDecodeError:
        # Fall back to whatever validated output we had
        try:
            return {"result": json.loads(raw)}
        except json.JSONDecodeError:
            return {"result": []}


def _normalise(name: str) -> str:
    name = name.strip().lower()
    name = re.sub(r"[\s\-]+", "_", name)
    name = re.sub(r"[^a-z0-9_]", "", name)
    return name or "state"
