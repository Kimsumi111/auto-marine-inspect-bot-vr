# Agent 도입 계획 — 미구현 제안

기준일: 2026-10-02. Backend와 Tool은 아직 미구현이다. REST 요청·응답 계약은 [VR_BACKEND_API_V1.md](VR_BACKEND_API_V1.md)로 확정했고 Unity 클라이언트·진행 UI·모의 서버만 구현했다. 아래 API 제안보다 해당 v1 계약이 우선한다.
팀 합의와 구현 후 상태를 갱신한다. 현재 계약은 `INTEGRATION_CONTRACTS.md` 참조.

사용자는 Unity 우선 개발을 선택했다. 첫 연동 대상은 Unity 시뮬레이터이며 실제 하드웨어 어댑터는 후속 통합 대상이다.
현재 설비는 A/B로 식별하고 펌프·모터 종류는 미확정이다. 아래 `pump_A` 예시는 확정 ID가 아니며 기존 Unity A/B와 자동 매핑하지 않는다.

## 기술 선택 상태

- 사용자 승인으로 확정: Python 3.12, FastAPI/Uvicorn, Pydantic, LangGraph, SQLite, pytest.
- LLM은 OpenAI 외부 API의 `gpt-5.4-mini`를 첫 모델로 사용한다. 모델명은 설정으로 관리하며 한국어 목표 해석·Tool 선택 정확도·응답 시간은 실제 테스트로 검증한다. 계정 접근 및 E2E 성공은 아직 검증하지 않았다.
- OpenAI Python SDK + Responses API의 function calling을 사용한다. 모델은 Tool 호출을 요청하고 Backend가 검증·실행하여 결과를 다시 전달한다. 목표·계획 등 구조화된 결과에는 Structured Outputs를 적용한다.
- 단일 Agent로 시작한다. LangGraph는 실행·대기·분기 흐름을 관리하고 LLM은 목표 해석·계획·Tool 선택·Observation 기반 후속 판단을 수행한다.
- 기존 Unity TCP와 Python 진단·음성 전사를 재사용한다. Backend가 Unity TCP 연결을 소유하고 Unity/VR은 Backend REST API로 명령·상태·결과를 주고받는 구조를 채택한다. 기존 WPF 동시 직접 연결은 현재 지원하지 않는다.
- API 키는 Backend 환경변수로만 관리하고 Unity, VR 클라이언트, 저장소, 실행 로그에 포함하지 않는다.
- Tool 인자는 엄격한 schema와 실행 시 검증을 적용한다. 구조화된 출력이 물리 실행 가능성이나 결과의 정확성을 보장하지는 않는다.
- 상태 polling마다 모델을 호출하지 않는다. 목표 해석과 계획, 새 결과·오류에 대한 판단, 최종 보고처럼 필요한 시점에 호출한다.
- 모델·프레임워크 사용 여부와 관계없이 사용자 취소 및 Unity 정지 경로는 LLM 응답을 기다리지 않도록 구현한다.

공식 참고: https://developers.openai.com/api/docs/models/gpt-5.4-mini , https://developers.openai.com/api/docs/guides/function-calling , https://docs.langchain.com/oss/python/langgraph/overview .
라이브러리 세부 버전은 구현 시 호환성을 검증하고 고정한다. 기술 선택 확정은 설치·Backend 구현 완료를 의미하지 않는다.

## 추가 구조

```text
backend/
  main.py
  agent/       # 목표, 계획, Tool 선택, Observation에 따른 판단
  tools/       # 공통 계약과 Unity/실제 하드웨어 어댑터
  api/         # 명령 접수, 상태 조회, 결과 조회, 취소
  state/       # 임무 상태와 실행 기록
  tests/       # 도구, 판단 분기, E2E
```

기존 `tools/diagnosis`를 재사용한다. 모델을 복사해 별도 진단 구현을 만드는 것은 우선하지 않는다.
VR 팀원은 API 클라이언트·화면, 하드웨어 팀원은 실제 이동·정지·측정 구현을 제공한다.
Agent 계층은 외부 구현을 래핑하여 공통 Tool 결과로 변환한다.

