import asyncio
import json
import time
from pathlib import Path
import pytest
from fastapi.testclient import TestClient
from backend.main import create_app
from backend.llm import ParsedPlan
from backend.vr_diagnosis import DiagnosisError, DiagnosisRunner
from test_backend import FakeLink, body, wait_state
from test_diagnosis import events, ControlledDiagnosis


class Model:
    def __init__(self, targets=None, block=None, retry=True):
        self.targets = ["A", "B"] if targets is None else targets
        self.block, self.retry = block, retry
        self.entered = asyncio.Event()
        self.release = asyncio.Event()
        self.observations = []

    async def plan(self, text):
        if self.block == "plan":
            self.entered.set()
            await self.release.wait()
        return ParsedPlan(task="inspection", targets=self.targets, steps=["상태 확인", "점검", "진단", "보고"])

    async def choose(self, observation, allowed):
        self.observations.append(observation)
        if self.block in allowed:
            self.entered.set()
            await self.release.wait()
        if "retry_diagnosis" in allowed and self.retry:
            return "retry_diagnosis", "같은 파일 분석 재시도"
        return next(iter(allowed)), "실제 결과 기반 선택"


class Diagnosis(ControlledDiagnosis):
    def __init__(self, failures=0):
        super().__init__()
        self.release.set()
        self.failures = failures
        self.attempts = 0

    async def run(self, path):
        self.attempts += 1
        if self.attempts <= self.failures:
            raise DiagnosisError("inference_failed")
        return await super().run(path)


def submit(client):
    request = body("A와 B 상태를 확인하고 결과를 알려줘")
    response = client.post("/mission", json=request)
    assert response.status_code == 202, response.text
    return response.json()["mission_id"], request


def entered(client, model):
    client.portal.call(lambda: asyncio.wait_for(model.entered.wait(), 3))


def test_graph_retry_report_and_recovery(tmp_path):
    link, model, diagnosis = FakeLink(), Model(), Diagnosis(failures=1)
    path = tmp_path / "mission.db"
    with TestClient(create_app(path, link, diagnosis, model=model, enable_agent=True)) as client:
        mid, req = submit(client)
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events()))
        completed = wait_state(client, mid, "COMPLETED")
        assert completed["agent_mode"] == "openai" and completed["plan"]
        assert diagnosis.attempts == 5
        assert len(link.sent) == 1
        assert client.post("/mission", json=req).status_code == 200
        report = client.get(f"/mission/{mid}/result").json()
        assert all(p["status"] == "SUCCEEDED" for p in report["points"])
        log = client.get(f"/mission/{mid}/events").json()["events"]
        assert {"plan", "decision", "tool_result", "diagnosis", "report"} <= {e["kind"] for e in log}
        assert {"llm_started", "llm_succeeded", "tool_started", "tool_succeeded", "tool_failed"} <= {e["kind"] for e in log}
        audit = [json.loads(line) for line in (tmp_path / "agent-events.jsonl").read_text(encoding="utf-8").splitlines()]
        assert all(row["mission_id"] == mid for row in audit)
        assert "fixture.csv" not in json.dumps(audit)
        assert all(e["data"]["duration_ms"] >= 0 for e in log if e["kind"] == "llm_succeeded")
        assert "fixture.csv" not in json.dumps(model.observations)
    with TestClient(create_app(path, FakeLink(), enable_agent=False)) as client:
        assert client.get("/mission/by-request/" + req["request_id"]).json()["state"] == "COMPLETED"
        assert client.get(f"/mission/{mid}/result").json() == report


@pytest.mark.parametrize("targets", [["A", "C"], ["C"], ["A", "A"], []])
def test_unsupported_targets_do_not_start(tmp_path, targets):
    link = FakeLink()
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=Model(targets), enable_agent=True)) as client:
        mid, _ = submit(client)
        assert "unsupported_goal" in wait_state(client, mid, "FAILED")["message"]
        assert not link.sent


def test_cancel_planning_never_starts_later(tmp_path):
    link, model = FakeLink(), Model(block="plan")
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=model, enable_agent=True)) as client:
        mid, _ = submit(client)
        entered(client, model)
        assert client.post(f"/mission/{mid}/cancel", json={}).json()["state"] == "CANCELLED"
        client.portal.call(model.release.set)
        assert not link.sent


