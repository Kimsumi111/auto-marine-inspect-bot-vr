"""실행 진입점: 기존 LLM 서버와 별도의 프로세스로 시작한다."""

import argparse
import logging
import select
import socket
import time
from uuid import uuid4

from .processor import process
from .protocol import unix_ms
from .receiver import TelemetryReceiver
from .sender import StateSender
from .transport import UdpTransport


def run(bind_address, unity_address, jetson_address, send_hz=20,
        pan_limit=90.0, tilt_limit=45.0, stop_event=None):
    transport = UdpTransport(bind_address)
    receiver = TelemetryReceiver(transport, unity_address)
    sender = StateSender(transport, jetson_address)
    bridge_id = str(uuid4())
    interval = 1.0 / send_hz
    pending = None
    next_send = time.monotonic()
    next_log = next_send + 1
    logging.info("UDP %s <- Unity %s / -> Jetson %s",
                 transport.socket.getsockname(), unity_address, jetson_address)
    try:
        while stop_event is None or not stop_event.is_set():
            now = time.monotonic()
            # 최대 약 20ms 간격으로 대기를 마치고 종료/수신을 확인한다.
            select.select([transport.socket], [], [],
                          min(0.02, max(0.0, next_send - now)))
            latest = receiver.drain()
            if latest is not None:
                # 큐에 쌓지 않고 최신 샘플 하나만 유지한다.
                pending = latest
            if pending is not None:
                # 세션 교체 또는 지연된 hello 때문에 이전 후보가 남지 않게 한다.
                same_session = pending["session_id"] == receiver.session
                if (not same_session and (receiver.session is not None
                                          or pending["type"] != "goodbye")
                        or unix_ms() >= pending["sent_at_unix_ms"] + pending["ttl_ms"]):
                    pending = None

            now = time.monotonic()
            if now >= next_send:
                # 밀린 주기를 몰아서 실행하지 않고 다음 시점부터 다시 계산한다.
                next_send = now + interval
                if pending is not None:
                    sender.send(process(pending, bridge_id, pan_limit, tilt_limit))
                    pending = None
            if now >= next_log:
                next_log = now + 1
                logging.info("수신=%d 폐기=%d ACK오류=%d 송신접수=%d 만료=%d 송신오류=%d",
                             receiver.accepted, receiver.rejected, receiver.ack_errors,
                             sender.sent, sender.expired, sender.errors)
    finally:
        transport.close()


def main():
    parser = argparse.ArgumentParser(description="Unity -> Backend -> Jetson 순차 TCP (기존 UDP 선택 가능)")
    parser.add_argument("--transport", choices=("tcp", "udp"), default="tcp")
    parser.add_argument("--bind-host", default="127.0.0.1")
    parser.add_argument("--bind-port", type=int, default=None)
    parser.add_argument("--unity-host", default="127.0.0.1")
    parser.add_argument("--unity-port", type=int, default=19001)
    parser.add_argument("--jetson-host", default="127.0.0.1")
    parser.add_argument("--jetson-port", type=int, default=None)
    parser.add_argument("--send-hz", type=float, default=20)
    parser.add_argument("--pan-limit", type=float, default=90)
    parser.add_argument("--tilt-limit", type=float, default=45)
    args = parser.parse_args()
    if args.bind_port is None:
        args.bind_port = 19101 if args.transport == "tcp" else 19002
    if args.jetson_port is None:
        args.jetson_port = 19102 if args.transport == "tcp" else 19003
    if not (1 <= args.send_hz <= 60 and 0 < args.pan_limit <= 180
            and 0 < args.tilt_limit <= 90):
        parser.error("send-hz: 1~60, pan-limit: 0초과~180, tilt-limit: 0초과~90")
    ports = (args.bind_port, args.unity_port, args.jetson_port)
    if not all(1024 <= port <= 65535 for port in ports):
        parser.error("포트 범위: 1024~65535")
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    try:
        unity = (socket.gethostbyname(args.unity_host), args.unity_port)
        jetson = (socket.gethostbyname(args.jetson_host), args.jetson_port)
        if (args.bind_host, args.bind_port) in (unity, jetson) or unity == jetson:
            parser.error("송수신 endpoint는 서로 달라야 합니다")
        if args.transport == "tcp":
            from .tcp_bridge import configure_logging, run_tcp
            from .data_display import DataDisplay
            configure_logging()
            display = DataDisplay()
            display.start()
            try:
                run_tcp((args.bind_host, args.bind_port), unity[0], jetson,
                        args.pan_limit, args.tilt_limit, display=display)
            finally:
                display.close()
        else:
            run((args.bind_host, args.bind_port), unity, jetson, args.send_hz,
                args.pan_limit, args.tilt_limit)
    except KeyboardInterrupt:
        logging.info("Networking 종료")
    except OSError as error:
        parser.exit(1, f"Networking 소켓 오류: {error}\n")


if __name__ == "__main__":
    main()
