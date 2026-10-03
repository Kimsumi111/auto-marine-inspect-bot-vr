# 공유 개발 컨텍스트

2026-10-03 Jetbot 순차 TCP: 현재 소스를 재분석하고 최근 VR 시선/주행/로그 변경을 보존했다.
JetbotTelemetrySender 기본 TCP(127.0.0.1:19101), Networking→Jetson TCP 19102,
duration_ms(20Hz=50ms) 순차 전달·수신 큐·목표 재생을 추가했다. UDP는 선택 모드로 유지한다.
TCP는 TTL/최신값 폐기를 사용하지 않는다. 실제 모터/서보/센서 및 물리 행동 모방은 연결하지 않았다.
UDP 13+TCP 11개 테스트, Unity 참조 컴파일 및 실제 C# TCP 작업자의 지연 ACK 검증 통과.
Unity Play/HMD/실제 LAN·Jetson·물리 동작은 미검증이다. 주행/씬/LLM 서버는 수정하지 않았다.
FPS/FixedUpdate 샘플링 한계와 재시작 후 복구 미지원은 [JETBOT_TCP_GUIDE.md](JETBOT_TCP_GUIDE.md)를 따른다.
아래 UDP 기록은 이전 단계 이력이다.

2026-10-03 VR 시선 송신: JetbotTelemetrySender의 CaptureGaze를 분리하고 Inspector Gaze Status에 추적/카메라/앵커/일시정지 사유를 표시한다. 유효한 고정 시선도 기존 주기에 따라 새 seq로 전송하며, 주행 정지와 시선 추적은 독립적이다. UDP 계약/포트/TTL 및 기존 씬 연결은 유지한다. 실제 Unity 참조 C# 컴파일 및 모의 입력/실제 localhost UDP를 사용한 Networking 13개 테스트 통과. 실제 HMD/Unity Play/Jetson 연결은 이번 변경에서 미검증이다. 실제 Jetson 저장소는 수정하지 않았으며 적용·로그 가이드는 [JETBOT_GAZE_GUIDE.md](JETBOT_GAZE_GUIDE.md)에 기록했다.

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

현재 상태: 2026-10-02 VR·Agent 단일 Backend 통합. 공개 계약은 VR_BACKEND_API_V1.md, 실행은 backend.main:app/8767. 이전 단계 기록은 아래 이력으로 보존한다.

2026-10-02 최신 사용자 정정: FollowingLane은 기존 차선 중앙 추종으로 복구했다. 회전 중 line search 후 VisualAlign을 추가해 경로 기준 좌회전은 오른쪽 경계, 우회전은 왼쪽 경계를 파란 기준선에 정렬한 뒤 중앙 추종으로 넘긴다. 선택은 경로 진입·출구 yaw 차이로 결정해 재탐색 때 남은 회전각 때문에 바뀌지 않는다. 정렬 횡오차 허용 0.08/각도 8°, 새 프레임 확인 중 정지. 경계 미관측은 정지·기존 제한 재탐색, 최대 정렬 이동 제한 유지. 컴파일 통과, 실제 주행 효과는 미검증이다.

2026-10-02 사용자 요청: FollowingLane의 파란 수직 기준선에 좌/우 경계선 자체를 맞추는 TrackingReference를 추가했다. jetbot_env는 RightBoundary이며 Inspector에서 LaneCentre/LeftBoundary/RightBoundary를 선택한다. 선택된 분석 띠의 좌측/우측 최외곽 HSV run으로 횡오차·방향오차·confidence를 계산한다. 수동 회전 탐색은 기존 중앙/차선 쌍 검출을 사용한다. reference 변경 시 이전 confidence·필터를 무효화하고 새 영상을 기다린다. Unity 참조 컴파일 통과, 실제 경계 추종 주행은 미검증이다.

2026-10-02 사용자 정정 반영: 출구 차선 탐색은 계획 각도 도달 여부와 무관하게 회전 중 유효한 차선 쌍과 LaneFollower의 실제 confidence/age 기준을 새 관측 3개에서 만족하면 바로 FollowingLane로 전환한다. VisualAlign을 거치지 않는다. 회전 중 저속 전진과 탐색 제한은 유지한다. 실제 회전·추종 성공은 미검증이다.

2026-10-02 사용자 요청: 출구 차선 탐색 회전 중 저속 전진 명령 0.05를 함께 적용한다. 탐색 시작점 기준 평면 변위 0.60m부터 회전 중 전진을 중단하며, 계획 방향 도달 후 기존 probe와 차선 쌍 3프레임 확인·정렬 조건은 유지한다. Inspector/jetbot_env/씬 생성 도구 설정과 searchAdvance 로그를 추가했다. 실제 회전 후 재획득 효과는 미검증이다.

2026-10-02: HSV 화면 확인을 위해 MissionPanel 접기/펼치기(F3·오른쪽 위 버튼)를 추가했다. HUD 표시도 함께 제어하며 상태 조회·임무·입력 내용은 유지한다. 숨김 중 전송은 막고 취소는 유지한다.

2026-10-02 첫 좌회전 후 차선 재획득 보완: exit search는 새 카메라 프레임 3개에서 연속 유효한 차선 쌍을 확인한 뒤 VisualAlign로 전환하고 확인 중 전진을 멈춘다. VisualAlign 완료도 새 timestamp의 관측만 집계한다. jetbot_env와 씬 생성 도구의 requiredPairFrames를 3으로 맞췄다. exit search/VisualAlign/FollowingLane/fallback 전환에 경로·방향·인식 지표 로그를 추가했다. FreshDetectionStreak 회귀 검증과 Unity 참조 컴파일 통과. 실제 좌회전 개선/전체 주행 성공은 새 Play에서 미검증이다.

2026-10-02 연결 후속: 종료된 이전 Editor PID의 8765 리스너 잔류와 새 Editor bind 실패를 확인했다. `.unity-tcp-port` 머신별 설정을 지원하고 이 PC는 8768로 우회했다. Backend health unity_port=8768 확인·Unity 코드 컴파일 통과. 사용자 새 Play에서 실제 Unity 연결과 시작 가능 상태가 모두 true인 것을 확인했다. Backend 테스트 46개 통과. HTTP/임무 계약과 주행 동작은 유지한다.

