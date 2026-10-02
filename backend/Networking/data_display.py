"""데이터 표시 전용 프로세스 관리. 제어 명령 큐와 독립된 최신 상태 하나만 유지한다."""

import json
import logging
import subprocess
import sys
import threading
from datetime import datetime
from pathlib import Path


class DataDisplay:
    def __init__(self):
        self.state = {"unity_status": "연결 대기", "jetson_status": "연결 대기",
                      "send_status": "데이터 대기", "received_count": 0, "sent_count": 0}
        self.lock = threading.Lock()
        self.changed = threading.Event()
        self.stopped = threading.Event()
        self.process = None
        self.worker = None

    def start(self):
        if sys.platform != "win32":
            logging.warning("(데이터 콘솔) 별도 창은 Windows에서 지원합니다. 통신은 계속합니다.")
            return
        try:
            script = Path(__file__).with_name("data_console.py")
            # 사용자가 요청한 별도 표시 창이다. 메인 VS Code 콘솔을 공유하지 않는다.
            self.process = subprocess.Popen(
                [sys.executable, "-B", str(script)], stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, encoding="utf-8", bufsize=1,
                creationflags=subprocess.CREATE_NEW_CONSOLE,
            )
        except OSError as error:
            logging.warning("(데이터 콘솔) 생성 실패: %s / 통신은 계속합니다.", error)
            return
        self.worker = threading.Thread(target=self._write, daemon=True)
        self.worker.start()
        self.changed.set()

    def update(self, **values):
        # TCP 처리 스레드는 작은 dict 갱신만 한다. 콘솔/파이프 출력은 기다리지 않는다.
        with self.lock:
            self.state.update(values)
        self.changed.set()

    def snapshot(self):
        with self.lock:
            return dict(self.state)

    def _write(self):
        try:
            if self.process.stdout.readline().strip() != "READY":
                raise OSError("표시 창 초기화 실패")
            logging.info("(데이터 콘솔) 별도 창 열림 / 닫아도 통신은 유지됩니다.")
            while not self.stopped.is_set():
                if self.process.poll() is not None:
                    raise OSError("표시 창 닫힘")
                if not self.changed.wait(0.1):
                    continue
                self.changed.clear()
                state = self.snapshot()
                self.process.stdin.write(json.dumps(state, ensure_ascii=False) + "\n")
                self.process.stdin.flush()
                # 최신 화면은 최대 20Hz. 이 대기는 TCP 송수신에 영향을 주지 않는다.
                self.stopped.wait(0.05)
        except (OSError, ValueError) as error:
            if not self.stopped.is_set():
                logging.warning("(데이터 콘솔) %s / 통신은 계속합니다.", error)
        finally:
            self.stopped.set()

    def close(self):
        self.stopped.set()
        self.changed.set()
        if self.process is not None:
            # 먼저 프로세스를 끝내면 닫힌 콘솔/막힌 파이프에 대기 중인 작업자도 깨어난다.
            if self.process.poll() is None:
                self.process.terminate()
            try:
                self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=2)
            if self.worker is not None:
                self.worker.join(2)
            self.process.stdin.close()
            self.process.stdout.close()


def data_timestamp():
    now = datetime.now()
    return now.strftime("%H/%M/%S.") + "%02d" % (now.microsecond // 10000)
