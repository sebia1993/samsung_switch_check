# Samsung Switch Watch

원격 PC의 숨겨진 Windows 서비스가 삼성 iES 스위치에 Telnet으로 접속하고, 운영자 PC의
Viewer가 장비 등록·조회 명령·결과 확인·주기 감시를 담당하는 Windows 전용 POC입니다.

현재 버전은 `v0.11.3-poc`입니다. IES4224GP, IES4028XP, IES4226XP의 실제 펌웨어별
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

- `SamsungSwitchWatch-Agent-0.11.3-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.11.3-poc-win-x64.zip`

두 패키지는 Windows x64용 self-contained 빌드이므로 Python이나 .NET을 별도로 설치하지
않습니다. API v4가 호환되면 버전 차이는 경고 후 연결되지만, 운영에는 같은 Release 조합을
권장합니다.

### 1. Agent PC

1. Agent ZIP을 로컬 임시 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행하고 UAC를 한 번 승인합니다.
3. 별도 IP나 CIDR을 입력하지 않고 `설치/업데이트`를 실행합니다. Setup이 필요한 사전 검사를
   수행한 뒤 Agent 서비스와 제품 전용 방화벽 규칙을 자동으로 구성합니다.
4. 완료 또는 연결 확인 경고를 확인합니다. 경고가 있어도 서비스 설치는 유지되므로 Viewer에서
   먼저 연결을 시험합니다.

Setup이 이전 설치·업데이트의 미완료 작업 기록을 발견하면 해당 기록을 읽기 전용으로
검사하고 `설치/업데이트`를 비활성화합니다. 복구 가능 상태일 때만 `이전 상태 복구`를
누르십시오. Setup은 검증된 staging·backup·failed·journal 경로만 제한적으로 다시 정리하고
각 대상이 실제로 사라졌는지 확인합니다. 새로 검사한 작업 기록에서도 미완료 상태가 없어야만
복구 성공과 설치 버튼 활성화를 표시합니다. 설치가 자동으로 이어지지는 않으므로 운영자가
상태를 확인한 뒤 별도로 설치 또는 업데이트해야 합니다. 작업 기록이 손상됐거나 안전한
복구를 증명할 수 없으면 복구와 설치를 모두 중단하고 Windows 관리자에게 확인합니다.

설치와 복구가 모두 실패하면 최초 설치·업데이트 원인과 복구 단계별 원인을 나누어 표시합니다.
이때만 보이는 `진단정보 복사`는 민감정보를 제외한 진단 요약을 클립보드에 복사합니다.
정리 실패 화면의 상단 상태는 `SETUP_ROLLBACK_FAILED`이고, 세부 행은 실제 경로 대신
staging·backup·failed·journal 중 어느 안전 단계에서 실패했는지
`ROLLBACK_STAGING_CLEANUP_FAILED`, `ROLLBACK_BACKUP_CLEANUP_FAILED`,
`ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`, `ROLLBACK_JOURNAL_CLEANUP_FAILED`로 구분합니다.
`.__staging_*`, `.__backup_*`, `.__failed_*` 폴더나 작업 기록을 수동으로 삭제·이동·이름
변경하지 마십시오.

`0.11.3-poc`는 설치 성공과 원격 연결 준비 확인을 분리합니다. 실행 파일·서비스 구성 같은
설치 변경이 실패하면 기존 트랜잭션 복구를 수행하지만, 서비스가 설치된 뒤 로컬 HTTPS/API,
버전 또는 방화벽 준비 상태를 확인하지 못한 경우에는 정상 설치를 되돌리지 않습니다. 대신
`AGENT_LOCAL_CONNECTION_UNCONFIRMED` 또는 방화벽 경고와 다음 확인 절차를 표시하며 Agent
서비스는 유지합니다. 이 변경으로 로컬 HTTPS 진단 실패가 반복 설치와 복구 실패로 확대되는
문제를 막습니다.

