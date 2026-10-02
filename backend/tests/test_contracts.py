"""Offline contract validation. No Unity connection or OpenAI API call."""
import unittest
from pydantic import ValidationError
from backend.contracts import (CreateMissionRequest, StartInspectionArgs, CommandReceipt,
    ClassifierResult, DiagnosisResult, CancelAccepted, ToolResult, MissionSnapshot)


class ContractTests(unittest.TestCase):
    def test_command_rejects_blank_unknown_fields_and_nonstring(self):
        for command in ["  ", 7]:
            with self.assertRaises(ValidationError):
                CreateMissionRequest(request_id="r1", command=command)
        with self.assertRaises(ValidationError):
            CreateMissionRequest(request_id="r1", command="점검", targets=["A"])

    def test_start_only_accepts_ab(self):
        for targets in [["A"], ["B"], ["A", "A"], ["A", "C"]]:
            with self.assertRaises(ValidationError):
                StartInspectionArgs(mission_id="m1", expected_session_id="s1", targets=targets)
        self.assertEqual(StartInspectionArgs(mission_id="m1", expected_session_id="s1",
                         targets=["B", "A"]).targets, ["B", "A"])

    def test_ack_is_not_completion(self):
        receipt = CommandReceipt(command_id="c1", session_id="s1", acknowledged=True, outcome="applied")
        self.assertNotIn("completed", receipt.model_dump())
        with self.assertRaises(ValidationError):
            CommandReceipt(command_id="c1", session_id="s1", acknowledged=True, outcome="unconfirmed")

    def test_probability_and_threshold_are_validated(self):
        for p, abnormal in [(float("nan"), False), (1.1, True), (0.6, False)]:
            with self.assertRaises(ValidationError):
                ClassifierResult(key="axis", abnormal_probability=p, abnormal=abnormal, model_sha256="a"*64)

    def test_four_distinct_models_required(self):
        row = dict(key="axis", abnormal_probability=0.2, abnormal=False, model_sha256="a"*64)
        with self.assertRaises(ValidationError):
            DiagnosisResult(inspection_id="i1", file_name="x.csv", sample_count=100,
                            sampling_frequency=1000.0, results=[row]*4)

    def test_cancel_requires_stop_confirmation(self):
        with self.assertRaises(ValidationError):
            CancelAccepted(mission_id="m1", status="CANCELLED", stop_confirmed=False)

    def test_tool_failure_cannot_be_success(self):
        with self.assertRaises(ValidationError):
            ToolResult[dict](tool_call_id="t1", mission_id="m1", origin="replay", ok=True,
                             error=dict(code="diagnosis_failed", message="failed", retryable=False))

    def test_snapshot_json_roundtrip_and_timezone(self):
        data = dict(mission_id="m1", status="PENDING", unity=dict(connected=False, stale=True),
                    updated_at="2026-10-02T00:00:00Z")
        model = MissionSnapshot.model_validate(data)
        self.assertEqual(MissionSnapshot.model_validate_json(model.model_dump_json()), model)
        data["updated_at"] = "2026-10-02T00:00:00"
        with self.assertRaises(ValidationError):
            MissionSnapshot.model_validate(data)


if __name__ == "__main__":
    unittest.main()
