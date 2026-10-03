# 현재 인터페이스 계약

2026-10-03 Jetbot TCP 순차 통신 추가. 기존 Mission TCP/REST는 변경하지 않았다.
Unity→Networking TCP 127.0.0.1:19101, Networking→Jetson TCP 19102. 길이 4바이트(big-endian)+UTF-8 JSON.
Unity 기존 입력에 transport=tcp/duration_ms를 추가하고 Jetson은 version=2/type=control,
duration_ms/end_session으로 순차 목표 재생한다. TTL 만료/최신값 덮어쓰기는 TCP에 적용하지 않는다.
Backend ACK는 송신 접수 확인이고 Jetson 실행 완료 확인이 아니다. Jetson 상태/센서 송신 및 실제 제어는 없다.
기존 VR 시선 계약/진단과 UDP 모드는 보존한다. 24개 테스트와 C# 검증 통과, 실제 장치 E2E는 미검증이다.
실행/계약/연결 복구·샘플링 제한은 [JETBOT_TCP_GUIDE.md](JETBOT_TCP_GUIDE.md)를 따른다.
아래 VR UDP 문단은 이전 검증 이력이다.

2026-10-03 VR 시선 진단: JetbotTelemetrySender는 유효한 동일 각도도 주기 송신하고 Gaze Status로 무효 사유를 표시한다. 기존 UDP gaze.valid/yaw_valid/yaw_deg/pitch_deg 계약은 유지하며, Backend는 camera_valid/camera_yaw_valid/pan/tilt로 변환한다. 유효한 0도와 무효 null, 수직 시선의 pitch-only를 구분한다. Unity 참조 컴파일과 Networking 13개 테스트 통과; 실제 HMD/Unity Play/Jetson E2E는 이번 변경에서 미검증. 실제 젯봇 수정은 [JETBOT_GAZE_GUIDE.md](JETBOT_GAZE_GUIDE.md)의 수신 전용 가이드로 제공한다.

2026-10-02 우측 하단 회전 조정: UnderMid(6)→UnderRight(5)→UpperRight(2) 경로 override의 searchTurnCommand를 0.20→0.24(20% 증대)로 변경했다. 씬 생성 도구도 junction=UnderRight 항목에 0.24를 적용한다. 이 값은 회전 명령 강도 상한이며 고정 회전각 증가가 아니다; confidence/새 영상 3개 전환과 전진 명령·계획 yaw·탐색 제한은 기존대로다. 실제 회전/재획득 효과는 새 Play에서 미검증.

2026-10-02 사용자 요청으로 출구 전환 단순화: SearchingExitLane에서 LaneCentre 관측이 LaneFollower.TryGetLaneDetection의 effective confidence/age 기준(기본 confidence 0.30/age 0.20초)을 새 영상 3개(requiredPairFrames)에서 만족하면 바로 FollowingLane으로 전환한다. 별도 pair confidence gate, 경계 파란선 정렬, near/far 각도 계산, 횡오차·계획 yaw 완료 gate는 전환에 요구하지 않는다. 중앙 관측을 유지해 reference 전환으로 생기는 confidence 초기화가 없다. 남은 횡오차/방향 보정은 기존 LaneFollower PD 제어에 맡긴다. 회전 탐색 전진 상수 0.40과 기존 탐색 이동/회전 한도는 유지한다. VisualAlign 상태/설정/핸들러는 호환을 위해 남았으나 현재 출구 탐색 흐름에서 진입하지 않는다. CSV/Console에 confidence confirmed; boundary alignment skipped 사유 및 중앙 confidence 연속 확인 수/영상 age를 기록한다. 기존 새 프레임/제어/로그 회귀 검증과 Unity 참조 컴파일 통과; 변경 후 실제 Play 전환/주행은 미검증.

2026-10-02 Align 역방향 회전 보완: 실제 로그에서 near 단일 관측 횡오차 보정이 yaw 298→345도로 출구 yaw -82(278도)에서 멀어지는 것을 확인했다. 단일 띠 모드는 횡오차 기반 조향을 제거하고 계획 출구 yaw 오차만으로 최대 0.20/4도 deadband 방향 보정 및 기존 제한 저속 전진을 사용한다. 양쪽 띠 정렬에서도 출구 yaw 오차 15도 밖에서는 출구 방향 보정 우선, 허용 범위 가장자리(12도 이상)에서 출구를 더 벗어나는 영상 조향은 차단한다. 단일/양쪽 정렬 완료는 기존 조건에 출구 yaw 오차 ≤15도 확인을 추가했다. 기존 이동/시간 제한 및 Search 복귀 없음 유지. 로그 상세에 출구 yaw 오차/허용각과 영상 원래 조향을 기록한다. 방향 대칭·deadband·반대 영상 조향 제한 회귀 검증과 Unity 참조 컴파일 통과; 실제 Play 효과는 미검증.

