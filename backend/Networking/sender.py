"""Jetson 목적지로 변환 상태만 송신한다. Jetson 응답을 기다리지 않는다."""

from .protocol import unix_ms
import logging

class StateSender:
    def __init__(self, transport, jetson_address):
        self.transport = transport
        self.jetson_address = jetson_address
        self.sent = self.expired = self.errors = 0

    def send(self, state):
        if unix_ms() >= state["expires_unix_ms"]:
            self.expired += 1
            logging.warning(
                "(명령 데이터 전송) (실패: 데이터 만료) seq=%d",
                state["seq"],
            )
            return False

        success = self.transport.send(state, self.jetson_address)

        if success:
            self.sent += 1
        else:
            self.errors += 1

        # 성공은 Jetson 수신 확인이 아니라 OS 송신 접수다.
        logging.info(
            "(명령 데이터 전송) (%s) "
            "목적지=%s:%d seq=%d "
            "enabled=%s left=%+.3f right=%+.3f "
            "camera_valid=%s yaw_valid=%s pan=%s tilt=%s",
            "OS 송신 접수 성공" if success else "송신 실패",
            self.jetson_address[0],
            self.jetson_address[1],
            state["seq"],
            state["drive_enabled"],
            state["left_normalized"],
            state["right_normalized"],
            state["camera_valid"],
            state["camera_yaw_valid"],
            state["camera_pan_deg"],
            state["camera_tilt_deg"],
        )

        return success

