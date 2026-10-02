"""Jetson에 복사할 독립 실행 파일. TCP 수신/검증/순차 50ms 목표 재생만 수행한다.
모터·서보·센서 API는 연결하지 않는다. apply_target()이 향후 실제 제어 연결 지점이다.
"""

import argparse
import json
import logging
import math
import queue
import socket
import struct
import threading
import time
from datetime import datetime
from uuid import UUID

MAX_FRAME_BYTES = 16384


class ClockFormatter(logging.Formatter):
    def formatTime(self, record, datefmt=None):
        now = datetime.fromtimestamp(record.created)
        return now.strftime("%H/%M/%S.") + "%02d" % (now.microsecond // 10000)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "중복 JSON 필드")
        result[key] = value
    return result


def reject_constant(value):
    raise ValueError("NaN/Infinity 금지: " + value)


def recv_exact(sock, count):
    data = bytearray()
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            require(not data, "TCP 프레임 중간 종료")
            return None
        data.extend(chunk)
    return bytes(data)


def recv_frame(sock):
    header = recv_exact(sock, 4)
    if header is None:
        return None
    size = struct.unpack("!I", header)[0]
    require(0 < size <= MAX_FRAME_BYTES, "TCP 프레임 크기 오류")
    data = recv_exact(sock, size)
    require(data is not None, "JSON 본문 누락")
    return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object,
                      parse_constant=reject_constant)


def numeric(value, low, high):
    require(type(value) in (int, float), "숫자 필요")
    try:
        require(math.isfinite(value) and low <= value <= high, "숫자 범위 오류")
    except OverflowError:
        raise ValueError("숫자 크기 오류")


def validate(packet):
    require(type(packet) is dict, "JSON 객체 필요")
    require(type(packet["version"]) is int and packet["version"] == 2, "version 오류")
    require(packet["type"] == "control", "type 오류")
    require(packet["source"] == "unity_simulation" and packet["robot_id"] == "jetbot",
            "source/robot 오류")
    for key in ("session_id", "bridge_id"):
        value = packet[key]
        require(type(value) is str and str(UUID(value)) == value, key + " 오류")
    require(type(packet["seq"]) is int and 2 <= packet["seq"] < 2**63, "seq 오류")
    duration = packet["duration_ms"]
    require(type(duration) is int and 0 <= duration <= 1000, "적용 시간 오류")
    for key in ("drive_enabled", "camera_valid", "camera_yaw_valid", "end_session"):
        require(type(packet[key]) is bool, key + " bool 필요")
    require((duration == 0) == packet["end_session"], "종료/시간 불일치")
    for key in ("left_normalized", "right_normalized"):
        numeric(packet[key], -1, 1)
        require(packet["drive_enabled"] or packet[key] == 0, "비활성 이동값 오류")
    require(not packet["camera_yaw_valid"] or packet["camera_valid"], "시선 플래그 오류")
    for key, valid, limit in (("camera_pan_deg", packet["camera_yaw_valid"], 180),
                              ("camera_tilt_deg", packet["camera_valid"], 90)):
        if valid:
            numeric(packet[key], -limit, limit)
        else:
            require(packet[key] is None, "무효 시선은 null 필요")
    if packet["end_session"]:
        require(not packet["drive_enabled"] and not packet["camera_valid"], "종료값 오류")
    return packet


def apply_target(packet):
    # 실제 하드웨어 행동 모방은 보류한다. 현재는 실행할 목표만 출력한다.
    # 추후 이 함수에 모터/카메라 제어를 연결한다. 수신 스레드에서는 제어하지 않는다.
    if packet is None:
        logging.info("(실행 목표) (정지/다음 명령 대기) left=+0.000 right=+0.000")
    else:
        logging.info("(실행 목표 적용) (성공: 목표 출력) seq=%d duration=%dms "
                     "left=%+.3f right=%+.3f camera_valid=%s yaw_valid=%s pan=%s tilt=%s",
                     packet["seq"], packet["duration_ms"], packet["left_normalized"],
                     packet["right_normalized"], packet["camera_valid"],
                     packet["camera_yaw_valid"], packet["camera_pan_deg"], packet["camera_tilt_deg"])