def test_missing_key_rejects_without_pending_record(tmp_path, monkeypatch):
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    link = FakeLink()
    with TestClient(create_app(tmp_path / "db", link, enable_agent=True)) as client:
        req = body()
        response = client.post("/mission", json=req)
        assert response.status_code == 503
        assert response.json()["error"]["code"] == "agent_unavailable"
        assert client.get("/mission/by-request/" + req["request_id"]).status_code == 404
        assert not link.sent


def test_lost_start_ack_locks_and_allows_stop_retry(tmp_path):
    link = FakeLink()
    link.lose_ack = True
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=Model(), enable_agent=True)) as client:
        mid, req = submit(client)
        assert wait_state(client, mid, "FAILED")["requires_attention"]
        assert client.post("/mission", json=body()).status_code == 409
        assert client.post("/mission", json=req).status_code == 200
        link.lose_ack = False
        client.portal.call(lambda: link.emit(state="FollowingLane", canStart=False))
        assert client.post(f"/mission/{mid}/cancel", json={}).status_code == 202
        client.portal.call(lambda: link.emit(state="Idle", canStart=False))
        assert wait_state(client, mid, "CANCELLED")["stop_confirmed"]
        assert [a for a, _, _ in link.sent] == ["mission_start", "mission_stop"]


def test_fault_during_llm_diagnosis_stops_without_waiting(tmp_path):
    link, model = FakeLink(), Model(block="diagnose_inspection")
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=model, enable_agent=True)) as client:
        mid, _ = submit(client)
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="FollowingLane", canStart=False, inspections=events()[:1]))
        entered(client, model)
        client.portal.call(lambda: link.emit(state="Fault", detail="Marker fallback limit reached"))
        wait_state(client, mid, "CANCELLING")
        client.portal.call(lambda: link.emit(state="Idle", canStart=False))
        assert wait_state(client, mid, "FAILED")["stop_confirmed"]
        assert any(e["kind"] == "unity_fault" for e in client.get(f"/mission/{mid}/events").json()["events"])
        client.portal.call(model.release.set)
        assert client.get(f"/mission/{mid}/result").json()["points"][0]["status"] != "SUCCEEDED"


def test_failed_diagnosis_never_completes(tmp_path):
    with TestClient(create_app(tmp_path / "db", FakeLink(), Diagnosis(failures=20), model=Model(), enable_agent=True)) as client:
        mid, _ = submit(client)
        wait_state(client, mid, "EXECUTING")
        link = client.app.state.unity
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events()))
        final = wait_state(client, mid, "FAILED")
        assert all(p["status"] == "FAILED" for p in final["points"])


def test_cancel_during_report_has_no_late_completion(tmp_path):
    model, link = Model(block="report_results"), FakeLink()
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=model, enable_agent=True)) as client:
        mid, _ = submit(client)
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events()))
        entered(client, model)
        final = client.post(f"/mission/{mid}/cancel", json={}).json()
        assert final["state"] == "CANCELLED" and final["stop_confirmed"]
        client.portal.call(model.release.set)
        assert client.get(f"/mission/{mid}").json() == final


def test_agent_with_real_csv_and_models(tmp_path):
    runner = DiagnosisRunner()
    root = Path(__file__).resolve().parents[2]
    csv = next((root / "data/vibration/2.2kW/L-DSF-01").rglob("*.csv"))
    link = FakeLink()
    with TestClient(create_app(tmp_path / "db", link, runner, model=Model(), enable_agent=True)) as client:
        mid, _ = submit(client)
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events(csv)))
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            snapshot = client.get(f"/mission/{mid}").json()
            if snapshot["state"] in {"FAILED", "COMPLETED"}:
                break
            time.sleep(.05)
        assert snapshot["state"] == "COMPLETED", snapshot
        assert all(len(p["models"]) == 4 and p["sample_count"] >= 100 for p in snapshot["points"])


