"""Local API scaffold. Agent planning and diagnosis execution follow separately."""
import asyncio
from contextlib import asynccontextmanager, suppress
from datetime import datetime, timezone
import os
from pathlib import Path
from uuid import uuid4

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from .contracts import (CancelAccepted, CreateMissionRequest, ErrorDetail, MissionAccepted,
    MissionResult, MissionSnapshot, MissionStatus, StartInspectionArgs)
from .store import Store
from .unity_client import UnityClient

TERMINAL = {MissionStatus.COMPLETED, MissionStatus.FAILED, MissionStatus.CANCELLED}


def now():
    return datetime.now(timezone.utc)


class APIError(Exception):
    def __init__(self, status, code, message):
        self.status = status
        self.detail = ErrorDetail(code=code, message=message, retryable=False)


class MissionService:
    def __init__(self, store, unity):
        self.store, self.unity = store, unity
        self.lock = asyncio.Lock()
        self.tasks = set()
        self.agent = None

    def get(self, mid):
        snapshot = self.store.get(mid)
        if not snapshot:
            raise APIError(404, "not_found", "임무가 없습니다.")
        return snapshot

    def update(self, snapshot, **fields):
        data = snapshot.model_dump()
        data.update(fields, updated_at=now())
        result = MissionSnapshot.model_validate(data)
        self.store.save(result)
        return result

    async def create(self, request):
        async with self.lock:
            existing = self.store.request(request.request_id)
            if existing:
                if existing[1] != request.command:
                    raise APIError(409, "id_conflict", "request_id가 다른 명령에 사용되었습니다.")
                return MissionAccepted(mission_id=existing[0])
            if any(s.status not in TERMINAL for s in self.store.all()):
                raise APIError(409, "mission_busy", "다른 임무가 활성 상태입니다.")
            snapshot = MissionSnapshot(mission_id=uuid4().hex, status="PENDING",
                current_step="awaiting_agent", unity=self.unity.state(), updated_at=now())
            self.store.save(snapshot, request.request_id, request.command)
            if self.agent:
                task = asyncio.create_task(self.agent.run(snapshot.mission_id,request.command))
                self.tasks.add(task)
                task.add_done_callback(self.tasks.discard)
            return MissionAccepted(mission_id=snapshot.mission_id)

    async def start_inspection(self, args: StartInspectionArgs):
        # Internal integration seam for the future validated Agent; no raw start HTTP endpoint.
        async with self.lock:
            snapshot = self.get(args.mission_id)
            if snapshot.status not in {MissionStatus.PENDING, MissionStatus.PLANNING}:
                raise APIError(409, "unity_not_ready", "임무 시작 상태가 아닙니다.")
            snapshot = self.update(snapshot, status="EXECUTING", current_step="starting_unity",
                goal=dict(task="inspection", targets=args.targets), unity=self.unity.state())
            try:
                receipt = await self.unity.command("mission_start", args.expected_session_id)
            except (ConnectionError, ValueError):
                self.update(snapshot, status="FAILED", error=dict(code="unity_not_ready",
                    message="Unity 상태 또는 세션을 확인할 수 없습니다.", retryable=False))
                raise APIError(409, "unity_not_ready", "Unity 상태 또는 세션을 확인할 수 없습니다.")
            if receipt.outcome != "applied":
                self.update(snapshot, status="FAILED", error=dict(
                    code="command_rejected" if receipt.outcome == "rejected" else "command_unconfirmed",
                    message="Unity 시작 명령 완료 여부를 확인하세요.", retryable=False))
            else:
                self.update(snapshot, current_step="following_unity", unity=self.unity.state())
            return receipt

    async def cancel(self, mid):
        async with self.lock:
            snapshot = self.get(mid)
            if snapshot.status == MissionStatus.CANCELLED:
                return CancelAccepted(mission_id=mid, status="CANCELLED", stop_confirmed=True)
            if snapshot.status in TERMINAL:
                raise APIError(409, "command_rejected", "종료된 임무는 취소할 수 없습니다.")
            if snapshot.status == MissionStatus.CANCELLING:
                return CancelAccepted(mission_id=mid, status="CANCELLING", stop_confirmed=False)
            if snapshot.status in {MissionStatus.PENDING, MissionStatus.PLANNING}:
                self.update(snapshot, status="CANCELLED", current_step="cancelled_before_start")
                return CancelAccepted(mission_id=mid, status="CANCELLED", stop_confirmed=True)
            session = snapshot.unity.session_id
            self.update(snapshot, status="CANCELLING", current_step="stopping_unity")
            task = asyncio.create_task(self._stop(mid, session))
            self.tasks.add(task)
            task.add_done_callback(self.tasks.discard)
            return CancelAccepted(mission_id=mid, status="CANCELLING", stop_confirmed=False)

    async def _stop(self, mid, session):
        try:
            receipt = await self.unity.command("mission_stop", session)
            revision = self.unity.revision  # Require telemetry AFTER ACK.
            if receipt.outcome != "applied":
                raise ConnectionError("Stop not acknowledged")
            async with asyncio.timeout(self.unity.timeout):
                while True:
                    state = self.unity.state()
                    if state.session_id != session:
                        raise ConnectionError("Session changed")
                    if self.unity.revision > revision and not state.stale and state.state == "Idle":
                        self.update(self.get(mid), status="CANCELLED", current_step="stopped", unity=state)
                        return
                    await asyncio.sleep(0.05)
        except (ConnectionError, ValueError, TimeoutError):
            self.update(self.get(mid), status="FAILED", error=dict(code="stop_unconfirmed",
                message="Unity 정지를 확인하지 못했습니다. 직접 상태를 확인하세요.", retryable=False))


