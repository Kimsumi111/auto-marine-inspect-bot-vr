"""Version 1 shared contracts; no HTTP server or tool execution in this module."""
from enum import StrEnum
from typing import Annotated, Generic, Literal, TypeVar

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, model_validator

Identifier = Annotated[str, Field(strict=True, min_length=1, max_length=80, pattern=r"^[A-Za-z0-9_-]+$")]
Text = Annotated[str, Field(strict=True, min_length=1, max_length=2000)]
EquipmentId = Literal["A", "B"]
PointId = Literal["inspect_point_A1", "inspect_point_A2", "inspect_point_B2", "inspect_point_B1"]


class Contract(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)


class MissionStatus(StrEnum):
    PENDING = "PENDING"
    PLANNING = "PLANNING"
    EXECUTING = "EXECUTING"
    DIAGNOSING = "DIAGNOSING"
    COMPLETED = "COMPLETED"
    FAILED = "FAILED"
    CANCELLING = "CANCELLING"
    CANCELLED = "CANCELLED"


class ErrorCode(StrEnum):
    INVALID_REQUEST = "invalid_request"
    UNKNOWN_EQUIPMENT = "unknown_equipment"
    UNSUPPORTED_TARGETS = "unsupported_targets"
    UNITY_UNAVAILABLE = "unity_unavailable"
    UNITY_NOT_READY = "unity_not_ready"
    SESSION_CHANGED = "session_changed"
    MISSION_BUSY = "mission_busy"
    ID_CONFLICT = "id_conflict"
    NOT_FOUND = "not_found"
    RESULT_NOT_READY = "result_not_ready"
    COMMAND_REJECTED = "command_rejected"
    COMMAND_UNCONFIRMED = "command_unconfirmed"
    TIMEOUT = "timeout"
    DIAGNOSIS_FAILED = "diagnosis_failed"
    STOP_UNCONFIRMED = "stop_unconfirmed"
    INTERNAL_ERROR = "internal_error"


class ErrorDetail(Contract):
    code: ErrorCode
    message: Text
    retryable: bool = Field(strict=True)


class CreateMissionRequest(Contract):
    request_id: Identifier
    command: Annotated[str, Field(strict=True, min_length=1, max_length=1000)]

    @model_validator(mode="after")
    def nonblank_command(self):
        if not self.command.strip():
            raise ValueError("command must not be blank")
        return self


class MissionAccepted(Contract):
    api_version: Literal[1] = 1
    mission_id: Identifier
    status: Literal["PENDING"] = "PENDING"


class InspectionTargets(Contract):
    targets: Annotated[list[EquipmentId], Field(min_length=2, max_length=2)]

    @model_validator(mode="after")
    def supported_combination(self):
        if set(self.targets) != {"A", "B"}:
            raise ValueError("Unity v1 supports A+B only")
        return self


class Goal(InspectionTargets):
    task: Literal["inspection"] = "inspection"


class EquipmentInfo(Contract):
    equipment_id: EquipmentId
    display_name: Text
    equipment_type: Literal["unspecified"] = "unspecified"
    source_equipment_id: Text
    inspection_points: list[PointId]
    data_origin: Literal["replay"] = "replay"


class UnityState(Contract):
    connected: bool = Field(strict=True)
    session_id: Identifier | None = None
    state: str | None = None
    can_start: bool = Field(default=False, strict=True)
    stale: bool = Field(strict=True)
    observed_at: AwareDatetime | None = None


class StartInspectionArgs(InspectionTargets):
    mission_id: Identifier
    expected_session_id: Identifier


class MissionArgs(Contract):
    mission_id: Identifier


class EquipmentArgs(Contract):
    equipment_id: EquipmentId


class EmptyArgs(Contract):
    pass


class DiagnoseInspectionArgs(MissionArgs):
    inspection_id: Identifier


class CommandReceipt(Contract):
    command_id: Identifier
    session_id: Identifier
    acknowledged: bool = Field(strict=True)
    outcome: Literal["applied", "rejected", "unconfirmed"]

    @model_validator(mode="after")
    def acknowledgement_consistency(self):
        if self.acknowledged != (self.outcome != "unconfirmed"):
            raise ValueError("unconfirmed commands cannot have an ACK")
        return self


