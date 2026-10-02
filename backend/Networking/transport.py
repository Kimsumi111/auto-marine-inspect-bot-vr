"""UDP 입출력 유틸리티. JSON 계약은 protocol.py가 담당한다."""

import errno
import socket

from .protocol import MAX_PACKET_BYTES, encode


class UdpTransport:
    def __init__(self, bind_address):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            # 같은 포트를 중복 점유하지 않도록 REUSEADDR를 사용하지 않는다.
            self.socket.bind(bind_address)
            self.socket.setblocking(False)
            # Windows UDP에서 상대 미실행으로 발생하는 ICMP 오류가
            # 이후 Unity 수신을 방해하지 않도록 비활성화한다.
            if hasattr(socket, "SIO_UDP_CONNRESET"):
                self.socket.ioctl(socket.SIO_UDP_CONNRESET, False)
        except OSError:
            self.socket.close()
            raise

    def receive(self):
        try:
            # 제한보다 1바이트 더 읽어 과대 패킷을 판별한다.
            return self.socket.recvfrom(MAX_PACKET_BYTES + 1)
        except BlockingIOError:
            return None
        except OSError as error:
            # Windows는 버퍼보다 큰 UDP를 읽으면 크기 오류를 발생시킨다.
            # 해당 데이터만 폐기하고 서버 수신은 계속한다.
            if error.errno == errno.EMSGSIZE or getattr(error, "winerror", None) == 10040:
                return b"", None
            raise

    def send(self, message, address):
        data = encode(message)
        try:
            self.socket.sendto(data, address)
            return True
        except OSError:
            # UDP 상태 스트림은 과거 샘플을 쌓아 재시도하지 않는다.
            return False

    def close(self):
        self.socket.close()