2026-10-02 후속 연결 진단: 서버 health 정상이나 새 Play TCP 연결 검사 거부. 종료된 서버 참조로 재시작을 건너뛰는 경로를 보완하고 Play 진입/재컴파일 후 활성 bridge의 TCP 복원·진단을 추가했다. 컴파일 통과; 실제 연결 회복은 사용자 새 Play에서 확인 필요하다.

2026-10-02 요청 실패 진단: Backend health 정상·Agent 구성됨, Unity telemetry 미연결. Editor의 8765 포트 잔류 및 포트 충돌 로그 확인. EquipmentTcpServer 등록 목록과 Editor Play 종료/재컴파일/종료 전 DisposeAll 훅을 추가했다. 연결 중 포트 해제·재바인딩·반복 정리 테스트 통과, Editor 훅 컴파일 통과. 기존 잔류 소켓 제거는 Editor 재시작 필요; 실제 연결 회복은 미검증.

2026-10-02: LLM 호출 시작·성공·실패·취소·시간·OpenAI 응답 메타데이터와 Tool 실행 이벤트를 추가하고 DB 옆 회전 JSONL 로그로 저장한다. 46개 테스트 통과. 실제 실행 기록에서 OpenAI 계획·시작 선택·Unity ACK 성공 후 `Exit lane not visible after 1.20 m at the planned heading` 실패를 확인했다. Navigation 실패 상세에 경로 노드·방향·차선 탐색 지표를 추가했다(주행 파라미터 유지). Unity 참조 컴파일 통과; 원인 해결/전체 주행 성공은 미확정이다.

2026-10-02: 사용자 요청으로 Unity Editor 점검 씬 Play 시 Backend 자동 시작을 추가했다. 이미 실행 중인 서버 재사용, 포트 점유 감지, 숨김 실행, 기존 Python 환경 대체를 지원한다. 주행·씬 코드 변경 없음. Unity 참조 컴파일 및 서버 단독 기동 확인, 실제 Play 자동 실행은 미검증이다.

기준일: 2026-10-02. 현재 저장소의 코드와 설정을 읽어 작성했다. Unity Play, VR 장치, 실제 하드웨어 및 전체 E2E 실행 성공을 이번 문서 작성에서 검증하지 않았다.

## 목표와 담당

## 지속 적용할 대회 기준

사용자가 제공한 2026-09-29 사업설명회 자료를 검토했다. 아래 기준을 이후 설계·구현·검증에 지속 적용한다.

- 3쪽: AI Agent 핵심 6요소는 Goal, Planning, Reasoning, Tool Use, Memory/State, Feedback이며 실제 작품에서 최소 4개 이상 확인. 이 프로젝트는 6개 모두 실행 증거로 보여주는 방향이다.
- 5쪽: 목표는 Level 3 MVP. 명확한 문제·사용자, 핵심 기능 3~5개(주요 기능 약 80% 이상 시연 가능), 안정적 E2E 최소 1개, AI의 실질적 역할, 실제 Tool/API/Data/파일/센서 연동 최소 1개, 사용 가능한 UI/명령 인터페이스, 수행·기록한 테스트 최소 5개, 3분 이내 시연이 필요하다.
- 7쪽: 주제·문제 15점, Agent 구현성 20점, 기술 구현·완성도 20점, 차별성 10점, 실용성·효과 15점, 데이터·안전·윤리 10점, 비즈니스 모델 10점. Agent E2E와 테스트 증거를 우선한다.
- 13쪽: 기존 자산과 신규 개발, 모델·데이터·코드·AI 도구 출처를 구분한다. 비밀정보 제거, 라이선스 준수, 검증 결과의 정직한 기록, 안전장치가 필요하다.
- 14쪽 Q4: 실장비뿐 아니라 시뮬레이션·VR·Recorded Data도 인정 가능한 방식이다. Unity 우선 MVP가 가능하며 실제 측정·동작으로 오인시키지 않는다.
- 기존 Unity 경로·상태·진단 코드만으로 Agent 6요소 충족을 주장하지 않는다. Agent가 목표를 해석하고 Tool을 호출하고 Observation을 다음 판단에 활용하는 것을 구현·시연해야 한다.
- 현재 재측정 Tool은 없다. 진단 재시도와 새 측정을 구분하고 불가능한 복구는 미판정·실패로 보고한다.

이는 제공된 설명회 자료에 대한 검토이며 이후 개정 공고까지 확인했다는 뜻은 아니다.

## 프로젝트 역할과 방향

경남 AI·SW 경진대회 확장 목표는 다음 흐름이다.

`VR 자연어 요청 → Agent 목표 해석·계획 → Tool 실행 → 물리 동작·측정 → Observation → 후속 판단 → VR 결과`

사용자는 Agent, Tool, Backend API, 진단 연동, 임무 상태 및 전체 통합을 담당한다.
다른 팀원은 VR 적용과 실제 로봇·센서 하드웨어를 담당한다. 사용자의 설명에 따르면 이들 기능은 어느 정도 개발되어 있다. 다만 저장소에서 확인한 구현과 팀원의 외부 구현은 별도로 확인해야 한다.

완전한 산업용 자율주행, SLAM, ROS 도입은 범위가 아니다. 검증된 사전 동작 sequence를 Tool로 실행할 수 있다. 하나의 안정적인 E2E 점검과 결과 기반 분기가 우선이다. 대회 평가·일정 정보는 전달받은 프로젝트 컨텍스트이며 공식 문서 검증 결과는 아니다.

## 저장소 지도와 구현 상태