## 제안 Tool

| Tool | 역할 | 연결 대상·미확정 사항 |
|---|---|---|
| `get_robot_state()` | 실행 가능 여부·현재 상태 | 하드웨어 상태 계약 필요 |
| `get_equipment_info(equipment_id)` | 허용 대상과 메타데이터 | `pump_A` 등 ID와 Unity A/B 매핑 합의 필요 |
| `move_robot(target)` | 검증된 이동 sequence 실행 | 실제 제어 방식과 완료 신호 필요 |
| `stop_robot()` | 취소·정지 | 하드웨어 정지 확인 계약 필요 |
| `collect_vibration(target)` | 측정과 품질 결과 | 데이터 형식·단위·품질 기준 필요 |
| `diagnose_equipment(measurement)` | 기존 모델 추론 | 저장 CSV와 실제 측정 입력 구분 필요 |

VR은 임무 상태 API를 polling하는 방식이 우선 제안이다. 별도 `update_vr_status` Tool은 필요성이 확인되면 추가한다.
Tool 결과에는 성공/실패, 식별자, 오류 코드, 실행 출처(`simulation`, `replay`, `physical`), 결과 데이터를 명시하는 방향으로 계약을 정한다.
이 필드명은 아직 확정 JSON schema가 아니다.

## 임무와 API 제안

`POST /mission`: 명령 접수와 mission_id 반환. 임무 전체 완료까지 요청을 붙잡지 않는다.
`GET /mission/{mission_id}`: 상태, 현재 단계, Tool 실행 결과와 진행 정보.
`GET /mission/{mission_id}/result`: 완료 결과 또는 실패 사유.
`POST /mission/{mission_id}/cancel`: 취소 요청; 실제 정지 확인까지의 상태를 구분한다.

상태 후보: `PENDING`, `PLANNING`, `EXECUTING`, `DIAGNOSING`, `COMPLETED`, `FAILED`, `CANCELLING`, `CANCELLED`.
mission_id는 Unity sessionId, TCP commandId와 별도이며 상호 참조를 기록한다.
단일 로봇에 대한 동시 실행 임무는 1개로 제한하는 방향이다.

## 판단과 실행 경계

- LLM은 목표 해석, 계획과 허용 Tool 선택, Observation 기반 후속 판단을 담당한다.
- Tool 계층은 허용 대상·인자 검증, timeout, 최대 재시도, 실행 제한을 강제한다.
- LLM이 모터 출력이나 임의 코드·명령을 생성하여 실행하게 하지 않는다.
- 센서 품질 부족 시 제한된 재측정, 이동 실패 시 상태 확인 후 재시도 여부 판단을 기록한다.
- timeout은 물리 동작 실패·정지 완료와 동일하지 않다. 명령 처리 여부 미확인 상태를 보존한다.
- 모의 어댑터는 동일 인터페이스로 제공하되 실제 하드웨어 실행으로 표시하지 않는다.
- 상태 기록을 복원했다고 이전 물리 동작을 자동 재실행하지 않는다.

## 구현 순서와 인수 기준

1. 팀원 제공 인터페이스와 PC VR/독립 Quest 실행 방식을 확인한다.
2. Tool 및 REST schema, 설비 ID, 완료·오류·취소 의미를 합의한다.
3. 진단 wrapper와 로봇·센서 어댑터를 독립 검증한다.
4. 하나의 자연어 점검을 명령부터 결과까지 연결한다.
5. LLM 선택·Observation·재측정 판단을 UI와 실행 기록으로 확인한다.
6. 정상 점검, 다른 설비, 품질 부족, 이동 실패, 이상 경고, 없는 설비, 사용자 취소를 검증한다.

완료 기준은 실제 실행 증거와 기대 결과로 기록한다. 모의 E2E 성공, 실제 하드웨어 E2E 성공, VR 장치 시연 성공을 각각 구분한다.
