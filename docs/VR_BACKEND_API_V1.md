# VR ↔ Backend REST v1 — 통합 확정 계약

2026-10-02: `backend.main:app`/8767로 VR과 OpenAI Agent를 통합했다. `backend.vr_main:app`은 동일 앱 별칭이다. 실행·설정·검증은 `backend/README.md`를 따른다. 모의 서버 8877은 UI 전용이며 Agent·Unity·실제 진단을 실행하지 않는다.

기본 `openai` 모드는 자연어 목표를 해석하되 실행은 A+B 전체 점검만 허용한다. A 단독/미등록 설비를 A+B로 확대하지 않는다. `fixed_ab`는 명시적인 통신 시험 모드이며 기존 세 문구만 받는다. 키 누락을 고정 모드로 숨기지 않는다.

MissionSnapshot의 추가 선택 필드 `agent_mode`는 openai/fixed_ab이고 `plan`은 Agent가 반환한 계획 문자열 배열이다. 기존 기록·모의 응답에서는 생략될 수 있다. 계획은 고정 A+B 실행 범위 안의 설명이며 임의 경로/코드 실행을 허용하지 않는다. Unity UI는 모드와 계획을 표시한다.

### v1 추가 필드: 진행 중 지점별 진단

MissionSnapshot에 선택 필드 `points: PointDiagnosis[]`를 추가했다. MissionReport와 같은 지점 형식이며 아직 이벤트가 없으면 빈 배열이다. 구형 모의 서버가 생략해도 클라이언트는 허용한다. `completed_points`는 도착/점검 이벤트 수이고 추론 성공 수가 아니다.

각 지점은 기존 필드에 선택 메타데이터 `source_file_name`, `source_sha256`, `sample_count`, `sampling_frequency`를 제공한다. 절대 파일 경로는 반환하지 않는다. 대기/실행 중은 NOT_EVALUATED, 성공은 SUCCEEDED, 오류는 FAILED이다. 취소로 미완료인 지점은 NOT_EVALUATED와 사유를 보존한다. 지점 결과가 바뀌면 snapshot revision도 증가한다.

Unity Completed 후 남은 추론은 DIAGNOSING이며, 네 지점 모두 SUCCEEDED일 때만 COMPLETED이다. 이상 검출도 성공적으로 완료된 진단이다. 한 지점이라도 진단에 실패하면 전체 FAILED이며 성공한 지점 결과는 남긴다. 이미 Unity 종료가 확인된 DIAGNOSING에서 취소하면 추가 TCP 정지 명령 없이 남은 추론을 중단하고 CANCELLED/stop_confirmed=true가 된다.

## 연결과 책임

- 실제 Backend 기본 주소: `http://127.0.0.1:8767`. 모의 서버: `http://127.0.0.1:8877`.
- UTF-8 JSON, `Content-Type: application/json`. v1은 같은 PC의 Unity Editor/Quest Link용이다. 독립 Quest/외부망 공개 및 인증 계약은 범위 밖이다.
- VR은 REST만 사용한다. Backend만 Unity TCP `127.0.0.1:8765`를 소유한다. 음성 서버 `8766`은 기존대로 유지한다.
- VR은 음성 전사를 편집 가능한 명령으로 채우며 자동 실행하지 않는다. 전송 버튼/Y/F5로 접수한다.
- 실제 Backend는 요청 검증, Agent 해석, A+B 지원 여부 판단, Unity 상태 검증을 담당한다. A만/B만/미등록 대상 요청을 A+B로 묵시 확대하지 않고 `unsupported_goal`로 실패시킨다.
- 임무 수락은 실행 완료 증거가 아니다. Unity ACK는 점검 완료 증거가 아니다.

## 공통 ID·중복·보존 규칙

