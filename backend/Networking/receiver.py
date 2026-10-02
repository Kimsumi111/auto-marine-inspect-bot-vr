"""Unity 패킷 수신, 검증, 세션/순서 관리 및 수신 ACK."""

from collections import deque
import logging

from .protocol import acknowledgement, decode_unity, unix_ms



class TelemetryReceiver:
    def __init__(self, transport, unity_address):
        self.transport = transport
        self.unity_address = unity_address
        self.session = None
        self.last_seq = -1
        self.expires_ms = 0
        # 종료한 최근 세션이 지연된 hello/telemetry로 다시 살아나지 않게 한다.
        # 무한한 세션 기록 대신 최근 16개만 보관한다.
        self.retired_sessions = deque(maxlen=16)
        self.accepted = self.rejected = self.ack_errors = 0

    def _accept(self, packet):
        session, seq = packet["session_id"], packet["seq"]
        if session in self.retired_sessions:
            return False
        if session == self.session:
            if seq <= self.last_seq:
                return False
        else:
            # 기존 스트림이 살아 있으면 다른 실행 세션을 덮어쓰지 않는다.
            if self.session is not None and unix_ms() < self.expires_ms:
                return False
            if self.session is not None:
                self.retired_sessions.append(self.session)
            self.session, self.last_seq = session, -1

        self.last_seq = seq
        self.expires_ms = packet["sent_at_unix_ms"] + packet["ttl_ms"]
        if packet["type"] == "goodbye":
            self.retired_sessions.append(session)
            self.session, self.expires_ms = None, 0
        return True

    def drain(self, limit=64):
        latest = None
        # 한 번에 무제한 수신하지 않아 송신 루프가 굶지 않게 한다.
        for _ in range(limit):
            received = self.transport.receive()
            if received is None:
                break
            data, address = received
            if address != self.unity_address:
                self.rejected += 1
                continue
            try:
                packet = decode_unity(data)
            except (ValueError, KeyError, TypeError, RecursionError):
                self.rejected += 1
                continue
            if not self._accept(packet):
                self.rejected += 1
                continue

            self.accepted += 1

            movement = packet["movement"]
            gaze = packet["gaze"]

            logging.info(
                "(Unity 데이터 수신) (검증 성공) "
                "type=%s seq=%d "
                "move=%+.3f turn=%+.3f "
                "drive_valid=%s enabled=%s safety_stopped=%s "
                "gaze_valid=%s yaw_valid=%s yaw=%+.2f pitch=%+.2f",
                packet["type"],
                packet["seq"],
                movement["move"],
                movement["turn"],
                movement["valid"],
                movement["drive_enabled"],
                movement["safety_stopped"],
                gaze["valid"],
                gaze["yaw_valid"],
                gaze["yaw_deg"],
                gaze["pitch_deg"],
            )

            if not self.transport.send(acknowledgement(packet), address):
                self.ack_errors += 1

            # hello는 연결 확인만 하고 Jetson 목표값을 만들지 않는다.
            # 같은 묶음에서 새 세션이 시작되면 이전 후보도 폐기한다.
            if latest is not None and latest["session_id"] != packet["session_id"]:
                latest = None
            if packet["type"] != "hello":
                latest = packet
        return latest
