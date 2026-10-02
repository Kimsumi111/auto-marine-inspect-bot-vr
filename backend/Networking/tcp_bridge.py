"""Unity TCP → 검증/기존 변환 → Jetson TCP. 최신값 덮어쓰기/TTL 폐기 없음."""

import logging
import socket
import threading
import time
from datetime import datetime
from uuid import uuid4

from .protocol import acknowledgement, require
from .tcp_protocol import control_from_sample, decode_sample
from .tcp_transport import recv_frame, send_frame
from .data_display import data_timestamp


def update_display(display, **values):
    if display is not None:
        display.update(**values)


class ClockFormatter(logging.Formatter):
    def formatTime(self, record, datefmt=None):
        now = datetime.fromtimestamp(record.created)
        return now.strftime("%H/%M/%S.") + f"{now.microsecond // 10000:02d}"


def configure_logging():
    handler = logging.StreamHandler()
    handler.setFormatter(ClockFormatter("[%(asctime)s] %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler], force=True)


def connect_jetson(address, stop, display=None):
    # 아직 한 바이트도 전달하지 않은 최초 연결만 재시도한다.
    # 성립한 연결이 끊긴 뒤 자동 재송신하면 부분 실행을 중복할 수 있으므로 금지한다.
    waiting_logged = False
    update_display(display, jetson_status="연결 대기")
    while not stop.is_set():
        try:
            sock = socket.create_connection(address, timeout=1)
            sock.settimeout(None)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            logging.info("(Jetson 연결) (성공) 목적지=%s:%d", *address)
            update_display(display, jetson_status="연결됨")
            return sock
        except OSError:
            # 동일 대기 상태를 매 재시도마다 메인 터미널에 누적하지 않는다.
            if not waiting_logged:
                logging.info("(Jetson 연결) (대기) 목적지=%s:%d", *address)
                waiting_logged = True
            stop.wait(0.5)
    raise InterruptedError("종료 요청")


def serve_session(unity, jetson_address, stop, pan_limit, tilt_limit, connections, display=None):
    unity.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello = recv_frame(unity, stop)
    require(hello is not None, "hello 없이 연결 종료")
    decode_sample(hello)
    require(hello["type"] == "hello" and hello["seq"] == 1, "첫 프레임은 hello seq=1")
    session, last_seq = hello["session_id"], 1
    bridge = str(uuid4())
    received_count = sent_count = 0
    logging.info("(Unity 세션) (시작) session=%s", session)
    update_display(display, received_count=0, sent_count=0, received_seq=None, sent_seq=None,
                   send_status="Jetson 연결 대기")
    # Jetson이 늦게 켜져도 첫 명령을 폐기하지 않고 연결을 기다린다.
    jetson = connect_jetson(jetson_address, stop, display)
    connections.append(jetson)
    try:
        with jetson:
            send_frame(unity, acknowledgement(hello))
            while not stop.is_set():
                packet = recv_frame(unity, stop)
                if packet is None:
                    logging.info("(Unity 연결 종료) (전달된 명령은 Jetson에서 순차 재생)")
                    return
                decode_sample(packet)
                require(packet["session_id"] == session, "실행 중 세션 변경 금지")
                require(packet["seq"] == last_seq + 1, "명령 순번 누락/중복/역순")
                require(packet["type"] != "hello", "반복 hello 금지")
                movement, gaze = packet["movement"], packet["gaze"]
                received_count += 1
                update_display(display, received_seq=packet["seq"], received_count=received_count,
                               duration_ms=packet["duration_ms"], move=movement["move"],
                               turn=movement["turn"], drive_valid=movement["valid"],
                               drive_enabled=movement["drive_enabled"], safety_stopped=movement["safety_stopped"],
                               yaw=gaze["yaw_deg"], pitch=gaze["pitch_deg"],
                               data_time=data_timestamp(), data_at=time.time(), send_status="전송 중")
                state = control_from_sample(packet, bridge, pan_limit, tilt_limit)
                # 다음 입력을 읽기 전에 이 명령의 송신을 완료한다.
                # 느린 Jetson은 TCP 버퍼/Unity 송신 큐로 압력을 전달하며 명령을 덮어쓰지 않는다.
                send_frame(jetson, state)
                sent_count += 1
                update_display(display, sent_seq=state["seq"], sent_count=sent_count,
                               left=state["left_normalized"], right=state["right_normalized"],
                               camera_valid=state["camera_valid"], yaw_valid=state["camera_yaw_valid"],
                               pan=state["camera_pan_deg"], tilt=state["camera_tilt_deg"],
                               send_status="TCP 송신 접수 성공")
                last_seq = packet["seq"]
                # Backend ACK는 Jetson으로의 sendall 완료 확인이며 실제 실행 완료가 아니다.
                send_frame(unity, acknowledgement(packet))
                if packet["type"] == "goodbye":
                    logging.info("(Unity 세션) (정상 종료) 마지막 seq=%d", last_seq)
                    return
    finally:
        connections.remove(jetson)
        update_display(display, jetson_status="연결 종료")


def run_tcp(bind_address, unity_host, jetson_address, pan_limit=90.0,
            tilt_limit=45.0, stop_event=None, ready_event=None, display=None):
    stop = stop_event or threading.Event()
    connections = []
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(bind_address)
    listener.listen(1)
    listener.settimeout(0.2)

    # 테스트/종료 요청이 차단 recv/sendall을 깨우도록 별도 정리 감시자를 둔다.
    def interrupt_connections():
        stop.wait()
        for sock in list(connections):
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    watcher = threading.Thread(target=interrupt_connections, daemon=True)
    watcher.start()
    if ready_event is not None:
        ready_event.set()
    logging.info("TCP %s <- Unity IP %s / -> Jetson %s", listener.getsockname(), unity_host, jetson_address)
    try:
        while not stop.is_set():
            try:
                unity, peer = listener.accept()
            except socket.timeout:
                continue
            with unity:
                if peer[0] != unity_host:
                    logging.warning("(Unity 연결) (허용 IP 불일치) %s", peer)
                    continue
                connections.append(unity)
                logging.info("(Unity 연결) (성공) %s", peer)
                update_display(display, unity_status="연결됨")
                try:
                    serve_session(unity, jetson_address, stop, pan_limit, tilt_limit, connections, display)
                except (OSError, ValueError, KeyError, TypeError, RecursionError) as error:
                    # 잘못된 한 명령을 건너뛰고 계속하지 않는다. 세션 전체를 중단한다.
                    if not stop.is_set():
                        logging.error("(TCP 세션) (중단: 순차 실행 보장 불가) %s", error)
                        update_display(display, send_status="세션 중단: " + str(error))
                finally:
                    connections.remove(unity)
                    logging.info("(Unity 연결) (종료)")
                    update_display(display, unity_status="연결 대기")
    finally:
        stop.set()
        listener.close()
        watcher.join(1)