2026-10-02 주행 로그 분석/단일 관측 정렬 전환 수정: 실제 Play CSV에서 LaneCentre→RightBoundary 변경 때 횡오차 -0.16→0.72, 이후 near=True/far=False로 횡오차 0.61인 상태에서 약 0.9초 뒤 partial observation handoff, 이어 중앙 차선 소실 및 StraightToNextMarker를 확인했다. 단일 관측은 이제 실패 횟수로 FollowingLane에 넘기지 않는다. usable/age와 nearObserved, 최소 정렬 변위 0.05m, |횡오차|≤0.08을 새 영상 3개에서 확인해야 partial lateral alignment confirmed 사유로 전환하며 각도 미확인은 명시한다. 단일 띠 전진 0.10은 정렬 시작점 변위 0.20m까지, 이후 전진 0/최대 조향 0.20으로 계속 보정한다. 단일 관측 모드 시간 제한은 6초이며 정상 양쪽 관측 회복 시 초기화한다. 시간 초과/관측 소실은 상세 fault로 정지, Search로 복귀하지 않는다. 기존 회귀 검증과 Unity 참조 컴파일 통과; 변경 후 실제 주행은 미검증.

2026-10-02 Align 조향 강화 및 주행 CSV 로그: visualHeadingGain 0.42→0.70, visualLateralGain 0.50→0.80, maximumVisualTurn 0.22→0.40; 단일 띠 회복 최대 조향 0.10→0.20. 전진 상수 0.40 유지. Play별 runtime/navigation/navigation-UTC-고유ID.csv를 생성한다(Editor는 저장소 루트, 빌드는 Application.persistentDataPath 아래). 주행은 0.10초 간격, Idle/Fault/Completed는 1초 간격, 상태 전환·fault·disable은 별도 행으로 즉시 flush. UTC/시뮬레이션 시간, 경로/노드/목표, 위치/yaw/방향오차, reference, near/far/각도 유효성, θ/횡오차/confidence/pair, 영상 timestamp/age, 수동 명령과 실제 적용 명령, drive/safety/avoidance 상태, 탐색/정렬 변위, 실패 영상 수/정렬 확인 수, 조향 파라미터와 상세 이유를 기록한다. source=unity_simulation; 수동 요청 값은 manual_control=true일 때만 유효하며 적용 명령은 최근 FixedUpdate 결과이므로 전환 행과 한 물리 tick 차이가 날 수 있다. 파일 I/O 실패는 경고하고 로그만 비활성화한다. 파일은 Git 제외, Console에 저장 위치 출력. CSV escaping/실행 중 flush·파일 종료·조향 강화 회귀 검증 및 Unity 참조 컴파일 통과. 실제 Play 주행 로그 생성과 조향 효과는 미검증.

2026-10-02 사용자 정정: VisualAlign→SearchingExitLane 복귀를 제거했다. 정상 정렬 완료는 FinishAlignment로 FollowingLane(LaneCentre)에 전환한다. 단일 띠 관측 손실 한도 도달 시 관측이 여전히 confidence/age 기준을 만족하면 partial observation handoff 사유를 기록하고 동일하게 FollowingLane으로 넘긴다(정렬 성공으로 표시하지 않음). 영상 timeout 또는 유효 관측 없음은 정지·명시적 fault로 처리한다. 불필요해진 최대 재탐색 횟수 설정을 제거했다. 중앙 기준 변경 시 이전 confidence를 무효화하여 새 영상을 기다리는 기존 동작 유지. Unity 참조 컴파일 및 기존 제어 회귀 검증 통과; 실제 Play 흐름은 미검증.

2026-10-02 정렬 관측 회복 보완: Detection에 nearObserved/farObserved를 추가하고 미검출 띠의 십자는 숨긴다(제어용 단일 띠 fallback 좌표와 구분). VisualAlign 관측 실패는 새 timestamp당 한 번만 집계하며 정상 양쪽 관측에서 초기화한다. 영상이 0.80초 동안 갱신되지 않으면 정지·제한 재탐색한다. fresh/age/confidence 기준을 만족하는 단일 띠 관측은 θ를 사용하지 않고 횡오차만으로 전진 0.10/최대 조향 0.10으로 회복을 시도한다. 허용 변위는 정렬 시작점 기준 0.20m(누적 경로 길이 아님)이며 이후 정지한다. 양쪽 관측 회복 시 기존 상수 전진 0.40 정렬로 복귀한다. near/far/각도 유효성·fresh 실패 수·timeout을 표시/실패 로그에 기록한다. 새 프레임 집계 회귀 검증 및 Unity 참조 컴파일 통과; 실제 Play 회복 효과는 미검증. 동일 물리 선의 near/far 대응 관계는 아직 보장하지 않는다.