class InspectionRecord(Contract):
    inspection_id: Identifier
    session_id: Identifier
    equipment_id: EquipmentId
    point: PointId
    completed_at: AwareDatetime
    data_available: bool = Field(strict=True)
    data_origin: Literal["replay"] = "replay"

    @model_validator(mode="after")
    def matching_point(self):
        if not self.point.startswith(f"inspect_point_{self.equipment_id}"):
            raise ValueError("inspection point does not belong to equipment")
        return self


class ClassifierResult(Contract):
    key: Literal["axis", "bearing", "belt", "rotating"]
    abnormal_probability: Annotated[float, Field(strict=True, ge=0, le=1)]
    threshold: Literal[0.5] = 0.5
    abnormal: bool = Field(strict=True)
    model_sha256: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]

    @model_validator(mode="after")
    def matching_threshold(self):
        if self.abnormal != (self.abnormal_probability >= self.threshold):
            raise ValueError("abnormal must agree with threshold")
        return self


class DiagnosisResult(Contract):
    inspection_id: Identifier
    file_name: Text
    sample_count: Annotated[int, Field(strict=True, ge=100)]
    sampling_frequency: Annotated[float, Field(strict=True, gt=0)]
    mode: Literal["offline_csv_replay"] = "offline_csv_replay"
    results: Annotated[list[ClassifierResult], Field(min_length=4, max_length=4)]

    @model_validator(mode="after")
    def four_independent_models(self):
        if {r.key for r in self.results} != {"axis", "bearing", "belt", "rotating"}:
            raise ValueError("all four distinct classifier results are required")
        return self


class Assessment(Contract):
    inspection: InspectionRecord
    status: Literal["pending", "running", "assessed", "unassessed"]
    diagnosis: DiagnosisResult | None = None
    error: ErrorDetail | None = None

    @model_validator(mode="after")
    def result_consistency(self):
        if (self.status == "assessed") != (self.diagnosis is not None):
            raise ValueError("only assessed records have a diagnosis")
        if (self.status == "unassessed") != (self.error is not None):
            raise ValueError("unassessed records require an error")
        if self.diagnosis and self.diagnosis.inspection_id != self.inspection.inspection_id:
            raise ValueError("diagnosis belongs to another inspection")
        return self


class MissionSnapshot(Contract):
    api_version: Literal[1] = 1
    mission_id: Identifier
    status: MissionStatus
    current_step: str | None = None
    goal: Goal | None = None
    plan: list[str] = Field(default_factory=list)
    unity: UnityState
    assessments: list[Assessment] = Field(default_factory=list)
    updated_at: AwareDatetime
    error: ErrorDetail | None = None


class MissionResult(Contract):
    api_version: Literal[1] = 1
    mission_id: Identifier
    status: Literal["COMPLETED", "FAILED", "CANCELLED"]
    summary: Text
    assessments: list[Assessment]
    diagnostic_coverage: Literal["complete", "partial", "none"]
    error: ErrorDetail | None = None


class CancelAccepted(Contract):
    mission_id: Identifier
    status: Literal["CANCELLING", "CANCELLED"]
    stop_confirmed: bool = Field(strict=True)

    @model_validator(mode="after")
    def confirmed_cancel(self):
        if self.stop_confirmed != (self.status == "CANCELLED"):
            raise ValueError("CANCELLED requires confirmed stop")
        return self


T = TypeVar("T")


class ToolResult(Contract, Generic[T]):
    tool_call_id: Identifier
    mission_id: Identifier
    origin: Literal["simulation", "replay", "physical", "backend"]
    ok: bool = Field(strict=True)
    data: T | None = None
    error: ErrorDetail | None = None

    @model_validator(mode="after")
    def success_or_failure(self):
        if self.ok:
            if self.data is None or self.error is not None:
                raise ValueError("success requires data and no error")
        elif self.error is None or self.data is not None:
            raise ValueError("failure requires error and no data")
        return self


TOOL_ARGUMENT_MODELS = {
    "get_equipment_info": EquipmentArgs,
    "get_unity_state": EmptyArgs,
    "start_inspection": StartInspectionArgs,
    "get_inspection_progress": MissionArgs,
    "diagnose_inspection": DiagnoseInspectionArgs,
    "stop_inspection": MissionArgs,
}