- `request_id`: 클라이언트 생성 UUID. `mission_id`: Backend 생성 UUID. Unity `sessionId`, TCP `commandId`와 별개.
- Backend는 request_id와 원문 요청, mission_id를 **SQLite에 원자적으로 영속화한 뒤** 실행을 예약한다. 동일 ID+동일 요청은 기존 임무를 반환하고 Tool을 다시 실행하지 않는다. 동일 ID+다른 요청은 409 `id_conflict`.
- 동일 요청 조회·재전송은 새 임무로 간주하지 않는다. 영속화 범위는 Backend 재시작을 포함하며, 이 MVP에는 자동 기록 만료가 없다.
- POST timeout/알 수 없는 5xx/잘못된 성공 응답은 **접수 여부 미확인**. 클라이언트는 새 ID로 재시작하지 않고 request_id로 조회한다. 사용자가 재전송을 누르면 동일 ID·동일 본문만 재전송한다.
- 상태 조회 404만으로 미접수를 확정하거나 새 ID를 만들지 않는다. 이전 POST가 처리 중일 수 있다.
- Unity TCP 재연결 시 미확인 `mission_start`를 자동 재전송하지 않는다. Backend가 저장된 상관관계/세션/telemetry로 확인한다.
- 클라이언트는 pending 요청과 추적 ID를 로컬 PlayerPrefs에 보존한다(평문 명령 포함, 음성 파일 제외). 정상적인 terminal 결과 수신 및 `requires_attention=false`일 때 지운다. Play 재시작은 조회만 복원하고 자동 실행하지 않는다.
- 접수 여부 미확인 상태에서도 서버 주소 수정은 허용한다(명령 POST/취소 전송 중에는 응답 또는 최대 8초 timeout까지 잠금). 서버 변경은 임무 취소가 아니다. 이전 서버의 요청 기록은 주소별로 유지하고 해당 주소로 돌아오면 조회를 복원한다. 새 서버에 이전 요청을 자동 전송하지 않는다. 마지막 적용 주소도 저장해 Play 재시작 시 복원한다. 실행 중인 임무가 확인됐거나 정지 확인이 필요하면 주소 변경을 막는다.
- 단일 로봇의 활성 임무 1개. 정지 미확인 임무도 신규 실행을 막는다. 임무 생성의 400/422/409는 **이번 요청으로 새 임무를 생성하지 않은 응답**이다.

## 엔드포인트

| Method | Path | 성공 | 설명 |
|---|---|---|---|
| POST | `/mission` | 202 신규 / 200 중복 | MissionSnapshot 반환 |
| GET | `/mission/by-request/{request_id}` | 200 | MissionSnapshot; 복구 조회, 이 경로를 동적 mission_id보다 먼저 등록 |
| GET | `/mission/{mission_id}` | 200 | MissionSnapshot; 1초 polling, LLM 호출 없음 |
| GET | `/mission/{mission_id}/events` | 200 | 계획·선택·진단·Fault·보고 이벤트; polling으로 LLM을 호출하지 않음 |
| GET | `/mission/{mission_id}/result` | 200 | MissionReport; 미완료는 409 result_not_ready |
| POST | `/mission/{mission_id}/cancel` | 202 진행 / 200 이미 terminal | 본문 `{}`, MissionSnapshot; 반복 호출은 멱등 |

요청 제한: 본문 8KiB 이하, 추가 필드 거부, api_version 정수 1, text 공백 제거 후 1~500자, execution_mode은 `simulation`만 허용. JSON 숫자의 bool 대체를 허용하지 않는다.

```json
{
  "api_version": 1,
  "request_id": "6bcf32cd-8826-47cb-b6c4-05a43d4d5320",
  "text": "설비 A와 B를 점검해줘",
  "execution_mode": "simulation"
}
```

## MissionSnapshot

아래 모든 필드를 반환한다. 문자열 정보가 아직 없으면 빈 문자열을 쓴다. null 대신 빈 배열/문자열을 사용한다.