def test_cancel_during_start_ack_does_not_wait_for_ack(tmp_path):
    class SlowLink(FakeLink):
        def __init__(self):
            super().__init__()
            self.entered = asyncio.Event()
            self.release = asyncio.Event()
        async def command(self, action, cid, session):
            self.sent.append((action, cid, session))
            if action == "mission_start":
                self.entered.set()
                await self.release.wait()
            return dict(ok=True)
    link = SlowLink()
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=Model(), enable_agent=True)) as client:
        mid, _ = submit(client)
        client.portal.call(lambda: asyncio.wait_for(link.entered.wait(), 3))
        response = client.post(f"/mission/{mid}/cancel", json={})
        assert response.json()["state"] == "CANCELLING"
        client.portal.call(lambda: link.emit(state="Idle", canStart=False))
        wait_state(client, mid, "CANCELLED")
        client.portal.call(link.release.set)
        assert [a for a, _, _ in link.sent] == ["mission_start", "mission_stop"]


def test_unity_session_change_during_plan_never_starts(tmp_path):
    model, link = Model(block="plan"), FakeLink()
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=model, enable_agent=True)) as client:
        mid, _ = submit(client)
        entered(client, model)
        client.portal.call(lambda: link.emit(sessionId="new-play"))
        wait_state(client, mid, "FAILED")
        client.portal.call(model.release.set)
        assert not link.sent


def test_model_failure_before_start_is_recorded(tmp_path):
    class BrokenModel(Model):
        async def plan(self, text):
            raise RuntimeError("simulated API failure")
    link = FakeLink()
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=BrokenModel(), enable_agent=True)) as client:
        mid, _ = submit(client)
        assert not wait_state(client, mid, "FAILED")["requires_attention"]
        assert not link.sent
        assert client.get(f"/mission/{mid}/events").json()["events"][-1]["kind"] == "agent_error"


def test_one_app_alias_and_legacy_request_rejected(tmp_path):
    from backend.main import app
    from backend.vr_main import app as alias
    assert app is alias
    with TestClient(create_app(tmp_path / "db", FakeLink(), enable_agent=False)) as client:
        assert client.post("/mission", json={"request_id": "old", "command": "A+B"}).status_code == 422


def test_restart_does_not_replay_inflight_agent(tmp_path):
    model, link, path = Model(block="diagnose_inspection"), FakeLink(), tmp_path / "db"
    with TestClient(create_app(path, link, Diagnosis(), model=model, enable_agent=True)) as client:
        mid, req = submit(client)
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="FollowingLane", canStart=False, inspections=events()[:1]))
        entered(client, model)
    second = FakeLink()
    with TestClient(create_app(path, second, Diagnosis(), model=Model(), enable_agent=True)) as client:
        snapshot = client.post("/mission", json=req).json()
        assert snapshot["state"] == "FAILED" and snapshot["requires_attention"]
        assert not second.sent


@pytest.mark.parametrize("targets,action", [(["A"], "mission_start_a"), (["B"], "mission_start_b"), (["B", "A"], "mission_start")])
def test_selected_equipment_end_to_end(tmp_path, targets, action):
    link, diagnosis = FakeLink(), Diagnosis()
    path = tmp_path / "db"
    with TestClient(create_app(path, link, diagnosis, model=Model(targets), enable_agent=True)) as client:
        mid, req = submit(client)
        snapshot = wait_state(client, mid, "EXECUTING")
        assert snapshot["targets"] == sorted(targets)
        assert snapshot["total_points"] == 2 * len(targets)
        assert link.sent[0][0] == action
        # Even if unrelated equipment events arrive, they must not enter diagnosis or report.
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events()))
        final = wait_state(client, mid, "COMPLETED")
        assert final["completed_points"] == final["total_points"]
        report = client.get(f"/mission/{mid}/result").json()
        assert {p["equipment_id"] for p in report["points"]} == set(targets)
        assert len(report["points"]) == diagnosis.attempts == 2 * len(targets)
        assert client.post("/mission", json=req).status_code == 200
        assert len(link.sent) == 1
    second = FakeLink()
    with TestClient(create_app(path, second, Diagnosis(), model=Model(), enable_agent=True)) as client:
        assert client.get(f"/mission/{mid}/result").json() == report
        assert not second.sent


@pytest.mark.parametrize("targets", [["A"], ["B"]])
def test_selected_equipment_missing_point_cannot_complete(tmp_path, targets):
    link = FakeLink()
    with TestClient(create_app(tmp_path / "db", link, Diagnosis(), model=Model(targets), enable_agent=True)) as client:
        mid, _ = submit(client)
        wait_state(client, mid, "EXECUTING")
        selected = [e for e in events() if e["equipmentId"] in targets]
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=selected[:1]))
        wait_state(client, mid, "FAILED")
