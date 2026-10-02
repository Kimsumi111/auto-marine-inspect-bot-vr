# 현재 인터페이스 계약

기준일: 2026-10-02. 아래는 코드에 존재하는 계약이다. 향후 Agent 계약은 `AGENT_PLAN.md`에 별도로 기록한다.
경로는 저장소 루트 기준이다.

## Unity TCP v1

근거: `Assets/EquipmentMonitoring/EquipmentWireProtocol.cs`, `EquipmentNetworkBridge.cs`, `EquipmentTcpServer.cs`, `RobotDashboardIntegration.cs`.

- `127.0.0.1:8765`, TCP, UTF-8 JSON 한 줄 + LF, 동시 클라이언트 1개.
- 수신 chunk와 메시지 경계는 다르다. LF까지 버퍼링한다.
- telemetry는 unscaled 시간 기준 약 10Hz. 느린 수신자에게 중간 프레임이 생략될 수 있다.
- 기존 WPF와 새 Backend를 동시에 직접 연결하는 구조는 현재 지원하지 않는다.

요청 예시:

```json
{"version":1,"type":"command","commandId":"request-001","equipmentId":"robot","action":"mission_start"}
```

응답 예시:

```json
{"version":1,"type":"commandResult","commandId":"request-001","ok":true,"code":"ok","message":"Command applied"}
```

| equipmentId | action | 의미 |
|---|---|---|
| `robot` | `mission_start` | 기존 A+B 점검 임무 시작 |
| `robot` | `mission_stop` | 회피 중지와 임무 리셋·정지 |
| `A` / `B` | `pause`, `resume`, `restart`, `stop`, `normal`, `fault` | CSV 재생 제어 |

위 표의 로봇 임무는 Unity 시뮬레이터에 대한 것이다. 현재 TCP에는 `move_robot("pump_A")`나 센서 실측 명령이 없다.
ACK는 명령 적용 응답이며 이동·점검 완료 증거가 아니다.

commandId는 공백이 아닌 1~80자. 연결별 최근 128개에서 같은 ID와 완전히 같은 JSON 문자열은 재실행하지 않고 응답을 반환한다. 같은 ID에 다른 문자열은 `id_conflict`이다. 재연결 후 중복 방지는 보장되지 않으며 미확인 이동 명령을 자동 재전송하지 않는다.
명령 한 줄은 최대 4096자이고 큐 제한이 있다. 세부 제한은 `Assets/EquipmentMonitoring/NETWORK.md`를 참조한다.

telemetry 최상위 필드: `version`, `type=telemetry`, `sequence`, `sentAtUtc`, `equipment`, `mission`.
`mission`은 연결되지 않은 씬에서는 null일 수 있다.

| mission 필드 | 의미 |
|---|---|
| `sessionId` | Unity Play 세션 식별자 |
| `state`, `detail` | NavigationCoordinator 상태와 설명 |
| `canStart` | 아직 시작하지 않았고 Idle인지 |
| `inspections` | 세션에 누적된 점검 완료 기록 |

점검 기록 필드: `id`, `point`, `equipmentId`, `completedAtUtc`, `sourceEquipmentId`, `filePath`, `error`.
`filePath`는 같은 PC의 재생 CSV 경로이며 파일 전송 기능이 아니다. 빈 경로·error는 진단 실패로 처리한다.
점검 id로 중복 처리하고 sessionId 변경 시 이전 상태를 분리한다.

Navigation 상태는 `Idle`, `FollowingLane`, `ConfirmingNode`, `ApproachingTurnCenter`, `SearchingExitLane`, `VisualAlign`, `StraightThroughJunction`, `StraightToNextMarker`, `InspectingEquipment`, `Completed`, `Fault`이다.
Agent의 향후 임무 상태와 동일한 enum으로 취급하지 않는다.

한 Play 세션에서 임무 시작은 1회이다. 정지 후 다시 시작하려면 새 Play가 필요하다. WPF 연결 해제는 자동 정지를 보장하지 않는다.
설비 snapshot의 `simulationLabel`은 재생 상태 설정이고 `diagnosis=NotEvaluated`는 Python 추론 결과가 아니다.

## 진단 Python

근거: `tools/diagnosis/diagnose.py`, `models/manifest.json`, `gui/MarineMonitor.Core/DiagnosisRunner.cs`.

호출 가능한 함수: `diagnose(csv_path, data_root)`.
CLI:

```powershell
python tools/diagnosis/diagnose.py --csv "<data/vibration 내부 CSV 절대 경로>" --data-root "<data 절대 경로>"
```

입력은 data_root/vibration 내부의 `.csv`만 허용한다. 메타데이터 9행을 건너뛰고 0열 시간, 1열 진동을 읽는다. 유효·유한 샘플 최소 100개, 선형 detrend, 특징 26개와 모델 feature_names 일치, 모델 SHA-256 검증이 적용된다.

반환 필드:

```text
fileName, sampleCount, samplingFrequency, mode="offline_csv_replay"
results[]: key, title, abnormalProbability, threshold=0.5, abnormal, modelSha256
key: axis / bearing / belt / rotating
```

네 분류기는 독립 결과이며 class 1이 이상이다. 확률을 검증된 통합 신뢰도로 해석하지 않는다.
CLI 성공은 stdout JSON과 exit 0, 실패는 stderr error JSON과 exit 1이다. 오류를 정상 판정으로 바꾸지 않는다.
현재 WPF 실행기는 비동기 프로세스, 동시 1건, 45초 timeout을 사용한다.
실제 센서 데이터 지원을 추가할 때는 형식과 출처를 새 계약으로 정의하고 `offline_csv_replay` 의미를 유지한다.

진단 Python 의존성은 `tools/diagnosis/requirements.txt`에 고정되어 있다. PPO 학습 환경과 별도 환경을 권장한다.
WPF 환경 설정: `SHIP_ROBOT_PROJECT`, `SHIP_DIAGNOSIS_PYTHON`. 상세 실행은 `tools/diagnosis/README.md` 참조.

## 음성 전사

근거: `tools/voice/server.py`, `Assets/MetaMarine/VR/VoiceTranscriptPanel.cs`.

- HTTP `http://127.0.0.1:8766`.
- `GET /health`: `service`, `ready`, `language`.
- `POST /transcribe`: mono PCM16 16000Hz WAV body, 0.1~20초. Unity 녹음 최대 15초.
- 성공 결과: `text`, `language`. 모델 로딩·busy·잘못된 WAV 등은 HTTP 오류와 `error`.
- Unity의 `TranscriptReady` 이벤트가 Agent 명령 연동 후보이다. 전사 기능 자체에는 목표 해석이나 Tool 실행이 없다.
- 로컬 모델 위치 `tools/voice/models/base`, 의존성 `tools/voice/requirements.txt`.
- 오디오와 전사 로그를 기본 저장하지 않는 현재 설계를 고려한다.

## 실행·검증 진입점

- Unity: `Assets/jetbot_env.unity`를 열고 컴파일 완료 후 Play.
- WPF: `gui/start-dashboard.ps1` 또는 `dotnet run --project gui/MarineMonitor/MarineMonitor.csproj`.
- 진단 테스트: `tools/diagnosis/test_diagnosis.py`.
- 통신·재생 테스트: `tools/EquipmentNetwork.Tests`, `tools/EquipmentMonitoring.Tests`.
- 관제 테스트: `gui/MarineMonitor.Tests`; Unity 참조 컴파일: `gui/UnityIntegration.Check`.

테스트별 전제조건은 각 기존 README와 csproj를 확인한다. 이 목록은 테스트 실행 성공 기록이 아니다.