2026-10-02 사용자 요청으로 회전 전진을 상수화: SearchingExitLane exitSearchMoveCommand=0.40, VisualAlign boundaryAlignMoveCommand=0.40. 정렬 전진의 cos 계산과 minimumBoundaryAlignMove/maximumBoundaryAlignMove 설정을 제거했다. 조향은 기존 sin(θ)+횡오차 보정을 유지한다. Inspector·씬 생성 도구·jetbot_env 값을 맞췄다. 탐색 전진 변위 0.60m 제한, 정렬 이동 1.50m 제한, 관측 확인/미관측 정지는 유지한다. 상수 전진 제어 검증과 Unity 참조 컴파일 통과; 실제 Play 주행은 미검증.

2026-10-02 사용자 요청으로 회전 중 전진 성분을 증대: exitSearchMoveCommand 0.05→0.20(4배), 경계 정렬 minimumBoundaryAlignMove 0.08→0.24(3배), maximumBoundaryAlignMove 0.20→0.40(2배). VisualAlign 전진은 0.24+0.16·clamp01(cos(θ)). 조향과 초반 직진 approachCommand는 유지한다. 탐색 전진 변위 제한 0.60m 이후 제자리 회전 및 정렬 이동 제한 1.50m, 관측 확인/미관측 정지는 유지한다. 코드 기본값·jetbot_env·씬 생성 설정 일치 확인. 실제 Play 주행 효과는 미검증.

2026-10-02 경계 정렬 전진을 cos 방식으로 변경: 조향은 기존 clamp(0.42·sin(θ)+0.50·횡오차, ±0.22)를 유지한다. 전진은 0.08+(0.20-0.08)·clamp01(cos(θ))이며 정렬 시 최대 0.20, 90°에서 최소 0.08이다. VisualAlign의 기존 기본/경로별 visualAlignMoveCommand 및 boundaryForwardTurnGain 설정은 제거하고 minimumBoundaryAlignMove/maximumBoundaryAlignMove로 조절한다. 정렬 완료 확인 중 정지·선 미관측 시 정지·기존 재탐색 제한은 유지한다. 제어 회귀 검증 및 Unity 참조 컴파일 통과, 실제 Play 주행은 미검증.

2026-10-02 HSV 잡음 필터: ROI/HSV 마스크 생성 직후 8방향 연결 영역을 분석한다. 처리 영상 기준 minimumComponentArea=24픽셀 미만은 제거하되, 4픽셀 이상이고 minimumPreservedLineLength=12픽셀 이상·주축 분산 기준 길쭉함 minimumPreservedLineAspectRatio=3 이상인 선은 보존한다(대각선 포함). removeSmallMaskComponents로 비활성화할 수 있으며 Inspector에서 기준을 조절한다. 모든 차선/경계 관측과 HSV 디버그 녹색 마스크는 필터 결과를 사용하며 removed 픽셀 수를 표시한다. 기존 ROI는 유지한다. 큰 시설 잡음 또는 차선에 붙은 잡음은 이 필터로 분리되지 않는다. 마스크 회귀 검증과 Unity 참조 컴파일 통과; 실제 Play 영상/주행은 미검증.

2026-10-02 경계 정렬 제어 변경: 선택 경계의 near/far 관측으로 파란 수직 기준선 대비 θ=atan2(x_far-x_near, y_far-y_near)를 측정한다. VisualAlign 조향은 clamp(0.42·sin(θ)+0.50·횡오차, ±0.22), 전진은 clamp(기본 0.11+0.35·|조향|, 0.08, 0.20)이다. 좌회전은 오른쪽 경계, 우회전은 왼쪽 경계를 선택하며 near/far 중 하나라도 없으면 정지·기존 재탐색 제한을 적용한다. 최소 이동 0.05m 이후 횡오차 ≤0.08 및 |θ|≤8°를 새 프레임 2개에서 확인하면 기존 중앙 추종으로 복귀한다. 로그에 θ(도), 횡오차, 전진/조향 명령을 표시한다. 순수 제어 회귀 검증과 Unity 참조 컴파일 통과; 실제 Unity Play 주행은 미검증이다.

2026-10-02 최신 추종 규칙: FollowingLane은 LaneCentre로 복구. SearchingExitLane → VisualAlign(좌회전 RightBoundary/우회전 LeftBoundary) → FollowingLane(LaneCentre). VisualAlign에만 경계 기준 수동 제어를 사용하고 완료 시 중앙 기준 새 영상을 기다린다. Inspector의 boundaryAlignmentLateralTolerance=0.08, boundaryAlignmentAngleTolerance=8°로 정렬 완료를 판단한다. 탐색/정렬 confidence·관측 age·새 프레임/이동 제한 유지, API/TCP 변경 없음.

2026-10-02 FollowingLane 영상 기준 변경: LaneFollower TrackingReference로 차선 중앙 또는 좌/우 경계를 선택한다. jetbot_env는 오른쪽 경계 기준. 파란 기준선은 화면 중앙에 고정하며 선택 경계의 횡오차·방향오차를 조향에 반영한다. 회전 탐색 SetManualCommand는 LaneCentre로 되돌린다. HSV 디버그 표시의 Lane [reference]와 십자 표시로 실제 추종 대상을 확인한다. 선택은 이미지 내 최외곽 run이며 마커 기반 실제 선 ID 추적은 아니다. API/TCP와 진단 계약은 유지한다.

