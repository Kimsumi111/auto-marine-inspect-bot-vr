import asyncio
from datetime import datetime, timezone
from pathlib import Path
import unittest
from backend.agent import AgentRunner
from backend.llm import ParsedPlan
from backend.main import MissionService
from backend.store import Store
from backend.diagnosis import DiagnosisRunner
from backend.contracts import CreateMissionRequest, UnityState, InspectionRecord, CommandReceipt, DiagnosisResult


class Model:
    def __init__(self,targets=None):
        self.targets = targets or ["A","B"]
    async def plan(self,command):
        return ParsedPlan(task="inspection",targets=self.targets,steps=["상태 확인","점검","진단","보고"])
    async def choose(self,observation,allowed):
        return next(iter(allowed)),"테스트 Observation 기반 선택"


class Unity:
    timeout=0.2
    revision=1
    def __init__(self):
        self.started=False
        self.commands=[]
    def state(self):
        return UnityState(connected=True,session_id="s1",state="Completed" if self.started else "Idle",
            can_start=not self.started,stale=False,observed_at=datetime.now(timezone.utc))
    async def command(self,action,session):
        self.commands.append(action)
        self.started=True
        return CommandReceipt(command_id="c1",session_id=session,acknowledged=True,outcome="applied")
    def inspections(self):
        return [(InspectionRecord(inspection_id=f"i{i}",session_id="s1",equipment_id=point[14],
            point=point,completed_at=datetime.now(timezone.utc),data_available=True),"hidden.csv","")
            for i,point in enumerate(["inspect_point_A1","inspect_point_A2","inspect_point_B2","inspect_point_B1"])]


class Diagnosis:
    def __init__(self,fail_once=False):
        self.calls=0
        self.fail_once=fail_once
    async def run(self,i,path):
        self.calls+=1
        if self.fail_once and self.calls==1:
            raise ValueError("temporary failure")
        return DiagnosisResult(inspection_id=i,file_name="x.csv",sample_count=1024,sampling_frequency=1000.0,
            results=[dict(key=k,abnormal_probability=0.6,abnormal=True,model_sha256="a"*64)
                     for k in ["axis","bearing","belt","rotating"]])


class AgentTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancel_during_planning_prevents_late_start(self):
        entered=asyncio.Event();release=asyncio.Event()
        class SlowModel(Model):
            async def plan(self,command):
                entered.set()
                await release.wait()
                return await super().plan(command)
        store=Store(":memory:");unity=Unity();service=MissionService(store,unity)
        runner=AgentRunner(service,SlowModel(),Diagnosis())
        mid=(await service.create(CreateMissionRequest(request_id="r1",command="점검"))).mission_id
        task=asyncio.create_task(runner.run(mid,"점검"))
        await entered.wait()
        await service.cancel(mid)
        release.set()
        try:
            await task
        except asyncio.CancelledError:
            pass
        self.assertEqual(service.get(mid).status.value,"CANCELLED")
        self.assertEqual(unity.commands,[])
        store.close()

    async def test_real_diagnosis_cli_on_original_csv(self):
        path=next((Path(__file__).resolve().parents[2]/"data/vibration/2.2kW/L-DSF-01").rglob("*.csv"))
        result=await DiagnosisRunner().run("real-test",str(path))
        self.assertEqual(len(result.results),4)
        self.assertEqual(result.mode,"offline_csv_replay")
        self.assertGreaterEqual(result.sample_count,100)

    async def test_full_graph_with_retry_and_no_duplicate_inspections(self):
        store=Store(":memory:");unity=Unity();service=MissionService(store,unity)
        diag=Diagnosis(fail_once=True)
        runner=AgentRunner(service,Model(),diag)
        mid=(await service.create(CreateMissionRequest(request_id="r1",command="A+B 점검"))).mission_id
        await runner.run(mid,"A+B 점검")
        snapshot=service.get(mid)
        self.assertEqual(snapshot.status.value,"COMPLETED")
        self.assertEqual(len(snapshot.assessments),4)
        self.assertEqual(diag.calls,5)
        self.assertEqual(unity.commands,["mission_start"])
        self.assertTrue(any(e["kind"]=="report" for e in store.events(mid)))
        self.assertNotIn("hidden.csv",str(store.events(mid)))
        store.close()

    async def test_unsupported_or_unknown_target_never_moves(self):
        for targets,code in [(["A"],"unsupported_targets"),(["C"],"unknown_equipment")]:
            store=Store(":memory:");unity=Unity();service=MissionService(store,unity)
            runner=AgentRunner(service,Model(targets),Diagnosis())
            mid=(await service.create(CreateMissionRequest(request_id="r1",command="점검"))).mission_id
            await runner.run(mid,"점검")
            self.assertEqual(service.get(mid).error.code.value,code)
            self.assertEqual(unity.commands,[])
            store.close()
