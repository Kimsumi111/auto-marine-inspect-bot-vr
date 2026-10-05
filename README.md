# auto-marine-inspect-bot-vr

## MetaMarine — 메타마린

**제조현장·선박 회전 설비 점검을 위한 VR 연계 Physical AI Agent**

반복적인 설비 순찰과 위험구역 접근 부담을 줄이기 위해, 자연어 점검 요청부터 Unity 주행·진단·결과 보고까지 연결하는 프로젝트입니다. 사용자는 VR에서 로봇 시점으로 현장을 확인하고 음성으로 점검을 요청하며, 진행 상황과 진단 결과를 대시보드에서 확인합니다.

### 주요 기능

- **자연어 점검 요청:** 설비 A, B 또는 A+B를 선택하여 점검합니다.
- **AI Agent:** 목표를 해석하고 실행·보류, 진단 재시도, 결과 보고를 판단합니다. 저수준 주행 제어는 Unity가 담당합니다.
- **Unity·로봇 연계:** 지정 경로를 순찰하고 지점별 점검을 수행하며, Unity 주행 명령을 JetBot으로 전달하는 통신 구조를 제공합니다.
- **설비 이상 진단:** 저장 진동 CSV를 축 정렬·베어링·벨트·회전체의 XGBoost 독립 모델 4개로 분석합니다. 모델별 이상 확률과 판정을 제공하며, 분석 실패는 미판정으로 구분합니다.
- **VR 대시보드·이력:** 설비별 정상·비정상 결과와 그래프를 표시하고, 임무 상태·진단 결과·실행 근거를 SQLite와 로그에 기록합니다. 사용자 취소는 LLM 응답을 기다리지 않고 처리합니다.

### 동작 흐름

**음성·텍스트 요청 → Backend / AI Agent → Unity 순찰·지점별 점검 → 저장 CSV 진단 → VR 결과 보고**

VR은 Backend REST API를 사용하고, Backend가 Unity TCP 연결과 임무 상태를 관리합니다.

| 영역 | 주요 기술 |
| --- | --- |
| 시뮬레이션·VR | Unity, C#, Meta Quest 2 / PCVR |
| Backend·Agent | Python 3.12, FastAPI, Pydantic, LangGraph, OpenAI Responses API |
| 진단·저장 | XGBoost, 진동 CSV, SQLite |
| 연동 | REST API, TCP, JetBot |

### 개발 결과와 범위

개발완료보고서에는 A+B 점검·진단·보고 완료, 미지원 요청 차단, 연결 오류 안내, 사용자 취소, 진단 실패 처리를 확인한 결과와 실물 JetBot 구동 연계가 정리되어 있습니다. 보고서의 시연 결과와 저장소 코드의 개별 검증 이력은 구분하며, 개발·연동 세부 사항은 아래 문서를 따릅니다.

현재 주요 인지·판단은 Unity 시뮬레이션을 중심으로 수행하고, 진단에는 **저장된 CSV**를 사용합니다. 실시간 센서 수집과 실제 현장 진단 성능 검증, 다양한 설비·환경으로의 확장은 후속 과제입니다.

## 팀원 및 AI 개발 도구 시작점

[공유 개발 컨텍스트](docs/PROJECT_CONTEXT.md), [현재 인터페이스](docs/INTEGRATION_CONTRACTS.md), [Agent 구현·후속 계획](docs/AGENT_PLAN.md)을 먼저 읽으세요.

Backend는 **하나의 서버 `backend.main:app` / 8767**로 통합했습니다. 기존 VR 계약을 유지하며 OpenAI/LangGraph가 자연어 목표, 시작/보류, 진단 재시도, 보고를 담당합니다. Unity 상태 감시·취소는 LLM과 독립적으로 처리합니다.

- 실행: [Backend 안내](backend/README.md), `backend/start-backend.bat`
- 공개 REST 계약: [VR Backend API v1](docs/VR_BACKEND_API_V1.md)
- 기존 Agent 계약 변경: [통합 결정](docs/API_INTEGRATION_GAP.md)
- 이전 `backend.vr_main:app`은 동일 앱의 호환 별칭입니다. 두 서버를 실행할 필요가 없습니다.

기본 모드는 OpenAI Agent이며 Backend에 API 키가 필요합니다. 통신만 시험하는 고정 명령 모드는 별도로 명시합니다. 저장 CSV 재생을 새 센서 측정으로 표시하지 않습니다.

코드의 자동화 검증은 모의 LLM·Unity 이벤트 및 실제 저장 CSV·진단 모델을 사용합니다. 실제 장치 연결과 주행 검증 조건·이력은 공유 개발 컨텍스트를 확인하세요.


## 2026-10-03 선택 설비 점검

OpenAI Agent는 A만/B만/A+B 요청을 지원한다. A는 A1→A2, B는 B1→B2, A+B는 네 지점을 진단한다. A는 왼쪽 한 바퀴, B는 오른쪽 한 바퀴, A+B는 기존 양쪽 경로를 주행한다. NFC 위치는 유지한다. 매 임무 전 Unity Play를 다시 시작한다.

TCP 시작 action은 각각 `mission_start_a`, `mission_start_b`, `mission_start`이다. Backend와 Unity를 함께 업데이트해야 한다. Snapshot 선택 필드 `targets`는 계획 확정 후 정규화된 `["A"]`, `["B"]`, `["A","B"]`이며 `total_points`는 각각 2/2/4이다. 계획 전과 과거 기록은 기본 4이며 targets가 없을 수 있다. COMPLETED는 Unity 종료와 선택 지점 전체 진단 성공을 모두 요구한다. 다른 설비 이벤트는 진단/보고에 포함하지 않는다.

`fixed_ab`와 8877 UI 모의 서버는 기존 A+B 시험용이다. 단독 설비 시나리오는 기본 `openai` 모드를 사용한다. 과거 `backend/contracts.py`의 별도 모델은 통합 REST 계약이 아니다.