2026-10-02 최신 출구 탐색 전환: 계획 방향 도달 gate를 제거했다. 회전 중 fresh 차선 쌍 및 LaneFollower.HasUsableLane을 새 프레임 3개에서 확인하면 바로 FollowingLane로 전환한다. 실제 tracking confidence 기준(기본 0.30)을 함께 만족해야 하므로 pair 기준 0.10만으로 전환하지 않는다. 기존 VisualAlign은 이 경로에서 사용하지 않는다.

2026-10-02 출구 탐색 동작: 회전 중 exitSearchMoveCommand=0.05 전진을 병행한다. 탐색 시작점과의 평면 거리 maximumExitSearchAdvanceDistance=0.60m에 도달하면 회전 중 전진 명령은 0이다. Inspector에서 조절 가능하며 거리는 경로 누적 길이가 아니다. 계획 방향 도달 후 기존 전진 probe·차선 확인·정렬은 유지한다. API/TCP 계약 변경 없음.

2026-10-02 주행 내부 전환 변경: 출구 차선은 같은 timestamp를 반복 집계하지 않고 새 관측 3개가 연속 유효해야 정렬에 진입한다(jetbot_env/생성 도구 설정). 확인 중 정지, 정렬 완료도 새 관측으로 확인한다. 전환 로그를 추가했으며 TCP/API 필드와 완료·정지 계약은 유지한다. 회귀 테스트·참조 컴파일 통과; 실제 주행은 미검증.

2026-10-02 로컬 TCP override: 루트 `.unity-tcp-port` 숫자 한 줄로 Agent용 Unity bridge와 Backend의 TCP 포트를 맞춘다. 없으면 8765, Backend 환경변수 MARINE_UNITY_PORT가 있으면 우선한다. HTTP 8767은 그대로다. Git 제외·머신별 설정이며 WPF 포트도 동일하게 설정해야 한다. health에 unity_port를 추가했다.

2026-10-02: Play 중 reload 후 활성 TCP bridge를 복원한다. 닫힌 서버 참조는 새 서버로 교체하며 `Tools/Ship Robot/Check and Restore Equipment TCP`에서 수동 복원·진단한다. 서버 수신 프로토콜과 주행은 변경하지 않는다.

2026-10-02: Editor에서 Play 종료·어셈블리 reload·종료 전 EquipmentTcpServer.DisposeAll로 TCP 리스너를 정리한다. 프로토콜·포트·주행 동작은 그대로다. 기존 어셈블리에 잔류한 리스너는 Editor 재시작이 필요할 수 있다. 실제 소켓 재바인딩 테스트와 Editor 컴파일만 검증했다.

2026-10-02: `/mission/{id}/events`에 LLM 호출 생명주기·응답 메타데이터와 Tool 실행 결과 이벤트를 추가했다. DB 옆 `agent-events.jsonl`로도 저장하며 로그 규칙은 Backend README 참조. Navigation 출구 차선 탐색 실패 detail은 경로·방향·탐색 지표를 포함한다. 실패 제한값과 성공 기준은 변경하지 않았다.

2026-10-02: Editor의 `BackendAutoStart`가 점검 씬 Play 진입 시 통합 Backend(8767)를 숨김 실행한다. 기존 health 정상 서버는 재사용하며 Play 종료 후 서버를 유지한다. 최초 환경 설치가 필요하다. 메뉴와 환경 선택은 `backend/README.md` 참조. C# 참조 컴파일과 서버 단독 기동만 검증했으며 Play 자동 시작은 아직 미검증이다.

기준일: 2026-10-02. 아래는 코드에 존재하는 계약이다. 향후 Agent 계약은 `AGENT_PLAN.md`에 별도로 기록한다.
경로는 저장소 루트 기준이다.

## VR Backend REST 클라이언트 (2026-10-02 추가)

확정 요청·응답 형식은 [VR_BACKEND_API_V1.md](VR_BACKEND_API_V1.md)를 따른다.
`MissionContract`, `MissionApiClient`, `MissionPanel`이 텍스트/음성 확인 후 접수, 상태 조회, 취소, 결과 표시를 구현한다.
`PcVrView`에서 자동 설치한다. 실제 Backend 8767은 `backend/main.py`로 구현했다. OpenAI/LangGraph 자연어 A/B/A+B 목표, 실행·조회·취소, SQLite 복원 및 지점별 CSV 진단·재시도를 지원한다. 실행 방법은 `backend/README.md`를 따른다.
`tools/mission_mock/server.py`는 별도 8877 포트의 UI 테스트용 서버이며 Unity TCP/Agent/진단을 실행하지 않는다.
음성 `TranscriptReady`는 입력창만 갱신한다. 사용자가 전송해야 임무를 접수한다.
이전 요청 ID와 명령문은 복구 조회를 위해 로컬 PlayerPrefs에 임시 보존하며 완료 결과 확인 후 제거한다.

