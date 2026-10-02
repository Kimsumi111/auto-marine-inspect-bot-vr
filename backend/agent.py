"""LangGraph decisions over the single VR-compatible mission service.
TCP monitoring and cancellation never wait for this graph or a diagnosis task.
"""
import asyncio
from typing import TypedDict
from langgraph.graph import StateGraph, START, END
from .vr_diagnosis import DiagnosisError


class GraphState(TypedDict):
    mission_id: str


class AgentRunner:
    def __init__(self, service, model, decision_timeout=65):
        self.service, self.model = service, model
        self.decision_timeout = decision_timeout
        graph = StateGraph(GraphState)
        for name in ("plan", "execute", "report"):
            graph.add_node(name, getattr(self, name))
        graph.add_edge(START, "plan")
        graph.add_edge("plan", "execute")
        graph.add_edge("execute", "report")
        graph.add_edge("report", END)
        self.graph = graph.compile()

    def job(self, state):
        job = self.service.get(state["mission_id"])
        self.service.active(job)
        return job

    async def choose(self, job, observation, allowed):
        self.service.active(job)
        name, reason = await asyncio.wait_for(self.model.choose(observation, allowed), self.decision_timeout)
        self.service.active(job)
        if name not in allowed or not isinstance(reason, str):
            raise ValueError("Invalid tool selection")
        self.service.event(job, "decision", dict(tool=name, reason=reason[:500]))
        return name

    async def run(self, job):
        try:
            await self.graph.ainvoke(dict(mission_id=job["snapshot"]["mission_id"]))
        except asyncio.CancelledError:
            raise
        except Exception:
            if job["snapshot"]["state"] in {"FAILED", "COMPLETED", "CANCELLED", "CANCELLING"}:
                return
            self.service.event(job, "agent_error", dict(code="agent_failed"))
            if job["sent"]:
                self.service.cancel(job["snapshot"]["mission_id"], failure=True)
            else:
                self.service.finish(job, "FAILED", "Agent 해석·도구 선택 실패. 출발하지 않았습니다.")

    async def plan(self, state):
        job = self.job(state)
        self.service.touch(job, state="PLANNING", step="agent_plan", message="Agent가 목표와 계획을 확인하고 있습니다.")
        plan = await asyncio.wait_for(self.model.plan(job["request"]["text"]), self.decision_timeout)
        self.service.active(job)
        if plan.task != "inspection" or len(plan.targets) != 2 or set(plan.targets) != {"A", "B"}:
            self.service.finish(job, "FAILED", "unsupported_goal: 현재는 설비 A+B 전체 점검만 지원합니다.")
            raise asyncio.CancelledError()
        self.service.event(job, "plan", plan.model_dump())
        self.service.touch(job, plan=plan.steps, message="A+B 점검 계획 확인 · Unity 시작 준비")
        return state

    async def execute(self, state):
        job = self.job(state)
        link = self.service.link
        observation = dict(connected=link.fresh, state=(link.latest or {}).get("state"),
                           can_start=(link.latest or {}).get("canStart", False))
        self.service.event(job, "observation", observation)
        choice = await self.choose(job, observation,
            {"start_inspection": "검증된 A+B 임무 시작", "abort_mission": "상태가 부적절하면 실행 보류"})
        if choice != "start_inspection":
            self.service.finish(job, "FAILED", "Agent가 Unity 실행을 보류했습니다.")
            raise asyncio.CancelledError()
        await self.service.execute_start(job)
        while True:
            self.service.active(job)
            if job.get("results_ready"):
                return state
            await asyncio.sleep(.05)

    async def diagnose(self, job, point, path):
        observation = dict(inspection_id=point["inspection_id"], point=point["point"],
                           equipment_id=point["equipment_id"], data_origin="replay")
        await self.choose(job, observation, {"diagnose_inspection": "완료 지점의 저장 CSV 진단"})
        for attempt in range(2):
            self.service.active(job)
            try:
                result = await self.service.diagnosis.run(path)
                self.service.active(job)
                return result
            except DiagnosisError:
                self.service.active(job)
                failure = dict(**observation, error="diagnosis_failed", attempt=attempt + 1)
                self.service.event(job, "observation", failure)
                allowed = {"mark_unassessed": "미판정으로 기록"}
                if attempt == 0:
                    allowed["retry_diagnosis"] = "같은 CSV 분석을 한 번 재시도 (새 측정 아님)"
                choice = await self.choose(job, failure, allowed)
                if choice != "retry_diagnosis":
                    raise DiagnosisError("diagnosis_failed: CSV 진단 실패로 미판정입니다.") from None
        raise DiagnosisError("diagnosis_failed: 재시도 한도 초과")

    async def report(self, state):
        job = self.job(state)
        self.service.touch(job, step="agent_report", message="4지점 진단 완료 · Agent 결과 확인 중")
        # Explicit allowlist excludes source paths, file names and raw waveform.
        observations = [dict(point=p["point"], status=p["status"], models=p["models"]) for p in job["points"]]
        await self.choose(job, dict(points=observations, data_mode="offline_csv_replay"),
                          {"report_results": "검증된 지점별 결과 보고"})
        self.service.active(job)
        if not job.get("unity_completed") or len(job["points"]) != 4 or any(p["status"] != "SUCCEEDED" for p in job["points"]):
            raise ValueError("Incomplete report")
        self.service.complete_report(job)
        return state
