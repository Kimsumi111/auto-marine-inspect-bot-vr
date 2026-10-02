# 통합 Backend: VR → Agent → Unity → CSV 진단

2026-10-02 통합. 실행 서버는 `backend.main:app`, 기본 포트는 **8767**이다. `backend.vr_main:app`도 같은 앱을 가리키는 호환 별칭이다. 8000용 `command/status/assessments` 계약은 더 이상 운영 API가 아니다. Unity UI는 기존 `text/state/points` 계약을 그대로 사용한다.

## 설치와 실행

Unity Editor에서는 `jetbot_env` 또는 `EquipmentInspectionDemo`의 Play 시작 시 Backend가 자동 실행된다(기본 켜짐). `Tools/MetaMarine/Auto Start Backend on Play`에서 끄고 켤 수 있으며 `Start Local Backend`로 수동 실행할 수도 있다.
8767의 `/health`를 확인해 실행 중인 통합 서버를 재사용한다. 포트가 다른 프로세스에 점유돼 있으면 중복 실행하지 않고 Console 오류를 표시한다. 준비 확인은 최대 20초다.
`.venv-backend`를 우선 사용하고 기존 `backend/.venv`가 있으면 대체 사용한다. 환경 설치는 최초 한 번 필요하며 자동 설치하지 않는다. `.venv-diagnosis`와 `SHIP_DIAGNOSIS_PYTHON`이 없으면 서버 Python을 진단에도 사용하므로 해당 환경에 진단 의존성이 필요하다.
키는 Unity Editor 프로세스가 상속한 환경변수를 서버에 전달한다. 서버는 숨김 창으로 실행되며 Play 종료 후에도 유지해 다음 Play에서 재사용한다. Editor 전용 기능이며 Quest/빌드에는 적용되지 않는다. 자동 시작은 임무 전송을 수행하지 않는다.
2026-10-02: Unity 참조 C# 컴파일(경고·오류 0) 및 기존 Backend Python으로 통합 서버 기동과 health 응답을 확인했다. 실제 Play 자동 기동은 별도 확인이 필요하다.

저장소 루트에서 Python 3.12로 실행한다.

```powershell
py -3.12 -m venv .venv-backend
.venv-backend/Scripts/python.exe -m pip install -r backend/requirements.txt
py -3.12 -m venv .venv-diagnosis
.venv-diagnosis/Scripts/python.exe -m pip install -r tools/diagnosis/requirements.txt
```

Backend 프로세스에 `OPENAI_API_KEY`를 설정한 뒤 `backend/start-backend.bat`를 실행한다. 키를 Unity, 저장소, 채팅, 로그에 넣지 않는다. 기존 서버와 WPF 직접 TCP 연결은 종료하고 **단일 서버·단일 worker**로 실행한다. 다음 명령도 같은 서버를 실행한다.

```powershell
.venv-backend/Scripts/python.exe -m uvicorn backend.main:app --host 127.0.0.1 --port 8767 --workers 1 --no-access-log
```

- `MARINE_AGENT_MODE`: 기본 `openai`. 키가 없으면 health의 agent_ready=false, 새 접수는 503 agent_unavailable이며 임무 기록을 만들지 않는다. 기존 임무 조회·취소는 계속 가능하다.
- `SHIP_AGENT_MODEL`: 기본 `gpt-5.4-mini`. 기존 OpenAI Responses 목표 해석과 Function Calling을 재사용한다.
- 외부 API 없이 통신을 시험하려면 같은 서버를 `MARINE_AGENT_MODE=fixed_ab`로 명시적으로 실행한다. 고정 문구 `설비 A와 B를 점검해줘`, `A+B 점검`, `A와 B를 점검해줘`만 지원하며 Agent 실행으로 표시하지 않는다. 키 누락 때 자동으로 이 모드로 바뀌지 않는다.
- `SHIP_DIAGNOSIS_PYTHON`: 기본 `.venv-diagnosis/Scripts/python.exe`.
- `MARINE_UNITY_PORT`: 기본 8765. `MARINE_DB`: 기본 `backend/runtime/missions.sqlite3`.
- `/health`의 agent_ready는 클라이언트 구성 여부이며 실제 API 인증 성공 증거가 아니다. unity_connected와 unity_can_start도 확인한다.

## 통합된 실행 흐름

