"""Step 1 — Extract I/O signal list from equipment PDFs / component description."""
from __future__ import annotations

from langchain_anthropic import ChatAnthropic
from langchain_core.messages import HumanMessage, SystemMessage

SYSTEM = """\
You are an IEC 61499 automation engineer.
Given equipment documentation and component context, extract a structured signal list.
For each signal produce one line:
  <DIRECTION> <signal_name> <data_type>  <description>
Where DIRECTION is IN or OUT, data_type is BOOL/INT/REAL/STRING, signal_name uses lowercase_underscore naming.
Output ONLY the signal list — no prose, no headings."""


async def run(state: dict, llm: ChatAnthropic) -> dict:
    components_text = _format_components(state["components"])
    pdf_text = state.get("pdf_content", "").strip()

    content = f"COMPONENT CONTEXT:\n{components_text}"
    if pdf_text:
        content += f"\n\nEQUIPMENT DOCUMENTATION:\n{pdf_text[:6000]}"
    rag = state.get("rag_context", "").strip()
    if rag:
        content += f"\n\nRELEVANT TEMPLATES (for naming reference):\n{rag[:2000]}"

    response = await llm.ainvoke([
        SystemMessage(content=SYSTEM),
        HumanMessage(content=content),
    ])
    return {"io_signals": response.content}


def _format_components(components: list[dict]) -> str:
    lines = []
    for c in components:
        lines.append(
            f"Name={c.get('Name','?')}  Type={c.get('Type','?')}  "
            f"Description={c.get('Description','')}"
        )
        for s in c.get("States", []):
            lines.append(f"  State {s.get('StateNumber',0)}: {s.get('Name','')}")
    return "\n".join(lines) if lines else "(no components)"
