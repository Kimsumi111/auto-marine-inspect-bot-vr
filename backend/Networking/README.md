# Jetbot Networking — 현재 기본 TCP / 기존 UDP 보존

2026-10-03 순차 TCP를 기본으로 추가했다. 기존 UDP·로그·최근 VR 시선 처리는 보존한다.
실행·계약·검증·한계는 [TCP 가이드](../../docs/JETBOT_TCP_GUIDE.md)를 따른다.

```powershell
python -B -m backend.Networking --transport tcp --jetson-host "<Jetson LAN IP>" --jetson-port 19102
```

Unity TCP 127.0.0.1:19101 → Networking → Jetson TCP 19102.
Unity Use Tcp=true, Tcp Backend Port=19101, Send Hz=20. Jetson에 jetson_tcp_receiver.py를 복사한다.
현재 Jetson 코드는 순차 목표 재생/로그까지이며 모터/카메라 제어는 연결하지 않았다.

| 추가 파일 | 책임 |
|---|---|
| `tcp_transport.py` | TCP 길이 프레임·분할 수신·sendall |
| `tcp_protocol.py` | 기존 검증/변환 재사용, TTL 대신 적용 시간 |
| `tcp_bridge.py` | 연결·연속 순번·순차 전달·시간 로그 |
| `jetson_tcp_receiver.py` | Jetson 복사용 독립 수신·순차 목표 재생 |
| `test_tcp_networking.py` | 실제 localhost TCP 테스트 11개 |

--send-hz/--unity-port는 UDP 모드 설정이다. TCP는 Unity duration_ms를 그대로 전달한다.
--pan-limit/--tilt-limit는 양쪽 모드에 적용한다. 총 24개 테스트·C# 참조 컴파일·TCP 작업자 검증 통과.
실제 장치 실행은 미검증이다. 아래는 이전 UDP 구현 기록이다. 실행 예제에는 --transport udp를 추가한다.

# 과거 UDP 구현 이력

2026-10-03 VR 고정 시선 확인: Unity CaptureGaze 분리/Inspector 진단 적용 후 유효한 동일 0도 시선의 반복 전달 및 추적 손실 시 null 변환을 실제 localhost UDP로 확인했다. 기존 테스트와 합계 13개 통과. Unity 참조 C# 컴파일 성공. 실제 HMD/Unity Play/Jetson은 이번 변경에서 미검증이며, 실제 젯봇 저장소는 수정하지 않았다. 설정과 Jetson 로그 적용은 [VR 시선 가이드](../../docs/JETBOT_GAZE_GUIDE.md)를 따른다. 아래 12개 테스트 기록은 최초 구현 이력이다.

2026-10-03 추가. 구현과 문서는 이 폴더 안에만 둔다. 기존 Backend 서버, Unity
스크립트, 씬, 공유 문서는 수정하지 않았다. Python 표준 라이브러리만 사용한다.

## 실행

저장소 루트에서 사용 가능한 Python 3.9 이상으로 실행한다.

```powershell
python -B -m backend.Networking
```

기존 LLM Backend와 별도 프로세스다. 기존 Unity의 Backend 자동 시작 기능은 이
프로세스를 실행하지 않는다. Ctrl+C로 종료하고 소켓을 정리한다.

```text
Unity 127.0.0.1:19001
  → Networking 127.0.0.1:19002 (검증/ACK/변환)
  → 수신기 127.0.0.1:19003 (현재는 로컬 테스트 목적지)
```

실제 Jetson을 목적지로 선택할 때만 다음처럼 IP를 설정한다.

```powershell
python -B -m backend.Networking --jetson-host 192.168.0.50 --jetson-port 19003
```

Jetson 수신기는 별도로 필요하다. Jetson에서는 PC의 localhost가 자기 자신을
뜻하므로, PC LAN IP와 UDP 19002 송신 출처를 사용해야 한다. 실제 LAN UDP
통과와 PC/Jetson 시계 동기화가 필요하다. Unity는 현재 같은 PC에서만 접속한다.
실제 장비에서 위 실행 명령은 아직 검증하지 않았다.

## 모듈 책임

| 파일 | 기능 |
|---|---|
| `protocol.py` | UTF-8 JSON 계약, 1200바이트 제한, 타입/범위/시간 검사, ACK 형식 |
| `transport.py` | 비차단 UDP 소켓의 바인딩/수신/송신/종료 |
| `receiver.py` | Unity 출처 검사, 세션 및 seq 관리, ACK, 최신 샘플 선택 |
| `processor.py` | 유효 이동값을 좌우 바퀴 목표값으로 변환하고 VR 각도 제한 |
| `sender.py` | 원본 TTL 재확인 후 Jetson 목적지로 UDP 송신 |
| `__main__.py` | 설정과 실행 루프, 최대 송신 빈도 및 소켓 정리 |
| `test_networking.py` | 계약/변환과 실제 localhost UDP 테스트 |

이번 통신은 UDP만 사용한다. TCP 제어 채널과 프레임 규약은 구현하지 않았다.
기존 Mission REST/TCP와 분리되어 있으며 LLM/Agent를 호출하지 않는다.

## Unity 수신 계약

