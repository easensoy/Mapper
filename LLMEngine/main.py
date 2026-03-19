"""FastAPI application entry point."""
from __future__ import annotations

import asyncio
import logging
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse

from config import KNOWLEDGE_BASE_DIR
from models.schemas import GenerateRequest, GenerateResponse
from rag.store import build_store
from chain import run_chain_async
import registry

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s — %(message)s",
)
logger = logging.getLogger(__name__)

_rag_retriever = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _rag_retriever
    logger.info("Building RAG store from %s …", KNOWLEDGE_BASE_DIR)
    _rag_retriever = build_store(KNOWLEDGE_BASE_DIR)
    if _rag_retriever:
        logger.info("RAG store ready.")
    else:
        logger.info("RAG disabled (empty knowledge_base/).")
    yield
    registry.job_queues.clear()


app = FastAPI(title="VueOne LLM Engine", version="1.0.0", lifespan=lifespan)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health")
async def health():
    return {"status": "ok"}


@app.post("/generate", response_model=GenerateResponse)
async def generate(request: GenerateRequest):
    job_id = str(uuid.uuid4())
    queue: asyncio.Queue = asyncio.Queue()
    registry.job_queues[job_id] = queue

    # Fire and forget — the chain runs in the background
    asyncio.create_task(run_chain_async(request, job_id, queue, _rag_retriever))

    logger.info("Job %s started for %d component(s).", job_id, len(request.components))
    return GenerateResponse(job_id=job_id)


@app.get("/stream/{job_id}")
async def stream(job_id: str):
    if job_id not in registry.job_queues:
        raise HTTPException(status_code=404, detail=f"Job '{job_id}' not found.")

    queue = registry.job_queues[job_id]

    async def event_generator():
        try:
            while True:
                try:
                    item = await asyncio.wait_for(queue.get(), timeout=300.0)
                except asyncio.TimeoutError:
                    yield "event: error\ndata: Generation timed out after 5 minutes.\n\n"
                    return

                if item is None:  # sentinel — chain finished
                    return

                event_type = item.get("event", "log")
                data = item.get("data", "").replace("\n", " ")
                yield f"event: {event_type}\ndata: {data}\n\n"

                if event_type in ("complete", "error"):
                    return
        finally:
            registry.job_queues.pop(job_id, None)

    return StreamingResponse(
        event_generator(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",
        },
    )