기본 Agent 모드는 자연어를 해석해 A 단독/B 단독/A+B를 실행한다. 고정 문구는 명시적 fixed_ab 시험 모드에서만 사용한다. Unity ACK와 완료 이벤트를 구분하고, 주행 중 취소는 같은 세션의 정지 ACK + 새 Idle/canStart=false telemetry로 확인한다. Backend 재시작/연결 끊김은 자동 재출발하지 않고 미확인 상태를 보존한다. Unity Completed와 선택한 설비의 모든 지점 CSV 진단 SUCCEEDED가 모두 확인되면 전체 COMPLETED이다. 진단 오류는 FAILED/미판정이며 모의 확률로 대체하지 않는다. Unity 종료 확인 후에는 오프라인 진단을 계속할 수 있으며, 그 상태의 취소는 남은 진단만 중단한다.

## Unity TCP v1

근거: `Assets/EquipmentMonitoring/EquipmentWireProtocol.cs`, `EquipmentNetworkBridge.cs`, `EquipmentTcpServer.cs`, `RobotDashboardIntegration.cs`.

- `127.0.0.1:8765`, TCP, UTF-8 JSON 한 줄 + LF, 동시 클라이언트 1개.
- 수신 chunk와 메시지 경계는 다르다. LF까지 버퍼링한다.
- telemetry는 unscaled 시간 기준 약 10Hz. 느린 수신자에게 중간 프레임이 생략될 수 있다.
- 기존 WPF와 새 Backend를 동시에 직접 연결하는 구조는 현재 지원하지 않는다.

요청 예시:

```json
{"version":1,"type":"command","commandId":"request-001","equipmentId":"robot","action":"mission_start"}
```

응답 예시:

```json
{"version":1,"type":"commandResult","commandId":"request-001","ok":true,"code":"ok","message":"Command applied"}
```

| equipmentId | action | 의미 |
|---|---|---|
| `robot` | `mission_start` | 기존 A+B 점검 임무 시작 |
| `robot` | `mission_start_a` | A1/A2 점검 (왼쪽 순환) |
| `robot` | `mission_start_b` | B2/B1 점검 (오른쪽 순환) |
| `robot` | `mission_stop` | 회피 중지와 임무 리셋·정지 |
| `A` / `B` | `pause`, `resume`, `restart`, `stop`, `normal`, `fault` | CSV 재생 제어 |

위 표의 로봇 임무는 Unity 시뮬레이터에 대한 것이다. 현재 TCP에는 `move_robot("pump_A")`나 센서 실측 명령이 없다.
ACK는 명령 적용 응답이며 이동·점검 완료 증거가 아니다.

commandId는 공백이 아닌 1~80자. 연결별 최근 128개에서 같은 ID와 완전히 같은 JSON 문자열은 재실행하지 않고 응답을 반환한다. 같은 ID에 다른 문자열은 `id_conflict`이다. 재연결 후 중복 방지는 보장되지 않으며 미확인 이동 명령을 자동 재전송하지 않는다.
명령 한 줄은 최대 4096자이고 큐 제한이 있다. 세부 제한은 `Assets/EquipmentMonitoring/NETWORK.md`를 참조한다.

telemetry 최상위 필드: `version`, `type=telemetry`, `sequence`, `sentAtUtc`, `equipment`, `mission`.
`mission`은 연결되지 않은 씬에서는 null일 수 있다.

| mission 필드 | 의미 |
|---|---|
| `sessionId` | Unity Play 세션 식별자 |
| `state`, `detail` | NavigationCoordinator 상태와 설명 |
| `canStart` | 아직 시작하지 않았고 Idle인지 |
| `inspections` | 세션에 누적된 점검 완료 기록 |

점검 기록 필드: `id`, `point`, `equipmentId`, `completedAtUtc`, `sourceEquipmentId`, `filePath`, `error`.
`filePath`는 같은 PC의 재생 CSV 경로이며 파일 전송 기능이 아니다. 빈 경로·error는 진단 실패로 처리한다.
점검 id로 중복 처리하고 sessionId 변경 시 이전 상태를 분리한다.

Navigation 상태는 `Idle`, `FollowingLane`, `ConfirmingNode`, `ApproachingTurnCenter`, `SearchingExitLane`, `VisualAlign`, `StraightThroughJunction`, `StraightToNextMarker`, `InspectingEquipment`, `Completed`, `Fault`이다.
Agent의 향후 임무 상태와 동일한 enum으로 취급하지 않는다.

한 Play 세션에서 임무 시작은 1회이다. 정지 후 다시 시작하려면 새 Play가 필요하다. WPF 연결 해제는 자동 정지를 보장하지 않는다.
설비 snapshot의 `simulationLabel`은 재생 상태 설정이고 `diagnosis=NotEvaluated`는 Python 추론 결과가 아니다.

