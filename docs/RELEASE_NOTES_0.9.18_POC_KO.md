# Samsung Switch Watch 0.9.18-poc 릴리스 노트

## Viewer 설치 경로와 사용자 설정 분리

- 기본 Viewer 프로그램은 UAC 승인 후
  `C:\Program Files\SamsungSwitchWatch\Viewer`에 설치합니다.
- 바로 가기와 Windows 로그인 자동 시작은 설치를 시작한 원래 Windows 사용자에게만
  적용합니다.
- 장비 목록, 자격 증명, 감시 이력과 Viewer 설정은 기존과 같이 현재 사용자의
  `%LOCALAPPDATA%\SamsungSwitchWatch`에 보존합니다.
- 이전 Program Files 버전은 관리자 보호 rollback 슬롯에 다음 업데이트까지 보존합니다.
  원래 사용자 권한의 실행 검사나 바로 가기 단계가 실패하면 복구 UAC를 통해 이전 버전을
  되돌립니다. 복구가 완료되지 않아도 rollback 슬롯은 자동 삭제하지 않습니다.
- 새 설치 파일이나 매니페스트가 격리·손상된 경우에는 현재 설치의 정상 패키지 검증을
  복구 선행 조건으로 삼지 않고, 보호된 현재 설치를 격리한 뒤 검증된 rollback 슬롯을
  복원합니다.
- 제거 중 Viewer 프로세스 종료 또는 활성 프로그램 폴더 삭제가 확인되지 않으면 유효한
  rollback 슬롯을 삭제하지 않습니다.
- 기존 사용자별 프로그램 폴더와 `%LOCALAPPDATA%\SamsungSwitchWatch`의 장비·계정·감시
  데이터는 자동 삭제하지 않습니다. 새 바로 가기만 Program Files 설치본을 가리킵니다.
- 보안 정책이 Program Files의 실행까지 차단하면 우회하지 않고 안정 진단 코드와 rollback
  결과를 표시합니다. 기존 사용자별 설치가 꼭 필요한 관리자는 `-PerUser` 호환 옵션을
  명시적으로 사용할 수 있습니다.

## Agent 재설정과 현장 진단 강화

- 허용 Viewer IP와 스위치 IP가 이전 값과 같아도 곧바로 성공으로 종료하지 않습니다.
- 서비스 상태, TCP/18443 listener, 로컬 live/ready, 제품 방화벽 규칙과 활성 네트워크
  프로필을 확인해 모두 정상일 때만 변경 없음으로 처리합니다.
- 이상이 있으면 기존 검증 설정을 유지한 채 서비스와 방화벽 적용 절차를 다시 수행합니다.
- `diagnose-agent.ps1`은 설치 버전, 서비스 상태·시작 모드·종료 코드, listener,
  방화벽 상태·프로필, 활성 네트워크 범주, 허용 목록 개수와 live/ready 결과를 분리해
  기록합니다.
- 진단 JSON에는 실제 IP, 경로, 계정, 비밀번호, 명령과 장비 출력이 포함되지 않습니다.

## Viewer 연결 오류 추적

- Viewer 진단에는 앱 버전, 연결 단계와 안정 오류 코드의 상태 전환만 기록합니다.
- 같은 오류가 반복될 때 로그를 계속 늘리지 않고, 연결 거부 뒤 정상 복구되면 복구 전환을
  남기고 화면의 오래된 연결 오류를 지웁니다.
- Agent API v4, Viewer 설정·저장 형식, 스위치 자격 증명 소유권과 읽기 전용 `show`
  실행 정책은 변경하지 않았습니다.

## 보안과 현장 검증 범위

- Agent 방화벽은 등록한 Viewer IPv4의 정확한 `/32`, Domain/Private 프로필만 허용합니다.
  `LocalSubnet` 또는 Public 프로필을 자동 허용하지 않습니다.
- 설치기는 파일 차단 해제, ACL 완화 또는 보안 제품 우회를 수행하지 않습니다.
- 이 `-poc` 패키지는 Authenticode 서명이 없는 현장 검증용 프리릴리스입니다.
- 합성 Telnet 서버와 로컬 계약 테스트로 검증했으며, 실제 사내 EDR·AppLocker·WDAC 정책과
  IES4224GP, IES4028XP, IES4226XP 펌웨어는 현장에서 확인해야 합니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.18-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.18-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
