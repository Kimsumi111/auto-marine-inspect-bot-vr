"""Explicit low-volume API smoke: no Unity connection and no physical actions."""
import asyncio
from .llm import OpenAIModel


async def main():
    model=OpenAIModel()
    try:
        plan=await model.plan("설비 A와 B를 점검해줘")
        if plan.task!="inspection" or set(plan.targets)!={"A","B"}:
            print("Plan validation failed:",plan.model_dump())
            raise AssertionError("Unexpected goal")
        tool,reason=await model.choose({"inspection_id":"test-only", "error":"diagnosis_failed", "attempt":2},
            {"mark_unassessed":"재시도 한도 도달: 미판정"})
        assert tool=="mark_unassessed"
        print("OpenAI plan + function calling smoke passed; no Unity commands executed.")
    except Exception as error:
        print("OpenAI smoke failed:",type(error).__name__)
        status=getattr(error,"status_code",None)
        if status is not None:
            print("HTTP status:",status)
        raise SystemExit(1)
    finally:
        await model.close()


if __name__=="__main__":
    asyncio.run(main())