## 진단 Python

근거: `tools/diagnosis/diagnose.py`, `models/manifest.json`, `gui/MarineMonitor.Core/DiagnosisRunner.cs`.

호출 가능한 함수: `diagnose(csv_path, data_root)`.
CLI:

```powershell
python tools/diagnosis/diagnose.py --csv "<data/vibration 내부 CSV 절대 경로>" --data-root "<data 절대 경로>"
```

입력은 data_root/vibration 내부의 `.csv`만 허용한다. 메타데이터 9행을 건너뛰고 0열 시간, 1열 진동을 읽는다. 유효·유한 샘플 최소 100개, 선형 detrend, 특징 26개와 모델 feature_names 일치, 모델 SHA-256 검증이 적용된다.

반환 필드:

```text
fileName, sampleCount, samplingFrequency, mode="offline_csv_replay"
results[]: key, title, abnormalProbability, threshold=0.5, abnormal, modelSha256
key: axis / bearing / belt / rotating
```

네 분류기는 독립 결과이며 class 1이 이상이다. 확률을 검증된 통합 신뢰도로 해석하지 않는다.
CLI 성공은 stdout JSON과 exit 0, 실패는 stderr error JSON과 exit 1이다. 오류를 정상 판정으로 바꾸지 않는다.
현재 WPF 실행기는 비동기 프로세스, 동시 1건, 45초 timeout을 사용한다.
Backend의 `backend/vr_diagnosis.py`도 동일 도구를 동시 1건/45초 제한으로 실행한다. 이벤트 ID·지점으로 중복을 제거하며 입력 파일 SHA-256, 모델별 해시와 결과를 저장한다. REST snapshot의 선택 필드 `points`로 진행 중 결과도 제공한다. 상세 추가 필드는 `VR_BACKEND_API_V1.md`를 따른다.
실제 센서 데이터 지원을 추가할 때는 형식과 출처를 새 계약으로 정의하고 `offline_csv_replay` 의미를 유지한다.

진단 Python 의존성은 `tools/diagnosis/requirements.txt`에 고정되어 있다. PPO 학습 환경과 별도 환경을 권장한다.
WPF 환경 설정: `SHIP_ROBOT_PROJECT`, `SHIP_DIAGNOSIS_PYTHON`. 상세 실행은 `tools/diagnosis/README.md` 참조.

## 음성 전사

근거: `tools/voice/server.py`, `Assets/MetaMarine/VR/VoiceTranscriptPanel.cs`.

- HTTP `http://127.0.0.1:8766`.
- `GET /health`: `service`, `ready`, `language`.
- `POST /transcribe`: mono PCM16 16000Hz WAV body, 0.1~20초. Unity 녹음 최대 15초.
- 성공 결과: `text`, `language`. 모델 로딩·busy·잘못된 WAV 등은 HTTP 오류와 `error`.
- Unity의 `TranscriptReady` 이벤트가 Agent 명령 연동 후보이다. 전사 기능 자체에는 목표 해석이나 Tool 실행이 없다.
- 로컬 모델 위치 `tools/voice/models/base`, 의존성 `tools/voice/requirements.txt`.
- 오디오와 전사 로그를 기본 저장하지 않는 현재 설계를 고려한다.

## 실행·검증 진입점

- Unity: `Assets/jetbot_env.unity`를 열고 컴파일 완료 후 Play.
- WPF: `gui/start-dashboard.ps1` 또는 `dotnet run --project gui/MarineMonitor/MarineMonitor.csproj`.
- 진단 테스트: `tools/diagnosis/test_diagnosis.py`.
- 통신·재생 테스트: `tools/EquipmentNetwork.Tests`, `tools/EquipmentMonitoring.Tests`.
- 관제 테스트: `gui/MarineMonitor.Tests`; Unity 참조 컴파일: `gui/UnityIntegration.Check`.

테스트별 전제조건은 각 기존 README와 csproj를 확인한다. 이 목록은 테스트 실행 성공 기록이 아니다.

OpenAI Agent와 VR은 `backend.main:app`/8767 단일 서비스다. `backend.vr_main:app`은 같은 앱의 별칭이다. 기존 8000 계약은 대체되었으며 `API_INTEGRATION_GAP.md`를 따른다.

2026-10-03 Unity 모의 교차점 마커: NavigationMarker.Role은 Entry/Centre이며 Entry.CentreMarker가 중앙 목표를 참조한다. 그래프 노드 ID는 Entry 기준으로 유지한다. 중간 교차점은 진입 QR 확인 → 중앙 QR 확인 및 좌표 도착 → 회전 → 출구 방향 범위의 차선 confidence 연속 확인 순서이다. REST/TCP 메시지는 변경하지 않았다. 실제 픽셀 해독이 아닌 씬 기반 관측이며 배치/검증 한계는 PROJECT_CONTEXT의 해당 변경 기록을 따른다.

