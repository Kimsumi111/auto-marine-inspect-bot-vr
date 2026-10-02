"""표준 unittest 및 실제 localhost UDP 소켓으로 통신 핵심을 검증한다."""

import copy
import json
import socket
import threading
import unittest
from uuid import uuid4

from .__main__ import run
from .processor import process
from .protocol import decode_unity, encode, unix_ms
from .receiver import TelemetryReceiver
from .sender import StateSender
from .transport import UdpTransport


def sample(seq=1, kind="telemetry", session=None):
    zero = {"x": 0.0, "y": 0.0, "z": 0.0}
    forward = {"x": 0.0, "y": 0.0, "z": 1.0}
    return {
        "version": 1, "type": kind, "session_id": session or str(uuid4()),
        "source": "unity_simulation", "robot_id": "jetbot", "seq": seq,
        "sent_at_unix_ms": unix_ms(), "ttl_ms": 200,
        "movement": {"valid": True, "drive_enabled": True, "safety_stopped": False,
                     "move": 0.4, "turn": 0.1, "forward_mps": 0.2,
                     "yaw_rate_dps": 4.0, "position_world_m": zero,
                     "body_yaw_deg": 0.0},
        "gaze": {"valid": True, "yaw_valid": True, "yaw_deg": 30.0,
                 "pitch_deg": 10.0, "direction_world": forward,
                 "direction_robot_yaw": forward},
    }


class ProtocolProcessingTests(unittest.TestCase):
    def test_unity_nested_contract(self):
        packet = sample()
        self.assertEqual(decode_unity(encode(packet)), packet)

    def test_invalid_packets(self):
        original = sample()
        invalid = []
        for key, value in (("version", True), ("seq", -1), ("ttl_ms", 2001),
                           ("sent_at_unix_ms", unix_ms() - 300),
                           ("sent_at_unix_ms", unix_ms() + 1000)):
            packet = copy.deepcopy(original)
            packet[key] = value
            invalid.append(encode(packet))
        packet = copy.deepcopy(original)
        packet["movement"]["move"] = 1.1
        invalid.extend([encode(packet), b"{bad", b"x" * 1201,
                        b'{"version":1,"version":1}', b'{"version":NaN}'])
        for data in invalid:
            with self.subTest(data=data[:60]):
                with self.assertRaises((ValueError, KeyError)):
                    decode_unity(data)

    def test_wheel_formula_matches_unity(self):
        packet = sample()
        packet["movement"].update(move=0.9, turn=0.5)
        output = process(packet, "bridge")
        self.assertEqual(output["left_normalized"], 1.0)
        self.assertAlmostEqual(output["right_normalized"], 0.4)
        self.assertEqual(output["expires_unix_ms"], packet["sent_at_unix_ms"] + 200)

    def test_disabled_safety_and_goodbye(self):
        for key in ("valid", "drive_enabled", "safety_stopped", "goodbye"):
            packet = sample()
            if key == "goodbye":
                packet["type"] = "goodbye"
            else:
                packet["movement"][key] = key == "safety_stopped"
            output = process(packet, "bridge")
            self.assertFalse(output["drive_enabled"])
            self.assertEqual(output["left_normalized"], 0)
            self.assertEqual(output["right_normalized"], 0)

    def test_gaze_limits_and_vertical(self):
        packet = sample()
        packet["gaze"].update(yaw_deg=160, pitch_deg=80)
        output = process(packet, "bridge")
        self.assertEqual((output["camera_pan_deg"], output["camera_tilt_deg"]), (90, 45))
        packet["gaze"]["yaw_valid"] = False
        output = process(packet, "bridge")
        self.assertTrue(output["camera_valid"])
        self.assertFalse(output["camera_yaw_valid"])
        self.assertIsNone(output["camera_pan_deg"])
        self.assertEqual(output["camera_tilt_deg"], 45)
        packet["gaze"]["valid"] = False
        output = process(packet, "bridge")
        self.assertIsNone(output["camera_tilt_deg"])


