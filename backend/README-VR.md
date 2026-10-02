# VR Backend 통합 안내

서버가 하나로 통합됐다. 기존 `backend.vr_main:app`은 `backend.main:app`과 동일한 앱의 호환 별칭이다. 실행 포트는 8767이며 `backend/start-backend.bat`를 사용한다.

설치, Agent 설정, 테스트와 검증 범위는 [README.md](README.md)를 따른다. 기존 VR 요청 복구·취소·points 계약은 유지하며 기본 모드는 OpenAI Agent이다. 외부 API 없는 고정 A+B 테스트는 `MARINE_AGENT_MODE=fixed_ab`를 명시한다.
