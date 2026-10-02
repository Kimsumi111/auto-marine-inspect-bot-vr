"""모의 Unity/Jetson의 실제 TCP로 순서, 지연, 50ms 재생 및 종료를 검증한다."""

import json
import socket
import struct
import threading
import time
import unittest
from uuid import uuid4

from .jetson_tcp_receiver import replay, validate
from .tcp_bridge import run_tcp
from .tcp_protocol import control_from_sample, decode_sample, validate_control
from .tcp_transport import recv_frame, send_frame
from .test_networking import sample


def tcp_sample(seq=1, kind="telemetry", session=None):
    packet = sample(seq, kind, session)
    packet.update(transport="tcp", duration_ms=50 if kind == "telemetry" else 0)
    # 오래된 UTC 명령도 TCP에서는 폐기하지 않는다.
    packet["sent_at_unix_ms"] = 1000
    return packet


def control(seq=2, end=False, session=None, bridge=None):
    packet = tcp_sample(seq, "goodbye" if end else "telemetry", session)
    return control_from_sample(packet, bridge or str(uuid4()))


class TcpContractTests(unittest.TestCase):
    def test_old_timestamp_and_fixed_gaze_preserved(self):
        packet = tcp_sample(2)
        packet["movement"]["drive_enabled"] = False
        packet["gaze"].update(yaw_deg=0.0, pitch_deg=0.0)
        decode_sample(packet)
        output = control_from_sample(packet, str(uuid4()))
        for check in (validate_control, validate):
            check(output)
        self.assertNotIn("expires_unix_ms", output)
        self.assertEqual(output["duration_ms"], 50)
        self.assertEqual(output["camera_pan_deg"], 0.0)
        self.assertTrue(output["camera_valid"])
        packet["gaze"].update(valid=False, yaw_valid=False)
        output = control_from_sample(packet, str(uuid4()))
        self.assertIsNone(output["camera_pan_deg"])
        self.assertIsNone(output["camera_tilt_deg"])

    def test_contract_rejects_bad_duration_and_nonfinite(self):
        for value in (True, 0, -1, 1001):
            packet = tcp_sample()
            packet["duration_ms"] = value
            with self.assertRaises(ValueError):
                decode_sample(packet)
        for key, value in (("left_normalized", float("nan")), ("seq", True),
                           ("camera_tilt_deg", None), ("camera_yaw_valid", 1)):
            packet = control()
            packet[key] = value
            for check in (validate, validate_control):
                with self.assertRaises(ValueError):
                    check(packet)

    def test_split_and_combined_frames(self):
        left, right = socket.socketpair()
        try:
            frames = []
            for seq in (2, 3):
                data = json.dumps(control(seq)).encode()
                frames.append(struct.pack("!I", len(data)) + data)
            wire = b"".join(frames)

            def write():
                for offset in range(0, len(wire), 7):
                    left.sendall(wire[offset:offset + 7])
                left.shutdown(socket.SHUT_WR)

            writer = threading.Thread(target=write)
            writer.start()
            self.assertEqual([recv_frame(right)["seq"], recv_frame(right)["seq"]], [2, 3])
            self.assertIsNone(recv_frame(right))
            writer.join(1)
        finally:
            left.close()
            right.close()

    def test_invalid_frame_and_truncated_body(self):
        for wire in (struct.pack("!I", 16385), struct.pack("!I", 20) + b"{}"):
            left, right = socket.socketpair()
            try:
                left.sendall(wire)
                left.shutdown(socket.SHUT_WR)
                with self.assertRaises((ValueError, ConnectionError)):
                    recv_frame(right)
            finally:
                left.close()
                right.close()


