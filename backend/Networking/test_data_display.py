"""데이터 표시가 명령 전달과 분리되고 최신 상태만 유지되는지 검증한다."""

import logging
import socket
import threading
import unittest
from unittest.mock import patch
from uuid import uuid4

from .data_console import format_screen
from .data_display import DataDisplay
from .tcp_bridge import connect_jetson, run_tcp
from .tcp_transport import recv_frame, send_frame
from .test_tcp_networking import tcp_sample


class DisplayTests(unittest.TestCase):
    def test_latest_snapshot_and_valid_zero_gaze(self):
        display = DataDisplay()
        for seq in range(1, 101):
            display.update(received_seq=seq, sent_seq=seq, camera_valid=True,
                           yaw_valid=True, pan=0.0, tilt=None, data_at=10.0)
        state = display.snapshot()
        self.assertEqual(state["received_seq"], 100)
        screen = "\n".join(format_screen(state, now=12.5))
        self.assertIn("100 / 100", screen)
        self.assertIn("+0.000 / —", screen)
        self.assertIn("2.50초", screen)
        self.assertIn("실제 Jetson 실행 완료 응답은 아님", screen)
        display.close()

    def test_snapshot_is_not_shared_mutable_dictionary(self):
        display = DataDisplay()
        display.update(sent_seq=2)
        snapshot = display.snapshot()
        snapshot["sent_seq"] = 99
        self.assertEqual(display.snapshot()["sent_seq"], 2)

    def test_jetson_connection_wait_logged_once(self):
        stop = threading.Event()
        class Connected:
            def settimeout(self, value):
                pass
            def setsockopt(self, *args):
                pass
        with patch("backend.Networking.tcp_bridge.socket.create_connection",
                   side_effect=[OSError(), OSError(), Connected()]), \
             patch.object(stop, "wait", return_value=False), \
             self.assertLogs(level="INFO") as logs:
            connect_jetson(("127.0.0.1", 19102), stop)
        self.assertEqual(sum("(대기)" in line for line in logs.output), 1)
        self.assertEqual(sum("(성공)" in line for line in logs.output), 1)

    def test_tcp_data_updates_display_not_main_logs(self):
        with socket.socket() as jetson_listener, socket.socket() as probe:
            jetson_listener.bind(("127.0.0.1", 0))
            jetson_listener.listen(1)
            jetson_listener.settimeout(2)
            probe.bind(("127.0.0.1", 0))
            bind = probe.getsockname()
            probe.close()
            stop, ready = threading.Event(), threading.Event()
            display = DataDisplay()  # 테스트에서는 화면 창을 띄우지 않는다.
            errors = []
            def worker():
                try:
                    run_tcp(bind, "127.0.0.1", jetson_listener.getsockname(),
                            stop_event=stop, ready_event=ready, display=display)
                except Exception as error:
                    errors.append(error)
            thread = threading.Thread(target=worker)
            with self.assertLogs(level="INFO") as logs:
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
                            for seq in range(2, 12):
                                if seq == 7:
                                    # 표시 작업자가 종료되어도 TCP 전달은 계속되어야 한다.
                                    display.stopped.set()
                                send_frame(unity, tcp_sample(seq, session=session))
                                self.assertEqual(recv_frame(jetson)["seq"], seq)
                                self.assertEqual(recv_frame(unity)["seq"], seq)
                            state = display.snapshot()
                            self.assertEqual(state["received_seq"], 11)
                            self.assertEqual(state["sent_seq"], 11)
                            self.assertEqual(state["received_count"], 10)
                            self.assertEqual(state["sent_count"], 10)
                            self.assertAlmostEqual(state["left"], 0.5)
                            self.assertEqual(state["send_status"], "TCP 송신 접수 성공")
                finally:
                    stop.set()
                    thread.join(2)
            self.assertFalse(thread.is_alive())
            self.assertFalse(errors)
            main_text = "\n".join(logs.output)
            self.assertIn("(Unity 연결)", main_text)
            self.assertIn("(Jetson 연결)", main_text)
            self.assertNotIn("(Unity 데이터 수신)", main_text)
            self.assertNotIn("(명령 데이터 전송)", main_text)


if __name__ == "__main__":
    unittest.main()
