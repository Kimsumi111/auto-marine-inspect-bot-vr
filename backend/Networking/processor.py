"""검증된 시뮬레이션 샘플을 Jetson 수신용 모방 목표값으로 변환한다."""


def clamp(value, low, high):
    return max(low, min(high, value))


def process(packet, bridge_id, pan_limit=90.0, tilt_limit=45.0):
    movement, gaze = packet["movement"], packet["gaze"]
    active = packet["type"] == "telemetry"
    drive_enabled = (active and movement["valid"] and movement["drive_enabled"]
                     and not movement["safety_stopped"])
    move = movement["move"] if drive_enabled else 0.0
    turn = movement["turn"] if drive_enabled else 0.0

    # 기존 Unity LaneFollowerController.ApplyDrive()와 같은 식을 사용한다.
    # 정규화된 바퀴 목표값이며 실제 PWM/속도 명령은 아니다.
    left = clamp(move + turn, -1.0, 1.0)
    right = clamp(move - turn, -1.0, 1.0)

    camera_valid = active and gaze["valid"]
    yaw_valid = camera_valid and gaze["yaw_valid"]
    # 수직 시선은 pitch만 유효하다. yaw=0을 확정 방향으로 오인하지 않는다.
    pan = clamp(gaze["yaw_deg"], -pan_limit, pan_limit) if yaw_valid else None
    tilt = clamp(gaze["pitch_deg"], -tilt_limit, tilt_limit) if camera_valid else None

    return {
        "version": 1, "type": "imitation_state", "source": "unity_simulation",
        "robot_id": packet["robot_id"], "bridge_id": bridge_id,
        "session_id": packet["session_id"], "seq": packet["seq"],
        # 원본 만료 시점을 보존한다. 변환/송신으로 TTL을 새로 시작하지 않는다.
        "expires_unix_ms": packet["sent_at_unix_ms"] + packet["ttl_ms"],
        "drive_enabled": drive_enabled,
        "left_normalized": left, "right_normalized": right,
        "camera_valid": camera_valid, "camera_yaw_valid": yaw_valid,
        "camera_pan_deg": pan, "camera_tilt_deg": tilt,
    }
