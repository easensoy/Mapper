"""FAISS vector store builder and retriever for the knowledge base."""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

_SUPPORTED_EXTENSIONS = {".fbt", ".xml", ".xsd", ".xlsx", ".txt", ".pdf"}


def build_store(knowledge_base_dir: Path):
    """Build a FAISS store from files in knowledge_base_dir.

    Returns a FAISS retriever, or None if the directory is empty or
    dependencies are not available.
    """
    if not knowledge_base_dir.exists():
        logger.info("knowledge_base/ directory not found — RAG disabled.")
        return None

    files = [
        f for f in knowledge_base_dir.rglob("*")
        if f.is_file() and f.suffix.lower() in _SUPPORTED_EXTENSIONS
    ]
    if not files:
        logger.info("knowledge_base/ is empty — RAG disabled.")
        return None

    try:
        from langchain_community.document_loaders import (
            TextLoader,
            PyPDFLoader,
        )
        from langchain.text_splitter import RecursiveCharacterTextSplitter
        from langchain_community.vectorstores import FAISS
        from langchain_community.embeddings import HuggingFaceEmbeddings
    except ImportError as exc:
        logger.warning("RAG dependencies not available (%s) — RAG disabled.", exc)
        return None

    docs = []
    splitter = RecursiveCharacterTextSplitter(chunk_size=1000, chunk_overlap=100)

    for path in files:
        try:
            if path.suffix.lower() == ".pdf":
                loader = PyPDFLoader(str(path))
            else:
                loader = TextLoader(str(path), encoding="utf-8", autodetect_encoding=True)
            raw = loader.load()
            docs.extend(splitter.split_documents(raw))
        except Exception as exc:
            logger.warning("Could not load %s: %s", path.name, exc)

    if not docs:
        logger.info("No documents loaded — RAG disabled.")
        return None

    try:
        embeddings = HuggingFaceEmbeddings(model_name="all-MiniLM-L6-v2")
        store = FAISS.from_documents(docs, embeddings)
        logger.info("RAG store built with %d chunks from %d files.", len(docs), len(files))
        return store.as_retriever(search_kwargs={"k": 4})
    except Exception as exc:
        logger.warning("Failed to build FAISS store: %s — RAG disabled.", exc)
        return None


def retrieve(retriever, query: str) -> str:
    """Return concatenated page content from retrieved docs, or empty string."""
    if retriever is None:
        return ""
    try:
        docs = retriever.invoke(query)
        return "\n\n---\n\n".join(d.page_content for d in docs)
    except Exception as exc:
        logger.warning("RAG retrieval failed: %s", exc)
        return ""