| 위치 | 역할 | 확인 상태 |
|---|---|---|
| `Assets/jetbot_env.unity` | 로봇 기본 씬 | Build Settings에서 활성화 |
| `Assets/Navigation/` | 그래프 경로, 교차로 제어, 설비 점검 | 코드 존재 |
| `Assets/LaneFollowing/` | HSV 차선 인식과 바퀴 제어 | 코드 존재 |
| `Assets/ObstacleAvoidance/` | YOLOX/BlazePose, 가상 ToF, SafetySupervisor, PPO 회피 | 코드 존재; 학습 성능은 별도 검증 |
| `Assets/MetaMarine/VR/` | PC VR 시점, 로봇 기준 시점, 음성 전사 UI | 코드 존재; 장치 검증 별도 |
| `Assets/EquipmentMonitoring/` | CSV 재생, TCP 통신, 관제 임무 연결 | 코드 존재 |
| `gui/MarineMonitor/` | WPF 관제 UI | .NET 10 Windows 프로젝트 |
| `gui/MarineMonitor.Core/` | TCP 클라이언트, 진단 프로세스 실행 | 코드 존재 |
| `tools/diagnosis/` | 특징 26개, XGBoost 모델 4개, 테스트 | 코드·모델 존재 |
| `tools/voice/` | 한국어 Whisper HTTP 전사 서버 | 코드 존재; 로컬 모델 준비 필요 |
| `config/`, `results/`, `wandb/` | PPO 설정과 학습 산출물 | 기존 자산 |
| `data/` | 전류·진동 원본 CSV | 기존 오프라인 데이터 |
| `Assets/MetaMarine/VR/Mission*.cs` | REST v1 클라이언트와 텍스트/음성·진행·결과 UI | 구현; 실제 Backend E2E 미검증 |
| `tools/mission_mock/` | VR 계약 확인용 HTTP 모의 서버 | 구현; 실제 이동·LLM·진단 없음 |
| `backend/` | 신규 Agent Backend | 계약·FastAPI·TCP·SQLite·LangGraph·OpenAI·진단 실행 구현, 실제 Unity Play E2E 미검증 |


Unity 버전은 `ProjectSettings/ProjectVersion.txt`의 `6000.5.7f1`이다.
패키지 기준은 `Packages/manifest.json`이며 OpenXR, XR Interaction Toolkit, ML-Agents가 포함된다.

## 현재 흐름

`WPF 명령 → TCP → Unity A+B 점검 → 점검 완료 이벤트 → 진동 CSV 경로 → Python 진단 → WPF 표시`

`RobotDashboardIntegration`은 `jetbot_env` 또는 `EquipmentInspectionDemo`에서 학습 모드를 제외하고 설치된다.
기존 로봇과 NavigationCoordinator를 사용하며 별도 실제 하드웨어를 제어하는 코드는 아니다.

## 해석 시 주의점

- `SimulatedMarkerObservationSource`는 씬 마커 관측 모사이며 실제 AprilTag 픽셀 해독이 아니다.
- `JetBotController.cs`의 `TrackedRobotController`는 WheelCollider 제어이다. 이를 물리 로봇 제어 진입점으로 해석하지 않는다.
- `VirtualToFSensor`는 가상 센서이다. CSV 재생은 센서의 실측 수집 기능이 아니다.
- 진단은 저장된 진동 CSV 전체를 사용한다. 약 10Hz telemetry는 원본 파형이 아니며 특징 추출 입력으로 쓰지 않는다.
- 기존 문서 중 초기 프로토타입 설명은 최신 관제 임무·진단 확장보다 오래되었다. 현재 계약은 `INTEGRATION_CONTRACTS.md`와 근거 코드를 함께 확인한다.
- 기존 자산과 이번 대회 신규 개발의 이력은 추후 Git 이력·팀 기록으로 확인한다. 문서 작성일만으로 개발 시점을 단정하지 않는다.

## 후속 확인

### Unity 우선 Agent 개발과 실제 대상 확인

사용자가 Unity 우선 개발을 선택했다. 현재 설비 식별자는 `A`, `B`이며, 코드에서 펌프·모터 같은 설비 종류는 확정되지 않았다. 이름을 추측하여 `pump_A`로 등록하지 않는다.

| 논리 설비 | 원본 데이터 ID | 점검 지점 | 기본 재생 설정 |
|---|---|---|---|
| A | L-DSF-01 | inspect_point_A1, inspect_point_A2 | 정상 |
| B | L-SF-04 | inspect_point_B2, inspect_point_B1 | 베어링불량 |

근거는 `RobotDashboardIntegration.cs`와 `NavigationCoordinator.cs`이다. 두 지점은 동일 논리 설비에 연결되며 별도 설비 네 개로 취급하지 않는다. 기본 상태는 진단 결과가 아니다.
현재 `jetbot_env.unity`에는 A1/A2 참조가 있고 B1/B2 참조는 비어 있다. A+B 임무 시작 시 B 점검 지점을 A 지점 복제로 생성한다.
TCP의 `mission_start`는 A+B, `mission_start_a`는 A 단독, `mission_start_b`는 B 단독 점검을 시작한다. A/B 단독은 해당 설비 쪽 한 바퀴만 주행하고, A+B는 양쪽을 점검한다.
현재 점검은 지점 접근 후 지정 시간 대기하고 완료 이벤트를 발생시키는 시뮬레이션이다. 이벤트에 연결되는 진동 파일은 이미 재생 중인 CSV이며 새 측정 수집이 아니다.

1. 실제 로봇·센서 코드 위치, 호출 방식, 완료·정지 계약.
2. Quest Link PC VR 또는 Quest 독립 실행 방식. 독립 실행 장치의 localhost는 PC 서버 주소가 아니다.
3. 실제 센서의 단위, 샘플링, 시간축과 모델 학습 데이터의 일치 여부.
4. Agent와 WPF가 Unity TCP 연결을 어떻게 공유할지. 현재 동시 클라이언트는 1개이다.

## 문서 갱신 기록

