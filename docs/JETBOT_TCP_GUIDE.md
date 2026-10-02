# Jetbot 순차 TCP 적용 가이드

2026-10-03. 현재 소스를 재분석한 통신 확장이다. 기존 VR 시선 진단/고정 시선 반복 송신,
주행·시선 독립 처리, 이동 변환식과 UDP 로그를 보존했다. 주행/Navigation/앵커/씬/LLM 서버는 수정하지 않았다.

## 실행

```text
Unity (기존 JetbotTelemetrySender, 순차 송신 큐)
 → TCP 127.0.0.1:19101 Backend Networking
 → 기존 이동/시선 검증·변환
 → TCP <Jetson LAN IP>:19102
 → Jetson 수신 큐 → 각 duration_ms 동안 목표 재생
```

1. 기존 UDP 프로세스를 종료한다. backend/Networking/jetson_tcp_receiver.py 전체를
   Jetson 작업 폴더의 jetson_tcp_receiver.py로 복사한다. 기존 jetson_receiver.py는 보존한다.

   ```bash
   python jetson_tcp_receiver.py --backend-host "<PC LAN IP>" --port 19102
   ```

2. PC 저장소 루트에서 실행한다. Python 표준 라이브러리만 필요하다.

   ```powershell
   python -B -m backend.Networking --transport tcp --jetson-host "<Jetson LAN IP>" --jetson-port 19102
   ```

   Backend listen은 기본 127.0.0.1:19101이며 Jetson 연결은 LAN 인터페이스를 사용한다.
   Jetson의 LAN TCP 19102 접속을 허용한다. TCP 클라이언트 출발 포트는 OS가 할당한다.
   과거 UDP의 19001/19002 출처 포트 검사는 적용하지 않는다. 기존 LLM 서버는 별도 프로세스다.

3. Unity 컴파일 후 기존 jetbot 컴포넌트에서 Use Tcp=true, Tcp Backend Port=19101,
   Send Hz=20을 확인한다. Drive/Body/Vr View/Head Camera 참조를 유지한다. 씬을 변경하지 않았다.
   설정 변경 후 새 Play로 시작한다. TCP에서는 기존 Local Port/Backend Port/Ttl Ms를 사용하지 않는다.

4. Backend 수신/송신 seq와 Jetson 수신/재생 완료 seq가 연속 증가하는지 확인한다.
   정지 상태의 유효한 동일 0도 시선도 계속 전달되며, 추적 없음은 null이다.

## 계약과 실행 원리

- 4바이트 big-endian JSON 바이트 길이 + UTF-8 JSON. TCP_NODELAY를 사용한다.
- Unity 기존 version=1/hello/telemetry/goodbye, movement/gaze에 transport=tcp,
  duration_ms를 추가한다. 20Hz면 telemetry=50ms, hello/goodbye=0ms. hello seq=1 뒤 2부터 연속이다.
- 기존 TTL 필드는 UDP 호환용으로 남지만 TCP는 UTC/TTL로 명령을 폐기하지 않는다.
- Jetson 출력은 version=2/type=control이다. 기존 좌우/시선 필드는 유지하고
  expires_unix_ms를 제거한다. duration_ms, end_session, source_sent_at_unix_ms를 추가한다.
- goodbye는 duration=0/end_session=true/이동·시선 무효인 종료 레코드다.
- Backend는 모든 수집 샘플을 순서대로 sendall하고 ACK한다. 별도 20Hz 다운샘플링/최신값 덮어쓰기가 없다.
  ACK는 Jetson으로의 TCP 송신 접수 확인이며 실행 완료 확인이 아니다. Jetson은 ACK/상태/센서를 송신하지 않는다.
- Jetson은 수신 스레드와 재생 루프를 분리한다. 작은 큐가 가득 차면 기다려 TCP 역압력을 전달한다.
  각 명령을 monotonic 기준 적용 시간 전체 동안 재생한다. 다음 명령이 없으면 정지 목표로 전환하고 대기한다.
  몰려온 명령을 덮어쓰거나 지연을 따라잡기 위해 적용 시간을 압축하지 않는다. 정상 EOF도 큐를 재생한다.
- Jetson apply_target()은 현재 목표값 출력만 한다. 실제 모터·서보·센서 및 행동 모방은 보류한다.
  추후 실제 제어를 연결할 곳은 이 함수이며 None 입력은 정지/대기/종료 목표다.

## 보존과 제한

Capture/CaptureGaze/IntoRobotYaw/CalculateAngles, 기존 LaneFollowerController/NavigationCoordinator,
씬/VR 앵커/LLM·REST 서버는 수정하지 않았다. 기존 좌우 clamp(move+turn)/clamp(move-turn),
VR 추적 검사, 무효 시선/null, 각도 제한, UDP 로그/테스트를 재사용한다.
decode_unity 기본 TTL 정책은 유지하며 TCP만 check_freshness=false를 사용한다.

Unity LateUpdate 수집은 화면 FPS의 영향을 받는다. 이번 작업은 이미 수집한 20Hz 샘플을
순서대로 재생하는 통신 변경이다. 모든 FixedUpdate 제어를 기록하거나 주행을 20Hz로 고정하지 않았다.
20Hz보다 낮은 FPS에서 수집하지 못한 과거 제어를 만들어 채우지 않는다. 실제 송신 빈도와
샘플 사이 제어 변화까지 포함한 위치·회전 일치는 별도 검증이 필요하다.
일반 OS 스케줄링 때문에 재생 시간이 조금 늘 수 있으며 하드 실시간 50ms 보장은 아니다.

처음 성립하지 않은 연결만 재시도한다. 유지 중인 연결의 TCP 재전송 지연은 수용하지만,
연결 리셋/프로세스 재시작 후 내구성·중복 없는 재개는 미구현이다. 순번/JSON 오류는 한 명령을
건너뛰는 대신 세션을 중단한다. Unity 송신 큐는 RAM에 보존하므로 장기 지연 시 메모리가 증가한다.
연결된 Unity 작업자는 Disable 시 큐/goodbye를 순차 송신한다. 최초 연결 전 Disable은 대기를 끝낸다.
앱 종료 시 미전달 RAM 큐까지 전달됐다고 보장하지 않는다. 오래된 명령도 재생하는 정책은 Emergency Stop과 별개다.

기존 UDP 검증은 Unity Use Tcp=false 및 Backend --transport udp를 함께 선택한다.
과거 19001/19002/19003 포트와 TTL/최신값 정책은 그대로다.

## 수행한 검증

```powershell
python -B -m unittest backend.Networking.test_networking backend.Networking.test_tcp_networking -v
```

- UDP 13개 + TCP 11개, 총 24개 테스트 통과.
- 실제 localhost TCP 분할/연속 프레임, 순서, 오래된 timestamp, 작은 큐 역압력,
  지연 시 정지·재개, 고정 시선/null 변환과 정상 종료를 확인했다.
- 모의 Unity→실제 TCP Backend→Jetson 재생 코드에서 샘플 6개를 50ms씩 재생,
  누적 300ms와 종료 레코드를 확인했다. 실제 장비를 사용한 테스트는 아니다.
- Unity 실제 참조 DLL을 사용한 변경 C# 컴파일 통과.
- 실제 C# TCP 작업자의 ACK를 지연시켜도 12개 프레임/ACK가 순서를 유지함을 확인했다.
- 실제 Unity Play/HMD/LAN/Jetson 설치·실행 및 물리 장비 동작은 이번 작업에서 미검증이다.