1. Unity 새 Play → UI 주소 `http://127.0.0.1:8767` 적용.
2. 음성 또는 텍스트 입력 후 사용자가 전송한다. 음성 전사만으로 실행하지 않는다.
3. 접수 ID를 SQLite에 저장하고 LangGraph가 목표·계획을 해석한다. A+B만 지원하며 A 단독/없는 설비를 A+B로 확대하지 않는다.
4. Agent의 시작/보류 선택 후 Backend가 fresh Idle과 같은 Play 세션을 다시 확인하고 mission_start를 한 번 전송한다.
5. 별도 TCP 수신 루프가 주행·Fault·점검 이벤트를 감시한다. 지점별 진단과 LLM 응답을 기다리지 않는다.
6. 각 지점에서 기존 CSV 진단을 실행한다. 동시 추론 1건, 실행당 45초. Agent는 실패 시 같은 CSV 분석을 최대 1회 재시도하거나 미판정으로 끝낸다. 새 센서 측정은 아니다.
7. Unity Completed와 4지점 SUCCEEDED를 모두 확인한 뒤 Agent가 결과 보고를 선택한다. 숫자와 요약은 실제 결과로 작성하며 이상 검출도 정상적인 임무 완료다. 미판정이 남으면 전체 FAILED이다.

진행 snapshot에 points, agent_mode, plan을 제공한다. `/mission/{id}/events`에는 계획, 도구 선택 근거, 실행·진단·Fault·보고를 저장한다. CSV 절대 경로·원본 파형을 모델 입력으로 보내지 않는다. 모델별 확률은 독립 결과다.

## 실행 로그

### 요청 전송 시 Unity 연결 없음

로컬 TCP 포트 우회: 프로젝트 루트 `.unity-tcp-port`에 숫자 한 줄(예: `8768`)을 저장하면 Agent용 Unity bridge와 Backend가 같은 포트를 사용한다. 파일은 Git에서 제외하며 없으면 기존 8765다. Backend의 `MARINE_UNITY_PORT` 환경변수가 있으면 해당 값이 우선하므로 Unity 파일과 일치시킨다. 변경 후 Backend 재시작 및 Unity 컴파일·새 Play가 필요하다. `/health.unity_port`로 선택을 확인한다. HTTP 주소 8767과 음성 8766은 별개다. WPF를 사용할 때는 WPF TCP 접속 포트도 맞춘다. 독립 재생 프로토타입의 networkPort는 이 파일로 변경하지 않는다.
2026-10-02: 종료된 이전 Editor PID의 8765 LISTENING 잔류·새 Editor bind 실패·실제 connect 거부를 확인해 이 PC의 로컬 설정을 8768로 우회했다. Backend health에서 unity_port=8768 확인. 사용자 새 Play에서 unity_connected=true·unity_can_start=true 및 Unity TCP 외부 클라이언트 연결 로그를 실제 확인했다. Backend 테스트 46개 통과. 임무는 자동 전송하지 않았다.

`Unity Play 연결이 없습니다`는 Backend가 응답했지만 fresh Unity telemetry가 없다는 뜻이다. `/health`에서 ready/agent_ready와 unity_connected를 구분한다. Play 활성·Pause 해제·WPF 연결 해제를 확인한다. Unity Console의 `Equipment TCP: ... 포트 ... 하나만 사용할 수 있습니다` 오류는 8765 리스너 중복/잔류를 의미한다.
Editor의 `EquipmentServerLifecycle`은 Play 종료·어셈블리 재컴파일·Editor 종료 전 등록된 TCP 서버를 정리한다. 변경 이전 어셈블리에서 이미 잔류한 소켓은 새 코드의 등록 목록으로 복구할 수 없으므로 Editor를 한 번 종료·재실행한 뒤 새 Play를 시작한다. 자동 Backend는 재사용 가능하다.
Play 중 재컴파일 뒤에는 활성 EquipmentNetworkBridge의 닫힌 서버를 다시 시작한다. `Tools/Ship Robot/Check and Restore Equipment TCP`로 복원·진단할 수도 있다. Console에 리스너·컴포넌트·임무 연결 여부를 출력하며, bridge가 없으면 Navigation/훈련 모드 상태를 표시한다. 코드 컴파일만 검증했고 실제 연결 회복은 별도 확인한다.
2026-10-02: 실제 Backend health는 ready/agent_ready=true, unity_connected=false였고 같은 Editor의 8765 점유·포트 충돌 로그를 확인했다. 다른 Unity 프로세스는 import worker였다. TCP 연결 중 DisposeAll 후 동일 포트 재바인딩과 반복 정리 테스트 통과, Editor 훅 참조 컴파일 경고/오류 0. 실제 Editor 재시작 후 연결 회복은 아직 미검증이다.

