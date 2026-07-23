# Samsung Switch Watch

삼성 `IES4224GP`, `IES4028XP`, `IES4226XP` 스위치에 Telnet으로 접속해 조회 명령을
실행하고, 결과와 변경점을 운영자 PC에서 확인하는 Windows 전용 도구입니다.

현재 버전은 `v0.9.5-poc`입니다. 실제 세 모델의 펌웨어에서 검증하기 전까지는
운영 확정판이 아닌 현장 검증용 프리릴리스로 취급해야 합니다.

```text
운영자 PC                                      스위치 접근용 원격 PC
SamsungSwitchWatch.Viewer                     SamsungSwitchWatch.Agent
- 장비 IP·모델 저장                           - 창 없는 Windows 서비스
- ID·PW·enable PW를 사용자 DPAPI로 보호       - 전달받은 정보로 실제 Telnet 접속
- 수동 show 명령과 주기 감시                  - 로그인·enable·명령·로그아웃
- 출력·변경·감시 공백 표시                    - 결과 반환 후 즉시 폐기
             └──────── HTTPS/18443 ────────>            │
                                                       └── Telnet/23 ──> 스위치
```

## 핵심 원칙

- Viewer가 장비, 자격 증명, 감시 일정과 이력의 소유자입니다.
- Agent는 장비나 자격 증명을 저장하지 않는 Telnet 실행 대행자입니다.
- Agent는 `LocalService` Windows 서비스로 실행되어 사용자 화면에 창이나 트레이 아이콘을
  표시하지 않습니다.
- 매 요청마다 새 Telnet 세션을 열고 종료하므로 장비의 짧은 `exec-timeout`에 의존하지 않습니다.
- 명령 실행 중 장비가 연결을 끊으면 완료된 명령은 반복하지 않고 남은 명령만 새 세션에서
  1회 재시도합니다. 인증·enable 실패와 명령 타임아웃은 자동 재시도하지 않습니다.
- 한 줄짜리 `show` 명령을 실행할 수 있으며 `show running-config`도 허용됩니다.
- 수동 명령과 원문 출력은 Viewer 메모리에서만 사용하고 Agent/Viewer DB나 진단 파일에
  저장·내보내지 않습니다.
- Viewer가 꺼져 있으면 주기 감시도 중단됩니다. 다시 실행하면 해당 시간을 `감시 공백`으로
  표시하며 공백 중 발생한 이벤트를 복원할 수 없습니다.

수동 명령 결과에는 사용한 Telnet 세션 수와 재연결 횟수가 함께 표시됩니다.

주요 현장 명령은 다음과 같습니다. 모델이나 펌웨어에서 지원하지 않으면 장비 장애가 아니라
`명령 미지원`으로 구분합니다.

```text
show port status
show sylog tail num 100
show syslog tail num 100
```

## 설치

GitHub Release의 Assets에서 아래 두 ZIP만 받습니다.

- `SamsungSwitchWatch-Agent-0.9.5-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.5-poc-win-x64.zip`

두 ZIP에는 바로 열어 볼 수 있는 `SamsungSwitchWatch_User_Manual_KO.pdf`가 포함됩니다.
편집용 DOCX는 배포 ZIP에 넣지 않습니다.

Agent ZIP을 원격 PC에 풀고 `Install-or-Update-Agent.cmd`를 더블클릭합니다.
UAC를 승인한 뒤 다음 두 범위를 입력합니다.

1. Viewer가 접속하는 관리망 IPv4 CIDR
2. Agent가 Telnet으로 접속해도 되는 스위치 IPv4 CIDR

설치기는 신규 설치와 기존 서비스를 자동 판별합니다. 업데이트라면 기존 CIDR 설정과
`%ProgramData%\SamsungSwitchWatch`의 HTTPS 신원 자료를 보존하고, 검증 실패 시 이전 버전으로
복구합니다.

