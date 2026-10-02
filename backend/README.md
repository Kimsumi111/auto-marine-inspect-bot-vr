# 통합 Backend: VR → Agent → Unity → CSV 진단

2026-10-02 통합. 실행 서버는 `backend.main:app`, 기본 포트는 **8767**이다. `backend.vr_main:app`도 같은 앱을 가리키는 호환 별칭이다. 8000용 `command/status/assessments` 계약은 더 이상 운영 API가 아니다. Unity UI는 기존 `text/state/points` 계약을 그대로 사용한다.

## 설치와 실행

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

## 취소·복구

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
