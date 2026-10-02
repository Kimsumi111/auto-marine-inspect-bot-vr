"""VR contract fixture only. Never connects to Unity TCP, LLM, or diagnosis tools."""
import argparse
import copy
import json
import threading
import time
import uuid
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

POINTS = ["inspect_point_A1", "inspect_point_A2", "inspect_point_B2", "inspect_point_B1"]
TERMINAL = {"COMPLETED", "FAILED", "CANCELLED"}


class Store:
    def __init__(self, scenario="success", clock=time.monotonic):
        self.scenario, self.clock = scenario, clock
        self.by_request, self.jobs = {}, {}
        self.lock = threading.RLock()

    def create(self, body):
        if set(body) != {"api_version", "request_id", "text", "execution_mode"}:
            return 422, error("invalid_request", "필수 필드 또는 허용 필드를 확인하세요.")
        try:
            uuid.UUID(body["request_id"])
        except (ValueError, TypeError, AttributeError):
            return 422, error("invalid_request", "request_id는 UUID여야 합니다.")
        if type(body["api_version"]) is not int or body["api_version"] != 1 or body["execution_mode"] != "simulation" or not isinstance(body["text"], str) or not 1 <= len(body["text"].strip()) <= 500:
            return 422, error("invalid_request", "simulation 모드와 1~500자 명령이 필요합니다.")
        with self.lock:
            old = self.by_request.get(body["request_id"])
            if old:
                if self.jobs[old]["request"] != body:
                    return 409, error("id_conflict", "같은 request_id에 다른 요청을 사용할 수 없습니다.")
                return 200, self.snapshot(old)
            for mid in self.jobs:
                state = self.snapshot(mid)
                if state["state"] not in TERMINAL or state["requires_attention"]:
                    return 409, error("mission_busy", "진행 중이거나 정지 확인이 필요한 임무가 있습니다.")
            mid = str(uuid.uuid4())
            self.jobs[mid] = {"request": copy.deepcopy(body), "start": self.clock(), "cancel": None}
            self.by_request[body["request_id"]] = mid
            return 202, self.snapshot(mid)

    def snapshot(self, mid):
        with self.lock:
            job = self.jobs[mid]
            elapsed = self.clock() - job["start"]
            stage = min(int(elapsed / 2), 7)
            state = ["PENDING", "PLANNING", "EXECUTING", "EXECUTING", "EXECUTING", "EXECUTING", "DIAGNOSING", "COMPLETED"][stage]
            done = max(0, min(stage - 2, 4))
            revision, stop, attention = stage, False, False
            if self.scenario == "failure" and stage >= 4:
                state, done, revision = "FAILED", 1, 4
            if job["cancel"] is not None:
                settled = self.clock() - job["cancel"] >= 2
                state = "CANCELLED" if settled else "CANCELLING"
                stop = settled
                revision = 100 + int(settled)
                done = job["cancel_points"]
                if self.scenario == "stop-unconfirmed" and settled:
                    state, stop, attention = "FAILED", False, True
            return dict(api_version=1, mission_id=mid, request_id=job["request"]["request_id"], revision=revision,
                        state=state, transport_mode="mock", execution_mode="simulation", data_mode="offline_csv_replay",
                        completed_points=done, total_points=4, stop_confirmed=stop, requires_attention=attention,
                        step="모의 " + state, message="UI 테스트 데이터: 실제 로봇 이동·CSV 진단을 수행하지 않았습니다.",
                        unity_session_id="", updated_at=datetime.now(timezone.utc).isoformat())

    def cancel(self, mid):
        with self.lock:
            state = self.snapshot(mid)
            job = self.jobs[mid]
            if not state["state"] in TERMINAL and job["cancel"] is None:
                job["cancel"] = self.clock()
                job["cancel_points"] = state["completed_points"]
            return 202 if self.snapshot(mid)["state"] == "CANCELLING" else 200, self.snapshot(mid)

    def report(self, mid):
        state = self.snapshot(mid)
        if state["state"] not in TERMINAL:
            return 409, error("result_not_ready", "아직 완료되지 않았습니다.")
        points = []
        for index, point in enumerate(POINTS):
            eq = "A" if "_A" in point else "B"
            success = state["state"] == "COMPLETED" or index < state["completed_points"]
            points.append(dict(inspection_id="mock-" + point, point=point, equipment_id=eq,
                               source_equipment_id="L-DSF-01" if eq == "A" else "L-SF-04",
                               status="SUCCEEDED" if success else "NOT_EVALUATED", message="모의 데이터" if success else "미수행 · 미판정",
                               models=[dict(key=key, abnormal_probability=p, threshold=0.5, abnormal=p >= 0.5, model_sha256="mock")
                                       for key, p in [("axis", .12), ("bearing", .82 if eq == "B" else .16), ("belt", .10), ("rotating", .18)]] if success else []))
        return 200, dict(api_version=1, mission_id=mid, state=state["state"], transport_mode="mock", execution_mode="simulation",
                         data_mode="offline_csv_replay", summary="[모의 결과] " + state["state"] + " · UI 확인용 고정 확률이며 실제 진단이 아닙니다.", points=points)


def error(code, message):
    return {"api_version": 1, "error": {"code": code, "message": message}}


def handler(store):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def setup(self):
            super().setup()
            self.connection.settimeout(5)

        def reply(self, status, body):
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def do_GET(self):
            if self.path == "/health":
                return self.reply(200, {"api_version": 1, "service": "metamarine-mission-mock", "ready": True, "transport_mode": "mock"})
            with store.lock:
                if self.path.startswith("/mission/by-request/"):
                    mid = store.by_request.get(self.path.rsplit("/", 1)[-1])
                    return self.reply(200, store.snapshot(mid)) if mid else self.reply(404, error("not_found", "접수 기록 없음"))
                parts = self.path.strip("/").split("/")
                if len(parts) in (2, 3) and parts[0] == "mission" and parts[1] in store.jobs:
                    if len(parts) == 2:
                        return self.reply(200, store.snapshot(parts[1]))
                    if parts[2] == "result":
                        return self.reply(*store.report(parts[1]))
                self.reply(404, error("not_found", "임무 또는 경로를 찾을 수 없습니다."))

        def do_POST(self):
            if self.headers.get("Origin"):
                return self.reply(403, error("forbidden", "Browser requests are disabled."))
            try:
                size = int(self.headers.get("Content-Length", 0))
                if not 0 < size <= 8192:
                    return self.reply(400, error("invalid_request", "Invalid body size"))
                body = json.loads(self.rfile.read(size))
                if not isinstance(body, dict):
                    raise ValueError()
            except (ValueError, OSError):
                return self.reply(400, error("invalid_request", "Invalid JSON"))
            with store.lock:
                if self.path == "/mission":
                    return self.reply(*store.create(body))
                parts = self.path.strip("/").split("/")
                if len(parts) == 3 and parts[0] == "mission" and parts[2] == "cancel" and parts[1] in store.jobs:
                    if body:
                        return self.reply(422, error("invalid_request", "Cancel body must be {}"))
                    return self.reply(*store.cancel(parts[1]))
                self.reply(404, error("not_found", "Unknown mission"))
    return Handler


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8877)
    parser.add_argument("--scenario", choices=["success", "failure", "stop-unconfirmed"], default="success")
    args = parser.parse_args()
    print("MOCK ONLY: http://127.0.0.1:%d | no Unity, LLM or real diagnosis" % args.port, flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), handler(Store(args.scenario))).serve_forever()
