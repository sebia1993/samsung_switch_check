# Samsung Switch Watch

원격 PC의 숨겨진 Windows 서비스가 삼성 iES 스위치에 Telnet으로 접속하고, 운영자 PC의
Viewer가 장비 등록·조회 명령·결과 확인·주기 감시를 담당하는 Windows 전용 POC입니다.

현재 버전은 `v0.10.3-poc`입니다. IES4224GP, IES4028XP, IES4226XP의 실제 펌웨어별
명령과 출력은 사내 현장 검증 전까지 확정된 것으로 간주하지 않습니다.

## 한눈에 보는 구조

```text
Viewer PC                                  Agent PC                          Switch
SamsungSwitchWatch.Viewer.exe              SamsungSwitchWatchAgent 서비스
장비 IP·ID·PW·enable PW 입력 ─ HTTPS/18443 → 창 없는 실행 중계 ─ Telnet/23 → show 명령
결과·변경점·감시 이력 표시                 장비 정보와 결과를 저장하지 않음
```

- Agent는 최초 한 번만 관리자 권한으로 설치하고 이후 창이나 트레이 아이콘 없이 서비스로
  실행합니다.
- Viewer는 ZIP을 풀어 EXE를 직접 실행하는 포터블 프로그램입니다. 설치, UAC, 자동 시작
  등록이 없습니다.
- Viewer가 장비·자격 증명·감시 일정과 이력을 소유합니다. 자격 증명은 현재 Windows
  사용자 DPAPI로 보호합니다.
- Viewer가 종료되면 주기 감시도 중단됩니다. Agent는 독립적으로 장비를 조회하지 않습니다.
- 수동 입력은 줄바꿈이나 구분자가 없는 한 줄 `show` 명령만 허용합니다. 설정 변경 명령은
  Viewer와 Agent 양쪽에서 차단합니다.
- 수동 명령과 원문 출력은 Viewer 메모리에서만 사용하고 저장하거나 내보내지 않습니다.

## 배포 파일

공식 GitHub Release Assets에서 다음 두 ZIP만 받습니다.

- `SamsungSwitchWatch-Agent-0.10.3-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.3-poc-win-x64.zip`

두 패키지는 Windows x64용 self-contained 빌드이므로 Python이나 .NET을 별도로 설치하지
않습니다. Agent와 Viewer는 반드시 같은 Release의 조합을 사용합니다.

### 1. Agent PC

1. Agent ZIP을 로컬 임시 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행하고 UAC를 한 번 승인합니다.
3. Viewer PC가 Agent에 접속할 때 사용하는 고정 IPv4 한 개를 입력합니다.
4. 자동 검색된 직접 연결 사설망 중 스위치 관리망 1~2개를 선택합니다.
5. `검사`에서 서비스·HTTPS/18443·방화벽 상태를 확인한 뒤 `설치/업데이트`를 실행합니다.

설치 후 `SamsungSwitchWatchAgent` 서비스가 자동 시작됩니다. 일반 사용자의 바탕 화면,
작업 표시줄과 트레이에는 Agent 창이 나타나지 않습니다. 로컬 관리자는 Windows 관리
정책상 서비스를 중지할 수 있으므로 관리자 계정 자체를 통제해야 합니다.

### 2. Viewer PC

0.9 설치형 Viewer를 사용했다면 먼저 기존 트레이 메뉴에서 프로그램을 완전히 종료하고
`Win+R` → `shell:startup`과 `shell:programs`에서 이전 자동 시작·시작 메뉴 바로 가기를
삭제합니다. 새 Viewer는 이전 버전과 같은 사용자 데이터를 동시에 쓰지 않도록 동시 실행을
차단하고 이 전환 순서를 안내합니다.

1. Viewer ZIP을 항상 사용할 로컬 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Viewer.exe`를 실행합니다.
3. Agent PC의 IPv4 또는 사내 DNS 이름을 입력하고 연결 진단을 완료합니다.
4. 장비 관리에서 장비명, 모델, IPv4, ID, 로그인 PW와 선택적 enable PW를 등록합니다.
5. 접속 시험 후 `show port status`, `show syslog tail num 100` 또는 장비에서 지원하는
   읽기 전용 명령을 실행합니다.

인증서 SHA-256 지문이나 페어링 토큰을 입력하는 절차는 없습니다. Viewer는 최초 연결에서
Agent의 공개 신원을 내부적으로 자동 저장하고, 같은 주소의 신원이 실제로 바뀐 경우에만
보호를 위해 연결을 중단합니다.

상세 절차와 연결 실패 단계는 [설치 및 운영 안내](docs/INSTALL_KO.md)를 확인하십시오.

## 연결 문제 확인 순서

Viewer의 연결 진단은 다음 순서로 표시됩니다.

1. Agent 주소·DNS
2. TCP/18443
3. HTTPS와 Agent 신원
4. Agent API와 준비 상태
5. Agent·Viewer 버전

`AGENT_CONNECTION_REFUSED`가 표시되면 Agent PC에서 Agent Setup을 다시 열고 `검사`를
실행하십시오. 서비스, 수신 포트와 방화벽 중 어느 단계가 실패했는지 확인한 뒤 Viewer에
실제 Agent PC 주소를 입력했는지 확인합니다. 스위치 IP나 Viewer PC 주소를 Agent 주소
입력란에 넣지 않습니다.

## 보안 경계

- Viewer→Agent는 HTTPS/TCP 18443을 사용합니다.
- Agent Setup은 입력한 고정 Viewer IPv4만 Windows 방화벽에서 `/32`로 허용하고, Agent도
  같은 주소를 모든 API 요청에서 다시 확인합니다.
- Agent→스위치는 선택한 관리망의 IPv4와 Telnet/TCP 23만 허용합니다.
- Agent API에는 별도 로그인이나 페어링 토큰이 없습니다. 고정 Viewer IP의 Windows 방화벽과
  Agent 내부 접근 제한을 함께 사용하므로 사용자 VLAN·공용 Wi-Fi·인터넷에 노출하면 안 됩니다.
- Agent가 만드는 ECDSA P-256 신원은 `%ProgramData%\SamsungSwitchWatch`에 보관하고
  DPAPI LocalMachine으로 보호합니다.
- Telnet 구간은 암호화되지 않습니다. Agent와 스위치는 격리된 관리망에서만 사용합니다.
- 실제 IP, 계정, 비밀번호, 장비 출력과 회사 데이터는 저장소·테스트·이슈에 올리지 않습니다.

## 개발과 검증

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
.\scripts\validate.ps1 -Configuration Release
.\scripts\build-release.ps1 -Version 0.10.3-poc
```

실제 장비 대신 합성 Telnet 서버와 비식별 Fixture를 사용합니다. Mock 통과를 실제 펌웨어
검증으로 표현하지 않습니다.

PowerShell/CMD 설치·제거·진단 스크립트는 개발과 레거시 복구를 위해 저장소에만 유지하며
공개 ZIP에는 포함하지 않습니다. GitHub Release의 사용자 정의 Assets는 Agent ZIP과 Viewer
ZIP 정확히 두 개입니다.

## 문서

- [설치 및 운영 안내](docs/INSTALL_KO.md)
- [구조 설명](docs/ARCHITECTURE.md)
- [보안 모델](docs/SECURITY.md)
- [현장 POC 점검표](docs/FIELD_POC_CHECKLIST_KO.md)
- [릴리스 절차](docs/RELEASE_PROCESS_KO.md)
- [0.10.3-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.3_POC_KO.md)
- [Figma 화면 설계 및 개발 전달](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
