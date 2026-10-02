import asyncio
import os
from contextlib import asynccontextmanager
from pathlib import Path
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException
from .models import ApiError, CancelRequest, MissionRequest, TERMINAL
from .service import MissionService
from .storage import Storage
from .unity import UnityLink
from .vr_diagnosis import DiagnosisRunner


def create_app(db_path=None, link=None, diagnosis=None, *, model=None, enable_agent=None):
    @asynccontextmanager
    async def lifespan(app):
        storage = Storage(db_path or os.environ.get("MARINE_DB", str(Path(__file__).parent / "runtime" / "missions.sqlite3")))
        unity = link or UnityLink(port=int(os.environ.get("MARINE_UNITY_PORT", "8765")))
        service = MissionService(storage, unity, diagnosis=diagnosis or DiagnosisRunner())
        mode = ("openai" if enable_agent else "fixed_ab") if enable_agent is not None else os.getenv("MARINE_AGENT_MODE", "openai")
        if mode not in {"openai", "fixed_ab"}:
            storage.close()
            raise ValueError("MARINE_AGENT_MODE must be openai or fixed_ab")
        llm = None
        service.agent_mode = mode
        if mode == "openai" and (model is not None or os.getenv("OPENAI_API_KEY")):
            from .agent import AgentRunner
            from .llm import OpenAIModel
            llm = model if model is not None else OpenAIModel()
            service.agent = AgentRunner(service, llm)
        app.state.service, app.state.unity = service, unity
        unity.task = asyncio.create_task(unity.run())
        watchdog = asyncio.create_task(service.watchdog())
        try:
            yield
        finally:
            watchdog.cancel()
            await asyncio.gather(watchdog, return_exceptions=True)
            await service.close()
            await unity.close()
            storage.close()
            if llm is not None and hasattr(llm, "close"):
                await llm.close()

    app = FastAPI(title="MetaMarine Unity mission backend", version="1.0.0", lifespan=lifespan)

    def error(status, code, message):
        return JSONResponse(status_code=status, content={"api_version": 1, "error": {"code": code, "message": message}})

    @app.exception_handler(ApiError)
    async def domain_error(_, exc):
        return error(exc.status, exc.code, exc.message)

    @app.exception_handler(RequestValidationError)
    async def validation_error(_, exc):
        return error(422, "invalid_request", "요청 필드/UUID/명령 길이 또는 형식이 잘못되었습니다.")

    @app.exception_handler(HTTPException)
    async def http_error(_, exc):
        return error(exc.status_code, "not_found" if exc.status_code == 404 else "invalid_request", "경로 또는 요청을 확인하세요.")

    @app.middleware("http")
    async def bounded_request(request: Request, call_next):
        if request.method == "POST":
            if request.headers.get("origin"):
                return error(403, "forbidden", "브라우저 요청은 지원하지 않습니다.")
            body = bytearray()
            try:
                async for chunk in request.stream():
                    body.extend(chunk)
                    if len(body) > 8192:
                        return error(400, "invalid_request", "요청은 8KiB 이하여야 합니다.")
            except Exception:
                return error(400, "invalid_request", "요청을 읽지 못했습니다.")
            request._body = bytes(body)
        return await call_next(request)

    @app.get("/health")
    async def health():
        unity = app.state.unity
        return dict(api_version=1, service="metamarine-backend", ready=True, transport_mode="backend",
                    unity_connected=unity.fresh, unity_can_start=bool(unity.fresh and unity.latest["canStart"]),
                    agent_mode=app.state.service.agent_mode, agent_ready=app.state.service.agent is not None, diagnosis_enabled=True,
                    diagnosis_python_available=app.state.service.diagnosis.python.is_file() if isinstance(app.state.service.diagnosis, DiagnosisRunner) else True)

    @app.post("/mission")
    async def create(body: MissionRequest):
        code, snapshot = app.state.service.submit(body)
        return JSONResponse(status_code=code, content=snapshot)

    @app.get("/mission/by-request/{request_id}")
    async def by_request(request_id: str):
        mid = app.state.service.request_ids.get(request_id)
        if mid is None:
            raise ApiError(404, "not_found", "접수 기록이 없습니다.")
        return app.state.service.snapshot(app.state.service.get(mid))

    @app.get("/mission/{mission_id}")
    async def snapshot(mission_id: str):
        return app.state.service.snapshot(app.state.service.get(mission_id))

    @app.get("/mission/{mission_id}/result")
    async def result(mission_id: str):
        job = app.state.service.get(mission_id)
        if job["snapshot"]["state"] not in TERMINAL:
            raise ApiError(409, "result_not_ready", "임무 진행 중입니다.")
        return app.state.service.report(job)

    @app.get("/mission/{mission_id}/events")
    async def events(mission_id: str):
        return {"mission_id": mission_id, "events": app.state.service.get(mission_id).get("agent_events", [])}

    @app.post("/mission/{mission_id}/cancel")
    async def cancel(mission_id: str, body: CancelRequest):
        code, snapshot = app.state.service.cancel(mission_id)
        return JSONResponse(status_code=code, content=snapshot)

    return app


app = create_app()
