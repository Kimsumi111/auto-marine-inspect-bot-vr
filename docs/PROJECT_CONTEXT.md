# 공유 개발 컨텍스트

현재 상태: 2026-10-02 VR·Agent 단일 Backend 통합. 공개 계약은 VR_BACKEND_API_V1.md, 실행은 backend.main:app/8767. 이전 단계 기록은 아래 이력으로 보존한다.

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
TCP의 `mission_start`는 A+B 전체 임무만 시작한다. A 전용 시작 함수는 NavigationCoordinator에 있지만 TCP에 노출되지 않았고, B 전용은 경로 계획만 존재하며 해당 점검 실행 함수는 없다.
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