| 날짜 | 변경 | 검증 |
|---|---|---|
| 2026-10-02 | 미확인 요청의 신규 임무 잠금과 주소 수정 잠금을 분리. 주소별 요청 보존 및 마지막 서버 주소 복원 | Unity 참조 컴파일 확인; 기존 요청 자동 삭제/재전송 없음 |
| 2026-10-02 | VR REST v1 계약 확정, Unity 클라이언트·UI·모의 서버 추가. 실제 Backend는 미구현 유지 | 모의 HTTP 테스트 9개 및 Unity 참조 C# 컴파일 통과; Unity Play 요청·모의 4지점 완료·결과 UI 확인; 실제 Agent E2E/Quest 미검증 |
| 2026-10-02 | 공유 컨텍스트·현재 계약·Agent 계획과 AI 도구 안내 추가 | 코드·설정 정적 검토; 런타임 미검증 |
| 2026-10-02 | Unity 우선 개발 선택과 A/B 데이터·점검 지점·외부 임무 제한 확인 | 씬 YAML·임무 코드 정적 검토 |
| 2026-10-02 | 사용자 승인으로 Python 3.12, FastAPI/Uvicorn, Pydantic, LangGraph, OpenAI Responses API·gpt-5.4-mini, SQLite, pytest 및 단일 Agent 구성 확정 | 설계 결정; 설치·API 실행·E2E 미검증 |
| 2026-10-02 | API·Tool v1 계약 문서, Pydantic 모델, JSON Schema 생성기 및 스키마 추가 | Python 3.12.14/Pydantic 2.13.5에서 계약 테스트 8개 통과; 서버·Unity·OpenAI 연동 미구현 |
| 2026-10-02 | FastAPI 접수/조회/취소, Unity TCP 재연결·ACK 처리, SQLite 중복 방지·재시작 처리 추가 | 총 11개 테스트 통과; 실제 Unity Play·OpenAI·전체 E2E 미검증 |
| 2026-10-02 | LangGraph Agent, OpenAI 목표·계획·Function Calling, 진단 CLI, 이벤트·최종 보고 연결 | 총 15개 테스트, 실제 CSV/모델 4개 진단, OpenAI 독립 호출 통과; 실제 Unity Play E2E 미검증 |

새 기능을 도입할 때 여기에 날짜, 근거 파일, 구현/제안 상태, 실제 수행한 검증을 추가한다.

2026-10-02 원격 VR REST 클라이언트·모의 서버 변경을 통합했다. 현재 Backend와 VR의 REST 계약은 호환되지 않으며 `API_INTEGRATION_GAP.md`에 차이를 기록했다. VR→Backend 실제 연동은 미완료이다.

2026-10-02 실제 Unity Play 검증: OpenAI 계획·Tool 선택과 Unity 시작·주행 성공, 지점 완료 전 Unity Fault로 실패. Agent가 정지 ACK 및 후속 Idle을 확인했다. 지점 진단·최종 보고까지의 E2E는 미완료이다. 정지 전 Fault detail을 `unity_fault` 이벤트로 보존하도록 보완했다.

새 Play 재검증: `Marker fallback limit reached: 7.0 m, 30.0 s`를 실제 이벤트에서 확인했다. `NavigationCoordinator.UpdateStraightToNextMarker`의 도착 확인 전 fallback 제한으로 실패했으며, 마커를 관측하지 못한 구체적인 원인은 미확정이다. 정지 후 Idle 확인 성공, 진단 지점 0개이다.

2026-10-02: `backend/smoke_unity.py` 실제 Backend·Unity E2E 실행 도구를 추가했다. 준비 상태 확인 후 A+B 명령을 접수하고 결과를 조회한다. 실제 Backend HTTP 기동 및 Unity 미연결 시 접수 차단만 검증했으며, Unity 주행은 아직 미검증이다.

## 2026-10-02 pull 충돌 통합

원격 Agent 구현은 그대로 보존하고, 로컬 VR 고정 A+B 서버는 `backend/vr_main.py`, CSV 실행기는 `backend/vr_diagnosis.py`로 분리했다. 실행·의존성 안내는 `backend/README-VR.md`이다. 기존 로컬 CSV 테스트 20개 및 Unity 참조 컴파일 통과 기록은 해당 구현에 대한 것이며 원격 Agent의 전체 E2E 검증을 뜻하지 않는다. 두 서버는 Unity TCP를 동시에 소유할 수 없다.

분리 후 재검증: VR 테스트 20개, 원격 계약·런타임 테스트 11개 통과. 원격 Agent 테스트는 현재 로컬 환경의 LangGraph 미설치로 수집 단계에서 중단되어 미검증이다. OpenAI 호출이나 새 Unity 주행은 수행하지 않았다.

## 2026-10-02 단일 Backend 통합

동료 OpenAIModel·LangGraph의 계획/도구 선택과 VR 임무 서비스의 복구·취소·진단을 연결했다. 두 실행 경로는 같은 앱이다. 정지 미확인 잠금, 재시도 제한, 미판정 FAILED 기준을 통일했다. 기본 OpenAI 모드에서 키가 없으면 미접수 503이며 고정 모드로 자동 대체하지 않는다.

통합 테스트 46개 통과. LLM·Unity는 모의 입력이며 CSV 추론은 실제 저장 파일·모델을 사용한 테스트도 포함한다. API 키가 없는 현재 환경에서는 실제 OpenAI 호출·Unity 네 지점 E2E·Quest 검증은 하지 않았다. 주행 마커 fallback 실패는 별도 과제로 남아 있다.

## 2026-10-03 QR 도착 후 직진 거리 축소

사용자 요청으로 회전 전 접근 거리 상한 maximumApproachDistance와 jetbot_env의 경로별 approachDistance 6개를 1.50m에서 1.35m로 15cm 줄였다. 코드 기본값과 씬 생성 도구도 동기화했다. 차선 소실 조건에 따라 더 일찍 회전할 수 있으며 고정 이동 거리가 아니다. 속도·QR 도착 판정·최소 접근 거리·별도 하단 회전 중심 오프셋은 변경하지 않았다. 저장값과 diff를 확인했으며 실제 Play 주행은 미검증이다.

## 2026-10-03 Unity 월드 좌표 회전

NavigationCoordinator의 useAbsoluteTurns를 jetbot_env 및 생성 도구에서 활성화했다. QR 도착 이후 차선 소실/상대 이동 거리 대신 고정 월드 목표점으로 조향 접근한다. 기본 목표점은 해당 교차점 마커의 월드 위치에서 진입 방향으로 0.65m 이동한 지점이며, 회전 진입 시 한 번 계산하고 회피 이동으로 변경하지 않는다. Maneuver Overrides의 useWorldTurnPosition/worldTurnPosition으로 경로별 월드 좌표를 직접 지정할 수 있다. 좌표는 로봇 Transform의 XZ 위치 기준이며 높이는 무시한다. 씬에서 정확한 차로 중심에 맞춘 좌표 보정은 실제 주행으로 확인해야 한다.

