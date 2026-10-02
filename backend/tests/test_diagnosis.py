import asyncio
import copy
import sys
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from backend.vr_diagnosis import DiagnosisError, DiagnosisRunner
from backend.main import create_app as unified_app

def create_app(*args, **kwargs):
    return unified_app(*args, **kwargs, enable_agent=False)

from backend.models import POINTS
from test_backend import FakeLink, body, wait_state


def events(path="fixture.csv"):
    return [dict(id=str(i), point=point, equipmentId=equipment,
                 sourceEquipmentId="fixture", filePath=str(path), error="")
            for i, (point, equipment) in enumerate(POINTS.items())]


class ControlledDiagnosis:
    def __init__(self, fail=False):
        self.calls = []
        self.release = asyncio.Event()
        self.cancelled = 0
        self.fail = fail

    async def run(self, path):
        self.calls.append(path)
        try:
            await self.release.wait()
        except asyncio.CancelledError:
            self.cancelled += 1
            raise
        if self.fail:
            raise DiagnosisError("inference_failed: test")
        return dict(models=[dict(key=k, abnormal_probability=.1, threshold=.5,
                                 abnormal=False, model_sha256="fixture")
                            for k in ("axis", "bearing", "belt", "rotating")])


def test_waits_for_both_unity_and_diagnosis_and_persists(tmp_path):
    link, runner, path = FakeLink(), ControlledDiagnosis(), tmp_path / "state.db"
    with TestClient(create_app(path, link, runner)) as client:
        mid = client.post("/mission", json=body()).json()["mission_id"]
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="FollowingLane", canStart=False, inspections=events()))
        client.portal.call(lambda: link.emit(inspections=events() * 2))
        client.portal.call(runner.release.set)
        for _ in range(100):
            snapshot = client.get(f"/mission/{mid}").json()
            if all(p["status"] == "SUCCEEDED" for p in snapshot["points"]):
                break
            time.sleep(.01)
        assert len(runner.calls) == 4
        assert len(snapshot["points"]) == 4
        assert all(p["status"] == "SUCCEEDED" for p in snapshot["points"])
        assert snapshot["state"] == "EXECUTING"
        client.portal.call(lambda: link.emit(state="Completed"))
        wait_state(client, mid, "COMPLETED")
        report = client.get(f"/mission/{mid}/result").json()
    with TestClient(create_app(path, FakeLink(), ControlledDiagnosis())) as client:
        assert client.get(f"/mission/{mid}/result").json() == report


@pytest.mark.parametrize("fail", [False, True])
def test_completed_waits_and_failed_diagnosis_never_becomes_normal(tmp_path, fail):
    link, runner = FakeLink(), ControlledDiagnosis(fail)
    with TestClient(create_app(tmp_path / "state.db", link, runner)) as client:
        mid = client.post("/mission", json=body()).json()["mission_id"]
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events()))
        wait_state(client, mid, "DIAGNOSING")
        assert client.get(f"/mission/{mid}/result").status_code == 409
        client.portal.call(link.on_disconnect)  # Confirmed route completion allows offline work.
        client.portal.call(runner.release.set)
        wait_state(client, mid, "FAILED" if fail else "COMPLETED")
        report = client.get(f"/mission/{mid}/result").json()
        assert all(p["status"] == ("FAILED" if fail else "SUCCEEDED") for p in report["points"])
        if fail:
            assert all(p["models"] == [] for p in report["points"])


@pytest.mark.parametrize("new_session", [False, True])
def test_cancels_pending_diagnosis_without_late_results(tmp_path, new_session):
    link, runner = FakeLink(), ControlledDiagnosis()
    with TestClient(create_app(tmp_path / "state.db", link, runner)) as client:
        mid = client.post("/mission", json=body()).json()["mission_id"]
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=events()))
        wait_state(client, mid, "DIAGNOSING")
        if new_session:
            client.portal.call(lambda: link.emit(sessionId="new-session", state="Idle", canStart=True, inspections=[]))
        else:
            client.post(f"/mission/{mid}/cancel", json={})
        final = wait_state(client, mid, "FAILED" if new_session else "CANCELLED")
        client.portal.call(runner.release.set)
        assert client.get(f"/mission/{mid}").json() == final
        assert runner.cancelled == 4
        assert all(p["status"] == "NOT_EVALUATED" for p in final["points"])


@pytest.mark.asyncio
async def test_runner_rejects_path_escape(tmp_path):
    (tmp_path / "data/vibration").mkdir(parents=True)
    outside = tmp_path / "outside.csv"
    outside.write_text("invalid")
    runner = DiagnosisRunner(tmp_path, sys.executable)
    for path in ("", "relative.csv", str(outside)):
        with pytest.raises(DiagnosisError, match="invalid_csv"):
            await runner.run(path)


@pytest.mark.asyncio
async def test_runner_timeout_releases_serial_slot(tmp_path):
    folder = tmp_path / "data/vibration"
    folder.mkdir(parents=True)
    csv = folder / "fixture.csv"
    csv.write_text("fixture")
    script = tmp_path / "tools/diagnosis/diagnose.py"
    script.parent.mkdir(parents=True)
    script.write_text("import time\ntime.sleep(30)\n")
    runner = DiagnosisRunner(tmp_path, sys.executable, timeout=.1)
    for _ in range(2):
        with pytest.raises(DiagnosisError, match="diagnosis_timeout"):
            await asyncio.wait_for(runner.run(str(csv)), 3)


def test_real_csv_four_points_via_rest(tmp_path):
    root = Path(__file__).resolve().parents[2]
    runner = DiagnosisRunner(root)
    assert runner.python.is_file(), "Install tools/diagnosis dependencies first"
    a = next(p for p in (root / "data/vibration").rglob("*.csv") if "L-DSF-01" in p.parts and "정상" in p.parts)
    b = next(p for p in (root / "data/vibration").rglob("*.csv") if "L-SF-04" in p.parts and "베어링불량" in p.parts)
    inspections = events()
    for record in inspections:
        record["filePath"] = str(a if record["equipmentId"] == "A" else b)
    link = FakeLink()
    with TestClient(create_app(tmp_path / "state.db", link, runner)) as client:
        mid = client.post("/mission", json=body()).json()["mission_id"]
        wait_state(client, mid, "EXECUTING")
        client.portal.call(lambda: link.emit(state="Completed", canStart=False, inspections=inspections))
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            snapshot = client.get(f"/mission/{mid}").json()
            if snapshot["state"] in {"COMPLETED", "FAILED"}:
                break
            time.sleep(.05)
        assert snapshot["state"] == "COMPLETED", snapshot
        report = client.get(f"/mission/{mid}/result").json()
        assert len(report["points"]) == 4
        for point in report["points"]:
            assert point["status"] == "SUCCEEDED"
            assert len(point["models"]) == 4
            assert point["sample_count"] >= 100
            assert len(point["source_sha256"]) == 64
        assert report["points"][0]["source_sha256"] == report["points"][1]["source_sha256"]
