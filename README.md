# Samsung Switch Watch

원격 PC의 숨겨진 Windows 서비스가 삼성 iES 스위치에 Telnet으로 접속하고, 운영자 PC의
Viewer가 장비 등록·조회 명령·결과 확인·주기 감시를 담당하는 Windows 전용 POC입니다.

현재 버전은 `v0.10.16-poc`입니다. IES4224GP, IES4028XP, IES4226XP의 실제 펌웨어별
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

- `SamsungSwitchWatch-Agent-0.10.16-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.16-poc-win-x64.zip`

두 패키지는 Windows x64용 self-contained 빌드이므로 Python이나 .NET을 별도로 설치하지
않습니다. Agent와 Viewer는 반드시 같은 Release의 조합을 사용합니다.

### 1. Agent PC

1. Agent ZIP을 로컬 임시 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행하고 UAC를 한 번 승인합니다.
3. `허용할 Viewer PC 고정 IPv4 · Agent PC 주소 아님`에 원격 Viewer PC의 주소 한 개를
   입력합니다. 같은 PC 시험이라면 `같은 PC 시험용 주소`를 눌러 Agent PC의 실제 사설 IPv4를
   사용합니다.
4. 자동 검색된 관리망을 선택합니다. 목록에 없으면 승인된 RFC1918 사설망을
   `IPv4/prefix`로 직접 추가하며, 자동 선택과 직접 추가를 합해 1~2개만 사용합니다.
5. `검사`에서 서비스·HTTPS/18443·방화벽 상태를 확인한 뒤 `설치/업데이트`를 실행합니다.

Setup이 이전 설치·업데이트의 미완료 작업 기록을 발견하면 해당 기록을 읽기 전용으로
검사하고 `설치/업데이트`를 비활성화합니다. 복구 가능 상태일 때만 `이전 상태 복구`를
누르십시오. Setup은 검증된 staging·backup·failed·journal 경로만 제한적으로 다시 정리하고
각 대상이 실제로 사라졌는지 확인합니다. 새로 검사한 작업 기록에서도 미완료 상태가 없어야만
복구 성공과 설치 버튼 활성화를 표시합니다. 설치가 자동으로 이어지지는 않으므로 운영자가
검사 결과를 확인한 뒤 별도로 설치 또는 업데이트해야 합니다. 작업 기록이 손상됐거나 안전한
복구를 증명할 수 없으면 복구와 설치를 모두 중단하고 Windows 관리자에게 확인합니다.

설치와 복구가 모두 실패하면 최초 설치·업데이트 원인과 복구 단계별 원인을 나누어 표시합니다.
이때만 보이는 `진단정보 복사`는 민감정보를 제외한 진단 요약을 클립보드에 복사합니다.
정리 실패 화면의 상단 상태는 `SETUP_ROLLBACK_FAILED`이고, 세부 행은 실제 경로 대신
staging·backup·failed·journal 중 어느 안전 단계에서 실패했는지
`ROLLBACK_STAGING_CLEANUP_FAILED`, `ROLLBACK_BACKUP_CLEANUP_FAILED`,
`ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`, `ROLLBACK_JOURNAL_CLEANUP_FAILED`로 구분합니다.
`.__staging_*`, `.__backup_*`, `.__failed_*` 폴더나 작업 기록을 수동으로 삭제·이동·이름
변경하지 마십시오.

`0.10.16-poc`는 `0.10.15-poc`에서 수정한 일부 Windows Schannel 환경의 로컬 HTTPS 준비 경로를
그대로 유지합니다. Agent는 DPAPI LocalMachine으로 보호한 기존 신원을 유지하며,
개인 키를 내보내거나 사용자 키 저장소에 별도 키 컨테이너로 영구 유지하지 않고 서비스 계정의
사용자 키 집합으로 프로세스 수명 동안 불러옵니다. Agent가 종료되면 호스트가 인증서 수명을
정리합니다.
Setup은 매 준비 상태 재시도마다 새 HTTPS 연결을 사용하고 HTTP/1.1로 연결을 닫아 실패한 TLS
연결 상태를 다음 시도에 재사용하지 않습니다.

이 수정은 성공 판정을 완화하지 않습니다. 사전 점검은 구형 Agent의 최소 `/health/ready`
응답과 API v4를 확인하고, 설치·업데이트 완료 판정은 로컬 HTTPS 성공 응답, API v4, HTTPS
프로토콜과 새 패키지의 정확한 제품 버전을 모두 계속 요구합니다. 확인하지 못하면 설치 실패와
rollback으로 처리합니다. 예상하지 못한 Setup 실패는 안전한 단계·범주와 제한된 소요 시간으로만
진단하고 실제 PID, 경로와 예외 원문은 기록하지 않습니다.

설치 완료 직전 로컬 준비 상태 확인이 실패하면 Setup은
`Setup → 127.0.0.1:18443 → Agent 서비스` 구간을 `HTTPS_TLS_FAILED`,
`HTTPS_REQUEST_TIMEOUT`, `HTTPS_CONNECTION_RESET`, `HTTPS_EOF`,
`HTTPS_CONNECT_FAILED` 중 하나로 구분합니다. 이는 Agent PC 내부 통신 실패이며 Viewer IP나
스위치 관리망 문제를 뜻하지 않습니다. 익명 진단은 서비스 실행, TCP/18443 수신 소유,
HTTPS 시도 횟수·마지막 전송 단계와 재시작 관측 여부만 기록하고 민감정보는 기록하지
않습니다. 실패 단계 시간은 `SERVICE_STARTED → SETUP_HEALTH_FAILED → ROLLBACK_COMPLETED`
순서로 나뉘어 표시됩니다. 짧은 SWD1 지원 코드는 하위 호환을 위해 기존
`HTTPS_REQUEST_FAILED` 범주를 유지합니다.

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
   같은 PC에서 먼저 시험할 때는 `Agent와 Viewer가 같은 PC일 때 테스트`를 직접 눌러 실제
   사설 IPv4를 찾습니다.