이번 버전은 설치 버튼이 읽기 전용 진단과 실제 배포에서 같은 서비스 상태를 연속 두 번 조회하던
흐름을 하나의 트랜잭션 설치로 단순화합니다. 초기 서비스 조회가 일시적으로 실패하면 200ms 뒤
한 번만 다시 확인하며, 계속 실패하면 `SETUP_UNEXPECTED`가 아니라
`SETUP_SERVICE_FAILED`로 표시합니다. 서비스 상태를 읽은 뒤에는 설치 결과에도
`SERVICE_NOT_INSTALLED`, `SERVICE_RUNNING` 또는 `SERVICE_STOPPED`를 남깁니다. 기존 서비스의
보안 설명자만 읽을 수 없는 경우에는 나머지 구성·상태를 사용해 설치를 계속하되 기존 서비스
보안 설정은 변경하지 않습니다. 설명자를 확보한 경우에만 새 제한 DACL을 적용하고 실패 시
원래 DACL까지 복원합니다.

Agent는 시작할 때마다 새 임시 RSA 자체 서명 인증서를 만들며 영구 Agent 신원 파일은 저장하지
않습니다. Windows Schannel 호환성을 위해 개인 키는 프로세스 수명 동안 임시 사용자 키
컨테이너에 로드하고, Agent 종료 시 인증서와 임시 키 컨테이너를 정리합니다.
Viewer는 해당 인증서를 자동 수락하므로 인증서 지문, 페어링 토큰 또는 신원 변경 확인 절차가
없습니다. HTTPS는 전송 내용을 암호화하지만 상대 Agent의 신원을 인증하지는 않습니다. 따라서
Agent와 Viewer는 신뢰할 수 있는 사내 사설망에서만 사용해야 합니다.

Viewer와 Agent의 제품 버전이 달라도 API v4가 호환되면 경고를 표시하고 연결합니다. 기능
호환성을 예측하기 어려우므로 실제 운영에는 같은 Release 조합을 권장합니다.

Viewer는 Agent 연결 교체·종료 중 진행 중인 요청을 안전하게 취소·정리하고, 자동 수집 전과
다른 작업 때문에 수집이 미뤄진 장비를 정상으로 단정하지 않습니다. 연결이 끊기면 현재 상태와
마지막으로 확인한 상태를 구분해 표시합니다. 릴리스 검증은 패키지 계약뿐 아니라 압축을 푼
Viewer·Mock Agent·Agent Setup 실행 파일의 제한된 smoke 검사도 포함합니다.

검사·설치·복구가 성공 또는 실패로 끝나면 `익명 진단 저장`으로
`SSW_FIELD_DIAGNOSTIC/2` UTF-8 BOM TXT를 수동 저장할 수 있습니다. 사진 한 장으로 전달할 수
있도록 최대 12줄, 줄당 88자로 제한하면서 제품·Windows 버전, 안전한 단계·결과·오류·조치
코드와 핵심 상태를 보존합니다. IP/CIDR, PC·사용자명, 계정, 인증서 정보, 절대 경로,
방화벽·예외 원문, 명령과 장비 출력은 제외됩니다. 기존 `/1` 파일도 재현 도구에서 계속
분석할 수 있습니다.

실패 화면에는 `SWD1-XXXX-XXXX-XXXX-XXXX` 형식의 짧은 `지원 코드`도 표시됩니다. 전화나
메신저로 장애 분류를 전달할 때는 이 코드만 선택해 복사할 수 있습니다. 지원 코드는 오프라인에서
생성되고 오류 입력 검사용 CRC를 포함하지만 비밀값, 인증 수단, 페어링 토큰 또는 인증서 지문은
아닙니다. 성공 화면과 실행 중에는 표시되지 않으며 새 작업을 시작하면 이전 코드는 지워집니다.

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
3. Agent PC의 IPv4 또는 사내 DNS 이름을 입력하고 연결 진단을 완료합니다. Agent와 Viewer를
   같은 PC에서 먼저 시험할 때는 `localhost` 또는 `127.0.0.1`을 입력합니다.
