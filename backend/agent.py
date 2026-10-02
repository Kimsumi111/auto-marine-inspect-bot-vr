"""Bounded LangGraph workflow with model-selected execution and diagnosis tools."""
import asyncio
from typing import TypedDict
from langgraph.graph import StateGraph, START, END
from .contracts import Assessment, StartInspectionArgs

POINTS = {"inspect_point_A1", "inspect_point_A2", "inspect_point_B2", "inspect_point_B1"}


class GraphState(TypedDict):
    mission_id: str
    command: str


class MissionAbort(Exception):
    def __init__(self, code, message):
        self.code, self.message = code, message


class AgentRunner:
    def __init__(self, service, model, diagnosis, mission_timeout=600):
        self.service, self.model, self.diagnosis = service, model, diagnosis
        self.mission_timeout = mission_timeout
        graph = StateGraph(GraphState)
        graph.add_node("plan", self.plan)
        graph.add_node("execute", self.execute)
        graph.add_node("report", self.report)
        graph.add_edge(START, "plan")
        graph.add_edge("plan", "execute")
        graph.add_edge("execute", "report")
        graph.add_edge("report", END)
        self.graph = graph.compile()

    def active(self, mid):
        snapshot = self.service.get(mid)
        if snapshot.status.value in {"CANCELLED", "CANCELLING", "FAILED", "COMPLETED"}:
            raise asyncio.CancelledError()
        return snapshot

    def event(self, mid, kind, data):
        self.service.store.event(mid, kind, data)

    async def choose(self, mid, observation, allowed):
        self.active(mid)
        name, reason = await self.model.choose(observation, allowed)
        self.active(mid)
        if name not in allowed:
            raise MissionAbort("internal_error", "허용되지 않은 도구 선택입니다.")
        self.event(mid, "decision", dict(tool=name, reason=reason))
        return name

    async def run(self, mid, command):
        try:
            async with asyncio.timeout(self.mission_timeout):
                await self.graph.ainvoke(dict(mission_id=mid, command=command))
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            snapshot = self.service.get(mid)
            if snapshot.status.value in {"CANCELLED", "CANCELLING", "FAILED", "COMPLETED"}:
                return
            code = exc.code if isinstance(exc, MissionAbort) else "timeout" if isinstance(exc, TimeoutError) else "internal_error"
            message = exc.message if isinstance(exc, MissionAbort) else "Agent 실행 실패. Unity 상태를 확인하세요."
            # An executing mission must be stopped before publishing failure when possible.
            if snapshot.status.value in {"EXECUTING", "DIAGNOSING"}:
                await self.service.cancel(mid)
                while self.service.get(mid).status.value == "CANCELLING":
                    await asyncio.sleep(0.05)
                snapshot = self.service.get(mid)
                if snapshot.error and snapshot.error.code.value == "stop_unconfirmed":
                    return
            self.service.update(snapshot, status="FAILED", error=dict(code=code,message=message,retryable=False))
            self.event(mid,"failure",dict(code=code))

    async def plan(self, state):
        mid = state["mission_id"]
        self.service.update(self.active(mid), status="PLANNING", current_step="planning")
        plan = await self.model.plan(state["command"])
        self.active(mid)
        self.event(mid, "plan", plan.model_dump())
        if plan.task != "inspection":
            raise MissionAbort("unsupported_targets", "점검 요청만 지원합니다.")
        if not plan.targets or any(t not in {"A","B"} for t in plan.targets):
            raise MissionAbort("unknown_equipment", "등록되지 않은 설비입니다.")
        if len(plan.targets) != 2 or set(plan.targets) != {"A","B"}:
            raise MissionAbort("unsupported_targets", "현재 A+B 전체 점검만 지원합니다.")
        self.service.update(self.active(mid), goal=dict(task="inspection",targets=plan.targets), plan=plan.steps)
        return state

    async def execute(self, state):
        mid = state["mission_id"]
        unity = self.service.unity
        observed = unity.state()
        self.event(mid,"observation",observed.model_dump(mode="json"))
        if observed.stale or not observed.connected:
            raise MissionAbort("unity_unavailable", "Unity 연결을 확인하세요.")
        if not observed.can_start:
            raise MissionAbort("unity_not_ready", "새 Unity Play 세션이 필요합니다.")
        choice = await self.choose(mid, observed.model_dump(mode="json"), {"start_inspection":"검증된 A+B 임무 시작", "abort_mission":"상태가 부적절하면 실패 종료"})
        # Respect abort selection; actual arguments are bound by backend, never model-provided.
        if choice == "abort_mission":
            raise MissionAbort("unity_not_ready", "Agent가 실행을 보류했습니다.")
        receipt = await self.service.start_inspection(StartInspectionArgs(mission_id=mid,
            expected_session_id=observed.session_id, targets=["A","B"]))
        self.event(mid,"tool_result",receipt.model_dump(mode="json"))
        self.active(mid)
        session = observed.session_id
        assessments = []
        seen = set()
        while True:
            self.active(mid)
            current = unity.state()
            if current.stale:
                raise MissionAbort("unity_unavailable", "Unity 연결이 끊겼습니다. 실행 여부 미확인입니다.")
            if current.session_id != session:
                raise MissionAbort("session_changed", "Unity 세션이 변경되었습니다.")
            if current.state == "Fault":
                # Preserve the Unity reason before mission_stop resets telemetry to Idle.
                mission = getattr(unity, "mission", None)
                detail = getattr(mission, "detail", "")
                self.event(mid, "unity_fault", dict(
                    state=current.model_dump(mode="json"), detail=detail[:1000]))
                raise MissionAbort("command_rejected", "Unity 점검 임무가 실패했습니다.")
            self.service.update(self.active(mid), unity=current)
            for record, path, error in unity.inspections():
                if record.inspection_id in seen:
                    continue
                if record.point in {a.inspection.point for a in assessments}:
                    raise MissionAbort("internal_error", "점검 지점 중복 기록입니다.")
                seen.add(record.inspection_id)
                self.event(mid,"inspection",record.model_dump(mode="json"))
                assessment = Assessment(inspection=record,status="pending")
                assessments.append(assessment)
                self.service.update(self.active(mid), assessments=assessments)
                if not record.data_available:
                    assessment = Assessment(inspection=record,status="unassessed",error=dict(
                        code="diagnosis_failed",message="진동 CSV가 없어 미판정입니다.",retryable=False))
                else:
                    await self.choose(mid,record.model_dump(mode="json"),{"diagnose_inspection":"완료 지점의 재생 CSV 진단"})
                    assessments[-1] = Assessment(inspection=record,status="running")
                    self.service.update(self.active(mid),assessments=assessments)
                    for attempt in range(2):
                        try:
                            result = await self.diagnosis.run(record.inspection_id,path)
                            self.active(mid)
                            assessment = Assessment(inspection=record,status="assessed",diagnosis=result)
                            self.event(mid,"diagnosis",result.model_dump(mode="json"))
                            break
                        except (OSError, ValueError, TimeoutError):
                            self.active(mid)
                            observation = dict(inspection_id=record.inspection_id,error="diagnosis_failed",attempt=attempt+1)
                            self.event(mid,"observation",observation)
                            choice = await self.choose(mid,observation,
                                {"retry_diagnosis":"같은 파일 분석을 한 번 재시도", "mark_unassessed":"미판정으로 기록"}
                                if attempt == 0 else {"mark_unassessed":"재시도 한도 도달: 미판정"})
                            if choice == "retry_diagnosis":
                                continue
                            assessment = Assessment(inspection=record,status="unassessed",error=dict(
                                code="diagnosis_failed",message="진동 분석 실패로 미판정입니다.",retryable=False))
                            break
                assessments[-1] = assessment
                self.service.update(self.active(mid),assessments=assessments)
            if current.state == "Completed":
                if {a.inspection.point for a in assessments} != POINTS:
                    raise MissionAbort("internal_error", "필수 점검 완료 기록이 누락되었습니다.")
                self.service.update(self.active(mid),status="DIAGNOSING",current_step="reporting")
                return state
            await asyncio.sleep(0.1)

    async def report(self, state):
        mid = state["mission_id"]
        snapshot = self.active(mid)
        await self.choose(mid,dict(assessments=[a.model_dump(mode="json") for a in snapshot.assessments],
            unity=snapshot.unity.model_dump(mode="json")),{"report_results":"실제 지점별 결과를 사용자에게 보고"})
        current = self.service.unity.state()
        if current.stale or current.session_id != snapshot.unity.session_id or current.state != "Completed":
            raise MissionAbort("session_changed", "보고 전 Unity 완료 상태를 확인하지 못했습니다.")
        # Numeric report is deterministic to preserve the actual model outputs.
        lines = []
        for a in snapshot.assessments:
            findings = ", ".join(f"{r.key} {r.abnormal_probability:.3f} ({'이상' if r.abnormal else '정상'})"
                for r in a.diagnosis.results) if a.diagnosis else "미판정"
            lines.append(f"{a.inspection.point}: {findings}")
        summary = "저장 CSV 기반 시뮬레이션 점검 결과. " + "; ".join(lines)
        self.service.store.event(mid,"report",dict(summary=summary))
        self.service.update(self.active(mid),status="COMPLETED",current_step="completed")
