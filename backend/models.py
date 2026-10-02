from typing import Literal
from uuid import UUID
from pydantic import BaseModel, ConfigDict, Field, StrictInt, field_validator


class MissionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    api_version: StrictInt = Field(ge=1, le=1)
    request_id: str
    text: str = Field(min_length=1, max_length=500)
    execution_mode: Literal["simulation"]

    @field_validator("request_id")
    @classmethod
    def uuid_id(cls, value):
        UUID(value)
        return value

    @field_validator("text")
    @classmethod
    def nonempty(cls, value):
        if not value.strip():
            raise ValueError("Empty command")
        return value


class CancelRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")


TERMINAL = {"COMPLETED", "FAILED", "CANCELLED"}
POINTS = {"inspect_point_A1": "A", "inspect_point_A2": "A", "inspect_point_B2": "B", "inspect_point_B1": "B"}
# Deliberately exact: do not silently interpret negation, A-only, or mixed goals.
SUPPORTED = {"설비 A와 B를 점검해줘", "A+B 점검", "A와 B를 점검해줘"}


class ApiError(Exception):
    def __init__(self, status, code, message):
        self.status, self.code, self.message = status, code, message