기준 코드는 `Assets/MetaMarine/JetBot_Networking/JetbotTelemetrySender.cs`다.
기존 Python 제안의 평탄한 필드나 hello_ack를 사용하지 않는다.

필수 최상위 필드:
`version=1`, `type=hello/telemetry/goodbye`, `session_id=UUID`,
`source=unity_simulation`, `robot_id=jetbot`, `seq`(1 이상),
`sent_at_unix_ms`, `ttl_ms`(50~2000), `movement`, `gaze`.

`movement` 필드:
`valid`, `drive_enabled`, `safety_stopped`(bool), `move`, `turn`(-1~1),
`forward_mps`, `yaw_rate_dps`, `position_world_m`(x/y/z), `body_yaw_deg`(0~360).

`gaze` 필드:
`valid`, `yaw_valid`(bool), `yaw_deg`(-180~180), `pitch_deg`(-90~90),
`direction_world`, `direction_robot_yaw`(x/y/z).

관측 벡터 및 속도는 형식/유한성 검증에 사용한다. 이번 변환은 기존 Unity의
최종 `move`/`turn`과 VR 각도를 사용하며 raw 영상이나 경로 계획을 처리하지 않는다.

수신 ACK 예시:

```json
{"version":1,"type":"ack","session_id":"Unity 원본 UUID","seq":2}
```

ACK는 새롭고 유효한 hello/telemetry/goodbye에 응답한다. 만료/중복/역순 패킷은
버리고 ACK하지 않는다. ACK의 의미는 Backend 수신 확인이며 Jetson 전달 확인이 아니다.
hello는 Jetson에 보내지 않고, goodbye는 무효 이동/시선 상태로 변환한다.

## Jetson 송신 계약

```json
{
  "version": 1,
  "type": "imitation_state",
  "source": "unity_simulation",
  "robot_id": "jetbot",
  "bridge_id": "Backend 실행 UUID",
  "session_id": "Unity 실행 UUID",
  "seq": 2,
  "expires_unix_ms": 1790000000200,
  "drive_enabled": true,
  "left_normalized": 0.5,
  "right_normalized": 0.3,
  "camera_valid": true,
  "camera_yaw_valid": true,
  "camera_pan_deg": 30.0,
  "camera_tilt_deg": 10.0
}
```

`expires_unix_ms`는 원본 생성 시각+TTL이다. 위 timestamp는 형식 예시일 뿐이다.
Jetson 수신기는 같은 계약을 사용하고 만료 및 중복/역순 seq를 자체 검사해야 한다.
특히 `camera_yaw_valid=false`에서는 pan이 null이며, camera_valid가 true여도
pitch만 유효할 수 있다. 이전에 제시한 Jetson 수신 코드는 이 계약에 맞춰야 한다.

좌우 값은 Unity ApplyDrive와 동일하게 `clamp(move+turn)`/`clamp(move-turn)`이다.
유효하지 않은 주행/비활성/안전 정지/goodbye에서는 둘 다 0이다.
정규화된 모방 목표값이며 실제 PWM/속도 단위가 아니다. VR yaw 양수는 오른쪽,
pitch 양수는 위쪽이다. 기본 각도 제한 ±90/±45도는 예시이고 실제 기구값이 아니다.
`--pan-limit`, `--tilt-limit`로 조절한다.

## 동작과 제한

- 기본 최대 송신 빈도 20Hz, `--send-hz`로 1~60 조절. 수신 샘플이 없으면 송신하지 않는다.
- 대기 샘플 하나만 유지하며 최신 값으로 대체한다. 과거 상태를 재송신하지 않는다.
- 만료한 입력/출력은 버리고 변환으로 TTL을 연장하지 않는다.
- 활성 세션을 다른 세션으로 덮어쓰지 않는다. 기존 데이터가 만료하거나 goodbye를
  받으면 새 세션을 허용한다. 종료/교체된 최근 16개 세션의 지연 패킷을 무시한다.
- Jetson ACK/센서 수집/상태 송신/모터 및 카메라 제어는 없다.
- UDP sendto 성공은 OS 송신 접수다. 목적지 부재 및 전달 실패를 확인할 수 없다.
- 서버 종료/통신 단절에서 물리 정지를 보장하지 않는다. 향후 실제 제어를 붙일 때
  Jetson 쪽 TTL 및 무수신 watchdog이 필요하다. goodbye 자체도 유실될 수 있다.
- 출처 IP/포트 검사는 인증이 아니다. 현재는 신뢰하는 로컬/LAN 테스트용이다.

## 검증

```powershell
python -B -m unittest backend.Networking.test_networking -v
```

2026-10-03 Windows/Python 3.12에서 12개 테스트 통과. 실제 localhost UDP 소켓으로
모의 Unity 송신 → ACK → 실행 루프 → 모의 Jetson 수신과 종료 후 재바인딩을 확인했다.
잘못된 JSON/타입/수명, 크기 초과, 다른 송신 출처, 중복/역순/세션 교체,
무효 주행/안전 정지, 각도 제한/수직 시선을 확인했다.

실제 Unity Play/HMD 데이터 송신, 실제 Jetson/LAN 수신과 장비 동작은 미검증이다.
