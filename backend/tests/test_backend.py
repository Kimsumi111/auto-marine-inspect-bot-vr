import asyncio
import copy
import json
import time
import uuid
import pytest
from fastapi.testclient import TestClient
from backend.main import create_app as unified_app

def create_app(*args, **kwargs):
    return unified_app(*args, **kwargs, enable_agent=False)

from backend.storage import Storage
from backend.unity import UnityLink


class FakeLink:
    def __init__(self):
        self.fresh = True
        self.latest = dict(sessionId="session-1", state="Idle", canStart=True, inspections=[])
        self.sequence = 1
        self.sent = []
        self.task = None
        self.lose_ack = False

    async def run(self):
        await asyncio.Event().wait()

    async def command(self, action, cid, session):
        self.sent.append((action, cid, session))
        if not self.fresh or self.lose_ack:
            raise ConnectionError("test disconnect")
        return dict(ok=True)

    async def emit(self, **changes):
        self.latest.update(changes)
        self.sequence += 1
        await self.on_frame(self.latest, self.sequence)

    async def close(self):
        self.task.cancel()
        await asyncio.gather(self.task, return_exceptions=True)


def body(text="설비 A와 B를 점검해줘"):
    return dict(api_version=1, request_id=str(uuid.uuid4()), text=text, execution_mode="simulation")


def wait_state(client, mid, state):
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        value = client.get("/mission/" + mid).json()
        if value["state"] == state:
            return value
        time.sleep(.01)
    raise AssertionError(value)


@pytest.fixture
def setup(tmp_path):
    link = FakeLink()
    app = create_app(tmp_path / "state.db", link)
    with TestClient(app) as client:
        yield client, link


def test_start_ack_not_completion_and_duplicate(setup):
    client, link = setup
    req = body()
    first = client.post("/mission", json=req)
    assert first.status_code == 202
    mid = first.json()["mission_id"]
    wait_state(client, mid, "EXECUTING")
    again = client.post("/mission", json=req)
    assert again.status_code == 200
    assert again.json()["mission_id"] == mid
    assert len(link.sent) == 1
    assert client.get(f"/mission/{mid}/result").status_code == 409
    assert client.get("/mission/by-request/" + req["request_id"]).json()["mission_id"] == mid


def test_conflict_and_busy(setup):
    client, _ = setup
    req = body()
    client.post("/mission", json=req)
    different = {**req, "text": "다른 목표"}
    assert client.post("/mission", json=different).json()["error"]["code"] == "id_conflict"
    assert client.post("/mission", json=body()).json()["error"]["code"] == "mission_busy"


def test_strict_validation_and_size(setup):
    client, link = setup
    for change in ({"api_version": True}, {"api_version": "1"}, {"extra": 1}, {"request_id": "bad"}, {"text": " "}, {"execution_mode": "physical"}):
        assert client.post("/mission", json={**body(), **change}).status_code == 422
    assert client.post("/mission", content=b"x" * 8193).status_code == 400
    assert not link.sent


def test_unsupported_goal_never_moves(setup):
    client, link = setup
    mid = client.post("/mission", json=body("A만 점검해줘")).json()["mission_id"]
    assert "unsupported_goal" in wait_state(client, mid, "FAILED")["message"]
    assert not link.sent


def test_unavailable_and_used_session(setup):
    client, link = setup
    link.fresh = False
    assert client.post("/mission", json=body()).status_code == 503
    link.fresh = True
    link.latest["canStart"] = False
    assert client.post("/mission", json=body()).json()["error"]["code"] == "unity_restart_required"
    assert not link.sent


def test_points_deduplicated_and_no_fake_diagnosis(setup):
    client, link = setup
    mid = client.post("/mission", json=body()).json()["mission_id"]
    wait_state(client, mid, "EXECUTING")
    points = [dict(id=str(i), point=p, equipmentId="A" if "_A" in p else "B", sourceEquipmentId="source")
              for i, p in enumerate(["inspect_point_A1", "inspect_point_A2", "inspect_point_B2", "inspect_point_B1"])]
    client.portal.call(lambda: link.emit(state="InspectingEquipment", canStart=False, inspections=points[:1] * 2))
    assert client.get(f"/mission/{mid}").json()["completed_points"] == 1
    client.portal.call(lambda: link.emit(state="Completed", inspections=points))
    snapshot = wait_state(client, mid, "FAILED")
    assert snapshot["completed_points"] == 4
    report = client.get(f"/mission/{mid}/result").json()
    assert len(report["points"]) == 4
    assert all(p["status"] == "FAILED" and p["models"] == [] for p in report["points"])