class ReplayTests(unittest.TestCase):
    def play(self, packets, delay=0, capacity=1024):
        sender, receiver = socket.socketpair()
        events, results, errors = [], [], []

        def worker():
            try:
                results.append(replay(receiver, apply=lambda p: events.append((p, time.monotonic())),
                                      capacity=capacity))
            except Exception as error:
                errors.append(error)

        thread = threading.Thread(target=worker)
        thread.start()
        try:
            for index, packet in enumerate(packets):
                if index == 1 and delay:
                    time.sleep(delay)
                send_frame(sender, packet)
            sender.shutdown(socket.SHUT_WR)
            thread.join(3)
            self.assertFalse(thread.is_alive())
        finally:
            sender.close()
            receiver.close()
        return events, results, errors

    def test_burst_executes_every_sample_for_full_duration(self):
        session, bridge = str(uuid4()), str(uuid4())
        packets = [control(i, i == 7, session, bridge) for i in range(2, 8)]
        events, results, errors = self.play(packets, capacity=1)
        self.assertFalse(errors)
        commands = [(p, t) for p, t in events if p is not None]
        self.assertEqual([p["seq"] for p, _ in commands], list(range(2, 8)))
        for (_, start), (_, next_start) in zip(commands, commands[1:]):
            self.assertGreaterEqual(next_start - start, 0.049)
        self.assertEqual(results[0], {"received": 6, "applied": 6, "duration_ms": 250})

    def test_delayed_next_command_stops_then_resumes_without_expiry(self):
        session, bridge = str(uuid4()), str(uuid4())
        packets = [control(2, False, session, bridge), control(3, True, session, bridge)]
        events, results, errors = self.play(packets, delay=0.18)
        self.assertFalse(errors)
        first = next(t for p, t in events if p is not None and p["seq"] == 2)
        second = next(t for p, t in events if p is not None and p["seq"] == 3)
        self.assertTrue(any(p is None and first + 0.049 <= t < second for p, t in events))
        self.assertGreaterEqual(second - first, 0.17)
        self.assertEqual(results[0]["duration_ms"], 50)

    def test_eof_drains_queue_without_goodbye(self):
        session, bridge = str(uuid4()), str(uuid4())
        events, results, errors = self.play([control(i, False, session, bridge) for i in (2, 3, 4)])
        self.assertFalse(errors)
        self.assertEqual(results[0]["applied"], 3)
        self.assertEqual(results[0]["duration_ms"], 150)

    def test_duplicate_or_missing_sequence_aborts(self):
        for second_seq in (2, 4):
            session, bridge = str(uuid4()), str(uuid4())
            _, results, errors = self.play([control(2, False, session, bridge),
                                            control(second_seq, False, session, bridge)])
            self.assertFalse(results)
            self.assertTrue(errors)


