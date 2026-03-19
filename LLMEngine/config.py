import os
from pathlib import Path

BASE_DIR = Path(__file__).parent
KNOWLEDGE_BASE_DIR = BASE_DIR / "rag" / "knowledge_base"

MODEL_NAME = "claude-sonnet-4-20250514"
PORT = 8100

ANTHROPIC_API_KEY: str = os.environ.get("ANTHROPIC_API_KEY", "")
