# auto-marine-inspect-bot-vr

선박 설비 점검 Unity 시뮬레이터·VR·AI Agent·진동 CSV 진단을 연결합니다. 실제 하드웨어 연동은 후속 단계입니다.

## 팀원 및 AI 개발 도구 시작점

[공유 개발 컨텍스트](docs/PROJECT_CONTEXT.md), [현재 인터페이스](docs/INTEGRATION_CONTRACTS.md), [Agent 구현·후속 계획](docs/AGENT_PLAN.md)을 먼저 읽으세요.

Backend는 **하나의 서버 `backend.main:app` / 8767**로 통합했습니다. 기존 VR 계약을 유지하며 OpenAI/LangGraph가 자연어 목표, 시작/보류, 진단 재시도, 보고를 담당합니다. Unity 상태 감시·취소는 LLM과 독립적으로 처리합니다.

- 실행: [Backend 안내](backend/README.md), `backend/start-backend.bat`
- 공개 REST 계약: [VR Backend API v1](docs/VR_BACKEND_API_V1.md)
- 기존 Agent 계약 변경: [통합 결정](docs/API_INTEGRATION_GAP.md)
- 이전 `backend.vr_main:app`은 동일 앱의 호환 별칭입니다. 두 서버를 실행할 필요가 없습니다.

기본 모드는 OpenAI Agent이며 Backend에 API 키가 필요합니다. 통신만 시험하는 고정 명령 모드는 별도로 명시합니다. 저장 CSV 재생을 새 센서 측정으로 표시하지 않습니다.

현재 통합 검증은 모의 LLM·Unity 이벤트 및 실제 저장 CSV·진단 모델로 수행했습니다. 실제 OpenAI→Unity 네 지점 전체 주행→VR 보고 시연은 별도 확인이 필요합니다. 기존 마커 fallback 주행 실패를 이 변경에서 수정했다고 주장하지 않습니다.


## 2026-10-03 선택 설비 점검

OpenAI Agent는 A만/B만/A+B 요청을 지원한다. A는 A1→A2, B는 B1→B2, A+B는 네 지점을 진단한다. A는 왼쪽 한 바퀴, B는 오른쪽 한 바퀴, A+B는 기존 양쪽 경로를 주행한다. NFC 위치는 유지한다. 매 임무 전 Unity Play를 다시 시작한다.

TCP 시작 action은 각각 `mission_start_a`, `mission_start_b`, `mission_start`이다. Backend와 Unity를 함께 업데이트해야 한다. Snapshot 선택 필드 `targets`는 계획 확정 후 정규화된 `["A"]`, `["B"]`, `["A","B"]`이며 `total_points`는 각각 2/2/4이다. 계획 전과 과거 기록은 기본 4이며 targets가 없을 수 있다. COMPLETED는 Unity 종료와 선택 지점 전체 진단 성공을 모두 요구한다. 다른 설비 이벤트는 진단/보고에 포함하지 않는다.

`fixed_ab`와 8877 UI 모의 서버는 기존 A+B 시험용이다. 단독 설비 시나리오는 기본 `openai` 모드를 사용한다. 과거 `backend/contracts.py`의 별도 모델은 통합 REST 계약이 아니다.
