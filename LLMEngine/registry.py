"""Shared job queue registry to avoid circular imports."""
import asyncio

job_queues: dict[str, asyncio.Queue] = {}


async def log_event(job_id: str, message: str) -> None:
    q = job_queues.get(job_id)
    if q:
        await q.put({"event": "log", "data": message})


async def complete_event(job_id: str, data: str) -> None:
    q = job_queues.get(job_id)
    if q:
        await q.put({"event": "complete", "data": data})


async def error_event(job_id: str, message: str) -> None:
    q = job_queues.get(job_id)
    if q:
        await q.put({"event": "error", "data": message})


async def close_queue(job_id: str) -> None:
    q = job_queues.get(job_id)
    if q:
        await q.put(None)  # sentinel
