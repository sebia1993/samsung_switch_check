# Samsung Switch Watch 0.9.15-poc 릴리스 노트

## Viewer 최초 연결 거부 수정

- 새 Viewer가 원격 Agent 주소를 받기 전에 자기 PC의 `localhost:18443`으로 접속하던
  기본값을 제거했습니다.
- 설정 파일이 없거나 손상된 최초 실행에서는 네트워크 접속을 시도하지 않고
  `연결 설정 필요` 상태와 빈 Agent 주소 입력란을 표시합니다.
- 같은 PC에 Agent와 Viewer를 함께 설치해 명시적으로 `localhost`를 저장한 기존 사용자는
  그대로 사용할 수 있습니다.
- `AGENT_CONNECTION_REFUSED` 안내는 스위치 IP나 Viewer PC가 아니라 실제 Agent를 설치한
  PC의 IPv4 또는 DNS 이름인지 먼저 확인하도록 바꿨습니다.

## 설치 및 현장 진단

- Agent 설치 성공은 설치 시점의 로컬 HTTPS readiness까지 검증한다는 사실을 문서화했습니다.
- 연결 거부 시 `Agent 주소 → Agent 서비스와 로컬 health → Viewer PC의 TCP/18443` 순서로
  확인하도록 설치 안내와 사용자 매뉴얼을 정리했습니다.
- 진단 파일 예시는 항상 존재하는 현재 사용자 임시 폴더를 사용합니다.

## 검증과 호환성

- Viewer 설정, 연결 오류 분류, WPF 화면과 전체 회귀 테스트를 실행합니다.
- Agent API, 장비 설정, 감시 이력, 자격 증명 저장 형식과 방화벽 정책은 변경하지 않습니다.
- 실제 사내 방화벽, EDR, 라우팅과 삼성 스위치 펌웨어는 현장 검증이 필요합니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.15-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.15-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
