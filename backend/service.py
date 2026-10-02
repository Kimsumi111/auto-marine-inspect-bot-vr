import asyncio
import copy
import time
import uuid
from datetime import datetime, timezone
from .models import ApiError, POINTS, SUPPORTED, TERMINAL


def now():
    return datetime.now(timezone.utc).isoformat()


class MissionService:
    def __init__(self, storage, link, mission_timeout=900, diagnosis=None):
        self.storage, self.link = storage, link
        self.jobs = storage.load()
        self.request_ids = {j["request"]["request_id"]: mid for mid, j in self.jobs.items()}
        self.tasks = set()
        self.agent = None
        self.agent_mode = "fixed_ab"
        self.agent_tasks = {}
        self.diagnosis = diagnosis
        self.diagnosis_tasks = {}
        self.mission_timeout = mission_timeout
        link.on_frame, link.on_disconnect = self.on_frame, self.on_disconnect
        for job in self.jobs.values():
            if job["snapshot"]["state"] not in TERMINAL:
                self.finish(job, "FAILED", "Backend 재시작으로 임무가 중단됐습니다. 자동 재출발하지 않습니다.", attention=job["sent"] and not job.get("unity_completed", False))

    def spawn(self, coro):
        task = asyncio.create_task(coro)
        self.tasks.add(task)
        task.add_done_callback(self.tasks.discard)
        return task

    def event(self, job, kind, data):
        job.setdefault("agent_events", []).append(dict(at=now(), kind=kind, data=copy.deepcopy(data)))
        self.storage.save(job)

    def active(self, job):
        if job["snapshot"]["state"] in TERMINAL | {"CANCELLING"}:
            raise asyncio.CancelledError()

    def stop_agent(self, job):
        task = self.agent_tasks.get(job["snapshot"]["mission_id"])
        if task is not None and task is not asyncio.current_task() and not task.done():
            task.cancel()

    def touch(self, job, **changes):
        state = job["snapshot"]
        if not any(state.get(k) != v for k, v in changes.items()):
            return
        state.update(changes)
        state["revision"] += 1
        state["updated_at"] = now()
        self.storage.save(job)

    def report(self, job):
        snapshot = job["snapshot"]
        return {**{k: snapshot[k] for k in ("api_version", "mission_id", "state", "transport_mode", "execution_mode", "data_mode")},
                "summary": snapshot["message"], "points": copy.deepcopy(job["points"])}

    def snapshot(self, job):
        return {**copy.deepcopy(job["snapshot"]), "points": copy.deepcopy(job["points"])}

    def stop_diagnosis(self, job, message):
        for task in self.diagnosis_tasks.get(job["snapshot"]["mission_id"], []):
            if task is not asyncio.current_task() and not task.done():
                task.cancel()
        for point in job["points"]:
            if point["status"] == "NOT_EVALUATED":
                point["message"] = message

    def finish(self, job, state, message, attention=False, stopped=False):
        self.stop_agent(job)
        self.stop_diagnosis(job, "임무 종료로 진단 미완료")
        self.touch(job, state=state, message=message, step=state, requires_attention=attention, stop_confirmed=stopped)

    def get(self, mid):
        if mid not in self.jobs:
            raise ApiError(404, "not_found", "임무를 찾을 수 없습니다.")
        return self.jobs[mid]

    def submit(self, request):
        body = request.model_dump()
        old = self.request_ids.get(request.request_id)
        if old:
            if self.jobs[old]["request"] != body:
                raise ApiError(409, "id_conflict", "같은 요청 ID의 내용이 다릅니다.")
            return 200, self.snapshot(self.jobs[old])
        if any(j["snapshot"]["state"] not in TERMINAL or j["snapshot"]["requires_attention"] for j in self.jobs.values()):
            raise ApiError(409, "mission_busy", "진행 중이거나 정지 확인이 필요한 임무가 있습니다.")
        if self.agent_mode == "openai" and self.agent is None:
            raise ApiError(503, "agent_unavailable", "OpenAI Agent 설정이 필요합니다. Backend 환경변수를 확인하세요.")
        if not self.link.fresh:
            raise ApiError(503, "unity_unavailable", "Unity Play 연결이 없습니다. WPF 연결을 종료하고 Unity를 실행하세요.")
        frame = self.link.latest
        if frame["canStart"] is not True or frame.get("state") != "Idle":
            raise ApiError(409, "unity_restart_required", "새 임무를 위해 Unity Play를 다시 시작하세요.")
        mid = str(uuid.uuid4())
        job = dict(request=body, sent=False, start_command_id=str(uuid.uuid4()), stop_ack=False, stop_sequence=-1,
                   points=[], inspections=[], agent_events=[], unity_completed=False, started_at=time.time(),
                   snapshot=dict(api_version=1, mission_id=mid, request_id=request.request_id, revision=0, state="PENDING",
                                 transport_mode="backend", execution_mode="simulation", data_mode="offline_csv_replay",
                                 completed_points=0, total_points=4, stop_confirmed=False, requires_attention=False,
                                 step="PENDING", message="요청 접수 · " + ("OpenAI Agent" if self.agent else "고정 A+B 테스트 모드"),
                                 agent_mode=self.agent_mode, plan=[],
                                 unity_session_id=frame["sessionId"], updated_at=now()))
        self.storage.save(job)  # Unique request ID is durable before scheduling any command.
        self.jobs[mid], self.request_ids[request.request_id] = job, mid
        self.agent_tasks[mid] = self.spawn(self.start(job))
        return 202, self.snapshot(job)

    async def start(self, job):
        if job["snapshot"]["state"] != "PENDING":
            return
        if self.agent is not None:
            await self.agent.run(job)
            return
        if job["request"]["text"].strip() not in SUPPORTED:
            self.finish(job, "FAILED", "unsupported_goal: 현재는 '설비 A와 B를 점검해줘' 고정 명령만 지원합니다. LLM은 아직 미연결입니다.")
            return
        self.touch(job, state="PLANNING", step="unity_precheck", message="Unity A+B 실행 조건 확인")
        await self.execute_start(job)

    async def execute_start(self, job):
        self.active(job)
        frame = self.link.latest
        if not self.link.fresh or frame["sessionId"] != job["snapshot"]["unity_session_id"] or frame.get("state") != "Idle" or frame.get("canStart") is not True:
            self.finish(job, "FAILED", "Unity 시작 조건 또는 세션이 변경됐습니다. 새 Play를 확인하세요.")
            return
        job["sent"] = True
        self.storage.save(job)  # Conservatively assume possible execution after this point.
        try:
            ack = await self.link.command("mission_start", job["start_command_id"], job["snapshot"]["unity_session_id"])
            if job["snapshot"]["state"] not in {"PLANNING", "EXECUTING"}:
                return
            self.event(job, "tool_result", dict(tool="start_inspection", acknowledged=ack.get("ok") is True))
            if ack.get("ok") is not True:
                self.finish(job, "FAILED", "Unity 시작 명령 거절 · 정지 확인이 필요합니다.", attention=True)
            else:
                self.touch(job, state="EXECUTING", step="mission_start_ack", message="시작 명령 적용됨 · 실제 점검 이벤트 대기")
        except (OSError, asyncio.TimeoutError):
            if job["snapshot"]["state"] in {"PLANNING", "EXECUTING"}:
                self.finish(job, "FAILED", "시작 명령 처리 여부 미확인 · 자동 재전송하지 않습니다. 취소로 정지를 확인하세요.", attention=True)

    def cancel(self, mid, failure=False):
        job = self.get(mid)
        state = job["snapshot"]
        if state["state"] == "CANCELLING" or (state["state"] in TERMINAL and not state["requires_attention"]):
            return 200 if state["state"] in TERMINAL else 202, self.snapshot(job)
        self.stop_agent(job)
        self.stop_diagnosis(job, "취소 요청으로 진단 미완료")
        if job.get("unity_completed"):
            self.finish(job, "FAILED" if failure else "CANCELLED", "Unity 주행 종료 확인 · 남은 진단 취소", stopped=True)
            return 200, self.snapshot(job)
        if not job["sent"]:
            self.finish(job, "FAILED" if failure else "CANCELLED", "시작 명령 전 종료", stopped=True)
            return 200, self.snapshot(job)
        job["stop_ack"] = False
        job["stop_sequence"] = self.link.sequence
        job["stop_command_id"] = str(uuid.uuid4())
        job["failure_stop"] = failure
        self.touch(job, state="CANCELLING", step="mission_stop", message="취소/정지 요청 중 · 정지 미확인", stop_confirmed=False)
        self.spawn(self.stop(job))
        return 202, self.snapshot(job)

    async def stop(self, job):
        command_id = job["stop_command_id"]
        try:
            ack = await self.link.command("mission_stop", command_id, job["snapshot"]["unity_session_id"])
            if job["snapshot"]["state"] != "CANCELLING" or job["stop_command_id"] != command_id:
                return
            job["stop_ack"] = ack.get("ok") is True
            self.storage.save(job)
            if not job["stop_ack"]:
                raise ConnectionError()
            # Telemetry may arrive before coroutine observes the ACK.
            if self.link.fresh:
                await self.on_frame(self.link.latest, self.link.sequence)
            await asyncio.sleep(5)
        except (OSError, asyncio.TimeoutError):
            pass
        if job["snapshot"]["state"] == "CANCELLING" and job["stop_command_id"] == command_id:
            self.finish(job, "FAILED", "정지 미확인 · Unity 연결을 확인하고 취소를 다시 요청하세요.", attention=True)

    async def on_frame(self, frame, sequence):
        for job in list(self.jobs.values()):
            state = job["snapshot"]
            if state["state"] in TERMINAL and not state["requires_attention"]:
                continue
            if frame["sessionId"] != state["unity_session_id"]:
                self.finish(job, "FAILED", "Unity Play 세션 변경으로 이전 임무 종료. 자동 재실행하지 않습니다.")
                continue
            if state["state"] == "CANCELLING":
                if job["stop_ack"] and sequence > job["stop_sequence"] and frame.get("state") == "Idle" and frame.get("canStart") is False:
                    self.finish(job, "FAILED" if job.get("failure_stop") else "CANCELLED",
                                "Unity 오류/timeout 후 제어 정지 확인" if job.get("failure_stop") else "Unity 정지 명령 적용 및 Idle 확인 · 취소 완료", stopped=True)
                continue
            if state["state"] in TERMINAL or not job["sent"]:
                continue
            for inspection in frame["inspections"]:
                if not isinstance(inspection, dict) or inspection.get("id") in job["inspections"]:
                    continue
                point = inspection.get("point")
                if point not in POINTS or inspection.get("equipmentId") != POINTS[point] or not inspection.get("id"):
                    continue
                if any(p["point"] == point for p in job["points"]):
                    continue
                job["inspections"].append(inspection["id"])
                job["points"].append(dict(inspection_id=inspection["id"], point=point, equipment_id=POINTS[point],
                                          source_equipment_id=inspection.get("sourceEquipmentId", ""), status="NOT_EVALUATED",
                                          message="CSV 진단 대기", models=[]))
                self.touch(job, completed_points=len(job["points"]), message="점검 완료 이벤트 수신: " + point)
                if self.diagnosis is not None:
                    task = self.spawn(self.diagnose_point(job, job["points"][-1], copy.deepcopy(inspection)))
                    self.diagnosis_tasks.setdefault(state["mission_id"], []).append(task)
            if frame.get("state") == "Fault":
                self.event(job, "unity_fault", dict(state="Fault", detail=str(frame.get("detail", ""))[:1000]))
                self.cancel(state["mission_id"], failure=True)
            elif frame.get("state") == "Completed":
                if not job.get("unity_completed"):
                    job["unity_completed"] = True
                    self.touch(job, state="DIAGNOSING", step="csv_diagnosis", message="Unity 주행 종료 · 지점별 CSV 진단 확인 중")
                self.complete_if_ready(job)
            else:
                self.touch(job, step=str(frame.get("state", "")), message="Unity " + str(frame.get("detail", ""))[:300])

    async def diagnose_point(self, job, point, inspection):
        try:
            if inspection.get("error"):
                raise ValueError("missing_measurement: Unity 점검 이벤트에 CSV 오류가 있습니다.")
            if self.agent is not None:
                result = await self.agent.diagnose(job, point, inspection.get("filePath", ""))
            else:
                result = await self.diagnosis.run(inspection.get("filePath", ""))
            if job["snapshot"]["state"] in TERMINAL | {"CANCELLING"}:
                return
            point.update(result, status="SUCCEEDED", message="저장 CSV 진단 완료 (새 센서 측정 아님)")
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            if job["snapshot"]["state"] in TERMINAL | {"CANCELLING"}:
                return
            from .vr_diagnosis import DiagnosisError
            message = str(exc) if isinstance(exc, (DiagnosisError, ValueError)) else "diagnosis_failed: 진단 실행 실패"
            point.update(status="FAILED", message=message, models=[])
        self.touch(job, message=point["point"] + ": " + point["message"])
        # touch may see an unchanged message, so always persist the result itself too.
        self.storage.save(job)
        self.event(job, "diagnosis", {k: copy.deepcopy(v) for k, v in point.items() if k != "source_file_name"})
        self.complete_if_ready(job)

    def complete_if_ready(self, job):
        if not job.get("unity_completed") or job["snapshot"]["state"] in TERMINAL | {"CANCELLING"}:
            return
        if len(job["points"]) != 4:
            self.finish(job, "FAILED", "Unity 종료 이벤트와 점검 지점 수가 일치하지 않습니다.")
        elif self.diagnosis is None:
            self.finish(job, "FAILED", "진단 실행기가 연결되지 않았습니다.")
        elif any(p["status"] == "NOT_EVALUATED" for p in job["points"]):
            return
        elif any(p["status"] == "FAILED" for p in job["points"]):
            self.finish(job, "FAILED", "4지점 점검 종료 · 일부 CSV 진단 실패. 미판정 지점을 확인하세요.")
        elif self.agent is not None:
            job["results_ready"] = True
        else:
            self.complete_report(job)

    def complete_report(self, job):
        self.active(job)
        abnormal = [p["point"] for p in job["points"] if any(m["abnormal"] for m in p["models"])]
        summary = "4지점 점검 및 저장 CSV 진단 완료. " + ("이상 검출: " + ", ".join(abnormal) if abnormal else "모든 모델의 판정 기준에서 이상 미검출")
        self.event(job, "report", dict(summary=summary))
        self.finish(job, "COMPLETED", summary, stopped=True)

    async def on_disconnect(self):
        for job in self.jobs.values():
            if job["sent"] and job["snapshot"]["state"] not in TERMINAL:
                if not job.get("unity_completed"):
                    self.finish(job, "FAILED", "Unity 연결 끊김 · 로봇 정지 여부 미확인. 재연결 후 취소를 요청하세요.", attention=True)

    async def watchdog(self):
        while True:
            await asyncio.sleep(1)
            for job in list(self.jobs.values()):
                if job["snapshot"]["state"] in {"PLANNING", "EXECUTING", "DIAGNOSING"} and time.time() - job["started_at"] > self.mission_timeout:
                    self.cancel(job["snapshot"]["mission_id"], failure=True)

    async def close(self):
        # Unexpected application shutdown is not a stop confirmation.
        for task in list(self.tasks):
            task.cancel()
        await asyncio.gather(*list(self.tasks), return_exceptions=True)
