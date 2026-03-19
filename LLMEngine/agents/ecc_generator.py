"""Step 2 — Generate an ECC state machine description from I/O signals."""
from __future__ import annotations

from langchain_anthropic import ChatAnthropic
from langchain_core.messages import HumanMessage, SystemMessage

SYSTEM = """\
You are an IEC 61499 ECC designer.
Given a component's I/O signal list and its description, define the Execution Control Chart:
- List each state with its number, name, and a one-line description.
- For Actuator components: exactly 5 states (e.g. Idle, Moving, InPosition, Error, Reset).
- For Sensor components: exactly 2 states (e.g. Off, On).
- For other types match the count implied by the component description.

Output format (plain text, no JSON):
STATE <n>: <Name> — <description>

Then on a new section:
TRANSITIONS:
<source_state> → <dest_state> : <condition>"""


async def run(state: dict, llm: ChatAnthropic) -> dict:
    components_text = _format_components(state["components"])
    io_signals = state.get("io_signals", "")
    rag = state.get("rag_context", "").strip()

    content = (
        f"COMPONENTS:\n{components_text}\n\n"
        f"I/O SIGNALS:\n{io_signals}"
    )
    if rag:
        content += f"\n\nTEMPLATE CONTEXT:\n{rag[:2000]}"

    response = await llm.ainvoke([
        SystemMessage(content=SYSTEM),
        HumanMessage(content=content),
    ])
    return {"ecc_description": response.content}


def _format_components(components: list[dict]) -> str:
    return "\n".join(
        f"{c.get('Name','?')} [{c.get('Type','?')}]: {c.get('Description','')}"
        for c in components
    ) or "(none)"