```json
{
  "api_version": 1,
  "mission_id": "170c8c27-8d66-44cd-9ca2-9e98517053ba",
  "request_id": "6bcf32cd-8826-47cb-b6c4-05a43d4d5320",
  "revision": 3,
  "state": "EXECUTING",
  "transport_mode": "backend",
  "execution_mode": "simulation",
  "data_mode": "offline_csv_replay",
  "completed_points": 1,
  "total_points": 4,
  "stop_confirmed": false,
  "requires_attention": false,
  "step": "inspect_point_A2",
  "message": "A1 점검 완료, 다음 지점으로 이동 중",
  "unity_session_id": "unity-session-example",
  "updated_at": "2026-10-02T03:00:00Z"
}
```

- `revision`: 임무별 단조 증가 정수(최초 0 이상). 상태/진행/주의사항이 바뀌면 증가. 클라이언트는 오래된 응답을 무시한다.
- `transport_mode`: `backend` / `mock`. mock은 UI에 항상 모의 데이터로 표시한다.
- `completed_points`: Unity 완료 이벤트로 확인한 지점 수 0~4. 진단 성공 개수나 이동 거리 백분율이 아니다.
- A 지점: inspect_point_A1, inspect_point_A2. B 지점: inspect_point_B2, inspect_point_B1. 다른 이름으로 임의 변환하지 않는다.
- 상태: PENDING → PLANNING → EXECUTING / DIAGNOSING → COMPLETED. 지점별 이동/진단이 겹칠 수 있으며 순서를 단순 enum 숫자로 비교하지 않는다. 실패는 FAILED.
- CANCEL 요청은 즉시 CANCELLING으로 기록하고 LLM 응답을 기다리지 않는 정지 경로로 전달한다. **정지 확인 후에만 CANCELLED + stop_confirmed=true**.
- 정지 timeout/연결 단절 등 결과가 불확실하면 FAILED + requires_attention=true + stop_confirmed=false. 오류 원인은 message에 표시한다. FAILED를 정지 완료로 해석하지 않는다.
- COMPLETED는 모든 4개 지점 완료와 4개 모델씩의 진단 결과 확보 후에만 사용한다. 진단 미판정은 FAILED와 부분 결과로 보고한다. 이상 검출 자체는 실행 실패가 아니므로 COMPLETED일 수 있다.
- terminal: COMPLETED / FAILED / CANCELLED. requires_attention=true인 임무는 terminal이어도 상태 조회와 취소 재요청을 허용하고 신규 임무를 막는다. 이미 CANCELLED/COMPLETED를 다시 취소해도 상태를 되돌리지 않는다.
- 기존 Unity는 Play 세션당 1회 시작만 지원한다. 다음 요청은 Backend가 canStart/sessionId를 확인하고 불가능하면 `unity_restart_required`로 거부한다.

## MissionReport

terminal 상태일 때만 제공한다. FAILED/CANCELLED도 summary와 부분 결과를 반환한다. 결과를 준비하지 못했으면 성공 상태를 먼저 확정하지 않는다.

```json
{
  "api_version": 1,
  "mission_id": "170c8c27-8d66-44cd-9ca2-9e98517053ba",
  "state": "FAILED",
  "transport_mode": "backend",
  "execution_mode": "simulation",
  "data_mode": "offline_csv_replay",
  "summary": "A1 CSV 진단 실패로 임무 결과를 확정하지 못했습니다.",
  "points": [
    {
      "inspection_id": "unity-inspection-id",
      "point": "inspect_point_A1",
      "equipment_id": "A",
      "source_equipment_id": "L-DSF-01",
      "status": "FAILED",
      "message": "유효한 진동 샘플 부족",
      "models": []
    }
  ]
}
```

points는 지점 중복 없이 0~4개. SUCCEEDED / FAILED / NOT_EVALUATED 상태를 사용한다. 미수행 지점을 생략하거나 NOT_EVALUATED로 명시할 수 있으며 정상 판정으로 채우지 않는다. SUCCEEDED는 아래 model 항목을 `axis`, `bearing`, `belt`, `rotating` 키로 정확히 하나씩 가진다.

