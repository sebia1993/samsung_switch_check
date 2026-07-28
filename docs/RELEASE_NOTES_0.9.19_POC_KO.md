# Samsung Switch Watch 0.9.19-poc 릴리스 노트

## Agent 전환 중 감시 요청 격리

- Viewer가 주기 감시를 수행하는 도중 Agent 주소를 변경해도, 기존 감시 작업의 대체 명령은
  감시를 시작한 기존 Agent 인스턴스에만 요청합니다.
- Agent가 교체된 것을 감지하면 기존 Agent의 늦은 대체 명령 결과와 수집기 상태를 폐기합니다.
- 따라서 이전 감시 작업에 포함된 장비 IP, ID, 로그인 PW와 enable PW가 새 Agent로
  전달되지 않습니다.
- 다음 감시 주기는 새 Agent에서 기존과 같은 방식으로 새로 시작합니다.

## 호환성과 운영 영향

- Agent API v4, Viewer 설정, 장비 저장 형식과 모니터링 이력 형식은 변경하지 않았습니다.
- 스위치 명령, 실행 주기, 동시 접속 제한과 Telnet 세션 종료 방식은 변경하지 않았습니다.
- 사용자 화면과 설치 순서는 변경하지 않았습니다.
- 기존 Agent와 Viewer를 함께 업데이트하는 현재 설치 절차를 그대로 사용합니다.

## 검증

- 초기 감시 응답 처리 중 Agent를 교체하는 결정적 경합 테스트를 추가했습니다.
- 테스트는 기존 감시의 단일 대체 명령이 새 Agent로 전달되지 않고, 새 Agent에서는 다음
  정상 감시의 두 명령 요청만 실행되는지 확인합니다.
- Core, Agent, Viewer 자동 테스트와 설치·복구·릴리스 계약 검사를 통과한 산출물만
  게시합니다.

## 보안과 현장 검증 범위

- Agent 방화벽은 등록한 Viewer IPv4의 정확한 `/32`, Domain/Private 프로필만 허용합니다.
- 설치기는 파일 차단 해제, ACL 완화 또는 보안 제품 우회를 수행하지 않습니다.
- 이 `-poc` 패키지는 Authenticode 서명이 없는 현장 검증용 프리릴리스입니다.
- 합성 Telnet 서버와 로컬 계약 테스트로 검증했으며, 실제 사내 EDR·AppLocker·WDAC 정책과
  IES4224GP, IES4028XP, IES4226XP 펌웨어는 현장에서 확인해야 합니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.19-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.19-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