위치 오차 0.15m 이내에서 전진을 중단하고 출구 경로의 절대 Yaw로 제자리 회전한다. 방향 오차 3도 이내를 0.25초 유지하면 차선 추종으로 복귀한다. 회전 중 차선이 먼저 보이더라도 조기 종료하지 않는다. 접근/회전 각 단계는 회피·안전 정지 시간을 제외하고 45초로 제한하며 회전점에서 0.4m 이상 이탈하면 Fault로 정지한다. 기존 QR 인식·직선 주행·장애물 회피는 유지하므로 전체 주행 성공을 보장하는 변경은 아니다. 이는 Unity 시뮬레이션의 월드 위치를 사용하는 제어이며 실제 로봇의 절대 위치 추정 기능은 아니다.

Unity 참조를 사용한 런타임 C# 컴파일 통과(기존 경고 존재). 실제 Unity Play에서 좌표 정확도·회전 안정성·A+B 완주는 아직 미검증이다.

## 2026-10-03 회전 종료를 차선 confidence 기준으로 변경

사용자 요청으로 위 절대 Yaw 완료 조건을 대체했다. 고정 월드 회전점 접근은 유지하고 회전 중 유효한 중앙 차선 detection.confidence가 turnExitConfidence(기본 0.60, 주행 최소 confidence보다 낮게 적용하지 않음) 이상이면 즉시 회전 명령을 0으로 한다. 서로 다른 최신 영상 3개(requiredPairFrames)에서 연속 확인하면 전진 차선 추종으로 복귀한다. 오래된 영상과 낮은 confidence는 연속 확인을 끊는다. 절대 목표 방향은 탐색 한계로만 사용하며 그 방향에 도달해도 confidence가 부족하면 정지 대기하고 기존 45초 제한에서 Fault 처리한다. 전진 복귀 후에는 기존 차선 추종의 조향 보정이 적용되며 무조건 무조향 직진은 아니다. Inspector의 Turn Exit Confidence로 조절할 수 있다.

런타임 C# 참조 컴파일 및 NavigationTracking.Tests 통과. 실제 Unity Play와 0.60 임계값의 현장 적합성은 미검증이다.

## 2026-10-03 진입 QR → 중앙 QR → 회전

현재 Unity 시뮬레이션에 Entry/Centre 마커 역할 및 Entry의 CentreMarker 참조를 추가했다. 경로 그래프는 Entry만 사용하며 관측기는 요청한 마커만 선택할 수 있다. 중간 교차점에서는 4m 인식 범위 내 진입 QR을 연속 확인하면 차선 유무와 관계없이 수동 전진/중앙점 접근으로 전환한다. 기존 1.2m 도착 조건과 우하단의 좌표만으로 QR을 건너뛰는 경로는 이 모드에서 사용하지 않는다. 최종 경로 노드의 임무 완료 조건은 기존 방식이다.

중앙 QR을 연속 확인한 사실을 보존하고 로봇 Transform의 XZ 위치가 중앙점 0.15m 이내에 도착해야 회전한다. 중앙점이 아직 멀면 조기 회전하지 않으며, 도착했어도 중앙 QR을 확인하지 못했다면 정지 대기 후 45초 제한으로 Fault 처리한다. 회전 중에는 출구 방향 오차 25도 이내이고 회전 시작 이후의 최신 중앙 차선 confidence가 0.60 이상인 영상 3개를 연속 확인해야 차선 추종으로 복귀한다. 높은 진입 차선 confidence만으로 회전을 즉시 끝내지 않는다. 안전 정지/회피 우선권은 유지한다.

현재 데모 경로의 중앙 마커가 없으면 임무 시작 시 CentreQR_*_SIM 자식 오브젝트를 만든다. 기본 위치는 기존 진입 마커에서 진입 방향으로 0.65m이며 경로별 명시적 worldTurnPosition 설정이 있으면 초기 생성 위치로 사용한다. 이미 지정된 CentreMarker는 이동시키지 않는다. 이 기본 위치는 실제 교차점 중앙을 자동 추정한 값이 아니므로 위치 보정이 필요할 수 있다. 기존 진입 QR 위치는 변경하지 않았다.

영구 배치: Play를 끄고 Tools > Ship Robot > Create Entry-Centre QR Pairs 실행 → 각 CentreQR_*_SIM을 교차점 중앙으로 이동 → 씬 저장. 메뉴는 현재 A+B 데모 경로용이다. 진입 마커는 차선이 끊기기 전 인식되는 위치에 둔다. 모의 QR의 이미지는 기존 AprilTagVisual을 복제하며 역할은 컴포넌트로 구분한다. 실제 QR/AprilTag 픽셀 해독 및 서로 다른 인쇄용 코드 생성은 구현하지 않았다.

검증: 런타임과 배치 Editor 코드의 Unity 참조 컴파일 통과, 기존 NavigationTracking.Tests 통과, diff 공백 검사 통과. 실제 Unity Play의 진입·중앙 가시성, 정지 위치, 전체 주행은 미검증이다.

## 2026-10-03 中央 접근 중 제자리 회전 방지

최근 navigation-20261003-081955 로그에서 Entry QR UpperMid 3/3 확인 후 ApproachingTurnCenter 상태로 진입했으며, 마지막에도 SearchingExitLane이 아닌 접근 상태에서 남은 거리 2.54m/방향 오차 -95.5도/전진 0/회전 -0.2가 기록됐다. QR 순서 혼동으로 확정할 증거는 없고, 접근 제어가 지나친 목표를 다시 향하며 제자리 회전하는 현상을 확인했다.

접근 중 조향을 ±0.05로 제한하고 목표까지 남은 전방 거리에 따라 감속한다. 목표 평면을 통과했을 때 횡오차 0.35m 이내이면 도착 처리하며, 범위를 벗어나거나 목표 방향이 전방 45도 밖이면 Fault로 정지한다. 뒤로 돌아 목표를 쫓지 않는다. 중앙 관측 확인은 여전히 필수다. Entry→Centre 참조의 역할·nodeId도 검증한다. 사용자 배치 좌표는 변경하지 않았다.