Agent 이벤트는 SQLite와 DB 폴더의 `agent-events.jsonl`에 저장한다(기본 `backend/runtime/agent-events.jsonl`, 5MB × 현재 파일과 백업 3개). `GET /mission/{id}/events`에서도 조회한다.
`llm_started/succeeded/failed/cancelled`는 call_id, 작업 종류, 소요 시간, 오류 타입·HTTP 상태를 기록한다. 실제 OpenAI 응답의 `llm_response`에는 모델·요청/응답 ID·입력/출력/총 토큰 수를 남긴다. 응답을 못 받은 호출은 토큰 수를 추정하지 않는다. SDK 내부 재시도는 개별 호출로 분리하지 않는다.
`decision`은 선택 근거, `tool_started/result/succeeded/failed`는 시작·정지 ACK 또는 CSV 진단 실행 결과다. ACK 성공은 주행 완료가 아니다. `unity_fault`, `diagnosis`, `report`도 보존한다. 비밀키·원본 파형·CSV 절대 경로·원문 예외 메시지는 추가 로그에 넣지 않는다.
변경 적용에는 기존 Backend 서버의 재시작이 필요하다. Play 종료만으로 서버는 재시작되지 않는다. 실행 중 임무를 취소하고 정지를 확인한 뒤 서버를 종료한다.
2026-10-02: 46개 테스트 통과, 로그 파일과 이벤트 저장을 테스트했다. 실제 OpenAI 목표 해석 호출에서 요청/응답 ID·모델·토큰 메타데이터 수집을 확인했다(입력 206, 출력 43, 총 249 토큰). 이 호출은 Unity를 실행하지 않았다.

## 취소·복구 상세

- 시작 ACK 유실·주행 중 연결 끊김은 FAILED + requires_attention으로 잠근다. 같은 요청을 다시 보내도 자동 출발하지 않는다. 취소를 다시 요청해 정지 ACK와 새 Idle/canStart=false를 확인한다.
- 취소는 LLM을 기다리지 않고 Agent·진단 작업을 중단한다. 지연된 결과는 반영하지 않는다. 시작 ACK 대기 중에도 정지 경로는 별도 실행한다.
- Unity Fault 원인을 정지 명령 전에 이벤트로 보존한다. 전체 임무 제한은 900초, Agent 판단 호출당 65초다.
- Unity 종료가 이미 확인된 상태에서는 남은 진단/보고만 취소하고 정지 확인 상태를 반환한다.
- Backend 재시작은 기존 기록을 보존하며 미완료 작업을 자동 재실행하지 않는다. 이전 VR DB를 그대로 사용한다. 동료 서버의 `backend/data/missions.db`는 별도 과거 기록이며 자동 합치거나 삭제하지 않는다. 서버 교체 전 이전 서버의 임무 종료와 Unity Play 종료를 확인한다.
- 같은 DB의 중복 프로세스는 owner 잠금으로 차단한다. 다른 DB를 사용해 이 제한을 우회하지 않는다. Unity TCP는 한 클라이언트만 지원한다.

## 검증

```powershell
.venv-backend/Scripts/python.exe -m pytest backend/tests -q
```

2026-10-02 통합 중 46개 테스트 통과: VR REST 접수·복구·중복, TCP 분할/ACK, 정지 미확인 잠금과 정지 재요청, 계획/보고 중 취소, LLM 진단 선택 대기 중 Fault 정지, 실패·재시도, 실제 저장 CSV와 모델을 쓰는 Agent 전체 흐름. 테스트의 LLM·Unity 이벤트는 모의이며 실제 OpenAI→Unity 전체 시연 성공을 뜻하지 않는다.

명시적인 실호출 점검:

```powershell
.venv-backend/Scripts/python.exe -m backend.smoke_openai
.venv-backend/Scripts/python.exe -m backend.smoke_unity
```

첫 명령은 OpenAI 실호출만 수행한다. 두 번째는 실제 Unity 점검을 시작한다. 현재 통합 환경의 API 키 미설정으로 실호출은 미실행이다. 기존 동료의 실호출·Unity 시작 기록은 Git 이력에 보존되어 있으며, 당시 전체 주행은 `Marker fallback limit reached: 7.0 m, 30.0 s`로 실패했다. 이 통합에서 주행 제한값은 변경하지 않았다.

API 기준은 [VR_BACKEND_API_V1.md](../docs/VR_BACKEND_API_V1.md)이다. 이전 `store.py`, `unity_client.py`, `diagnosis.py`, `contracts.py`의 Agent 전용 응답 모델은 과거 구현/단독 도구 참고용이며 통합 서버에서 임무를 실행하는 별도 경로가 아니다.

OpenAI API 참고: [공식 Function Calling 문서](https://developers.openai.com/api/docs/guides/function-calling). 엄격한 도구 스키마와 단일 도구 선택을 사용하며 실행 인자는 Backend에서 제한한다.

통합 후 Unity 참조 C# 컴파일과 pip check도 통과했다. 현재 런타임의 OpenAI 키는 미설정이다.