class TcpPipelineTests(unittest.TestCase):
    def test_real_bridge_and_jetson_replay_end_to_end(self):
        with socket.socket() as jetson_listener, socket.socket() as probe:
            jetson_listener.bind(("127.0.0.1", 0))
            jetson_listener.listen(1)
            jetson_listener.settimeout(2)
            probe.bind(("127.0.0.1", 0))
            bind = probe.getsockname()
            probe.close()
            stop, ready = threading.Event(), threading.Event()
            events, results, errors = [], [], []

            def bridge():
                try:
                    run_tcp(bind, "127.0.0.1", jetson_listener.getsockname(),
                            stop_event=stop, ready_event=ready)
                except Exception as error:
                    errors.append(error)

            def jetson():
                try:
                    conn, _ = jetson_listener.accept()
                    with conn:
                        results.append(replay(conn, apply=lambda p: events.append((p, time.monotonic()))))
                except Exception as error:
                    errors.append(error)

            bridge_thread = threading.Thread(target=bridge)
            jetson_thread = threading.Thread(target=jetson)
            bridge_thread.start()
            jetson_thread.start()
            self.assertTrue(ready.wait(2))
            try:
                with socket.create_connection(bind) as unity:
                    unity.settimeout(2)
                    session = str(uuid4())
                    send_frame(unity, tcp_sample(1, "hello", session))
                    self.assertEqual(recv_frame(unity)["seq"], 1)
                    for seq in range(2, 8):
                        packet = tcp_sample(seq, session=session)
                        packet["gaze"].update(yaw_deg=0.0, pitch_deg=0.0)
                        send_frame(unity, packet)
                    for seq in range(2, 8):
                        self.assertEqual(recv_frame(unity)["seq"], seq)
                    send_frame(unity, tcp_sample(8, "goodbye", session))
                    self.assertEqual(recv_frame(unity)["seq"], 8)
                jetson_thread.join(2)
                self.assertFalse(jetson_thread.is_alive())
                self.assertFalse(errors)
                self.assertEqual(results[0], {"received": 7, "applied": 7, "duration_ms": 300})
                commands = [(p, t) for p, t in events if p is not None]
                self.assertEqual([p["seq"] for p, _ in commands], list(range(2, 9)))
                for (p, t), (_, next_t) in zip(commands, commands[1:]):
                    self.assertAlmostEqual(p["left_normalized"], 0.5)
                    self.assertEqual(p["camera_pan_deg"], 0.0)
                    self.assertGreaterEqual(next_t - t, 0.049)
            finally:
                stop.set()
                bridge_thread.join(2)
                jetson_thread.join(2)
            self.assertFalse(bridge_thread.is_alive())

    def test_real_tcp_forward_ack_and_shutdown(self):
        with socket.socket() as jetson_listener, socket.socket() as probe:
            jetson_listener.bind(("127.0.0.1", 0))
            jetson_listener.listen(1)
            jetson_listener.settimeout(2)
            probe.bind(("127.0.0.1", 0))
            bind = probe.getsockname()
            probe.close()
            stop, ready = threading.Event(), threading.Event()
            errors = []

            def worker():
                try:
                    run_tcp(bind, "127.0.0.1", jetson_listener.getsockname(),
                            stop_event=stop, ready_event=ready)
                except Exception as error:
                    errors.append(error)

            thread = threading.Thread(target=worker)
            thread.start()
            self.assertTrue(ready.wait(2))
            try:
                with socket.create_connection(bind) as unity:
                    unity.settimeout(2)
                    session = str(uuid4())
                    send_frame(unity, tcp_sample(1, "hello", session))
                    jetson, _ = jetson_listener.accept()
                    with jetson:
                        jetson.settimeout(2)
                        self.assertEqual(recv_frame(unity)["seq"], 1)
                        # 한꺼번에 송신해도 전부 순서대로 전달되어야 한다.
                        for seq in range(2, 12):
                            send_frame(unity, tcp_sample(seq, session=session))
                        for seq in range(2, 12):
                            state = recv_frame(jetson)
                            self.assertEqual(state["seq"], seq)
                            self.assertEqual(state["duration_ms"], 50)
                            self.assertAlmostEqual(state["left_normalized"], 0.5)
                            self.assertEqual(recv_frame(unity)["seq"], seq)
                        send_frame(unity, tcp_sample(12, "goodbye", session))
                        self.assertTrue(recv_frame(jetson)["end_session"])
                        self.assertEqual(recv_frame(unity)["seq"], 12)
                        self.assertIsNone(recv_frame(jetson))
            finally:
                stop.set()
                thread.join(2)
            self.assertFalse(thread.is_alive())
            self.assertFalse(errors)

    def test_shutdown_interrupts_blocked_unity_read(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            bind = probe.getsockname()
        stop, ready = threading.Event(), threading.Event()
        thread = threading.Thread(target=run_tcp, args=(bind, "127.0.0.1", ("127.0.0.1", 9)),
                                  kwargs={"stop_event": stop, "ready_event": ready})
        thread.start()
        self.assertTrue(ready.wait(2))
        with socket.create_connection(bind):
            time.sleep(0.03)
            stop.set()
            thread.join(2)
        self.assertFalse(thread.is_alive())


if __name__ == "__main__":
    unittest.main()