회전 종료의 절대 출구각 ±25도 조건은 제거했다. 최소 탐색각(기본 최대 10도)을 지난 뒤 유효한 새 차선 confidence 0.60 이상을 3회 확인하면 종료한다. 진입 차선을 즉시 재사용하지 않도록 최소 회전각·회전 이후 영상 조건은 유지한다. 상태에 usable/turned/threshold를 표시한다. 이 confidence 방식 자체가 출구 식별을 보장하지는 않는다.

런타임 참조 컴파일 및 기존 NavigationTracking.Tests 통과. 변경 후 Unity Play 재현·완주 검증은 미수행이다.

## 2026-10-03 사용자 요청으로 직전 접근 제한 복원

직전 추가한 전방 45도/통과 횡오차 Fault, ±0.05 조향 제한, 목표 평면 도착 판정 및 Centre 참조 추가 검증을 제거하고 이전 중앙점 접근 제어로 복원했다. 회전 종료는 유효한 최신 차선 confidence 임계값(현재 씬 0.60)을 새 관측 3회 연속 충족하는 조건만 사용한다. 최소 회전각, 절대 출구각, 회전 시작 이후 타임스탬프 및 TrackingReference 이름 조건은 종료 판정에서 제거했다. 동일 영상은 중복 계수하지 않는다. 기존 진입→중앙 주행 단계와 기본 안전 정지/시간 제한은 유지한다. 런타임 참조 컴파일 통과, Play 미검증.

2026-10-03: 사용자 요청으로 회전 종료 requiredPairFrames를 3에서 2로 변경했다(코드·씬·생성 도구 동기화). 차선 detectionInterval 설정은 0.05초로 유지한다. 새 유효 영상 2개 사이의 최소 설정 간격은 약 0.05초이며 실제 간격은 프레임률과 처리 지연에 따라 길어질 수 있다. 중간 confidence 미달/영상 만료 시 연속 횟수는 초기화된다. 저장값 확인 완료, Play 미검증.

2026-10-03: 노이즈가 거의 없는 환경이라는 사용자 가정에 따라 회전 종료 requiredPairFrames를 2에서 1로 변경했다. 유효한 차선 confidence 0.60 이상 한 프레임으로 회전 종료/차선 추종 복귀한다. 코드·씬·생성 도구 저장값 확인 완료. 영상 처리 주기 0.05초는 유지하며 실제 Play는 미검증이다.

2026-10-03: 사용자 요청으로 SimulatedMarkerObservationSource의 자동 스캔·지정 마커 조회·최신 관측 조회·화면 표시 모두 같은 주행 상태 필터를 적용한다. FollowingLane에서는 Entry만, ApproachingTurnCenter에서는 현재 교차점에 연결된 Centre만 허용한다. 상태 전환 직전의 다른 역할 캐시도 조회/표시하지 않는다. 기존 자동 스캔이 접근 중 Entry로 표시를 덮어쓰던 경로를 제거했다. 다른 상태는 기본 Entry 필터를 사용한다. confidence 0.60 한 프레임 회전 종료 설정은 유지했다. Unity 참조 컴파일 통과, 실제 Play 미검증.

2026-10-03: 태그 상태 전환 전 정지 대기 추가. 중간 교차점 진입 QR 확인 후 entryTagDelay(0.30초) 동안 수동 이동/회전 명령을 0으로 두고 중앙 접근으로 전환한다. 중앙 QR 확인 및 위치 도착 후 centreTagDelay(0.30초) 정지 후 회전(직진 경로는 차선 추종)한다. 중앙 도착 허용 범위를 벗어나면 중앙 대기는 다시 시작한다. 회피/안전 정지 복귀 시 대기를 재시작하고 임무 시작/리셋 시 예약을 제거한다. 기존 FSM 상태 안의 비차단 타이머이며 새로운 상태 enum은 추가하지 않았다. 회전 종료 confidence 0.60 한 프레임 조건은 유지한다. 코드·씬·생성 도구 동기화 및 런타임/Editor 참조 컴파일 통과. 실제 Play의 정지·전환 타이밍은 미검증이다.

2026-10-03: navigation-20261003-085143 기록에서 중앙 QR 확인=True이나 최소 남은 거리 약 0.30m로 0.15m 도착 조건을 충족하지 못한 것을 확인했다. 도착 허용 거리를 0.35m로 변경(씬/코드/생성 도구)하고, 정지 대기 시작 후에는 추가 0.15m 범위까지 타이머를 유지한다. 그 밖으로 벗어나면 대기를 다시 시작한다. 기존 회전점 이탈 제한은 허용 거리의 2배 계산으로 0.70m가 된다. 중앙 QR 확인·0.3초 대기·confidence 0.60 한 프레임 조건은 유지한다. 런타임/Editor 참조 컴파일 통과. 실제 Play 개선 여부는 미검증이다.

## 2026-10-03 가상 NFC + 이상적 UWB 위치 입력

jetbot_env에서 useIndoorSensorSimulation을 켰다. 기존 NavigationMarker Entry/Centre 및 중앙점 참조와 사용자 배치를 그대로 가상 NFC 구역으로 재사용한다. 이름 CentreQR은 기존 씬 호환을 위해 보존한다. QR 카메라 관측/영상 표시가 이 모드의 도착 판정에 관여하지 않는다.

- FollowingLane: 예정된 Entry 구역 안에 리더가 들어오면 한 번 처리하고 0.3초 정지 후 중앙 접근한다. 경로 마지막 Entry도 같은 근접 판정으로 처리한다. 중간 교차점 진행 후에는 다음 목표만 검사하므로 이전 구역에서 중복 전환하지 않는다.
- ApproachingTurnCenter: 10Hz로 샘플링한 가상 UWB 위치에서 중앙까지 남은 벡터를 계산해 주행한다. 현재 노드의 Centre 구역에 진입하면 0.3초 정지 후 회전/직진 경로로 전환한다. 대기 중 구역을 벗어나면 대기를 초기화한다. QR 연속 확인과 별도 35cm 도착 판정은 이 모드에서 사용하지 않는다.
- 차선 소실 시 기존 거리/시간 제한 안에서 가상 UWB로 예정된 Entry까지 안내한다. 위치 입력이 비활성/무효이면 Fault로 정지한다.
- 회전 종료는 기존 유효한 차선 confidence 0.60 이상 한 프레임 조건이다. 장애물 회피, 점검 이벤트, REST/TCP 계약은 유지한다.

