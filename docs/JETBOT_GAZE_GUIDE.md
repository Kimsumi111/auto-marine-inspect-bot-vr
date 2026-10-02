# VR 시선 주기 송신과 Jetson 확인 가이드

2026-10-03. Unity 변경을 적용했다. 실제 Jetson 저장소에 접근하지 않았으며,
아래 Jetson 코드는 사용자가 수신기에 직접 반영하는 가이드다. 실제 행동 모방은
수신 확인과 별개이며 이번 작업에 포함하지 않는다.

## Unity 적용 결과

`Assets/MetaMarine/JetBot_Networking/JetbotTelemetrySender.cs`의 시선 처리를
`CaptureGaze()`로 분리하고 Inspector의 `Gaze Status` 진단을 추가했다.

- 정상 VR 회전 추적 중에는 머리를 고정해도 매 송신 주기마다 현재 방향을 읽는다.
- 동일 yaw/pitch도 새 seq로 보내며, 각도 변화량으로 송신을 제한하지 않는다.
- 주행 활성 상태와 시선 유효성은 독립적이다. 로봇이 정지해도 VR 시선은 보낸다.
- 추적 손실/카메라 비활성/앵커 불일치/일시정지는 시선을 무효로 기록한다.
  이전 유효 방향을 현재 값으로 재사용하지 않는다.
- 정면의 유효한 0도와 추적이 없는 무효 상태는 서로 다르다.
- 수직 시선은 pitch가 유효해도 yaw를 확정할 수 없으므로 별도 플래그를 사용한다.

기존 UDP JSON 필드, 포트, sendHz, TTL은 유지한다. 머리 각도가 같아도 보내는
주기 송신 자체는 기존 코드에 존재했으며, 이번 변경은 수집 책임과 진단을 명확히 한다.
TCP 기반 제어 기록 전송은 별도의 제안이고 이번 변경에서 구현하지 않았다.

## Unity에서 확인할 설정

1. 실행 중인 jetbot의 `JetbotTelemetrySender` Inspector를 연다.
2. `Vr View`는 로봇 기준 `PcVrView`, `Head Camera`는 그 컴포넌트의 머리 카메라다.
3. HMD를 연결하고 XR 표시/회전 추적이 활성화된 상태로 Play한다.
4. 실행 중 `PcVrView`의 `RobotVrAnchor.robot`은 해당 jetbot Transform,
   `head`는 Head Camera Transform이어야 한다. 기존 PcVrView가 Awake에서 연결한다.
5. 머리를 고정하고 `Gaze Status`가 아래 정상 문자열인지 확인한다.

```text
현재 VR 시선 유효 / 동일 각도도 주기 송신
```

추적이 없으면 `HMD 장치 없음`, `HMD 추적 없음`, `HMD 회전 추적 없음`을 표시한다.
VR 표시가 꺼져 카메라가 비활성인 경우에는 카메라 연결/활성 상태 메시지가 먼저
표시될 수 있다. 진단 문자열은 검사를 처음 통과하지 못한 조건을 나타낸다.

`Send Hz=20`은 목표 최대 빈도이며 실제 Unity 프레임 속도에 영향을 받는다.
Time.timeScale=0 또는 앱 일시정지에서는 기존대로 시선을 무효 처리한다.

## Backend 확인

현재 Networking의 수신/전송 로그에서 아래 값을 확인한다.

```text
(Unity 데이터 수신) ... seq=101 ... gaze_valid=True yaw_valid=True yaw=+0.00 pitch=+0.00
(명령 데이터 전송) ... seq=101 ... camera_valid=True yaw_valid=True pan=0.0 tilt=0.0
(Unity 데이터 수신) ... seq=102 ... gaze_valid=True yaw_valid=True yaw=+0.00 pitch=+0.00
(명령 데이터 전송) ... seq=102 ... camera_valid=True yaw_valid=True pan=0.0 tilt=0.0
```

