"""Explicit real Unity check against the unified VR REST contract (8767)."""
import argparse
import json
import time
from uuid import uuid4
import httpx


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8767")
    args = parser.parse_args()
    mid, settled = None, False
    request_id = str(uuid4())
    with httpx.Client(base_url=args.url, timeout=10, trust_env=False) as client:
        def get(path):
            response = client.get(path)
            response.raise_for_status()
            return response.json()
        health = get("/health")
        if not health.get("agent_ready"):
            print("Agent 설정이 필요합니다. 임무를 접수하지 않았습니다.")
            return 1
        if not health.get("unity_connected") or not health.get("unity_can_start"):
            print("Unity 새 Play의 Idle 상태가 필요합니다. 임무를 접수하지 않았습니다.")
            return 1
        print("request_id:", request_id, flush=True)
        try:
            response = client.post("/mission", json=dict(api_version=1, request_id=request_id,
                text="설비 A와 B를 점검하고 지점별 진단 결과를 보고해줘", execution_mode="simulation"))
            response.raise_for_status()
            mid = response.json()["mission_id"]
            deadline, previous = time.monotonic() + 960, None
            while time.monotonic() < deadline:
                snapshot = get(f"/mission/{mid}")
                progress = (snapshot["state"], snapshot["step"], [(p["point"], p["status"]) for p in snapshot["points"]])
                if progress != previous:
                    print(json.dumps(progress, ensure_ascii=False), flush=True)
                    previous = progress
                if snapshot["state"] in {"COMPLETED", "FAILED", "CANCELLED"}:
                    settled = not snapshot["requires_attention"]
                    print(json.dumps(get(f"/mission/{mid}/result"), ensure_ascii=False, indent=2))
                    return 0 if snapshot["state"] == "COMPLETED" else 1
                time.sleep(1)
            print("검증 대기 시간이 초과됐습니다.")
            return 1
        except (KeyboardInterrupt, httpx.HTTPError):
            print("검증 중단 또는 응답 미확인. 같은 요청 ID로 상태를 확인합니다.")
            return 1
        finally:
            if mid is None:
                try:
                    mid = get("/mission/by-request/" + request_id)["mission_id"]
                except httpx.HTTPError:
                    print("접수 확인 불가. 출력된 request_id를 보존하고 서버 상태를 확인하세요.")
            if mid and not settled:
                try:
                    response = client.post(f"/mission/{mid}/cancel", json={})
                    response.raise_for_status()
                    deadline = time.monotonic() + 15
                    while time.monotonic() < deadline:
                        snapshot = get(f"/mission/{mid}")
                        if snapshot["state"] in {"COMPLETED", "FAILED", "CANCELLED"}:
                            print("최종 상태:", snapshot["state"], "정지 확인:", snapshot["stop_confirmed"],
                                  "추가 확인:", snapshot["requires_attention"])
                            break
                        time.sleep(.5)
                    else:
                        print("정지 확인 제한 시간 초과: Unity 상태를 직접 확인하세요.")
                except httpx.HTTPError:
                    print("정지 요청 미확인: Unity 상태를 직접 확인하세요.")


if __name__ == "__main__":
    raise SystemExit(main())