SimulatedIndoorSensors는 로봇에 없으면 Awake/임무 시작 시 자동 추가한다. 사전 설정이 필요하면 jetbot에 해당 컴포넌트를 추가한다. Reader Transform을 지정하지 않으면 로봇 Transform 원점이 감지점이다. 위치와 방향은 별도 입력이다: 위치는 0.10초 주기/최대 유효 나이 0.50초이며, HeadingForward는 Unity의 이상적인 방향 입력으로 UWB 측정값이 아니다. 위치 오차·전파 차폐·멀티패스·하드웨어 통신은 구현하지 않았다.

NFC는 물리 Trigger/실제 리더 대신 XZ 평면의 원형 구역 포함 판정이다. NavigationMarker.ProximityRadius 기본값 0.45m이며 높이는 무시한다. 이는 도착 시나리오용 반경이고 실제 NFC 통신 거리의 모델이 아니다. Scene Gizmos로 범위를 볼 수 있다. 기존 지점의 Proximity Radius를 조절하고 씬을 저장한다. 신규 지점은 Tools > Ship Robot > Create Virtual NFC Zones 메뉴로 기존 데모 경로에 생성할 수 있다. 초기 중앙점은 기존 오프셋 가정이므로 직접 배치가 필요할 수 있다.

검증: 런타임/Editor Unity 참조 컴파일, 기존 NavigationTracking.Tests 및 신규 근접 판정 6개(중심/경계/외부/대각선/음수 좌표/무효 입력) 통과. 실제 Unity Play 완주와 NFC/UWB 실장치는 미검증이다. 이 변경은 이상적 실내 위치·근접 감지 시뮬레이션이며 실제 센서 성능을 검증하지 않는다.

2026-10-03: 사용자 요청으로 StraightToNextMarker의 목표 좌표 조향을 제거했다. 가상 NFC 모드에서는 fallbackStraightCommand 전진/회전 0을 유지하며 Entry NFC를 검사한다. 기존 QR 모드의 회피 후 목표 좌표 조향도 제거했다. 실제 장애물 회피의 우선권은 유지한다. 거리·시간 제한과 도착 후 대기는 유지하며 UWB 목표 조향은 중앙 접근에서 사용한다. 방향 오차를 능동 보정하는 yaw-hold 제어는 추가하지 않았다. 런타임 참조 컴파일 및 해당 분기 정적 확인 통과. 실제 Play 직진/완주는 미검증이다.

2026-10-03: NavigationMarker에 NFC 별도 표시를 추가했다. Entry=청록, Centre=분홍, 바닥 XZ 감지 반경 원/중심 십자/이름·반경·월드 XZ 좌표를 표시한다. Scene은 Gizmos/Handles, Game은 Camera.main 투영 GUI 오버레이를 사용한다. 센서용 RenderTexture에 시각 장식을 렌더링하지 않아 차선 영상 입력을 변경하지 않는다. 표시 바닥 높이는 기본 Y=0.05이며 Show Nfc Display로 끌 수 있다. 실제 마커 좌표/반경은 바꾸지 않았다. Game 오버레이는 MainCamera가 필요하며 VR 헤드셋 내부 표시는 보장하지 않는다. Unity 참조 컴파일 통과, 실제 화면 시각 검증은 미수행이다.

## 2026-10-03 위치 기준 및 NFC/FSM 진단 확대

주행 CSV에 nfc_decision, entry/centre_delay_remaining, target_route_index, indoor_mode, absolute_mode, centre_confirmed, position_diagnostics_json을 추가했다. 주행 중 기본 0.05초 간격, 상태/NFC 판정 사유 변화 및 진입 대기 완료 시 즉시 기록한다. 진단 JSON에는 실제 로봇 원점/리더 경로·월드/로컬 좌표·스케일, UWB 마지막 샘플/나이/유효성, Rigidbody 위치·질량중심·선속도/각속도, 루트 Collider 중심, 바퀴 중심 평균 및 각 WheelCollider 중심/접지/회전수/토크/브레이크/슬립을 담는다. 대상 Entry와 연결된 Centre의 ID·역할·경로·활성·좌표·반경·리더 거리·원점 거리·IsInside를 같은 행에 기록한다. 없는 Rigidbody/Collider/센서는 존재 여부 필드로 구분한다. 차체의 시각 중심과 질량중심/Collider 중심은 같은 개념으로 단정하지 않는다.

센서 진단은 읽기 전용 getter로 마지막 샘플을 읽으며 위치 샘플링이나 주행 제어를 변경하지 않는다. nfc_decision은 마지막 실제 검사 결과로, 상태와 함께 해석한다. 루트 Collider가 없으면 하위 시각 모델 중심은 별도로 계산하지 않는다. 20Hz는 목표 간격이며 프레임률에 따라 달라지고 매우 짧은 구역 통과의 전체 물리 프레임 기록은 아니다. 컴파일 및 기존 CSV 테스트 포함 NavigationTracking.Tests 통과. 새 Play 파일의 실제 데이터 수집은 다음 실행에서 확인해야 한다.

2026-10-03: 가상 NFC/UWB 기본 위치를 루트 BoxCollider의 TransformPoint(center)로 변경했다. 명시적 Reader가 있으면 우선 사용하며 BoxCollider가 없으면 Rigidbody.worldCenterOfMass, 둘 다 없으면 원점으로 대체한다. 방향과 XZ 거리 계산·지점 배치·반경은 유지했다. 진단 JSON version=2에 positionBasis/readerOffsetFromOrigin을 추가하고 reader.world/local이 실제 사용 기준점을 나타내도록 수정했다. 런타임 참조 컴파일 통과. 이전 092603 주행 기록에 차체 중심 판정을 재적용하면 기존 리더 감지 0회에서 중앙 기준 감지 발생을 확인했다(기록 재계산이며 새 Unity Play/FSM 완주 검증은 아님).


