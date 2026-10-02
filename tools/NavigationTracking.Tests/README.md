# 차선 안정화 회귀 검증

프로젝트 루트에서 `dotnet run --project tools/NavigationTracking.Tests/NavigationTracking.Tests.csproj`를 실행한다(.NET 10).
같은/과거 프레임 반복 집계 방지, 연속 새 프레임 확인, 만료/잘못된 관측의 streak 초기화, 재탐색 초기화, 비유한 timestamp 거부를 검증한다.
2026-10-02 실행 통과. Unity 참조 주행 코드 컴파일도 통과했으며 실제 카메라·주행 성공을 증명하는 테스트는 아니다.

경계 정렬 제어 검증: sin(θ) 조향, 평행한 선의 횡오차 보정, 좌우 대칭과 양의 전진, 전진 증가 및 조향/전진 상한을 확인한다. 실제 영상 인식·바퀴 동작·Unity Play 주행은 이 검증 범위에 포함하지 않는다.

HSV mask regression checks: compact specks, vertical/diagonal thin markings, large areas, edge wrapping, repeated filtering and resized buffers. Camera/Play performance remains unverified.

Cosine forward regression: aligned maximum, perpendicular minimum, symmetric positive motion, monotonic slowing with increasing angle, lateral steering independence and no reverse motion.

Current forward control is constant: angle/lateral independence and symmetric positive movement are verified; the earlier cosine cases are historical.

NavigationCsvLog checks cover CSV commas/quotes/newlines, live flush using a shared reader, header and disposal. Stronger alignment steering retains constant forward speed. Unity Play logging is not exercised by these checks.

Exit-heading constraint regression covers both signs, heading deadband, overriding opposing visual steering outside the range, blocking outward correction at its edge, and preserving interior correction.
