# Backend 계약 단계

현재 구현: `contracts.py` Pydantic 모델, `schemas/` JSON Schema, 오프라인 테스트.
FastAPI 서버, 재연결하는 Unity TCP 클라이언트, SQLite 접수·조회·취소 저장을 구현했다.
LangGraph 계획→실행→보고 그래프, OpenAI Responses 목표 해석·Function Calling,
지점별 진단 CLI wrapper 및 완료 처리를 구현했다.
OPENAI_API_KEY가 설정되면 POST /mission을 Agent가 처리한다. 키가 없으면 awaiting_agent로 대기한다.
실제 Unity Play E2E는 아직 검증하지 않았다.

## Agent 실행 설정

- `OPENAI_API_KEY`: Backend 환경변수로 설정. 키를 코드·Unity·로그에 넣지 않는다.
- `SHIP_AGENT_MODEL`: 기본 gpt-5.4-mini.
- `SHIP_DIAGNOSIS_PYTHON`: 진단 실행 Python 경로. 기본은 .venv-diagnosis가 있으면 해당 환경, 없으면 Backend Python.
- 서버 실행 환경에서 키가 있으면 **자연어 명령이 실제 Unity 시작으로 이어질 수 있다**. WPF 연결을 해제하고 새 Play의 Idle 상태를 확인한다.
- 같은 세션의 A+B 점검만 허용하며 A/B 단독·없는 대상은 실패로 보고한다.
- 진단은 지점별 처리(동시 1건), 45초 제한, LLM 선택에 따라 같은 CSV 분석 최대 1회 재시도. 새 측정이 아니다.
- Agent 임무 전체 제한은 600초. 실행 중 Agent 오류는 정지를 요청하며 확인 실패는 stop_unconfirmed로 보존한다.
- `/mission/{id}/events`에서 계획, 짧은 선택 근거, 실제 결과, 보고 기록을 조회한다. 원본 파형·CSV 절대 경로는 OpenAI에 전송하지 않는다.
- agent_ready는 클라이언트 구성 여부이며 API 계정 접근 성공이나 Unity 준비 완료를 보장하지 않는다.

```powershell
backend/.venv/Scripts/python.exe -m backend.smoke_openai
```

위 명령은 소량의 실제 OpenAI 호출을 수행하며 Unity 동작은 실행하지 않는다.
2026-10-02: OpenAI 목표·계획 parse 및 Function Calling 실호출 통과.
총 15개 테스트 통과: 기존 11개, 모의 Agent E2E·진단 재시도, 대상 거부, 계획 중 취소 후 늦은 응답의 재실행 차단, 실제 원본 CSV·모델 4개 진단 CLI 검증.
모의 모델·Unity·진단으로 수행한 그래프 테스트와 실제 API·진단 검증을 구분한다.
실제 VR 입력→OpenAI→Unity 주행→진단→결과의 통합 시연은 다음 검증 단계이다.

## 실제 Unity E2E 검증

Backend 서버를 실행하고 WPF 연결을 해제한 뒤 Unity의 `Assets/jetbot_env.unity`에서 새 Play를 시작한다.
별도 터미널에서 아래 명령으로 실제 A+B 시뮬레이션 점검을 시작한다. OpenAI API 호출 비용이 발생한다.

```powershell
backend/.venv/Scripts/python.exe -m backend.smoke_unity
```

Agent 구성과 fresh Idle/can_start를 먼저 확인하며, 준비되지 않으면 임무를 접수하지 않는다.
접수 후 상태 변화·지점별 진단·최종 결과·저장된 이벤트 수를 출력한다.
중단/대기 시간 초과/조회 오류 시 취소 요청과 종료 상태 확인을 시도한다. 정지 미확인은 명시적으로 표시한다.
이 도구는 VR 음성 입력이나 실제 하드웨어 검증을 대신하지 않는다.
2026-10-02: 실제 Backend HTTP 실행과 Unity 미연결 시 접수 차단을 검증했다. Unity 주행 E2E는 미검증이다.

2026-10-02 실제 Play 검증: OpenAI 계획·Tool 선택→Unity 시작 ACK→주행까지 성공했으나 지점 완료 전 Unity Fault로 종료됐다. Agent의 mission_stop ACK와 후속 Idle 확인은 성공했다. 진단·최종 보고 E2E 성공으로 기록하지 않는다. Fault 원인이 정지 후 사라지는 문제를 보완해 `unity_fault` 이벤트에 정지 전 상태와 detail을 저장한다. 설비 A/B 목표의 ID 추출 안내도 명확히 했다. 재검증에는 새 Play가 필요하다.

새 Play 재검증에서도 지점 완료 전 실패했고, `unity_fault.detail`은 `Marker fallback limit reached: 7.0 m, 30.0 s`였다. `NavigationCoordinator.UpdateStraightToNextMarker`의 도착 확인 전 fallback 시간/거리 제한이다. Agent 정지 및 Idle 확인은 성공했다. 마커 관측·경로·방향 중 구체적인 실패 원인은 추가 확인이 필요하며 제한값을 늘려 해결했다고 주장하지 않는다.

## 서버 실행

저장소 루트에서:

```powershell
backend/.venv/Scripts/python.exe -m pip install -r backend/requirements-dev.txt
backend/.venv/Scripts/python.exe -m uvicorn backend.main:app --host 127.0.0.1 --port 8000 --workers 1
backend/.venv/Scripts/python.exe -m pytest backend/tests -q
```

가상환경이 없으면 Python 3.12의 `python -m venv backend/.venv`로 먼저 만든다.
`GET /health`, `GET /unity/state`, `/docs`에서 상태와 API를 확인한다. OpenAI 키는 이 단계에서 필요하지 않다.
Unity에서 jetbot_env 씬 Play, 기존 WPF는 연결 해제한다. 서버는 loopback 전용, 단일 worker로 실행한다.
`SHIP_UNITY_PORT`(기본 8765), `SHIP_AGENT_DB`(기본 backend/data/missions.db)를 지원한다.
SQLite 기록은 삭제 전까지 유지된다. 서버 재시작 시 미종료 임무는 FAILED/command_unconfirmed로 바꾸고 재실행하지 않는다.
Backend 종료·연결 해제는 Unity 자동 정지를 보장하지 않는다. 실행 중이면 종료 전에 취소와 정지 확인이 필요하다.

2026-10-02: 계약 8개와 API/SQLite 재시작/TCP 분할 수신·ACK·미응답 3개, 총 11개 통과.
테스트는 실제 Unity가 아닌 loopback 테스트 서버와 FastAPI TestClient를 사용했다. 테스트 라이브러리의 httpx 사용 중단 예정 경고 1개가 있다.

VR·Agent·하드웨어 팀은 [API·Tool 계약 v1](../docs/AGENT_API_V1.md)을 참고한다.

저장소 루트에서 Python 3.12 환경으로 실행:

```powershell
python -m pip install -r backend/requirements-contracts.txt
python -m unittest discover -s backend/tests -v
python -m backend.export_schemas
```

requirements-contracts.txt는 계약 작업용이며 전체 Backend lockfile이 아니다.
