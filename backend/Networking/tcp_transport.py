"""TCP 유틸: 4바이트 big-endian 길이 + UTF-8 JSON. recv 경계는 메시지 경계가 아니다."""

import json
import select
import struct

from .protocol import _reject_constant, _unique_object, require

MAX_FRAME_BYTES = 16384


def recv_exact(sock, count, stop_event=None):
    data = bytearray()
    while len(data) < count:
        if stop_event is not None and stop_event.is_set():
            raise InterruptedError("종료 요청")
        if stop_event is not None and not select.select([sock], [], [], 0.1)[0]:
            continue
        chunk = sock.recv(count - len(data))
        if not chunk:
            if not data:
                return None
            raise ConnectionError("TCP 프레임 중간에 연결 종료")
        data.extend(chunk)
    return bytes(data)


def recv_frame(sock, stop_event=None):
    header = recv_exact(sock, 4, stop_event)
    if header is None:
        return None
    size = struct.unpack("!I", header)[0]
    require(0 < size <= MAX_FRAME_BYTES, "TCP 프레임 크기 오류")
    data = recv_exact(sock, size, stop_event)
    require(data is not None, "TCP JSON 본문 누락")
    packet = json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object,
                        parse_constant=_reject_constant)
    require(type(packet) is dict, "JSON 객체 필요")
    return packet


def send_frame(sock, packet):
    data = json.dumps(packet, ensure_ascii=False, allow_nan=False,
                      separators=(",", ":")).encode("utf-8")
    require(0 < len(data) <= MAX_FRAME_BYTES, "TCP 프레임 크기 오류")
    # sendall은 분할 송신을 완료할 때까지 기다린다. 실행 완료 응답은 아니다.
    sock.sendall(struct.pack("!I", len(data)) + data)
