"""기존 이동/VR 계약을 재사용하되 TCP에서는 TTL 대신 적용 시간을 사용한다."""

import json
from uuid import UUID

from .processor import process
from .protocol import boolean, decode_unity, integer, number, require


def decode_sample(packet):
    require(type(packet) is dict, "JSON 객체 필요")
    # 기존 입력의 타입/범위/VR 검사를 그대로 재사용한다. timestamp는 로그용이다.
    decode_unity(json.dumps(packet, allow_nan=False, separators=(",", ":")).encode("utf-8"),
                 check_freshness=False)
    require(packet.get("transport") == "tcp", "TCP 계약 표시 필요")
    integer(packet.get("duration_ms"), "duration_ms", 0, 1000)
    if packet["type"] == "telemetry":
        require(packet["duration_ms"] > 0, "telemetry 적용 시간 필요")
    else:
        require(packet["duration_ms"] == 0, "hello/goodbye 적용 시간은 0")
    return packet


def control_from_sample(packet, bridge_id, pan_limit=90.0, tilt_limit=45.0):
    # 최근 변경된 좌우 변환/시선 유효성/각도 제한 로직은 그대로 사용한다.
    state = process(packet, bridge_id, pan_limit, tilt_limit)
    state.pop("expires_unix_ms")
    state.update(version=2, type="control", duration_ms=packet["duration_ms"],
                 end_session=packet["type"] == "goodbye",
                 source_sent_at_unix_ms=packet["sent_at_unix_ms"])
    return state


def validate_control(packet):
    require(type(packet) is dict, "JSON 객체 필요")
    integer(packet["version"], "version", 2, 2)
    require(packet["type"] == "control", "type 오류")
    require(packet["source"] == "unity_simulation" and
            packet["robot_id"] == "jetbot", "source/robot 오류")
    for key in ("bridge_id", "session_id"):
        value = packet[key]
        require(type(value) is str and str(UUID(value)) == value, key + " 오류")
    integer(packet["seq"], "seq", 2, 2**63 - 1)
    integer(packet["duration_ms"], "duration_ms", 0, 1000)
    for key in ("drive_enabled", "camera_valid", "camera_yaw_valid", "end_session"):
        boolean(packet[key], key)
    require((packet["duration_ms"] == 0) == packet["end_session"], "종료/시간 불일치")
    for key in ("left_normalized", "right_normalized"):
        number(packet[key], key, -1, 1)
        require(packet["drive_enabled"] or packet[key] == 0, "비활성 주행값 오류")
    require(not packet["camera_yaw_valid"] or packet["camera_valid"], "시선 플래그 오류")
    for key, valid, limit in (("camera_pan_deg", packet["camera_yaw_valid"], 180),
                              ("camera_tilt_deg", packet["camera_valid"], 90)):
        if valid:
            number(packet[key], key, -limit, limit)
        else:
            require(packet[key] is None, "무효 시선은 null 필요")
    if packet["end_session"]:
        require(not packet["drive_enabled"] and not packet["camera_valid"], "종료값 오류")
    return packet