4. 장비 관리에서 장비명, 모델, IPv4, ID, 로그인 PW와 선택적 enable PW를 등록합니다.
5. 접속 시험 후 `show port status`, `show syslog tail num 100` 또는 장비에서 지원하는
   읽기 전용 명령을 실행합니다.

인증서 SHA-256 지문이나 페어링 토큰을 입력하는 절차는 없습니다. Viewer는 최초 연결에서
Agent의 공개 신원을 내부적으로 자동 저장하고, 같은 주소의 신원이 실제로 바뀐 경우에만
보호를 위해 연결을 중단합니다.

동일 PC 사전 테스트는 자동으로 실행되지 않으며 Agent 서비스, TCP/18443, HTTPS, Agent API와
버전만 확인합니다. 스위치에는 접속하지 않고 자격 증명이나 명령도 보내지 않습니다. 성공해도
원격 Viewer PC에서 Agent PC로 가는 방화벽·라우팅 경로는 검증되지 않으므로, 실제 배치 전에는
Agent Setup에 원격 Viewer의 고정 IPv4를 다시 입력하고 원격 Viewer에서 연결을 확인해야 합니다.
`localhost`, `localhost.`와 `127.x.x.x`는 동일 PC 시험에서도 Agent 주소로 사용하지 않습니다.

연결 검사가 끝나면 성공 또는 실패와 관계없이 `익명 진단 저장`을 사용할 수 있습니다. 이
최대 12줄 TXT는 일반/같은-PC 모드, 주소·DNS·TCP·HTTPS·Identity 단계 상태와 제한된 소요
시간, 후보 수와 확인된 Agent/API 버전만 남기며 입력 주소·DNS 이름과 장비 정보는 저장하지
않습니다.
연결 실패 때는 같은 형식의 짧은 `지원 코드`가 연결 단계 아래에만 나타납니다. 별도 복사 버튼은
없으며 읽기 전용 코드를 선택해 `Ctrl+C`로 복사합니다. 성공하면 코드가 나타나지 않습니다.

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

Windows가 단일 호스트 방화벽 주소를 `/32` 대신 `/255.255.255.255`로 반환해도 Setup은
같은 Viewer IPv4인지 의미 기준으로 확인합니다. 적용 직후 Windows 반영 지연은 최대 2초까지만
재확인하며, 그 뒤에도 방향·동작·프로토콜·포트·주소·프로필·Edge Traversal 중 하나가 다르면
정확한 제품 소유 규칙만 원격 연결 준비 완료로 인정합니다. 규칙 적용·재조회 또는 회사 GPO
정책 확인이 실패하면 `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고를 표시하지만, Agent의 로컬
HTTPS/API/버전 준비 상태가 정상이라면 설치된 서비스는 유지합니다. 이 경우 Viewer에서 원격
연결을 확인하고 필요하면 Windows 관리자에게 방화벽·GPO 정책을 요청해야 합니다. 제품은
`Any`, `LocalSubnet` 또는 넓은 대역 규칙을 만들지 않습니다.
이 오류를 피하려고 규칙을 `Any`, `LocalSubnet` 또는 넓은 대역으로 수동 변경하지 마십시오.

정확한 제품 규칙까지 확인되면 `설치 완료 · 원격 연결 준비됨`, 방화벽 확인만 남으면
`설치 완료 · 원격 Viewer 연결 확인 필요`로 표시합니다. Viewer의 TCP/18443 단계가 실패하면
방화벽·GPO·라우팅을 확인하고, TCP는 성공했지만 HTTPS 단계가 실패하면 Agent PC의 로컬
HTTPS/TLS 준비 상태를 확인합니다.

## 보안 경계

- Viewer→Agent는 HTTPS/TCP 18443을 사용합니다.
- Agent Setup은 입력한 고정 Viewer IPv4만 Windows 방화벽에서 `/32`로 허용하고, Agent도
  같은 주소를 모든 API 요청에서 다시 확인합니다.
- 방화벽 조회 결과는 같은 IPv4의 `IP`, `IP/32`, `IP/255.255.255.255`만 동일한 단일
  호스트로 인정합니다. 다른 prefix, 주소 목록·범위와 특수 범위는 거부합니다.
- Agent→스위치는 Setup에서 선택하거나 직접 추가한 관리망의 IPv4와 Telnet/TCP 23만
  허용합니다. 직접 입력한 호스트 주소는 canonical 네트워크 주소로 정규화되고, 공인망과
  중복 범위는 거부됩니다.
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
.\scripts\build-release.ps1 -Version 0.10.16-poc
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
- [0.10.16-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.16_POC_KO.md)
- [0.10.15-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.15_POC_KO.md)
- [0.10.14-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.14_POC_KO.md)
- [0.10.13-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.13_POC_KO.md)
- [0.10.12-poc 릴리스 노트](docs/RELEASE_NOTES_0.10.12_POC_KO.md)
- [Figma 화면 설계 및 개발 전달](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)