```json
{"key":"bearing","abnormal_probability":0.82,"threshold":0.5,"abnormal":true,"model_sha256":"실제 모델의 SHA-256"}
```

확률은 유한한 0~1, threshold 0.5, abnormal은 확률 >= threshold. 기존 진단 도구의 camelCase를 Backend wrapper에서 snake_case로 변환한다. 네 확률을 합치거나 한 종류의 다중 분류 confidence로 바꾸지 않는다. 실제 Backend의 model_sha256은 64자리 hex, mock만 `mock`을 쓴다. 결과에 로컬 절대 CSV 경로나 API 키를 노출하지 않는다.

## 오류

```json
{"api_version":1,"error":{"code":"mission_busy","message":"진행 중인 임무가 있습니다."}}
```

- 400 invalid_request: JSON/크기 오류.
- 422 invalid_request: schema 오류.
- 409 id_conflict / mission_busy / unity_restart_required / result_not_ready.
- 404 not_found: 조회할 ID 없음.
- 503 agent_unavailable / unity_unavailable: 임무 생성 전 거부이며 새 기록이 없다. Unity는 이 두 명시적 코드에 한해서 입력 잠금을 해제한다. 같은 요청 ID가 이미 존재하면 이 검사 전에 해당 임무를 반환한다.
- 기타 5xx/알 수 없는 오류는 접수 미확인으로 보존한다.
- 자연어의 unsupported_goal은 접수 후 PLANNING에서 FAILED 결과로 보고할 수 있다. Tool 실행은 금지.
- Pydantic 기본 `detail` 응답을 그대로 노출하지 말고 위 error envelope로 통일한다.

## 실행과 검증

1. 실제 Backend 없이 UI만 시험: `python tools/mission_mock/server.py` (Python 3.12 표준 라이브러리만 필요).
2. Unity jetbot_env Play → 패널 아래 Backend 주소를 `http://127.0.0.1:8877`로 적용 → 기본 A+B 문장 전송.
3. 약 14초 후 4지점 모의 결과가 표시된다. 로봇은 이 요청으로 움직이지 않는다. UI의 ‘모의 서버’ 표시를 확인한다.
4. `--scenario failure` 또는 `--scenario stop-unconfirmed`는 별도 서버 실행에서 사용한다. 후자는 취소 요청 후 정지 미확인/신규 임무 잠금을 시험한다.
5. 실제 Backend 연결 시 기본 주소 8767을 사용하고 WPF의 직접 TCP 연결을 종료한다.

PC: 텍스트 편집/전송 버튼, F5 전송, F6 취소, F8 녹음, F7 마이크 변경. VR: Y 전송, 오른쪽 스틱 클릭 취소, A 녹음, X 마이크 변경, 왼쪽 스틱 클릭 결과 페이지, B 시점 재정렬. 헤드셋 없이 모든 REST 흐름을 시험할 수 있다.

모의 서버는 메모리 저장이며 재시작 시 기록이 사라진다. 이는 실제 Backend의 SQLite 영속화 요구를 대체하지 않는다. UI에서 pending이 복원되면 같은 요청 재전송으로만 회복한다. 단위 테스트: `python tools/mission_mock/test_server.py`.

2026-10-02 검증: 모의 HTTP 테스트 9개 통과, Unity 참조를 사용한 C# 컴파일 통과. Unity Play에서 텍스트 요청 → 모의 상태 조회 → 4지점 완료와 결과 UI 표시 확인. 별도 모의 요청에서 취소 → CANCELLED/정지 확인 표시도 확인했다. 실제 Backend·LLM·Unity TCP 임무 E2E 및 Quest 장치 검증은 이 변경으로 수행하지 않았다.

통합 검증(2026-10-02): Backend 테스트 46개 통과. 모의 LLM·Unity 이벤트와 실제 저장 CSV/모델 기반 실행을 구분한다. 실계정 API와 Unity 전체 주행·Quest 시연은 아직 미검증이다.
