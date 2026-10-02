"""Unity JSON 계약과 UDP 패킷 제한. 소켓 처리와 데이터 변환은 하지 않는다."""

import json
import math
import time
from uuid import UUID

MAX_PACKET_BYTES = 1200
CLOCK_TOLERANCE_MS = 50


def unix_ms():
    return time.time_ns() // 1_000_000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def integer(value, name, low, high):
    # Python의 bool도 int처럼 동작하므로 정확한 타입으로 검사한다.
    require(type(value) is int and low <= value <= high, f"{name}: 정수 범위 오류")


def number(value, name, low=None, high=None):
    require(type(value) in (int, float), f"{name}: 숫자 필요")
    try:
        value = float(value)
    except OverflowError as error:
        raise ValueError(f"{name}: 숫자 크기 오류") from error
    require(math.isfinite(value), f"{name}: NaN/Infinity 금지")
    require(low is None or value >= low, f"{name}: 하한 초과")
    require(high is None or value <= high, f"{name}: 상한 초과")
    return value


def boolean(value, name):
    require(type(value) is bool, f"{name}: bool 필요")


def vector(value, name, direction=False):
    require(type(value) is dict, f"{name}: 벡터 객체 필요")
    for axis in ("x", "y", "z"):
        number(value[axis], name + "." + axis,
               -1.001 if direction else None, 1.001 if direction else None)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "중복 JSON 필드: " + key)
        result[key] = value
    return result


def _reject_constant(value):
    raise ValueError("허용하지 않는 JSON 숫자: " + value)


def encode(message):
    data = json.dumps(message, ensure_ascii=False, allow_nan=False,
                      separators=(",", ":")).encode("utf-8")
    require(len(data) <= MAX_PACKET_BYTES, "UDP 출력 크기 초과")
    return data


def decode_unity(data, now_ms=None, check_freshness=True):
    """CS의 Packet/Movement/Gaze 구조를 검사한다. 오류 패킷은 ACK하지 않는다."""
    require(0 < len(data) <= MAX_PACKET_BYTES, "UDP 입력 크기 초과")
    packet = json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object,
                        parse_constant=_reject_constant)
    require(type(packet) is dict, "JSON 객체 필요")
    integer(packet["version"], "version", 1, 1)
    require(packet["type"] in ("hello", "telemetry", "goodbye"), "type 오류")
    require(packet["source"] == "unity_simulation", "source 오류")
    require(packet["robot_id"] == "jetbot", "robot_id 오류")
    session = packet["session_id"]
    require(type(session) is str and len(session) == 36, "session_id 오류")
    require(str(UUID(session)) == session, "표준 UUID 필요")
    integer(packet["seq"], "seq", 1, 2**63 - 1)
    integer(packet["sent_at_unix_ms"], "sent_at_unix_ms", 1, 2**63 - 1)
    # Unity Inspector 및 Send()의 실제 허용 범위와 일치시킨다.
    integer(packet["ttl_ms"], "ttl_ms", 50, 2000)
    now_ms = unix_ms() if now_ms is None else now_ms
    sent = packet["sent_at_unix_ms"]
    # TCP 순차 재생에서는 과거 명령도 실행한다. 형식 검사는 유지하고
    # UTC/TTL 기반 폐기만 생략한다. 기존 UDP 호출의 기본 동작은 그대로다.
    if check_freshness:
        require(sent <= now_ms + CLOCK_TOLERANCE_MS, "미래 timestamp")
        require(now_ms < sent + packet["ttl_ms"], "만료된 패킷")

    movement, gaze = packet["movement"], packet["gaze"]
    require(type(movement) is dict and type(gaze) is dict, "movement/gaze 객체 필요")
    for key in ("valid", "drive_enabled", "safety_stopped"):
        boolean(movement[key], "movement." + key)
    for key in ("move", "turn"):
        number(movement[key], "movement." + key, -1, 1)
    for key in ("forward_mps", "yaw_rate_dps"):
        number(movement[key], "movement." + key)
    number(movement["body_yaw_deg"], "body_yaw_deg", 0, 360)
    vector(movement["position_world_m"], "position_world_m")
    for key in ("valid", "yaw_valid"):
        boolean(gaze[key], "gaze." + key)
    number(gaze["yaw_deg"], "yaw_deg", -180, 180)
    number(gaze["pitch_deg"], "pitch_deg", -90, 90)
    vector(gaze["direction_world"], "direction_world", direction=True)
    vector(gaze["direction_robot_yaw"], "direction_robot_yaw", direction=True)
    return packet


def acknowledgement(packet):
    # Unity ReadAcks()는 hello_ack가 아닌 ack와 원본 seq를 기대한다.
    # 의미는 백엔드 수신/검증 확인이며 Jetson 수신/실행 확인이 아니다.
    return {"version": 1, "type": "ack", "session_id": packet["session_id"],
            "seq": packet["seq"]}
