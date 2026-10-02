"""Loopback Unity JSON-lines client. Commands are never retried automatically."""
import asyncio
from contextlib import suppress
from datetime import datetime, timezone
import json
import time
from uuid import uuid4

from pydantic import BaseModel, ConfigDict, Field
from .contracts import CommandReceipt, Identifier, InspectionRecord, UnityState


class WireMission(BaseModel):
    model_config = ConfigDict(extra="ignore")
    sessionId: Identifier
    state: str
    detail: str
    canStart: bool = Field(strict=True)
    inspections: list[dict]


class UnityClient:
    def __init__(self, port=8765, timeout=5.0, reconnect=2.0):
        self.port, self.timeout, self.reconnect = port, timeout, reconnect
        self.writer = None
        self.mission = None
        self.received = 0.0
        self.observed_at = None
        self.revision = 0
        self.pending = {}
        self.task = None
        self.command_lock = asyncio.Lock()

    async def start(self):
        self.task = asyncio.create_task(self._run())

    async def close(self):
        if self.task:
            self.task.cancel()
            with suppress(asyncio.CancelledError):
                await self.task

    def state(self):
        connected = self.writer is not None
        stale = not connected or time.monotonic() - self.received > self.timeout
        m = self.mission
        return UnityState(connected=connected, stale=stale,
                          session_id=m.sessionId if m else None,
                          state=m.state if m else None,
                          can_start=bool(m and m.canStart and not stale), observed_at=self.observed_at)

    def inspections(self):
        if not self.mission or self.state().stale:
            return []
        rows = []
        for item in self.mission.inspections:
            record = InspectionRecord(inspection_id=item["id"], session_id=self.mission.sessionId,
                equipment_id=item["equipmentId"], point=item["point"],
                completed_at=item["completedAtUtc"],
                data_available=bool(item.get("filePath") and not item.get("error")))
            rows.append((record, item.get("filePath", ""), item.get("error", "")))
        return rows

    async def command(self, action, expected_session_id):
        if action not in {"mission_start", "mission_stop"}:
            raise ValueError("Unsupported Unity action")
        async with self.command_lock:
            state = self.state()
            if state.stale or state.session_id != expected_session_id:
                raise ConnectionError("Unity session is unavailable or changed")
            if action == "mission_start" and not state.can_start:
                raise ValueError("Unity mission cannot start")
            writer = self.writer
            command_id = uuid4().hex
            future = asyncio.get_running_loop().create_future()
            self.pending[command_id] = future
            try:
                payload = dict(version=1, type="command", commandId=command_id,
                               equipmentId="robot", action=action)
                writer.write((json.dumps(payload) + "\n").encode("utf-8"))
                await asyncio.wait_for(writer.drain(), self.timeout)
                response = await asyncio.wait_for(future, self.timeout)
                if self.state().session_id != expected_session_id:
                    raise ConnectionError("Session changed during command")
                return CommandReceipt(command_id=command_id, session_id=expected_session_id,
                    acknowledged=True, outcome="applied" if response["ok"] else "rejected")
            except (TimeoutError, ConnectionError, OSError):
                return CommandReceipt(command_id=command_id, session_id=expected_session_id,
                                      acknowledged=False, outcome="unconfirmed")
            finally:
                self.pending.pop(command_id, None)

    async def _run(self):
        while True:
            writer = None
            try:
                reader, writer = await asyncio.wait_for(
                    asyncio.open_connection("127.0.0.1", self.port, limit=1024*1024), self.timeout)
                self.writer = writer
                self.mission = None
                self.received = 0.0
                while True:
                    line = await asyncio.wait_for(reader.readline(), self.timeout)
                    if not line:
                        raise ConnectionError("Unity disconnected")
                    data = json.loads(line.decode("utf-8"))
                    if data.get("version") != 1:
                        raise ValueError("Unsupported protocol")
                    if data.get("type") == "telemetry":
                        mission = WireMission.model_validate(data["mission"]) if data.get("mission") else None
                        # Validate all inspection identities before publishing the frame.
                        previous = self.mission
                        self.mission = mission
                        try:
                            self.received = time.monotonic()
                            self.inspections()
                        except Exception:
                            self.mission = previous
                            raise
                        self.observed_at = datetime.now(timezone.utc)
                        self.revision += 1
                    elif data.get("type") == "commandResult":
                        if not isinstance(data.get("ok"), bool):
                            raise ValueError("Invalid ACK")
                        f = self.pending.get(data.get("commandId"))
                        if f and not f.done():
                            f.set_result(data)
                    else:
                        raise ValueError("Unsupported message")
            except (OSError, ConnectionError, TimeoutError, ValueError, KeyError, TypeError):
                pass
            finally:
                self.writer = None
                for f in self.pending.values():
                    if not f.done():
                        f.set_exception(ConnectionError("Unity disconnected"))
                if writer:
                    writer.close()
                    with suppress(OSError):
                        await writer.wait_closed()
            await asyncio.sleep(self.reconnect)
