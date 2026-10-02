import asyncio
import json
import time


class UnityLink:
    def __init__(self, host="127.0.0.1", port=8765, timeout=5):
        self.host, self.port, self.timeout = host, port, timeout
        self.writer = None
        self.latest = None
        self.received_at = 0
        self.sequence = -1
        self.pending = {}
        self.task = None
        self.on_frame = None
        self.on_disconnect = None

    @property
    def fresh(self):
        return self.writer is not None and self.latest is not None and time.monotonic() - self.received_at < 3

    async def run(self):
        while True:
            try:
                reader, self.writer = await asyncio.wait_for(asyncio.open_connection(self.host, self.port, limit=1024 * 1024), 3)
                while True:
                    line = await asyncio.wait_for(reader.readline(), 4)
                    if not line or not line.endswith(b"\n"):
                        raise ConnectionError("Unity disconnected")
                    message = json.loads(line)
                    if not isinstance(message, dict):
                        continue
                    if message.get("version") != 1:
                        continue
                    if message.get("type") == "commandResult":
                        future = self.pending.get(message.get("commandId"))
                        if future is not None and not future.done():
                            future.set_result(message)
                    elif message.get("type") == "telemetry":
                        mission, sequence = message.get("mission"), message.get("sequence")
                        if not isinstance(mission, dict) or not isinstance(mission.get("sessionId"), str) or not mission["sessionId"] or type(sequence) is not int:
                            continue
                        if type(mission.get("canStart")) is not bool or not isinstance(mission.get("inspections"), list):
                            continue
                        if self.latest and mission["sessionId"] == self.latest["sessionId"] and sequence <= self.sequence:
                            continue
                        self.latest, self.sequence, self.received_at = mission, sequence, time.monotonic()
                        if self.on_frame:
                            await self.on_frame(mission, sequence)
            except (OSError, asyncio.TimeoutError, ValueError, TypeError):
                pass
            finally:
                writer, self.writer = self.writer, None
                if writer:
                    writer.close()
                    try:
                        await writer.wait_closed()
                    except OSError:
                        pass
                for future in list(self.pending.values()):
                    if not future.done():
                        future.set_exception(ConnectionError("Unity disconnected"))
                if self.on_disconnect:
                    await self.on_disconnect()
            await asyncio.sleep(1)

    async def command(self, action, command_id, session):
        if not self.fresh or self.latest["sessionId"] != session:
            raise ConnectionError("Unity session unavailable")
        future = asyncio.get_running_loop().create_future()
        self.pending[command_id] = future
        frame = dict(version=1, type="command", commandId=command_id, equipmentId="robot", action=action)
        try:
            # No suspension before write: start/stop ordering follows event-loop order.
            self.writer.write((json.dumps(frame, separators=(",", ":")) + "\n").encode())
            await asyncio.wait_for(self.writer.drain(), self.timeout)
            return await asyncio.wait_for(future, self.timeout)
        finally:
            self.pending.pop(command_id, None)
            if not future.done():
                future.cancel()

    async def close(self):
        if self.task:
            self.task.cancel()
            await asyncio.gather(self.task, return_exceptions=True)