같은 각도에서 seq가 증가하면 고정 시선 주기 전송을 확인한 것이다.
Backend의 `processor.py`는 yaw_valid가 false일 때 pan=null,
camera_valid가 false일 때 tilt=null로 변환한다. 따라서 Backend 처리 코드나
프로토콜을 추가 변경할 필요가 없다. 기본 각도 제한은 pan ±90/pitch ±45도다.

## 실제 Jetson 수신기 수정 가이드

기존 제안의 Receiver.decode/accept를 사용하는 경우 시선 계약은 이미 호환된다.
각도 변화 비교를 추가하지 말고 새 seq마다 수신한다. 모터/카메라 제어는 연결하지 않는다.

`jetson_receiver.py`에 `import logging`이 있는지 확인한다. `Receiver.accept()`에서
검증과 seq 확인이 끝나고 `self.accepted += 1`을 실행한 다음, `return True` 전에
기존 성공 로그를 아래로 교체한다. 이미 성공 로그가 있으면 중복 추가하지 않는다.

```python
logging.info(
    "(시선 데이터 수신) (검증 성공) "
    "seq=%d camera_valid=%s yaw_valid=%s pan=%s tilt=%s 잔여유효시간=%dms",
    packet["seq"],
    packet["camera_valid"],
    packet["camera_yaw_valid"],
    packet["camera_pan_deg"],
    packet["camera_tilt_deg"],
    packet["expires_unix_ms"] - unix_ms(),
)
```

INFO가 출력되도록 기존 로깅 초기화를 사용한다. 아직 로깅 설정이 없다면 main에서
run 호출 전에 최소한 다음을 추가한다. 이미 시간 포맷터를 설정했다면 기존 설정을 유지한다.

```python
logging.basicConfig(level=logging.INFO, format="%(message)s")
```

올바른 유효성 처리:

```python
# 숫자 0은 유효한 정면 방향이다. bool(pan)으로 유효성을 검사하지 않는다.
if packet["camera_valid"]:
    pitch = packet["camera_tilt_deg"]
    if packet["camera_yaw_valid"]:
        yaw = packet["camera_pan_deg"]
    else:
        yaw = None  # 수직 시선: yaw를 임의의 0도로 바꾸지 않는다.
else:
    yaw = pitch = None  # 마지막 유효 각도를 현재 추적으로 오인하지 않는다.
```

위 코드는 값 해석 예시이고 실제 카메라를 제어하지 않는다. 현재 Receiver가
만료 시 current=None으로 만드는 처리는 유지한다. 반복 만료 문제는 이번 시선
수집 변경으로 해결되는 문제가 아니다.

## 실제 확인 순서와 성공 기준

1. PC와 Jetson 시계를 동기화한다.
2. Jetson에서 수신기를 시작한다. backend-host는 호스트 PC의 LAN IP다.
3. PC에서 Networking을 `--bind-host 0.0.0.0 --jetson-host <Jetson IP>`로 시작한다.
4. HMD 연결 후 Unity Play를 시작한다.
5. 로봇을 정지한 채 머리를 고정한다. 유효한 동일 pan/tilt와 증가하는 seq가
   Backend와 Jetson에서 연속 출력되는지 확인한다.
6. 머리를 좌우/위아래로 움직여 각도 변화와 부호(오른쪽/위쪽 양수)를 확인한다.
7. HMD 추적을 끊는다. Unity 진단이 무효 사유로 바뀌고 Jetson에는
   camera_valid=false, pan=null, tilt=null이 도착하는지 확인한다.

## 이번 작업에서 수행한 검증

- Unity 6000.5.7f1 실제 참조 DLL을 사용한 해당 C# 소스 컴파일 성공(오류/경고 없음).
- Networking unittest 13개 통과. 실제 localhost UDP로 정지 상태의 동일한 유효
  0도 시선이 새 seq로 두 번 전달되고, 다음 추적 손실 샘플이 null 각도로
  변환되는 것을 확인했다. Unity/HMD 송신 대신 모의 입력 패킷을 사용했다.
- HMD 실연결/Unity Play/실제 Jetson 수신은 이번 작업에서 실행하지 않았다.
  이전 사용자 로그의 실제 LAN 수신 성공과 이번 코드 검증을 구분한다.