class UdpTests(unittest.TestCase):
    def setUp(self):
        self.transport = UdpTransport(("127.0.0.1", 0))
        self.unity = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.unity.bind(("127.0.0.1", 0))
        self.unity.settimeout(0.5)
        self.jetson = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.jetson.bind(("127.0.0.1", 0))
        self.jetson.settimeout(0.1)
        self.receiver = TelemetryReceiver(self.transport, self.unity.getsockname())
        self.sender = StateSender(self.transport, self.jetson.getsockname())

    def tearDown(self):
        self.transport.close()
        self.unity.close()
        self.jetson.close()

    def receive(self, packet):
        self.unity.sendto(encode(packet), self.transport.socket.getsockname())
        return self.receiver.drain()

    def test_hello_ack_and_forward(self):
        packet = sample(kind="hello")
        self.assertIsNone(self.receive(packet))
        ack, source = self.unity.recvfrom(1200)
        self.assertEqual(json.loads(ack), {"version": 1, "type": "ack",
                         "session_id": packet["session_id"], "seq": 1})
        self.assertEqual(source, self.transport.socket.getsockname())
        packet.update(type="telemetry", seq=2)
        self.sender.send(process(self.receive(packet), "bridge"))
        state, _ = self.jetson.recvfrom(1200)
        self.assertEqual(json.loads(state)["type"], "imitation_state")

    def test_latest_only_duplicate_and_reverse(self):
        session = str(uuid4())
        for seq in (1, 3, 2, 3):
            self.unity.sendto(encode(sample(seq=seq, session=session)),
                              self.transport.socket.getsockname())
        latest = self.receiver.drain()
        self.assertEqual(latest["seq"], 3)
        self.assertEqual(self.receiver.rejected, 2)

    def test_stationary_gaze_repeats_and_tracking_loss_clears_angles(self):
        # 주행이 꺼져 있어도 유효한 정면 시선은 계속 전달되어야 한다.
        # 0도와 추적 없음(null)을 구분하고, 각도가 같다는 이유로 버리지 않는다.
        packet = sample()
        packet["movement"]["drive_enabled"] = False
        packet["gaze"].update(yaw_deg=0.0, pitch_deg=0.0)
        for seq in (1, 2):
            packet["seq"] = seq
            packet["sent_at_unix_ms"] = unix_ms()
            self.assertTrue(self.sender.send(process(self.receive(packet), "bridge")))
            state = json.loads(self.jetson.recvfrom(1200)[0])
            self.assertEqual(state["seq"], seq)
            self.assertTrue(state["camera_valid"])
            self.assertTrue(state["camera_yaw_valid"])
            self.assertEqual(state["camera_pan_deg"], 0.0)
            self.assertEqual(state["camera_tilt_deg"], 0.0)
            self.assertFalse(state["drive_enabled"])

        packet["seq"] = 3
        packet["sent_at_unix_ms"] = unix_ms()
        packet["gaze"].update(valid=False, yaw_valid=False)
        self.sender.send(process(self.receive(packet), "bridge"))
        state = json.loads(self.jetson.recvfrom(1200)[0])
        self.assertFalse(state["camera_valid"])
        self.assertIsNone(state["camera_pan_deg"])
        self.assertIsNone(state["camera_tilt_deg"])

    def test_active_session_and_retired_goodbye(self):
        packet = sample()
        self.assertIsNotNone(self.receive(packet))
        self.assertIsNone(self.receive(sample()))
        goodbye = sample(seq=2, kind="goodbye", session=packet["session_id"])
        self.assertIsNotNone(self.receive(goodbye))
        self.assertIsNone(self.receive(sample(seq=3, session=packet["session_id"])))
        self.assertIsNotNone(self.receive(sample()))

    def test_expired_or_invalid_not_acked(self):
        packet = sample()
        packet["sent_at_unix_ms"] -= 300
        self.assertIsNone(self.receive(packet))
        with self.assertRaises(socket.timeout):
            self.unity.recvfrom(1200)

    def test_expired_output_not_sent(self):
        output = process(sample(), "bridge")
        output["expires_unix_ms"] = unix_ms() - 1
        self.assertFalse(self.sender.send(output))
        with self.assertRaises(socket.timeout):
            self.jetson.recvfrom(1200)

    def test_oversized_and_foreign_packets(self):
        self.unity.sendto(b"x" * 5000, self.transport.socket.getsockname())
        self.assertIsNone(self.receiver.drain())
        self.jetson.sendto(encode(sample()), self.transport.socket.getsockname())
        self.assertIsNone(self.receiver.drain())
        self.assertEqual(self.receiver.rejected, 2)


class FullLoopTests(unittest.TestCase):
    def test_pipeline_and_socket_cleanup(self):
        # 빈 포트를 확보한 뒤 실제 실행 루프를 시작한다.
        unity = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        jetson = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        for sock in (unity, jetson, probe):
            sock.bind(("127.0.0.1", 0))
        bind = probe.getsockname()
        probe.close()
        stop = threading.Event()
        errors = []

        def worker():
            try:
                run(bind, unity.getsockname(), jetson.getsockname(), stop_event=stop)
            except Exception as error:
                errors.append(error)

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()
        unity.settimeout(0.05)
        jetson.settimeout(1)
        session = str(uuid4())
        try:
            # 포트 준비를 기다리며 Unity와 같은 증가 seq로 hello를 보낸다.
            for seq in range(1, 21):
                unity.sendto(encode(sample(seq, "hello", session)), bind)
                try:
                    ack = json.loads(unity.recvfrom(1200)[0])
                    break
                except (socket.timeout, ConnectionResetError):
                    # 실제 CS도 상대 미실행 SocketException을 처리한 뒤 재송신한다.
                    continue
            else:
                self.fail(f"Backend ACK 없음: {errors}")
            self.assertEqual(ack["type"], "ack")
            packet = sample(seq + 1, session=session)
            unity.sendto(encode(packet), bind)
            output = json.loads(jetson.recvfrom(1200)[0])
            self.assertEqual(output["seq"], packet["seq"])
            self.assertAlmostEqual(output["left_normalized"], 0.5)
            self.assertAlmostEqual(output["right_normalized"], 0.3)
            self.assertEqual(output["camera_pan_deg"], 30)
        finally:
            stop.set()
            thread.join(1)
            unity.close()
            jetson.close()
        self.assertFalse(thread.is_alive())
        self.assertFalse(errors)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(bind)


if __name__ == "__main__":
    unittest.main()
