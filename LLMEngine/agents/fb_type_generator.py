"""Step 3 — Generate complete IEC 61499 FB definition as VueOneComponent JSON."""
from __future__ import annotations

import json

from langchain_anthropic import ChatAnthropic
from langchain_core.messages import HumanMessage, SystemMessage

SCHEMA = """\
{
  "ComponentID": "<uuid-string>",
  "Name": "<component name>",
  "Description": "<one-line description>",
  "Type": "Actuator|Sensor|Process|Robot",
  "States": [
    {
      "StateID": "<uuid-string>",
      "Name": "<state name, lowercase_underscore>",
      "StateNumber": <int starting at 1>,
      "InitialState": <true for state 1 only>,
      "Time": <milliseconds, 0 if unknown>,
      "Position": <0.0-1.0 normalised, 0.0 if not applicable>,
      "Counter": 0,
      "StaticState": false
    }
  ],
  "NameTag": "Name"
}"""

SYSTEM = f"""\
You are an IEC 61499 function block architect.
Produce a SINGLE valid JSON object matching this schema exactly:
{SCHEMA}

Rules:
- Actuator → exactly 5 states; Sensor → exactly 2 states.
- StateNumber is sequential starting at 1.
- Exactly one state has InitialState=true (the first one).
- State names use lowercase_underscore convention.
- Return ONLY the JSON object — no markdown fences, no prose."""


async def run(state: dict, llm: ChatAnthropic) -> dict:
    components = state["components"]
    io_signals = state.get("io_signals", "")
    ecc = state.get("ecc_description", "")
    rag = state.get("rag_context", "").strip()

    results = []
    for comp in components:
        content = (
            f"COMPONENT:\n{json.dumps(comp, indent=2)}\n\n"
            f"I/O SIGNALS:\n{io_signals}\n\n"
            f"ECC DESIGN:\n{ecc}"
        )
        if rag:
            content += f"\n\nTEMPLATE EXAMPLES:\n{rag[:2000]}"

        response = await llm.ainvoke([
            SystemMessage(content=SYSTEM),
            HumanMessage(content=content),
        ])

        raw = response.content.strip()
        # Strip markdown fences if the model added them
        if raw.startswith("```"):
            raw = raw.split("```")[1]
            if raw.startswith("json"):
                raw = raw[4:]
            raw = raw.strip()

        try:
            fb = json.loads(raw)
            results.append(fb)
        except json.JSONDecodeError:
            # Return raw string so validator can attempt repair
            results.append({"_raw": raw, "Name": comp.get("Name", ""), "Type": comp.get("Type", "")})

    return {"generated_fb_json": json.dumps(results)}
