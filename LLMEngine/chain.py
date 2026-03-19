"""LangGraph sequential chain — five prompt steps."""
from __future__ import annotations

import json
import logging

from langchain_anthropic import ChatAnthropic
from langgraph.graph import StateGraph, END
from typing import TypedDict

import registry
from config import MODEL_NAME, ANTHROPIC_API_KEY
from models.schemas import GenerateRequest
from rag.store import retrieve
from agents import io_extractor, ecc_generator, fb_type_generator, schema_validator, signal_matcher

logger = logging.getLogger(__name__)


class GraphState(TypedDict):
    components: list[dict]
    control_xml_path: str
    pdf_paths: list[str]
    job_id: str
    rag_context: str
    pdf_content: str
    io_signals: str
    ecc_description: str
    generated_fb_json: str
    result: list[dict]


def _build_llm() -> ChatAnthropic:
    return ChatAnthropic(
        model=MODEL_NAME,
        api_key=ANTHROPIC_API_KEY,
        max_tokens=4096,
    )


def _load_pdfs(pdf_paths: list[str]) -> str:
    if not pdf_paths:
        return ""
    try:
        from langchain_community.document_loaders import PyPDFLoader
        texts = []
        for path in pdf_paths:
            try:
                docs = PyPDFLoader(path).load()
                texts.extend(d.page_content for d in docs)
            except Exception as exc:
                logger.warning("Could not load PDF %s: %s", path, exc)
        return "\n\n".join(texts)
    except ImportError:
        logger.warning("pypdf not available — PDF loading skipped.")
        return ""


def _make_node(step_name: str, agent_module, llm: ChatAnthropic):
    async def node(state: GraphState) -> dict:
        job_id = state["job_id"]
        await registry.log_event(job_id, f"[{step_name}] Starting…")
        result = await agent_module.run(state, llm)
        summary = next(iter(result.values()), "")
        if isinstance(summary, str):
            preview = summary[:120].replace("\n", " ")
        else:
            preview = str(summary)[:120]
        await registry.log_event(job_id, f"[{step_name}] Done. {preview}")
        return result
    node.__name__ = step_name
    return node


async def run_chain_async(request: GenerateRequest, job_id: str, queue, rag_retriever) -> None:
    """Entry point called by the FastAPI background task."""
    registry.job_queues[job_id] = queue

    try:
        await registry.log_event(job_id, "Chain started.")

        llm = _build_llm()

        # Load PDFs
        pdf_content = _load_pdfs(request.pdf_paths)
        if pdf_content:
            await registry.log_event(job_id, f"Loaded {len(request.pdf_paths)} PDF(s).")

        # RAG context
        rag_context = ""
        if rag_retriever is not None:
            query = " ".join(c.Name for c in request.components)
            rag_context = retrieve(rag_retriever, f"IEC 61499 template {query}")
            if rag_context:
                await registry.log_event(job_id, "RAG context retrieved.")

        components_dicts = [c.model_dump() for c in request.components]

        initial_state: GraphState = {
            "components": components_dicts,
            "control_xml_path": request.control_xml_path,
            "pdf_paths": request.pdf_paths,
            "job_id": job_id,
            "rag_context": rag_context,
            "pdf_content": pdf_content,
            "io_signals": "",
            "ecc_description": "",
            "generated_fb_json": "[]",
            "result": [],
        }

        # Build and run the LangGraph chain
        graph = StateGraph(GraphState)
        graph.add_node("io_extract",  _make_node("io_extractor",     io_extractor,     llm))
        graph.add_node("ecc_gen",     _make_node("ecc_generator",    ecc_generator,    llm))
        graph.add_node("fb_gen",      _make_node("fb_type_generator", fb_type_generator, llm))
        graph.add_node("validate",    _make_node("schema_validator", schema_validator, llm))
        graph.add_node("sig_match",   _make_node("signal_matcher",   signal_matcher,   llm))

        graph.set_entry_point("io_extract")
        graph.add_edge("io_extract", "ecc_gen")
        graph.add_edge("ecc_gen",    "fb_gen")
        graph.add_edge("fb_gen",     "validate")
        graph.add_edge("validate",   "sig_match")
        graph.add_edge("sig_match",  END)

        chain = graph.compile()
        final_state: GraphState = await chain.ainvoke(initial_state)

        result = final_state.get("result", [])
        await registry.log_event(job_id, f"Chain complete. {len(result)} component(s) generated.")
        await registry.complete_event(job_id, json.dumps(result))

    except Exception as exc:
        logger.exception("Chain error for job %s", job_id)
        await registry.error_event(job_id, str(exc))
    finally:
        await registry.close_queue(job_id)
