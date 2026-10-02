# VR와 Agent 통합 결정

2026-10-02. 서로 달랐던 서버 두 개를 단일 임무 서비스로 통합했다. 공개 계약은 `VR_BACKEND_API_V1.md`, 실행은 `backend.main:app`/8767이다.

| 항목 | 통합 결정 |
|---|---|
| 요청 | api_version, request_id(UUID), text, execution_mode=simulation |
| 접수 | MissionSnapshot, 신규 202/중복 200 |
| 상태·결과 | state/revision/points, 기존 VR 클라이언트 유지 |
| Agent | 기존 OpenAIModel 재사용, LangGraph 계획→실행→보고를 단일 임무 서비스에 연결 |
| 취소 | LLM·진단을 취소하고 별도 TCP 정지 경로 실행 |
| 정지 미확인 | requires_attention 유지, 신규 실행 잠금, 취소 재요청 가능 |
| 완료 | Unity Completed + 4지점 SUCCEEDED + 결과 보고. 미판정이면 FAILED |
| 재시도 | Agent 선택으로 동일 CSV 최대 1회, 새 측정 아님 |
| 관찰 | 별도 TCP 수신 루프. LLM/진단 대기 중에도 Fault 감시 |
| 저장 | 기존 VR runtime SQLite 재사용. 동료의 과거 backend/data DB는 보존하되 자동 이관하지 않음 |

`backend.vr_main:app`은 별도 서버가 아니라 같은 앱의 별칭이다. 이전 8000 command/status/assessments API 및 JSON Schema는 과거 계약이다. 기존 외부 호출자는 공개 VR 계약으로 바꿔야 한다. 통합하지 않은 과거 DB의 활성 임무를 무시하지 않도록 서버 교체 전 임무·Play를 종료한다.

남은 검증: 실제 API 키 설정 후 OpenAI 실호출, Unity 네 지점 전체 주행, Quest UI 확인. 기존 `Marker fallback limit reached: 7.0 m, 30.0 s` 주행 문제는 이번 API 통합 범위에서 해결하지 않았다.
