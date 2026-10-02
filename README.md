# auto-marine-inspect-bot-vr

선박 설비 점검 Unity 시뮬레이터와 VR, WPF 관제, 진동 진단을 연결하는 프로젝트입니다.
경진대회 확장 목표는 자연어 요청을 받아 실제 Tool과 하드웨어를 실행하는 Physical AI Agent입니다.

## 팀원 및 AI 개발 도구 시작점

먼저 [공유 개발 컨텍스트](docs/PROJECT_CONTEXT.md)를 읽고,
연동 작업 전 [현재 인터페이스](docs/INTEGRATION_CONTRACTS.md)를 확인하세요.
[Agent 도입 계획](docs/AGENT_PLAN.md)은 아직 구현되지 않은 제안입니다.

VR 연동은 [확정 REST v1 계약 및 UI 실행 방법](docs/VR_BACKEND_API_V1.md)을 따릅니다.
Unity REST 클라이언트·진행 UI와 모의 서버는 구현되어 있으며, 실제 Backend/Agent는 아직 미구현입니다.

문서에는 구현 상태와 근거 코드가 함께 기록됩니다. 기능을 변경하면 관련 문서도 같은 변경에 포함하세요.