2026-10-03: 사용자 요청으로 SimulatedMarkerObservationSource의 자동 스캔·지정 마커 조회·최신 관측 조회·화면 표시 모두 같은 주행 상태 필터를 적용한다. FollowingLane에서는 Entry만, ApproachingTurnCenter에서는 현재 교차점에 연결된 Centre만 허용한다. 상태 전환 직전의 다른 역할 캐시도 조회/표시하지 않는다. 기존 자동 스캔이 접근 중 Entry로 표시를 덮어쓰던 경로를 제거했다. 다른 상태는 기본 Entry 필터를 사용한다. confidence 0.60 한 프레임 회전 종료 설정은 유지했다. Unity 참조 컴파일 통과, 실제 Play 미검증.

2026-10-03: 태그 상태 전환 전 정지 대기 추가. 중간 교차점 진입 QR 확인 후 entryTagDelay(0.30초) 동안 수동 이동/회전 명령을 0으로 두고 중앙 접근으로 전환한다. 중앙 QR 확인 및 위치 도착 후 centreTagDelay(0.30초) 정지 후 회전(직진 경로는 차선 추종)한다. 중앙 도착 허용 범위를 벗어나면 중앙 대기는 다시 시작한다. 회피/안전 정지 복귀 시 대기를 재시작하고 임무 시작/리셋 시 예약을 제거한다. 기존 FSM 상태 안의 비차단 타이머이며 새로운 상태 enum은 추가하지 않았다. 회전 종료 confidence 0.60 한 프레임 조건은 유지한다. 코드·씬·생성 도구 동기화 및 런타임/Editor 참조 컴파일 통과. 실제 Play의 정지·전환 타이밍은 미검증이다.

2026-10-03: navigation-20261003-085143 기록에서 중앙 QR 확인=True이나 최소 남은 거리 약 0.30m로 0.15m 도착 조건을 충족하지 못한 것을 확인했다. 도착 허용 거리를 0.35m로 변경(씬/코드/생성 도구)하고, 정지 대기 시작 후에는 추가 0.15m 범위까지 타이머를 유지한다. 그 밖으로 벗어나면 대기를 다시 시작한다. 기존 회전점 이탈 제한은 허용 거리의 2배 계산으로 0.70m가 된다. 중앙 QR 확인·0.3초 대기·confidence 0.60 한 프레임 조건은 유지한다. 런타임/Editor 참조 컴파일 통과. 실제 Play 개선 여부는 미검증이다.

2026-10-03: jetbot_env 기본 도착 판정을 가상 NFC 구역으로 전환했다. Entry/Centre 노드와 기존 FSM을 유지한다. SimulatedIndoorSensors가 10Hz 이상적 UWB 위치를 제공하고 방향은 별도 Unity 입력이다. NFC는 XZ 반경 포함 판정(기본 0.45m)이며 실제 무선 센서 계약이 아니다. 기존 REST/TCP는 변경하지 않았다. 상세 실행·배치·검증 범위는 PROJECT_CONTEXT의 가상 NFC/UWB 항목 참조.

2026-10-03: 사용자 요청으로 StraightToNextMarker의 목표 좌표 조향을 제거했다. 가상 NFC 모드에서는 fallbackStraightCommand 전진/회전 0을 유지하며 Entry NFC를 검사한다. 기존 QR 모드의 회피 후 목표 좌표 조향도 제거했다. 실제 장애물 회피의 우선권은 유지한다. 거리·시간 제한과 도착 후 대기는 유지하며 UWB 목표 조향은 중앙 접근에서 사용한다. 방향 오차를 능동 보정하는 yaw-hold 제어는 추가하지 않았다. 런타임 참조 컴파일 및 해당 분기 정적 확인 통과. 실제 Play 직진/완주는 미검증이다.

2026-10-03: NavigationMarker에 NFC 별도 표시를 추가했다. Entry=청록, Centre=분홍, 바닥 XZ 감지 반경 원/중심 십자/이름·반경·월드 XZ 좌표를 표시한다. Scene은 Gizmos/Handles, Game은 Camera.main 투영 GUI 오버레이를 사용한다. 센서용 RenderTexture에 시각 장식을 렌더링하지 않아 차선 영상 입력을 변경하지 않는다. 표시 바닥 높이는 기본 Y=0.05이며 Show Nfc Display로 끌 수 있다. 실제 마커 좌표/반경은 바꾸지 않았다. Game 오버레이는 MainCamera가 필요하며 VR 헤드셋 내부 표시는 보장하지 않는다. Unity 참조 컴파일 통과, 실제 화면 시각 검증은 미수행이다.

2026-10-03: 주행 CSV에 위치/NFC/FSM 진단 열과 position_diagnostics_json(version=1)을 추가했다. 기존 열은 유지하며 이름으로 열을 읽는다. REST/TCP 계약 변경 없음. 상세 필드와 검증 범위는 PROJECT_CONTEXT 참조.

