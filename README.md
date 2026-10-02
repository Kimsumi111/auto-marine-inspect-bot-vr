# auto-marine-inspect-bot-vr

선박 설비 점검 Unity 시뮬레이터와 VR, WPF 관제, 진동 진단을 연결하는 프로젝트입니다.
경진대회 확장 목표는 자연어 요청을 받아 실제 Tool과 하드웨어를 실행하는 Physical AI Agent입니다.

## 팀원 및 AI 개발 도구 시작점

먼저 [공유 개발 컨텍스트](docs/PROJECT_CONTEXT.md)를 읽고,
연동 작업 전 [현재 인터페이스](docs/INTEGRATION_CONTRACTS.md)를 확인하세요.
[Agent 도입 계획](docs/AGENT_PLAN.md)은 아직 구현되지 않은 제안입니다.
[신규 Agent API·Tool 계약 v1](docs/AGENT_API_V1.md)은 Pydantic 모델과 JSON Schema를 포함합니다. [Backend 실행 안내](backend/README.md)에 FastAPI·Unity TCP·OpenAI Agent·진단 연결 방법이 있습니다. 실제 Unity Play E2E는 아직 미검증입니다.

VR 연동은 [확정 REST v1 계약 및 UI 실행 방법](docs/VR_BACKEND_API_V1.md)을 따릅니다.
Unity REST 클라이언트·진행 UI와 모의 서버, 실제 Backend/Agent는 각각 구현되어 있습니다. 현재 두 REST 계약은 달라 직접 연동되지 않습니다. [계약 차이](docs/API_INTEGRATION_GAP.md)를 먼저 확인하세요. 실제 Backend→Unity 시작·주행·실패 후 정지는 검증했고, 전체 점검 완료와 VR→Backend E2E는 미검증입니다.

문서에는 구현 상태와 근거 코드가 함께 기록됩니다. 기능을 변경하면 관련 문서도 같은 변경에 포함하세요.
