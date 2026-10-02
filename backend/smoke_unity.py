"""Run one real Unity mission through the local Backend; no mock substitution."""
import argparse
import json
import time
from uuid import uuid4

import httpx


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8000")
    args = parser.parse_args()
    mid = None
    terminal = False
    with httpx.Client(base_url=args.url, timeout=10, trust_env=False) as client:
        def get(path):
            response = client.get(path)
            response.raise_for_status()
            return response.json()

        health = get("/health")
        unity = health["unity"]
        if not health["agent_ready"]:
            print("Agent가 구성되지 않았습니다. Backend 환경변수를 확인하세요.")
            return 1
        if not (unity["connected"] and not unity["stale"] and unity["can_start"]
                and unity["state"] == "Idle"):
            print("Unity 새 Play의 Idle 상태가 필요합니다. 임무를 접수하지 않았습니다.")
            print(json.dumps(unity, ensure_ascii=False))
            return 1
        try:
            response = client.post("/mission", json={
                "request_id": "e2e-" + uuid4().hex,
                "command": "설비 A와 B를 점검하고 지점별 진단 결과를 보고해줘",
            })
            response.raise_for_status()
            mid = response.json()["mission_id"]
            print("mission_id:", mid, flush=True)
            deadline = time.monotonic() + 660
            previous = None
            while time.monotonic() < deadline:
                snapshot = get(f"/mission/{mid}")
                progress = (snapshot["status"], snapshot["current_step"],
                            [(a["inspection"]["point"], a["status"])
                             for a in snapshot["assessments"]])
                if progress != previous:
                    print(json.dumps(progress, ensure_ascii=False), flush=True)
                    previous = progress
                if snapshot["status"] in {"COMPLETED", "FAILED", "CANCELLED"}:
                    terminal = True
                    result = get(f"/mission/{mid}/result")
                    print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
                    events = get(f"/mission/{mid}/events")["events"]
                    print("저장된 이벤트 수:", len(events), flush=True)
                    return 0 if result["status"] == "COMPLETED" else 1
                time.sleep(1)
            print("검증 대기 시간이 초과됐습니다.")
            return 1
        except KeyboardInterrupt:
            print("검증을 중단하고 임무 취소를 요청합니다.")
            return 130
        finally:
            if mid and not terminal:
                try:
                    response = client.post(f"/mission/{mid}/cancel")
                    response.raise_for_status()
                    print("취소 응답:", response.text, flush=True)
                    deadline = time.monotonic() + 15
                    while time.monotonic() < deadline:
                        snapshot = get(f"/mission/{mid}")
                        if snapshot["status"] in {"CANCELLED", "FAILED", "COMPLETED"}:
                            print("최종 상태:", snapshot["status"], snapshot["error"], flush=True)
                            break
                        time.sleep(0.5)
                    else:
                        print("정지 확인 대기 종료: Unity 상태를 직접 확인하세요.")
                except httpx.HTTPError:
                    print("취소 요청 확인 실패: Unity 상태를 직접 확인하세요.")


if __name__ == "__main__":
    raise SystemExit(main())
