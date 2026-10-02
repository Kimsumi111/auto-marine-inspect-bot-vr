# Schema 범위

이 폴더의 기존 MissionSnapshot/MissionResult/CreateMissionRequest 등은 이전 8000 Agent 계약의 참고 자료이다. 통합 서버가 이 응답 형식을 사용한다고 가정하지 않는다.

현재 운영 REST 계약: docs/VR_BACKEND_API_V1.md, 요청 모델: backend/models.py, Unity 수신 DTO: Assets/MetaMarine/VR/MissionContract.cs. 현재 요청 schema는 VRMissionRequest.json이다. 서버 /openapi.json에서도 요청을 확인할 수 있다.