4. 장비 관리에서 장비명, 모델, IPv4, ID, 로그인 PW와 선택적 enable PW를 등록합니다.
5. `로그인 확인` 후 수집 진단에서 `show port status`, `show sylog tail num 100` 또는 장비에서
   지원하는 읽기 전용 명령의 실제 동작을 확인합니다.

`로그인 확인`은 TCP/23, 계정, enable과 최종 프롬프트까지만 검사합니다. 자동 수집은 포트 상태와
시스템 로그를 순차적인 개별 세션으로 실행하므로 한 항목이 시간 초과되어도 다른 결과를 계속
수집합니다. 명령은 30초 동안 새 응답이 없을 때 중단하며, 출력이 계속되더라도 전체 90초를
넘기지 않습니다. 이번 POC의 자동 감시 검증·지원 범위는 등록 장비 10대 이하입니다.

인증서 SHA-256 지문이나 페어링 토큰을 입력하는 절차는 없습니다. Viewer는 Agent의 임시 TLS
인증서를 자동 수락하며 인증서 신원을 저장하거나 비교하지 않습니다.

동일 PC에서 `localhost`로 연결하면 Agent 서비스, TCP/18443, HTTPS와 Agent API만 확인합니다.
스위치에는 접속하지 않고 자격 증명이나 명령도 보내지 않습니다. 성공해도 원격 Viewer PC에서
Agent PC로 가는 방화벽·라우팅 경로는 검증되지 않으므로 실제 Viewer PC에서도 연결을 확인해야
합니다.

연결 검사가 끝나면 성공 또는 실패와 관계없이 `익명 진단 저장`을 사용할 수 있습니다. 이
최대 12줄 TXT는 주소·DNS·TCP·HTTPS·API 단계 상태와 제한된 소요 시간, 확인된 Agent/API
버전만 남기며 입력 주소·DNS 이름과 장비 정보는 저장하지
않습니다.
연결 실패 때는 같은 형식의 짧은 `지원 코드`가 연결 단계 아래에만 나타납니다. 별도 복사 버튼은
없으며 읽기 전용 코드를 선택해 `Ctrl+C`로 복사합니다. 성공하면 코드가 나타나지 않습니다.

상세 절차와 연결 실패 단계는 [설치 및 운영 안내](docs/INSTALL_KO.md)를 확인하십시오.

## 연결 문제 확인 순서

Viewer의 연결 진단은 다음 순서로 표시됩니다.

1. Agent 주소·DNS
2. TCP/18443
3. HTTPS
4. Agent API와 준비 상태
5. Agent·Viewer API 호환성과 버전 경고

`AGENT_CONNECTION_REFUSED`가 표시되면 Agent PC에서 Agent Setup을 다시 열어 서비스 설치
상태를 확인하고, Viewer에 실제 Agent PC 주소를 입력했는지 확인합니다. 스위치 IP나 Viewer PC
주소를 Agent 주소 입력란에 넣지 않습니다. 설치가 완료됐는데도 TCP 단계가 실패하면 Windows
방화벽·GPO·라우팅을 확인합니다.

