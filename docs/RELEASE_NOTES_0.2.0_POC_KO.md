# Samsung Switch Watch v0.2.0-poc

현장 POC의 설치·수집·이벤트 동기화·Viewer 복원력을 강화한 사전 릴리스입니다.

## 주요 개선

- 배포 ZIP 루트에서 설치 스크립트를 그대로 실행할 수 있도록 패키지 경로 계약을 수정했습니다.
- Agent 설치 전 검사, 실패 시 자동 롤백, 복구 설치(`-Repair`), 서비스 readiness 확인을 추가했습니다.
- Agent 데이터는 고정 서비스 SID로만 접근하도록 폴더 권한을 축소했습니다.
- Viewer 업데이트는 임시 폴더 준비 후 원자적으로 교체하며 실패 시 이전 버전을 복구합니다.
- 이벤트 생성·확인·복구를 v2 변경 피드로 동기화하고 재연결 후 누락분을 복구합니다.
- 명령별 실패 상태, 마지막 정상값 보존, 인증 실패 회로 차단과 Telnet 제한시간을 강화했습니다.
- `/health/live`, `/health/ready`, `/api/v2/snapshot` 기반 진단을 지원합니다.
- 핵심 수집기인 `log_ram` 또는 `interface_status`가 미지원이면 `COLLECTOR_UNUSABLE`로 readiness를 차단합니다.

## 릴리스 파일

- `SamsungSwitchWatch-Agent-0.2.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.2.0-poc-win-x64.zip`
- `SHA256SUMS.txt`

설치 전에 `SHA256SUMS.txt`와 내려받은 ZIP의 SHA-256을 대조하십시오.

## POC 제한사항

- Windows x64, IPv4, `IES4224GP` 한 대, Agent 한 대와 Viewer 한 명을 대상으로 합니다.
- 스위치 연결은 암호화되지 않은 Telnet TCP/23입니다. 격리된 관리망과 장비 ACL이 필수입니다.
- Agent와 Viewer 바이너리 및 PowerShell 스크립트는 코드 서명되지 않았습니다. Windows SmartScreen 경고가 표시될 수 있습니다.
- 실제 장비에서 실행되는 명령은 등록된 읽기 전용 `show` 명령으로 제한됩니다.
- 실제 회사 로그, IP, 호스트명, 계정명, MAC 주소와 명령 원문은 외부 진단 자료에 포함하지 마십시오.

운영 환경으로 확대하기 전에 실제 펌웨어 출력 검증, 장시간 soak test, 사내 인증서와 코드 서명을 별도로 완료해야 합니다.
