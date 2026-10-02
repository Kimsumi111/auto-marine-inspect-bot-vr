# VR와 Backend 계약 차이

2026-10-02 원격 main의 VR 구현을 pull해 Backend 작업과 통합했다. 양쪽 구현은 보존했으며 REST 호환 어댑터는 아직 없다. 포트만 변경해도 직접 연동되지 않는다.

| 항목 | 현재 Backend (`AGENT_API_V1.md`) | 현재 VR (`VR_BACKEND_API_V1.md`) |
|---|---|---|
| 기본 포트 | 8000 | 8767; mock 8877 |
| 접수 본문 | request_id, command | api_version, request_id, text, execution_mode |
| 접수 응답 | MissionAccepted; 중복도 202 | MissionSnapshot; 중복 200 |
| 상태 | status, current_step, unity, assessments | state, step, revision, requires_attention 등 |
| request_id 조회 | 미구현 | /mission/by-request/{request_id} 필요 |
| 취소 응답 | CancelAccepted; 종료된 임무 일부 409 | MissionSnapshot; terminal 멱등 응답 |
| 최종 보고 | assessments, diagnostic_coverage | points, models 및 실행 출처 |
| 진단 미판정 | 명시적 unassessed 포함 COMPLETED 가능 | FAILED와 부분 결과 |
| 정지 미확인 후 신규 접수 | terminal FAILED면 활성 잠금 해제 | requires_attention 동안 신규 임무 차단 |

다음 연동 작업에서 스키마·복구·취소·종료 기준을 함께 정해야 한다. 필드 이름만 변환하는 것으로 완료하지 않는다. VR 모의 검증은 실제 Backend 연결 검증을 대신하지 않는다.

Backend 테스트 15개 통과. 실제 OpenAI→Unity 시작·주행은 성공했으나 마커 fallback 30초 제한으로 실패했고 정지 후 Idle을 확인했다. 전체 진단·보고 및 실제 VR→Backend E2E는 미완료이다.