2026-10-03: 가상 NFC/UWB 기본 위치를 루트 BoxCollider의 TransformPoint(center)로 변경했다. 명시적 Reader가 있으면 우선 사용하며 BoxCollider가 없으면 Rigidbody.worldCenterOfMass, 둘 다 없으면 원점으로 대체한다. 방향과 XZ 거리 계산·지점 배치·반경은 유지했다. 진단 JSON version=2에 positionBasis/readerOffsetFromOrigin을 추가하고 reader.world/local이 실제 사용 기준점을 나타내도록 수정했다. 런타임 참조 컴파일 통과. 이전 092603 주행 기록에 차체 중심 판정을 재적용하면 기존 리더 감지 0회에서 중앙 기준 감지 발생을 확인했다(기록 재계산이며 새 Unity Play/FSM 완주 검증은 아님).


2026-10-03 B 출발 구간 NFC: 115604 로그에서 AligningStartHeading→FollowingLane→StraightToNextMarker→Fault(30초)를 확인했다. 출발 정렬 후 차선 없는 구간을 고정 방향으로 진행해 UnderRight Entry 감지 범위를 벗어났다. B 단독/UnderMid 출발/가상 센서 모드에 FollowingSegmentNfc를 추가했다. 시작 시 실제 NFC 리더 위치부터 UnderRight Entry까지 1.5m 이하 간격으로 반경 0.20m Segment NFC를 런타임 생성하고, UWB 위치 유도로 차례로 접근한다. 각 감지 후 기존 entryTagDelay만큼 정지하고 다음 구간으로 진행한다. 마지막에는 기존 Entry→Centre FSM으로 인계한다. 구간마다 기존 absoluteTurnStageTimeout을 적용하며 회피 시간은 제외한다. 오브젝트는 NFC_B_Departure_Segments_SIM 아래 NFC_B_Departure_01_SIM 등의 이름으로 생성되고 Play 종료 시 사라진다. 수동 조정은 Play 중 가능하며 영구 씬 배치는 아직 하지 않았다. Segment 역할은 route graph Entry 인덱스에 포함하지 않는다. A/A+B 및 나머지 구간은 기존 NFC를 사용한다. StraightToNextMarker의 회전 0은 유지한다. Unity 참조 C# 컴파일 통과(기존 경고); 실제 Play 완주 및 구간 장애물 검증은 미수행.


2026-10-03 방향별 NFC 영구 배치: jetbot_env 씬에 14개 방향별 Entry를 추가하고 기존 6개 Centre를 NFC_<Node>_Center로 이름 변경했다. 기존 노드 오브젝트는 graph anchor로 유지한다. 각 Entry는 공통 Centre와 incomingNode를 참조하며 Centre에서 해당 방향 1.2m 위치를 초기값으로 사용한다. UnderRight는 Entry_Left/Entry_Upper, UpperRight는 Left/Lower, UnderLeft는 Right/Upper, UpperLeft는 Right/Lower, UnderMid는 Left/Right/Upper, UpperMid는 Left/Right/Lower이다. 각 기존 under_/upper_ 오브젝트 아래에 영구 저장되어 Play 없이 위치 조정 가능하다. graph는 방향별 Entry를 중복 노드로 인덱싱하지 않으며 TryGetEntry(from,to)로 진입점을 선택한다. B 출발 중간 NFC 자동 생성 호출은 제거했다. Unity 참조 컴파일 및 씬 14개 Entry의 부모/공통 Centre 참조·ID 중복 검사 통과. 실제 Unity 씬 재로드와 주행은 미검증이며 배치 간격은 현장 조정 가능하다.


2026-10-03 점검 임무 기준점: A/B/A+B의 시작·종료를 6번(UnderMid)으로 고정했다. 6번이 아닌 논리 노드에서 새 점검 시작은 거부하며 로봇을 순간이동시키지 않는다. B 단독은 6→3→2→5→6: 상단 중앙까지 직진 후 우회전하며 B1→B2 순서로 점검한다. A는 6→3→1→4→6, A+B는 6→3→1→4→6→5→2→3→6(기존 B2→B1) 유지. 새 방향별 Entry NFC를 사용한다. 실제 역방향 B 주행 검증은 남아 있다.


2026-10-03 VR 로봇 카메라 시점: PcVrView가 jetbot 하위 front_camera를 찾아 RobotVrAnchor.robotCamera에 연결한다. 중립 HMD 자세를 센서 카메라의 실제 월드 위치·회전에 맞추고 상대 머리 움직임을 유지한다. 기존 로봇 원점+eyeOffset은 카메라 누락 시 fallback이며 경고를 출력한다. F9/오른쪽 B 버튼 재중앙 정렬은 위치와 방향을 함께 보정한다. 차선 인식용 RenderTexture/카메라 Transform은 수정하지 않는다. Unity 참조 C# 컴파일 통과(기존 경고), 실제 Quest 착용 시점 검증은 미수행.
