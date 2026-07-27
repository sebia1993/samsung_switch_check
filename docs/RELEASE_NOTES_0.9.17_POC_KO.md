# Samsung Switch Watch 0.9.17-poc 릴리스 노트

## Agent 설치 입력 단순화

- 신규 Agent 설치에서는 CIDR을 직접 계산하지 않고 Viewer PC IPv4와 스위치 관리 IPv4만
  입력합니다.
- 설치기는 입력한 각 IPv4를 정확한 `/32` 허용 정책으로 변환해 기존 Windows 방화벽과
  Telnet 대상 제한을 유지합니다.
- 기존 Agent 업데이트는 검증된 설정과 제품 소유 방화벽의 기존 CIDR을 그대로 보존합니다.
- `Configure-Agent-Allowed-IPs.cmd`에서 일반 IPv4 목록만 사용해 허용 대상을 다시 설정할 수
  있습니다. 적용 실패 시 Agent 프로그램·설정·방화벽과 서비스 상태를 설치 전 상태로
  복구합니다.
- 기존 `-ClientManagementCidrs`와 `-AllowedTargetCidrs` PowerShell 옵션은 다수 장비와
  DHCP·서브넷 운영을 위한 고급 호환 경로로 유지합니다.

## Viewer 설치 자체점검 개선

- Viewer 설치기는 `BUILD-MANIFEST.json`에 선언된 모든 파일의 이름·크기·SHA-256을 설치 전과
  보호된 staging 복사 후에 각각 확인합니다.
- 실제 Viewer 창을 5초 동안 실행하던 점검을 Agent 연결·트레이·사용자 설정을 사용하지 않는
  `--install-smoke-check` 무화면 점검으로 교체했습니다.
- 자체점검 프로세스 시작 실패, 제한 시간 초과와 비정상 종료를 별도 상세 코드로 표시합니다.
- 실패 시 설치 journal과 Viewer 런타임 진단 로그 위치를 함께 안내하며, 기존 Viewer 복구
  계약은 유지합니다.

## 보안과 호환성

- Agent와 Viewer CMD는 현재 폴더나 사용자 `PATH`의 동명 프로그램을 사용하지 않고 Windows
  System32의 Windows PowerShell 절대 경로만 실행합니다.
- Agent API v4, `AllowedTargetCidrs` 설정 키, 설치 영수증과 Viewer 저장 형식은 변경하지
  않았습니다.
- Agent API에는 별도 사용자 인증이 없으므로 Viewer PC와 스위치의 정확한 IPv4만 기본
  허용하며 `LocalSubnet`, 사설망 전체 또는 첫 요청 대상을 자동 허용하지 않습니다.
- Telnet은 계속 TCP/23의 읽기 전용 조회 경로에만 사용합니다.
- 이 `-poc` 패키지는 코드 서명된 설치 프로그램이 아닙니다. 승인된 GitHub Release의 원본
  ZIP과 SHA-256을 확인하고, 압축 해제 뒤 PS1·CMD를 수정하거나 신뢰할 수 없는 파일을 같은
  폴더에 추가하지 마십시오.
- 실제 사내 EDR·AppLocker 정책과 IES4224GP, IES4028XP, IES4226XP 펌웨어는 현장에서
  확인해야 합니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.17-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.17-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