def test_cancel_requires_ack_and_new_idle_frame(setup):
    client, link = setup
    mid = client.post("/mission", json=body()).json()["mission_id"]
    wait_state(client, mid, "EXECUTING")
    client.portal.call(lambda: link.emit(state="FollowingLane", canStart=False))
    cancel = client.post(f"/mission/{mid}/cancel", json={})
    assert cancel.json()["state"] == "CANCELLING"
    assert not cancel.json()["stop_confirmed"]
    # Duplicate cancel must not issue another command while waiting.
    client.post(f"/mission/{mid}/cancel", json={})
    time.sleep(.02)
    client.portal.call(lambda: link.emit(state="Idle", canStart=False))
    assert wait_state(client, mid, "CANCELLED")["stop_confirmed"]
    assert [a for a, _, _ in link.sent] == ["mission_start", "mission_stop"]


def test_lost_ack_no_auto_retry(setup):
    client, link = setup
    link.lose_ack = True
    req = body()
    mid = client.post("/mission", json=req).json()["mission_id"]
    assert wait_state(client, mid, "FAILED")["requires_attention"]
    client.post("/mission", json=req)
    assert len(link.sent) == 1
    assert client.post("/mission", json=body()).status_code == 409


def test_disconnect_then_new_session_releases_lock(setup):
    client, link = setup
    mid = client.post("/mission", json=body()).json()["mission_id"]
    wait_state(client, mid, "EXECUTING")
    client.portal.call(link.on_disconnect)
    assert wait_state(client, mid, "FAILED")["requires_attention"]
    client.portal.call(lambda: link.emit(sessionId="session-2", state="Idle", canStart=True))
    assert not client.get(f"/mission/{mid}").json()["requires_attention"]
    assert len(link.sent) == 1


def test_restart_retains_id_and_does_not_restart_robot(tmp_path):
    path, req = tmp_path / "persistent.db", body()
    first = FakeLink()
    with TestClient(create_app(path, first)) as client:
        mid = client.post("/mission", json=req).json()["mission_id"]
        wait_state(client, mid, "EXECUTING")
    second = FakeLink()
    with TestClient(create_app(path, second)) as client:
        value = client.post("/mission", json=req).json()
        assert value["mission_id"] == mid and value["state"] == "FAILED"
        assert value["requires_attention"]
        assert not second.sent


def test_single_owner(tmp_path):
    store = Storage(tmp_path / "state.db")
    try:
        with pytest.raises(Exception, match="locked"):
            Storage(tmp_path / "state.db")
    finally:
        store.close()


@pytest.mark.asyncio
async def test_tcp_fragmented_frames_and_ack():
    commands = []
    async def serve(reader, writer):
        try:
            frame = dict(version=1, type="telemetry", sequence=1, mission=dict(sessionId="s", state="Idle", canStart=True, inspections=[]))
            data = (json.dumps(frame) + "\n").encode()
            writer.write(data[:30]); await writer.drain()
            await asyncio.sleep(.02)
            writer.write(data[30:]); await writer.drain()
            command = json.loads(await reader.readline())
            commands.append(command)
            writer.write((json.dumps(dict(version=1, type="commandResult", commandId=command["commandId"], ok=True)) + "\n").encode())
            await writer.drain()
            await reader.read()
        finally:
            writer.close()
            await writer.wait_closed()
    server = await asyncio.start_server(serve, "127.0.0.1", 0)
    link = UnityLink(port=server.sockets[0].getsockname()[1])
    link.task = asyncio.create_task(link.run())
    try:
        for _ in range(100):
            if link.fresh:
                break
            await asyncio.sleep(.01)
        assert link.fresh
        result = await link.command("mission_start", "id-1", "s")
        assert result["ok"] and len(commands) == 1
        assert commands[0]["equipmentId"] == "robot"
    finally:
        await link.close()
        server.close()
        await server.wait_closed()