def replay(sock, stop=None, apply=apply_target, capacity=1024):
    stop = stop or threading.Event()
    pending = queue.Queue(maxsize=capacity)
    eof = threading.Event()
    errors = []
    stats = {"received": 0, "applied": 0, "duration_ms": 0}

    def receive():
        session = bridge = None
        expected = 2  # hello seq=1은 Backend에서 소비한다.
        try:
            while not stop.is_set():
                packet = recv_frame(sock)
                if packet is None:
                    break
                validate(packet)
                if session is None:
                    session, bridge = packet["session_id"], packet["bridge_id"]
                require((packet["session_id"], packet["bridge_id"]) == (session, bridge),
                        "연결 중 세션 변경")
                require(packet["seq"] == expected, "순번 누락/중복/역순")
                # 큐가 가득 차면 기다린다. 이전 명령을 버리거나 최신값으로 덮어쓰지 않는다.
                while not stop.is_set():
                    try:
                        pending.put(packet, timeout=0.1)
                        break
                    except queue.Full:
                        continue
                if stop.is_set():
                    break
                stats["received"] += 1
                expected += 1
                logging.info("(명령 데이터 수신) (검증 성공) seq=%d 큐=%d",
                             packet["seq"], pending.qsize())
                if packet["end_session"]:
                    break
        except (OSError, ValueError, KeyError, TypeError, RecursionError) as error:
            if not stop.is_set():
                errors.append(str(error))
                stop.set()  # 오류 명령을 건너뛰고 뒤 명령을 실행하지 않는다.
        finally:
            eof.set()

    reader = threading.Thread(target=receive, daemon=True)
    reader.start()
    waiting = False
    try:
        while not stop.is_set():
            try:
                packet = pending.get(timeout=0.01)
            except queue.Empty:
                if not waiting:
                    apply(None)
                    waiting = True
                if eof.is_set():
                    break
                continue
            waiting = False
            apply(packet)
            # 수신 시각이나 UTC 만료 시각으로 적용 시간을 줄이지 않는다.
            # 여러 명령이 몰려와도 각각 duration_ms 전체를 실행하고, 늦은 주기를 압축하지 않는다.
            deadline = time.monotonic() + packet["duration_ms"] / 1000.0
            while not stop.is_set():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                stop.wait(remaining)
            if stop.is_set():
                break
            stats["applied"] += 1
            stats["duration_ms"] += packet["duration_ms"]
            logging.info("(명령 재생 완료) (성공: 시간 구간 완료) seq=%d 누적=%dms",
                         packet["seq"], stats["duration_ms"])
            if packet["end_session"]:
                break
            # 다음 명령이 없으면 즉시 정지 목표로 전환한다. 이전 이동값을 연장하지 않는다.
            if pending.empty():
                apply(None)
                waiting = True
    finally:
        stop.set()
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        reader.join(1)
        apply(None)
    if errors:
        raise ValueError(errors[0])
    return stats


def main():
    parser = argparse.ArgumentParser(description="Jetson TCP 수신/순차 목표 재생 (하드웨어 제어 없음)")
    parser.add_argument("--backend-host", required=True, help="허용할 PC LAN IP")
    parser.add_argument("--bind-host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=19102)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error("포트 범위: 1024~65535")
    handler = logging.StreamHandler()
    handler.setFormatter(ClockFormatter("[%(asctime)s] %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler])
    backend_host = socket.gethostbyname(args.backend_host)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind((args.bind_host, args.port))
            listener.listen(1)
            logging.info("TCP 수신 전용 %s / 허용 Backend IP=%s", listener.getsockname(), backend_host)
            while True:
                sock, peer = listener.accept()
                with sock:
                    if peer[0] != backend_host:
                        logging.warning("(연결 거부) Backend IP 불일치: %s", peer)
                        continue
                    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                    logging.info("(Backend 연결) (성공) %s", peer)
                    try:
                        stats = replay(sock)
                        logging.info("(세션 종료) 수신=%d 재생=%d 누적=%dms",
                                     stats["received"], stats["applied"], stats["duration_ms"])
                    except (OSError, ValueError) as error:
                        logging.error("(순차 재생 중단) %s", error)
    except KeyboardInterrupt:
        logging.info("Jetson TCP 수신기 종료")


if __name__ == "__main__":
    main()
