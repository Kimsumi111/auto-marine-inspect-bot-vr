# Agent API・Tool 계약 v1

2026-10-02. **계약·FastAPI·Unity TCP·SQLite·LangGraph Agent·OpenAI·진단 CLI·완료 처리 구현 완료**이다. 실제 Unity 주행을 포함한 E2E는 미검증이다.
모델 기준은 `backend/contracts.py`, 생성된 schema는 `backend/schemas/`이다.
현재 Unity TCP v1을 변경하지 않는 신규 Backend 계약이다.

## VR 연동

REST JSON 필드는 snake_case, enum은 명시된 문자열, 시각은 timezone을 포함한 ISO 8601이다.
누락 가능한 단일 값은 null, 배열은 빈 배열로 반환한다. 알 수 없는 입력 필드는 거부한다.
로컬 주소는 `http://127.0.0.1:8000`이며 실행 방법은 `backend/README.md` 참조. `/health`의 agent_ready는 Agent 구성 여부다. 키가 있으면 명령을 OpenAI가 해석하고 Tool 실행으로 연결한다. 키가 없으면 awaiting_agent로 대기한다. 고정 파서로 자연어 목표를 대체하지 않는다.
Quest 독립 실행이면 PC 주소 및 접근 제한을 별도로 설정한다. Unity TCP 포트를 장치에 직접 노출하지 않는다.

| 경로 | 요청 | 성공 응답 | 의미 |
|---|---|---|---|
| POST `/mission` | CreateMissionRequest | 202 MissionAccepted | 명령을 접수; Unity 실행 성공을 뜻하지 않음 |
| GET `/mission/{mission_id}` | 없음 | 200 MissionSnapshot | 계획·현재 단계·Unity 상태·지점별 평가 |
| GET `/mission/{mission_id}/result` | 없음 | 200 MissionResult | 종료된 업무의 결과 |
| POST `/mission/{mission_id}/cancel` | 없음 | 202 CancelAccepted 또는 정지 확인 후 200 | LLM 대기 없이 취소 요청 |

```json
{"request_id":"vr-request-001","command":"설비 A와 B를 점검해줘"}
```

```json
{"api_version":1,"mission_id":"mission-001","status":"PENDING"}
```

request_id는 VR이 생성하여 재요청에 재사용한다. Backend는 정해진 기록 보존기간 내 같은 ID+같은 command에 기존 mission_id를 반환하며 실행을 중복하지 않는다. 다른 command면 409 id_conflict이다.
보존기간은 runtime 구현 시 명시한다. SQLite 기록이 삭제된 뒤의 중복 방지는 보장하지 않는다.
모델로 추출한 A 단독·B 단독은 FAILED/unsupported_targets, 등록되지 않은 설비는 FAILED/unknown_equipment로 처리하고 Unity 시작 Tool을 호출하지 않는다. A+B로 자동 확대하지 않는다.
유효한 목표는 상태 응답의 `goal={task:"inspection",targets:["A","B"]}`로 나타낸다.

HTTP 오류 body는 `{"error": ErrorDetail}`이다. 스키마 검증은 422, 없는 임무는 404/not_found, 다른 활성 임무는 409/mission_busy, 미완료 결과 조회는 409/result_not_ready이다.
HTTP 형식 오류와 접수 후 Agent 업무 실패를 구분한다. VR은 202 수신 후 상태를 polling한다. 권장 시작 주기는 1초이며 클라이언트 네트워크 오류가 곧 Unity 임무 정지라는 의미는 아니다.

## 업무 상태와 종료 기준

- PENDING → PLANNING → EXECUTING. 시작 실패 등은 FAILED로 종료한다.
- 주행 중 진단은 병행할 수 있다. 업무는 EXECUTING을 유지하고 지점별 assessment.status로 진단 진행을 표시한다.
- Unity 임무 Completed 후 남은 진단은 DIAGNOSING으로 처리한다.
- COMPLETED: 같은 Unity 세션에서 정상 임무 종료 확인, 기대 지점 A1/A2/B2/B1 완료 기록 확보, 각 진단이 assessed 또는 명시적인 unassessed로 종료, 최종 보고 생성 완료.
- COMPLETED는 모든 설비가 정상이라는 뜻이 아니다. diagnostic_coverage는 네 지점 모두 assessed이면 complete, 일부만 assessed이면 partial, 하나도 아니면 none이다. 미판정을 정상으로 보고하지 않는다.
- Unity Fault, 세션 변경, 필수 완료 기록 누락 등은 FAILED. 연결 소실·timeout 시 실행/정지 여부 불확실성을 error로 보존한다.
- 사용자 취소: CANCELLING. 시작 전 실행 미발생 확인 또는 같은 세션의 stop ACK와 후속 Idle telemetry 확인 후 CANCELLED. ACK만으로 정지를 확정하지 않는다.
- 취소 시 정지 확인 timeout이면 FAILED/stop_unconfirmed로 종료하며 stop_confirmed를 true로 꾸미지 않는다. 진단 결과의 뒤늦은 도착이 종료 상태를 덮어쓰면 안 된다.
- 종료된 임무는 재시작하지 않는다. Unity는 새 Play 세션이 필요하다. 한 로봇에서 활성 임무는 1개이다.