Viewer ZIP을 운영자 PC에 풀고 `Install-or-Update-Viewer.cmd`를 더블클릭합니다. 관리자
권한은 필요하지 않으며 현재 Windows 사용자에게 설치되고 로그인 시 자동 시작됩니다.

설치 전 검사, 설치 위치 또는 자동 시작 상태를 직접 지정하는 관리자는 기존 PowerShell
경로를 사용할 수 있습니다.

```powershell
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows -Preflight
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows
```

`-StartWithWindows`를 생략하면 기존 자동 시작 상태를 보존합니다. 자동 시작을 끌 때만
`-DisableStartWithWindows`를 지정합니다.

Viewer에서 Agent 주소를 연결한 뒤 장비 IP, 모델, ID, 로그인 PW, 선택적 enable PW를
등록합니다. 인증서 SHA-256 지문이나 페어링 토큰을 사용자가 입력하는 과정은 없습니다.

상세 절차는 [설치 및 운영 안내](docs/INSTALL_KO.md)를 확인하십시오.

## 개발과 검증

Windows x64와 `global.json`에 고정된 .NET 10 SDK가 필요합니다.

```powershell
dotnet restore .\SamsungSwitchWatch.sln --locked-mode
.\scripts\validate.ps1 -Configuration Release
```

회사망 자료 없이 합성 Telnet 서버와 sanitized fixture로 검증합니다. 합성 테스트 통과를
실제 펌웨어 검증으로 표현하지 않습니다.

릴리스 패키지는 깨끗한 Git 작업 트리에서 만듭니다.

```powershell
.\scripts\build-release.ps1 -Version 0.9.5-poc
```

`artifacts\release` 내부에는 ZIP 2개와 내부 검증용 매니페스트·SBOM·해시 파일이 생깁니다.
GitHub Release의 사용자 정의 Assets에는 Agent ZIP과 Viewer ZIP, 정확히 두 개만 게시합니다.

## 보안 경계

- Agent–Viewer 구간은 고정 `HTTPS/18443`을 사용합니다.
- Agent가 만든 ECDSA P-256 신원은 Agent 데이터 폴더에 영구 저장되고 Windows DPAPI
  LocalMachine 범위로 보호됩니다.
- Viewer는 첫 연결에서 Agent 신원을 자동 신뢰하고 이후 변경을 감지합니다.
- 애플리케이션 로그인은 없습니다. Windows 방화벽의 Viewer 관리 CIDR이 API 접근 경계입니다.
- Agent는 설치 시 지정한 대상 CIDR의 IPv4 및 `Telnet/23`으로만 접속합니다.
- 같은 허용 관리망의 다른 API 클라이언트도 Agent를 호출할 수 있으므로 사용자 VLAN, 공용 Wi-Fi,
  인터넷 또는 신뢰하지 않는 중계망에 노출하면 안 됩니다.
- Telnet의 ID, PW, enable PW와 명령 내용은 암호화되지 않으므로 Agent와 스위치 사이를 격리된
  관리망으로 제한해야 합니다.

자세한 내용은 [보안 설계](docs/SECURITY.md)를 확인하십시오.

## 문서

- [설치 및 운영 안내](docs/INSTALL_KO.md)
- [아키텍처와 API](docs/ARCHITECTURE.md)
- [보안 설계](docs/SECURITY.md)
- [현장 POC 체크리스트](docs/FIELD_POC_CHECKLIST_KO.md)
- [릴리스 절차](docs/RELEASE_PROCESS_KO.md)
- [0.9.5-poc 릴리스 노트](docs/RELEASE_NOTES_0.9.5_POC_KO.md)
- [0.9.4-poc 릴리스 노트](docs/RELEASE_NOTES_0.9.4_POC_KO.md)
- [0.9.3-poc 릴리스 노트](docs/RELEASE_NOTES_0.9.3_POC_KO.md)
- [0.9.2-poc 릴리스 노트](docs/RELEASE_NOTES_0.9.2_POC_KO.md)
- [Figma handoff](docs/figma/README.md)

Figma source of truth:
[Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
