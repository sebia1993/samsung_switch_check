# Samsung Switch Watch v0.6.0-poc

## 핵심 변경

- Agent–Viewer 연결을 `HTTP/18443`으로 단순화했습니다.
- 인증서 SHA-256 지문, `SSW1:` 페어링, 일회용 코드와 Bearer 토큰을 제거했습니다.
- Viewer 연결 화면은 Agent IPv4/DNS와 포트만 받습니다.
- `show port status`, `show syslog tail num 100`을 우선 사용하고, 현장 장비의
  `show sylog tail num 100` 표기와 기존 후보 명령도 모델·펌웨어별로 자동 탐지합니다.
- 짧은 VTY 세션을 위해 완료한 명령 결과를 보존하고 남은 명령만 한 번 재접속합니다.

## 보안 주의

HTTP API에는 암호화와 사용자 인증이 없습니다. 설치 시 지정한 1~32개의 고정 Viewer IPv4만
허용하는 Windows 방화벽이 유일한 접근 통제입니다. 격리된 사내 관리망 밖에 노출하지 마십시오.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42
```

CIDR·서브넷·DNS와 축약형·16진수·선행 0 IPv4는 방화벽 입력으로 거부됩니다. 방화벽 서비스나
세 프로필이 꺼져 있거나 기본 인바운드 Allow·외부 중첩 Allow 규칙이 있으면 작업을 중단합니다.
설치 후 주소 변경은 원자적 rollback을 제공하는
`set-viewer-access.ps1`을 사용합니다.

## v0.5.x 자동 마이그레이션

Agent를 먼저 Repair하고 Viewer를 이어서 업데이트하십시오.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -Repair -ReuseData `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42
```

Repair는 DB·WAL/SHM, snapshot/event/raw/audit, 스위치 자격 증명과 장비 설정을 보존합니다.
기존 서비스가 중지되어 있어도 새 Agent를 임시 시작해 readiness와 schema v5 migration을
검증하고 원래 중지 상태로 되돌립니다. 성공 뒤에만 구 인증서 서비스 secret과 설치기 소유
인증서를 제거합니다. 실패하면 프로그램, DB, 서비스 환경, 방화벽과 이전 서비스 상태를
복원합니다.

Viewer는 기존 `https://` authority를 같은 `http://` authority로 전환하고 구 fingerprint/token
설정은 다음 저장 시 제거합니다. 새 연결 identity는 현재 high-watermark에서 기준선을 만들어
이전 이벤트가 대량 재알림되지 않게 합니다.

## 호환성과 검증 범위

- v0.5 Viewer와 v0.6 Agent는 호환되지 않으므로 같은 작업 시간에 순차 업그레이드합니다.
- 실제 `IES4224GP`, `IES4028XP`, `IES4226XP` 펌웨어 검증은 회사망 현장 POC가 필요합니다.
- 로컬 검증은 합성 Telnet 출력, mock Agent, PowerShell 5.1 구문, Windows 패키지 계약을
  사용하며 실제 장비 검증을 대신하지 않습니다.

## 산출물

GitHub Release의 사용자 정의 Assets에는 실행 파일과 설치 자료를 묶은 ZIP 두 개만 게시합니다.

- `SamsungSwitchWatch-Agent-0.6.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.6.0-poc-win-x64.zip`

Actions 내부 artifact에는 ZIP 2개, `BUILD-MANIFEST.json`, SPDX/CycloneDX SBOM 2개,
`SHA256SUMS.txt`까지 정확히 6개를 유지합니다.