def create_app(db_path=None, unity=None, enable_agent=None, model=None, diagnosis=None):
    client = unity or UnityClient(port=int(os.getenv("SHIP_UNITY_PORT", "8765")))
    path = db_path or os.getenv("SHIP_AGENT_DB", str(Path(__file__).parent / "data/missions.db"))

    @asynccontextmanager
    async def lifespan(app):
        store = Store(path)
        service = MissionService(store, client)
        llm = None
        if enable_agent is True or (enable_agent is None and os.getenv("OPENAI_API_KEY")):
            from .agent import AgentRunner
            from .llm import OpenAIModel
            from .diagnosis import DiagnosisRunner
            llm = model or OpenAIModel()
            service.agent = AgentRunner(service,llm,diagnosis or DiagnosisRunner())
        app.state.service = service
        # Never resume or replay physical commands after a backend restart.
        for snapshot in store.all():
            if snapshot.status not in TERMINAL:
                service.update(snapshot, status="FAILED", error=dict(code="command_unconfirmed",
                    message="Backend 재시작으로 임무를 복구하지 않았습니다. Unity 상태를 확인하세요.", retryable=False))
        await client.start()
        try:
            yield
        finally:
            for task in list(service.tasks):
                task.cancel()
            for task in list(service.tasks):
                with suppress(asyncio.CancelledError):
                    await task
            await client.close()
            if llm and hasattr(llm,"close"):
                await llm.close()
            store.close()

    app = FastAPI(title="Ship Inspection Agent API", version="1.0.0", lifespan=lifespan)

    @app.exception_handler(APIError)
    async def api_error(request, error):
        return JSONResponse(status_code=error.status, content={"error": error.detail.model_dump(mode="json")})

    @app.exception_handler(RequestValidationError)
    async def validation_error(request, error):
        return JSONResponse(status_code=422, content={"error": {"code": "invalid_request",
            "message": "요청 형식이 올바르지 않습니다.", "retryable": False}})

    @app.get("/health")
    async def health(context: Request):
        return {"api_version": 1, "agent_ready": context.app.state.service.agent is not None,
                "unity": client.state()}

    @app.get("/unity/state")
    async def unity_state():
        return client.state()

    @app.post("/mission", response_model=MissionAccepted, status_code=202)
    async def create(request: CreateMissionRequest, context: Request):
        return await context.app.state.service.create(request)

    @app.get("/mission/{mid}", response_model=MissionSnapshot)
    async def status(mid: str, context: Request):
        snapshot = context.app.state.service.get(mid)
        return snapshot.model_copy(update={"unity": client.state()})

    @app.get("/mission/{mid}/result", response_model=MissionResult)
    async def result(mid: str, context: Request):
        snapshot = context.app.state.service.get(mid)
        if snapshot.status not in TERMINAL:
            raise APIError(409, "result_not_ready", "임무가 아직 종료되지 않았습니다.")
        assessed = sum(a.status == "assessed" for a in snapshot.assessments)
        coverage = "complete" if assessed == 4 else "partial" if assessed else "none"
        reports = [e for e in context.app.state.service.store.events(mid) if e["kind"] == "report"]
        return MissionResult(mission_id=mid, status=snapshot.status.value,
            summary=snapshot.error.message if snapshot.error else reports[-1]["data"]["summary"] if reports else "임무가 취소되었습니다.",
            assessments=snapshot.assessments, diagnostic_coverage=coverage, error=snapshot.error)

    @app.get("/mission/{mid}/events")
    async def events(mid: str, context: Request):
        context.app.state.service.get(mid)
        return {"mission_id":mid,"events":context.app.state.service.store.events(mid)}

    @app.post("/mission/{mid}/cancel", response_model=CancelAccepted)
    async def cancel(mid: str, context: Request):
        answer = await context.app.state.service.cancel(mid)
        return JSONResponse(status_code=200 if answer.stop_confirmed else 202,
                            content=answer.model_dump(mode="json"))

    return app


app = create_app()
