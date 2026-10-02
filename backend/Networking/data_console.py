"""새 콘솔 창의 고정 화면. stdin의 최신 JSON만 표시하고 로그 행을 누적하지 않는다."""

import ctypes
import json
import os
import sys
import threading
import time
import unicodedata


def format_screen(state, now=None):
    now = time.time() if now is None else now
    last = state.get("data_at")
    age = "데이터 없음" if last is None else "%.2f초" % max(0, now - last)

    def value(key):
        item = state.get(key)
        return "—" if item is None else str(item)

    def signed(key):
        item = state.get(key)
        return "—" if item is None else "%+.3f" % item

    return [
        "Jetbot Networking | Unity → Backend → Jetson | TCP",
        "현재 전송 상태 (실제 Jetson 실행 완료 응답은 아님)",
        "",
        "Unity 연결       : " + value("unity_status"),
        "Jetson 연결      : " + value("jetson_status"),
        "최근 데이터 시각 : " + value("data_time"),
        "마지막 데이터 후 : " + age,
        "",
        "수신 / 송신 seq  : %s / %s" % (value("received_seq"), value("sent_seq")),
        "적용 시간        : %s ms" % value("duration_ms"),
        "송신 상태        : " + value("send_status"),
        "",
        "Move / Turn      : %s / %s" % (signed("move"), signed("turn")),
        "Left / Right     : %s / %s" % (signed("left"), signed("right")),
        "주행 유효/활성   : %s / %s" % (value("drive_valid"), value("drive_enabled")),
        "안전 정지        : " + value("safety_stopped"),
        "",
        "시선 / Yaw 유효  : %s / %s" % (value("camera_valid"), value("yaw_valid")),
        "Yaw / Pitch      : %s / %s" % (signed("yaw"), signed("pitch")),
        "Pan / Tilt       : %s / %s" % (signed("pan"), signed("tilt")),
        "",
        "누적 수신 / 송신 : %s / %s" % (value("received_count"), value("sent_count")),
        "",
        "이 창을 닫아도 Backend 통신은 계속됩니다.",
    ]


def main():
    # stdin은 부모와 연결된 파이프다. 출력은 새 콘솔의 CONOUT$를 명시적으로 연다.
    # 부모의 VS Code stdout을 상속하더라도 데이터가 그곳에 출력되지 않는다.
    import msvcrt
    sys.stdin.reconfigure(encoding="utf-8")
    # 콘솔 장치는 seek가 불가능하므로 w+ BufferedRandom 대신
    # 읽기/쓰기 핸들을 열고 쓰기 전용 텍스트 스트림으로 감싼다.
    descriptor = os.open("CONOUT$", os.O_RDWR)
    console = os.fdopen(descriptor, "w", encoding="utf-8", buffering=1)
    handle = msvcrt.get_osfhandle(console.fileno())
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetConsoleMode.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint)]
    kernel.SetConsoleMode.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    kernel.SetConsoleTitleW.argtypes = [ctypes.c_wchar_p]
    kernel.SetConsoleOutputCP(65001)
    kernel.SetConsoleTitleW("Jetbot 데이터 모니터")
    mode = ctypes.c_uint()
    if not kernel.GetConsoleMode(handle, ctypes.byref(mode)) or not kernel.SetConsoleMode(handle, mode.value | 4):
        raise OSError("콘솔 화면 갱신 모드 설정 실패")
    # 전체 삭제는 처음 한 번만 한다. 이후에는 같은 위치의 행을 덮어쓴다.
    console.write("\x1b[2J\x1b[H\x1b[?25l")
    console.flush()
    print("READY", flush=True)  # 부모의 초기화 확인 파이프. 사용자 터미널에는 출력되지 않는다.
    latest = {}
    lock = threading.Lock()
    ended = threading.Event()

    def receive():
        nonlocal latest
        try:
            for line in sys.stdin:
                packet = json.loads(line)
                if type(packet) is dict:
                    with lock:
                        latest = packet
        finally:
            ended.set()

    reader = threading.Thread(target=receive, daemon=True)
    reader.start()
    try:
        while not ended.is_set():
            with lock:
                state = dict(latest)
            lines = format_screen(state)
            # 폭을 창에 맞추고 마지막 열을 비워 자동 줄바꿈/스크롤을 피한다.
            # 한국어 전각 문자는 폭이 2이므로 표시 폭을 직접 계산한다.
            size = os.get_terminal_size(console.fileno())
            visible = []
            for line in lines[:max(1, size.lines - 1)]:
                width, text = 0, ""
                for char in line:
                    char_width = 2 if unicodedata.east_asian_width(char) in ("W", "F") else 1
                    if width + char_width >= size.columns:
                        break
                    text += char
                    width += char_width
                visible.append("\x1b[2K" + text)
            console.write("\x1b[H" + "\n".join(visible) + "\x1b[J")
            console.flush()
            ended.wait(0.05)
    finally:
        console.write("\x1b[?25h")
        console.flush()
        console.close()


if __name__ == "__main__":
    main()