Setup은 제품 소유 방화벽 규칙에 Domain/Private 프로필의 TCP/18443 인바운드와 RFC1918
사설 IPv4 원격 대역만 허용하도록 시도합니다. 규칙 적용·재조회 또는 회사 GPO 확인이 실패해도
설치를 되돌리지 않고 `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고를 표시합니다. 이 경우 Viewer
연결 테스트가 성공하면 그대로 사용할 수 있고, TCP 단계가 실패할 때만 Windows 관리자에게
방화벽·GPO·라우팅 확인을 요청합니다.

정확한 제품 규칙까지 확인되면 `설치 완료 · 원격 연결 준비됨`, 방화벽 확인만 남으면
`설치 완료 · 원격 Viewer 연결 확인 필요`로 표시합니다. Viewer의 TCP/18443 단계가 실패하면
방화벽·GPO·라우팅을 확인하고, TCP는 성공했지만 HTTPS 단계가 실패하면 Agent PC의 로컬
HTTPS/TLS 준비 상태를 확인합니다.

## 보안 경계

- Viewer→Agent는 HTTPS/TCP 18443을 사용합니다.
- Agent Setup은 Domain/Private 프로필에서 RFC1918 사설 IPv4 원격 대역의 TCP/18443만 허용하는
  제품 방화벽 규칙을 자동 구성하려고 시도합니다.
- Agent API도 loopback과 RFC1918 IPv4 요청만 허용합니다. 인증 기능이 아니므로 사용자 VLAN,
  공용 Wi-Fi 또는 인터넷에 노출하면 안 됩니다.
- Agent→스위치는 RFC1918 사설 IPv4와 Telnet/TCP 23만 허용합니다.
- Agent API에는 별도 로그인, 페어링 토큰 또는 인증서 신원 검증이 없습니다.
- Agent의 자체 서명 RSA 인증서는 매 서비스 시작 시 새로 생성됩니다. Windows Schannel용
  임시 사용자 키 컨테이너는 프로세스 수명에만 사용하고 종료 시 제거합니다.
- Telnet 구간은 암호화되지 않습니다. Agent와 스위치는 격리된 관리망에서만 사용합니다.
- 실제 IP, 계정, 비밀번호, 장비 출력과 회사 데이터는 저장소·테스트·이슈에 올리지 않습니다.

## 개발과 검증

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
.\scripts\validate.ps1 -Configuration Release
.\scripts\build-release.ps1 -Version 0.11.3-poc
```

실제 장비 대신 합성 Telnet 서버와 비식별 Fixture를 사용합니다. Mock 통과를 실제 펌웨어
검증으로 표현하지 않습니다.

자동 검증은 실제 삼성 스위치의 펌웨어별 명령·출력, 사내 EDR/백신 정책, 원격 PC 사이의
방화벽·라우팅과 관리자 권한이 필요한 전체 Agent 설치 과정을 증명하지 않습니다. 이 항목들은
공식 두 ZIP을 사용해 승인된 사내 시험 PC에서 단계적으로 확인해야 합니다.

PowerShell/CMD 설치·제거·진단 스크립트는 개발과 레거시 복구를 위해 저장소에만 유지하며
공개 ZIP에는 포함하지 않습니다. GitHub Release의 사용자 정의 Assets는 Agent ZIP과 Viewer
ZIP 정확히 두 개입니다.

## 문서

- [설치 및 운영 안내](docs/INSTALL_KO.md)
- [구조 설명](docs/ARCHITECTURE.md)
- [v0.10.12 프로젝트 진단 및 개선 계획](docs/PROJECT_DIAGNOSIS_0.10.12_KO.md)
- [보안 모델](docs/SECURITY.md)
- [현장 POC 점검표](docs/FIELD_POC_CHECKLIST_KO.md)
- [릴리스 절차](docs/RELEASE_PROCESS_KO.md)
- [0.11.3-poc 릴리스 노트](docs/RELEASE_NOTES_0.11.3_POC_KO.md)
- [0.11.2-poc 릴리스 노트](docs/RELEASE_NOTES_0.11.2_POC_KO.md)
- [0.11.1-poc 릴리스 노트](docs/RELEASE_NOTES_0.11.1_POC_KO.md)
- [0.11.0-poc 릴리스 노트](docs/RELEASE_NOTES_0.11.0_POC_KO.md)
- [0.10.16-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.16_POC_KO.md)
- [0.10.15-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.15_POC_KO.md)
- [0.10.14-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.14_POC_KO.md)
- [0.10.13-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.13_POC_KO.md)
- [0.10.12-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.12_POC_KO.md)
- [Figma 화면 설계 및 개발 전달](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
