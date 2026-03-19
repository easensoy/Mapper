"""Pydantic models mirroring C# VueOneComponent / VueOneState schema."""
from pydantic import BaseModel, Field


class VueOneState(BaseModel):
    StateID: str = ""
    Name: str = ""
    StateNumber: int = 0
    InitialState: bool = False
    Time: int = 0
    Position: float = 0.0
    Counter: int = 0
    StaticState: bool = False


class VueOneComponent(BaseModel):
    ComponentID: str = ""
    Name: str = ""
    Description: str = ""
    Type: str = ""
    States: list[VueOneState] = Field(default_factory=list)
    NameTag: str = "Name"


class GenerateRequest(BaseModel):
    components: list[VueOneComponent]
    control_xml_path: str = ""
    pdf_paths: list[str] = Field(default_factory=list)


class GenerateResponse(BaseModel):
    job_id: str