이 규칙은 runtime 구현의 인수 기준이다. Pydantic은 개별 메시지 형식을 검증하며 상태 전이·TCP 효과·완료 지점 수를 실행 시 증명하지 않는다.

## Tool 계약

| 이름 | 인자 모델 | 성공 data | origin |
|---|---|---|---|
| get_equipment_info | EquipmentArgs | EquipmentInfo | backend |
| get_unity_state | EmptyArgs | UnityState | simulation |
| start_inspection | StartInspectionArgs | CommandReceipt | simulation |
| get_inspection_progress | MissionArgs | MissionSnapshot | simulation |
| diagnose_inspection | DiagnoseInspectionArgs | DiagnosisResult | replay |
| stop_inspection | MissionArgs | CommandReceipt | simulation |

공통 반환은 `ToolResult[T]`: tool_call_id, mission_id, origin, ok, data, error.
성공은 data와 error=null, 실패는 data=null과 ErrorDetail이다. retryable은 실행 계층의 판단이며 무제한 재시도를 허용하지 않는다.
CommandReceipt의 applied는 명령 적용, rejected는 거부 ACK, unconfirmed는 ACK 미확인이다. 시작/정지 완료는 별도 관찰한다.
Tool 오류에서 receipt 상세가 필요하면 실행 기록에 별도 저장한다. 실패 ToolResult의 data에 성공 데이터를 섞지 않는다.

mission_id와 expected_session_id는 Backend가 현재 임무에 바인딩해 확인한다. LLM이 다른 임무나 세션을 지정해도 실행을 허용하지 않는다.
diagnose_inspection은 inspection_id를 서버 기록에서 조회해 파일 경로를 결정한다. VR/LLM에서 임의 파일 경로를 받지 않는다.
재생 CSV의 실제 sourcePath는 내부 기록에 보관하며 공개 MissionSnapshot에는 노출하지 않는다.
Tool에서 motor 출력, 임의 명령, CSV normal/fault 제어를 Agent에 노출하지 않는다.

## 진단과 기존 코드 변환

논리 대상은 A/B, 종류는 unspecified이다. A는 L-DSF-01/A1·A2, B는 L-SF-04/B2·B1이다.
Unity camelCase telemetry를 Backend snake_case로 명시 변환한다. sessionId → session_id, 점검 id → inspection_id, completedAtUtc → completed_at.
Python 진단의 fileName/sampleCount/samplingFrequency, abnormalProbability/modelSha256도 명시 변환하며 검사 없이 이름만 바꾸지 않는다.
네 모델 결과와 threshold=0.5를 보존한다. 통합 confidence·확정 고장명은 생성하지 않는다.
과거 CSV 전체 분석이며 mode=offline_csv_replay와 origin=replay를 유지한다. 실제 재측정 Tool은 v1 범위에 없다.

## 검증과 다음 구현

```powershell
python -m pip install -r backend/requirements-contracts.txt
python -m unittest discover -s backend/tests -v
python -m backend.export_schemas
```

2026-10-02: Python 3.12.14/Pydantic 2.13.5에서 오프라인 계약 테스트 8개 통과.
pytest 미설치 환경에서도 검증하도록 unittest를 사용했다. 이후 pytest에서도 수집할 수 있다.
JSON Schema는 일반 연동용이며 OpenAI strict Tool schema로 그대로 전달할 수 있다고 가정하지 않는다. OpenAI 연동 시 required/null/additionalProperties 등 별도 변환·검증한다.
FastAPI route·SQLite·Unity TCP 어댑터를 추가했다. request_id 기록은 DB 삭제 전까지 보존한다. 검증 오류에는 invalid_request 코드를 사용한다.
총 15개 테스트 통과. 모의 Agent E2E·재시도·대상 거부·계획 중 취소와 실제 CSV/모델 CLI를 포함한다. OpenAI 목표 해석·Function Calling의 독립 실호출도 통과했다. 실제 Unity Play E2E는 미검증이다.
start_inspection은 내부 Agent 연동 함수이며 공개 HTTP 직접 시작 endpoint는 없다.
`GET /mission/{mission_id}/events`는 SQLite에 보존된 계획·decision·observation·tool_result·inspection·diagnosis·report·failure 이벤트를 반환한다. 이벤트 data는 종류별 구조이며 내부 사고 과정이나 파일 절대 경로를 포함하지 않는다.
LLM Tool 인자는 짧은 reason이며 임무·세션·점검 지점·파일은 Backend가 현재 Observation에 바인딩한다. v1의 내부 Tool 계약과 공개 LLM schema를 구분한다.
최종 수치는 실제 진단 결과를 코드로 렌더링한다. LLM은 report_results 선택을 담당하며 임의의 통합 확률을 생성하지 않는다.

실제 Unity Play 검증에서 시작·주행 후 Fault 및 정지 확인을 관찰했다. 전체 완료·진단 보고는 미검증이다. `unity_fault` 이벤트는 정지 전 UnityState와 Unity detail을 보존한다.
