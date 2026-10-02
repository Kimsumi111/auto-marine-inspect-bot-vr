import asyncio
import json
from pathlib import Path
import tempfile
import unittest
from fastapi.testclient import TestClient
from backend.main import create_app
from backend.unity_client import UnityClient


class ApiTests(unittest.TestCase):
    def test_create_deduplicate_conflict_cancel_result(self):
        with TestClient(create_app(":memory:", UnityClient(port=1, timeout=0.1), enable_agent=False)) as c:
            self.assertFalse(c.get("/health").json()["agent_ready"])
            body = dict(request_id="r1", command="설비 A와 B 점검")
            response = c.post("/mission", json=body)
            self.assertEqual(response.status_code, 202)
            mid = response.json()["mission_id"]
            self.assertEqual(c.post("/mission", json=body).json()["mission_id"], mid)
            self.assertEqual(c.post("/mission", json={**body, "command":"다른 명령"}).status_code, 409)
            self.assertEqual(c.post("/mission", json={**body, "request_id":"r2"}).status_code, 409)
            self.assertEqual(c.get(f"/mission/{mid}").json()["current_step"], "awaiting_agent")
            self.assertEqual(c.get(f"/mission/{mid}/result").status_code, 409)
            self.assertTrue(c.post(f"/mission/{mid}/cancel").json()["stop_confirmed"])
            self.assertEqual(c.get(f"/mission/{mid}/result").json()["status"], "CANCELLED")
            self.assertEqual(c.get("/mission/missing").status_code, 404)
            self.assertEqual(c.post("/mission", json={**body,"extra":1}).status_code, 422)

    def test_restart_retains_id_without_replaying(self):
        with tempfile.TemporaryDirectory() as directory:
            db = str(Path(directory)/"missions.db")
            body = dict(request_id="r1", command="점검")
            with TestClient(create_app(db, UnityClient(port=1, timeout=0.1), enable_agent=False)) as c:
                mid = c.post("/mission", json=body).json()["mission_id"]
            with TestClient(create_app(db, UnityClient(port=1, timeout=0.1), enable_agent=False)) as c:
                self.assertEqual(c.get(f"/mission/{mid}").json()["status"], "FAILED")
                self.assertEqual(c.post("/mission", json=body).json()["mission_id"], mid)


class TcpTests(unittest.IsolatedAsyncioTestCase):
    async def test_fragmented_telemetry_ack_and_no_ack_timeout(self):
        requests = []
        async def server(reader, writer):
            frame = json.dumps(dict(version=1,type="telemetry",mission=dict(
                sessionId="s1", state="Idle", detail="ready", canStart=True, inspections=[]))).encode()+b"\n"
            writer.write(frame[:25]); await writer.drain()
            await asyncio.sleep(0.01)
            writer.write(frame[25:]); await writer.drain()
            try:
                while line := await reader.readline():
                    request = json.loads(line); requests.append(request)
                    if request["action"] == "mission_start":
                        writer.write((json.dumps(dict(version=1,type="commandResult",
                            commandId=request["commandId"],ok=True,code="ok",message="applied"))+"\n").encode())
                        await writer.drain()
            finally:
                writer.close()
        listener = await asyncio.start_server(server,"127.0.0.1",0)
        client = UnityClient(port=listener.sockets[0].getsockname()[1],timeout=0.3,reconnect=1)
        await client.start()
        try:
            async with asyncio.timeout(2):
                while not client.state().can_start:
                    await asyncio.sleep(0.01)
            receipt = await client.command("mission_start","s1")
            self.assertEqual(receipt.outcome,"applied")
            self.assertEqual(client.state().state,"Idle")  # ACK did not invent completion.
            receipt = await client.command("mission_stop","s1")
            self.assertEqual(receipt.outcome,"unconfirmed")
            self.assertEqual(len(requests),2)
        finally:
            await client.close()
            listener.close(); await listener.wait_closed()