## 2026-10-03 선택 설비 점검

OpenAI Agent는 A만/B만/A+B 요청을 지원한다. A는 A1→A2, B는 B1→B2, A+B는 네 지점을 진단한다. A는 왼쪽 한 바퀴, B는 오른쪽 한 바퀴, A+B는 기존 양쪽 경로를 주행한다. NFC 위치는 유지한다. 매 임무 전 Unity Play를 다시 시작한다.

TCP 시작 action은 각각 `mission_start_a`, `mission_start_b`, `mission_start`이다. Backend와 Unity를 함께 업데이트해야 한다. Snapshot 선택 필드 `targets`는 계획 확정 후 정규화된 `["A"]`, `["B"]`, `["A","B"]`이며 `total_points`는 각각 2/2/4이다. 계획 전과 과거 기록은 기본 4이며 targets가 없을 수 있다. COMPLETED는 Unity 종료와 선택 지점 전체 진단 성공을 모두 요구한다. 다른 설비 이벤트는 진단/보고에 포함하지 않는다.

`fixed_ab`와 8877 UI 모의 서버는 기존 A+B 시험용이다. 단독 설비 시나리오는 기본 `openai` 모드를 사용한다. 과거 `backend/contracts.py`의 별도 모델은 통합 REST 계약이 아니다.


2026-10-03 단독 순환: A=UnderMid→UpperMid→UpperLeft→UnderLeft→UnderMid, B=UnderMid→UpperMid→UpperRight→UnderRight→UnderMid. A+B 경로는 유지한다. 출발 방향이 첫 구간과 3도 이상 다르면 AligningStartHeading에서 제자리 정렬 후 FollowingLane으로 전이한다. 교차점 confidence 종료와 NFC 배치는 유지한다. 실제 Play에서 특히 B 출발 및 기존 NFC 접근 가능 여부를 확인해야 한다.


2026-10-03 B 출발 구간 NFC: 115604 로그에서 AligningStartHeading→FollowingLane→StraightToNextMarker→Fault(30초)를 확인했다. 출발 정렬 후 차선 없는 구간을 고정 방향으로 진행해 UnderRight Entry 감지 범위를 벗어났다. B 단독/UnderMid 출발/가상 센서 모드에 FollowingSegmentNfc를 추가했다. 시작 시 실제 NFC 리더 위치부터 UnderRight Entry까지 1.5m 이하 간격으로 반경 0.20m Segment NFC를 런타임 생성하고, UWB 위치 유도로 차례로 접근한다. 각 감지 후 기존 entryTagDelay만큼 정지하고 다음 구간으로 진행한다. 마지막에는 기존 Entry→Centre FSM으로 인계한다. 구간마다 기존 absoluteTurnStageTimeout을 적용하며 회피 시간은 제외한다. 오브젝트는 NFC_B_Departure_Segments_SIM 아래 NFC_B_Departure_01_SIM 등의 이름으로 생성되고 Play 종료 시 사라진다. 수동 조정은 Play 중 가능하며 영구 씬 배치는 아직 하지 않았다. Segment 역할은 route graph Entry 인덱스에 포함하지 않는다. A/A+B 및 나머지 구간은 기존 NFC를 사용한다. StraightToNextMarker의 회전 0은 유지한다. Unity 참조 C# 컴파일 통과(기존 경고); 실제 Play 완주 및 구간 장애물 검증은 미수행.


2026-10-03 방향별 NFC 영구 배치: jetbot_env 씬에 14개 방향별 Entry를 추가하고 기존 6개 Centre를 NFC_<Node>_Center로 이름 변경했다. 기존 노드 오브젝트는 graph anchor로 유지한다. 각 Entry는 공통 Centre와 incomingNode를 참조하며 Centre에서 해당 방향 1.2m 위치를 초기값으로 사용한다. UnderRight는 Entry_Left/Entry_Upper, UpperRight는 Left/Lower, UnderLeft는 Right/Upper, UpperLeft는 Right/Lower, UnderMid는 Left/Right/Upper, UpperMid는 Left/Right/Lower이다. 각 기존 under_/upper_ 오브젝트 아래에 영구 저장되어 Play 없이 위치 조정 가능하다. graph는 방향별 Entry를 중복 노드로 인덱싱하지 않으며 TryGetEntry(from,to)로 진입점을 선택한다. B 출발 중간 NFC 자동 생성 호출은 제거했다. Unity 참조 컴파일 및 씬 14개 Entry의 부모/공통 Centre 참조·ID 중복 검사 통과. 실제 Unity 씬 재로드와 주행은 미검증이며 배치 간격은 현장 조정 가능하다.


2026-10-03 점검 임무 기준점: A/B/A+B의 시작·종료를 6번(UnderMid)으로 고정했다. 6번이 아닌 논리 노드에서 새 점검 시작은 거부하며 로봇을 순간이동시키지 않는다. B 단독은 6→3→2→5→6: 상단 중앙까지 직진 후 우회전하며 B1→B2 순서로 점검한다. A는 6→3→1→4→6, A+B는 6→3→1→4→6→5→2→3→6(기존 B2→B1) 유지. 새 방향별 Entry NFC를 사용한다. 실제 역방향 B 주행 검증은 남아 있다.


2026-10-03 VR 로봇 카메라 시점: PcVrView가 jetbot 하위 front_camera를 찾아 RobotVrAnchor.robotCamera에 연결한다. 중립 HMD 자세를 센서 카메라의 실제 월드 위치·회전에 맞추고 상대 머리 움직임을 유지한다. 기존 로봇 원점+eyeOffset은 카메라 누락 시 fallback이며 경고를 출력한다. F9/오른쪽 B 버튼 재중앙 정렬은 위치와 방향을 함께 보정한다. 차선 인식용 RenderTexture/카메라 Transform은 수정하지 않는다. Unity 참조 C# 컴파일 통과(기존 경고), 실제 Quest 착용 시점 검증은 미수행.
