# Samsung Switch Watch 설치 및 운영 안내

## 1. 필요한 파일

공식 GitHub `v0.9.18-poc` Release의 Assets에서 다음 두 파일만 받습니다.

- `SamsungSwitchWatch-Agent-0.9.18-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.18-poc-win-x64.zip`

GitHub가 자동으로 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다.
각 ZIP에는 self-contained Windows x64 실행 파일이 있으므로 .NET이나 Python을 별도로
설치하지 않습니다.
두 ZIP 모두 루트에 `SamsungSwitchWatch_User_Manual_KO.pdf`가 포함되어 있습니다.

## 2. 설치 전 네트워크 확인

일반 설치에서는 CIDR을 계산하지 않습니다. 다음 두 종류의 평범한 IPv4만 준비합니다.

| 구분 | 의미 | 예 |
|---|---|---|
| Viewer PC IPv4 | Viewer에서 Agent HTTPS/18443으로 접근할 때 사용하는 주소 | `10.20.30.25` |
| 스위치 관리 IPv4 | Agent가 Telnet/23으로 접속할 장비 주소 | `10.40.0.11,10.40.0.12` |

설치기는 각 주소를 내부적으로 정확한 `/32` 허용 정책으로 변환합니다. Viewer PC가 DHCP로
주소가 바뀌거나 32대를 초과하는 스위치를 운영한다면 고정 IP·DHCP 예약을 적용하거나 아래의
고급 CIDR 설치 방법을 사용하십시오. `LocalSubnet`, 사설망 전체와 첫 요청 대상을 자동으로
허용하지 않습니다.

필수 통신은 다음과 같습니다.

```text
Viewer PC ── HTTPS/TCP 18443 ──> Agent PC
Agent PC  ── Telnet/TCP 23 ────> 허용된 삼성 스위치
```

Agent 설치·업데이트·제거는 전체 PC에서 하나씩만 실행되며, Viewer 설치·업데이트·제거는
같은 Windows 사용자에서 하나씩만 실행됩니다. 먼저 시작한 작업이 끝나기 전에 다른 작업을
실행하면 대기하거나 파일을 동시에 변경하지 않고 즉시 중단합니다.

Agent 설치기와 제거기는 잠금을 획득한 직후 두 Agent 작업 journal을 모두 검사합니다.
재부팅 뒤에도 미완료 작업, rollback 오류 또는 손상된 기록이 남아 있으면 서비스, Program
Files, ProgramData와 방화벽을 더 변경하기 전에 중단합니다. 이것은 부분 설치를 추측해
복원하는 자동 복구가 아닙니다. 작업 기록 폴더와 하위 파일은 로컬 Administrators가 소유하고
SYSTEM·Administrators만 접근하도록 제한합니다. journal은 64KiB를 넘으면 읽지 않습니다.

## 3. Agent 신규 설치 또는 업데이트

### 가장 간단한 방법

1. Agent ZIP을 원격 PC의 임시 폴더에 압축 해제합니다.
2. `Install-or-Update-Agent.cmd`를 더블클릭합니다.
3. Windows UAC 창에서 관리자 권한을 승인합니다.
4. 신규 설치라면 Viewer PC IPv4와 스위치 관리 IPv4를 쉼표로 구분해 입력합니다. `/24` 같은
   CIDR은 일반 입력란에 넣지 않습니다.
5. `설치/업데이트가 완료되었습니다`와 `창 없이 Windows 서비스로 실행 중입니다` 메시지를
   확인합니다.

설치기는 `SamsungSwitchWatchAgent` 서비스 존재 여부로 신규 설치와 업데이트를 자동 판별합니다.
서비스는 암호가 필요 없는 `NT SERVICE\SamsungSwitchWatchAgent` 전용 가상 계정으로
등록합니다. 업데이트에서는 기존 설정의 스위치 대상 정책과 제품 소유 방화벽 규칙의 Viewer
허용 정책을 그대로 사용하므로 일반적인 업데이트에 재입력이 필요하지 않습니다.

이전 버전의 `SamsungSwitchWatchAgent-CurrentUser` 예약 작업이 있으면 설치기는 이름만 보고
중지하지 않습니다. 현재 Windows 사용자, 설명, 실행 경로·인수, 설치 영수증, 패키지
매니페스트와 실행 파일·숨김 실행기·보존 설정 SHA-256이 모두 정확히 맞는 제품 소유 작업만
중지·제거하고 Windows 서비스로 이관합니다.
새 서비스 준비가 실패하면 예약 작업 등록과 이전 실행 상태도 함께 복구합니다. 이름이 같은
다른 작업, 영수증이 없는 작업 또는 고아 프로세스는 자동 변경하지 않고 확인 방법이 포함된
오류로 중단합니다. 이전 HTTPS 신원 파일이 완전하면 새 서비스가 같은 신원을 사용하도록
복사합니다. 이관 성공 뒤 이전 `%LOCALAPPDATA%` 프로그램·데이터는
`%ProgramData%\SamsungSwitchWatch\legacy-background-backup-*`으로 이동하고 모든 하위
항목을 SYSTEM·Administrators 전용으로 잠급니다. 자동 삭제하지 않습니다.

v0.7에서 처음 업데이트하면 이전 Agent가 보관하던 장비 목록 설정 사본, 자격 증명 폴더와
SQLite 원문·이력 DB는 새 HTTPS Agent의 readiness가 성공한 뒤 다음 제한 폴더로 이동합니다.

```text
%ProgramData%\SamsungSwitchWatch\legacy-v0.7-backup-<UTC시각>-<식별자>
```

새 Agent는 이 폴더를 읽을 수 없습니다. 활성 DataDirectory는 SYSTEM, Administrators와 Agent
서비스 SID만 접근하도록 제한하지만, 레거시 백업 폴더와 모든 하위 항목은 더 엄격하게
SYSTEM과 Administrators만 접근하도록 다시 잠급니다. 설치 성공 뒤에도 자동 삭제하지 않습니다. 과거
이력 복구 또는 보존 기간 종료 후 삭제는 관리자가 사내 정책과 별도 승인을 확인해 수동으로
수행하십시오.

DataDirectory는 정확히 `%ProgramData%\SamsungSwitchWatch`만 허용합니다. 신규 설치에서는
이 경로가 이미 존재하면 비어 있더라도 설치용 폴더로 채택하지 않습니다. 기존 설치 폴더나
DataDirectory를 사용하는 업데이트·제거는 설정·영수증을 읽기 전에 루트 소유자 SID와 reparse
point를 검사합니다. 설치 트랜잭션에서는 먼저 전체 트리를 읽기 전용으로 검사하고, 루트 ACL을
잠근 뒤 부모부터 하위 항목을 다시 검사·이관합니다. 마지막 전체 재열거까지 통과하면 루트와
하위 항목의 소유자는 Administrators이며, ACL에는 SYSTEM·Administrators·정확한 Agent 서비스
SID만 남습니다.

기존 `LocalService` Agent를 업데이트할 때만 서비스가 중지된 사실을 확인한 뒤
`LocalService` 소유 하위 파일을 한 번 이관 대상으로 인정합니다. 이 항목들도 Administrators
소유로 다시 고정하고 서비스 등록을 `NT SERVICE\SamsungSwitchWatchAgent`로 전환합니다.
신규 설치, 실행 중인 서비스 또는 일반 프로그램 트리에는 이 예외를 적용하지 않습니다.

기존 릴리스가 다른 관리자 계정을 owner로 남긴 환경은 자동으로 관리자 그룹 포함 여부를
조회하지 않습니다. 폐쇄망의 도메인 조회 지연과 잘못된 권한 추측을 피하기 위한 fail-closed
동작입니다. `AGENT_DIRECTORY_TRUST_INVALID`가 표시되면 폴더를 삭제하거나 `takeown` 등으로
강제 변경하지 말고, 사내 Windows 관리자가 현재 소유권·ACL과 설치 이력을 확인해야 합니다.

설치 결과:

```text
프로그램: %ProgramFiles%\SamsungSwitchWatch\Agent
데이터:   %ProgramData%\SamsungSwitchWatch
서비스:   SamsungSwitchWatchAgent
수신:     HTTPS/18443
```

Agent 데이터 폴더에는 Agent의 영구 HTTPS 신원과 설치 영수증이 들어갑니다. 설치 영수증 파일은
상위 폴더의 서비스 쓰기 권한을 상속하지 않고 SYSTEM·Administrators 전용 ACL로 보호합니다.
업데이트 시 허용 정책은 영수증이 아니라 검증된 `appsettings.Production.json`과 정확히 제품이
소유한 방화벽 규칙에서 가져옵니다. 과거 서비스 쓰기 가능 영수증은 정책 권한원으로 사용하지
않고 새 관리자 전용 영수증으로 교체합니다.

패키지는 먼저 관리자 전용 staging 폴더로 복사합니다. 복사된 모든 파일과 Agent EXE를 메모리에
읽어 둔 매니페스트의 SHA-256과 다시 비교한 뒤에만 프로그램 폴더를 교체합니다. 업데이트는
DataDirectory 전체를 제한된 transaction snapshot에 백업합니다. 새 버전이 `/health/ready`
검증을 통과하지 못하면 프로그램, 데이터, 방화벽과 이전 실행 상태를 복구합니다. 다만 서비스
중지·삭제 또는 선행 복구 단계를 확인하지 못하면 파일을 계속 삭제하거나 덮어쓰지 않습니다.
복구 오류가 하나라도 남으면 snapshot, legacy archive, program backup과 작업 journal 등 남아
있는 증거를 자동 정리하지 않고 관리자 확인 대상으로 보존합니다.

### Viewer 또는 스위치 허용 IP를 변경하는 경우

현재 Agent와 같은 버전의 Agent ZIP 폴더에서 `Configure-Agent-Allowed-IPs.cmd`를
더블클릭하고 UAC를 승인합니다. 현재 값을 보면서 Viewer PC IPv4와 스위치 관리 IPv4의 전체
목록을 쉼표로 입력합니다. 빈 입력은 기존값을 유지하며 적용 전 최종 목록을 다시 확인합니다.
오류가 발생하면 설정·방화벽과 서비스 상태를 변경 전 상태로 복구합니다.

다른 버전의 ZIP으로 허용 IP만 변경하지 마십시오. 먼저 `Install-or-Update-Agent.cmd`로
Agent와 ZIP 버전을 맞춘 뒤 같은 ZIP의 설정 도구를 사용합니다.

### 고급 CIDR을 사용하는 경우

Viewer PC가 DHCP이거나 여러 관리 서브넷과 32대가 넘는 장비를 운영할 때만 Agent ZIP
폴더에서 관리자 PowerShell을 열어 설치기를 직접 실행합니다.

```powershell
.\install-agent.ps1 `
  -ClientManagementCidrs 10.20.30.0/24,10.20.31.0/24 `
  -AllowedTargetCidrs 10.40.0.0/16
```

Agent 프로그램·데이터·서비스·방화벽을 변경하지 않고 입력과 패키지만 검사하려면
`-Preflight`를 추가합니다. 보안상 작업 journal 폴더가 없거나 이전 ACL이면 이 폴더 생성과
SYSTEM·Administrators 전용 ACL 초기화는 수행될 수 있습니다.

```powershell
.\install-agent.ps1 `
  -ClientManagementCidrs 10.20.30.0/24 `
  -AllowedTargetCidrs 10.40.0.0/16 `
  -Preflight
```

CIDR은 IPv4 네트워크 형식만 허용됩니다. `LocalSubnet`, DNS 이름, IPv6와 포트 18443 또는
Telnet 23의 변경은 지원하지 않습니다.

### Agent 창이 보이지 않는 이유

Agent는 `NT SERVICE\SamsungSwitchWatchAgent` 전용 가상 계정의 Windows 서비스로 Session
0에서 실행됩니다. 별도 비밀번호를 만들거나 저장하지 않습니다. 바탕 화면, 작업 표시줄 또는
시스템 트레이에 창을 만들지 않으며 RDP 연결 종료와 사용자 로그오프 뒤에도 계속 실행됩니다.
일반 사용자는 보이는 창을 실수로 닫을 수 없습니다.

`SamsungSwitchWatch.Agent.exe`를 직접 더블클릭하면 Agent를 별도로 실행하지 않고 즉시
종료합니다. 운영할 때는 반드시 설치된 Windows 서비스를 사용하십시오.

서비스는 비정상 종료 후 5초, 15초, 60초 간격으로 재시작하도록 설치됩니다. 서비스 중지와
제거는 관리자만 수행해야 합니다.

## 4. Viewer 설치와 Agent 연결

### 가장 간단한 방법

1. Viewer ZIP을 운영자 PC의 임시 폴더에 압축 해제합니다.
2. `Install-or-Update-Viewer.cmd`를 더블클릭합니다.
3. UAC 관리자 승인을 한 번 진행합니다.
4. 설치 완료 메시지를 확인합니다. Viewer 프로그램은
   `C:\Program Files\SamsungSwitchWatch\Viewer`에 설치되고, 원래 설치를 시작한 Windows
   사용자에게 시작 메뉴와 로그인 자동 시작 바로 가기를 만든 뒤 일반 사용자 권한으로
   실행됩니다.

UAC는 Program Files의 프로그램 파일을 설치하거나 업데이트하는 단계에만 사용합니다.
장비 목록, DPAPI 자격 증명, 감시 이력과 화면 설정은 관리자 계정으로 옮기지 않고 기존과
같이 원래 사용자의 `%LOCALAPPDATA%\SamsungSwitchWatch`에 보존합니다.

설치기는 다음 두 단계를 분리합니다.

1. 관리자 단계: 패키지를 보호된 staging에서 다시 검증하고 Program Files 설치본을
   교체한 뒤, 설치된 파일의 SHA-256과 무화면 자체점검을 확인합니다. 이전 Program Files
   버전은 설치 폴더 옆의 관리자 보호 rollback 슬롯에 다음 업데이트까지 보존합니다.
2. 원래 사용자 단계: 시작 메뉴와 로그인 자동 시작 바로 가기를 반영하고 Viewer를
   일반 사용자 권한으로 실행합니다.

원래 사용자 실행 검사 또는 바로 가기 단계가 실패하면 이전 Program Files 버전을 되돌리기
위한 UAC가 한 번 더 표시될 수 있습니다. 이 복구 UAC를 취소하거나 복구가 실패하면
rollback 슬롯을 자동 삭제하지 않습니다. 설치 폴더나 rollback 슬롯을 수동 정리하지 말고
표시된 `Recovery`와 진단 코드를 Windows 관리자에게 전달하십시오.

현재 설치의 실행 파일이나 매니페스트가 EDR 격리 또는 파일 손상으로 사라져도 자동 복구는
현재 설치의 정상 패키지 검증을 요구하지 않습니다. 보호된 정확한 설치 경로를 격리한 뒤
이미 검증된 `Viewer.__rollback`만 활성 경로로 복원합니다. 제거할 때도 Viewer 프로세스
종료와 활성 프로그램 폴더 삭제가 모두 확인된 경우에만 rollback 슬롯을 삭제합니다.

다른 관리자 계정으로 UAC를 승인했는데 그 계정이 압축 해제 원본을 읽을 수 없으면
설치 대상은 변경하지 않고 중단합니다. 원본 폴더 ACL을 완화하거나 파일 차단을 해제해
우회하지 말고, 사내 정책상 관리자도 읽을 수 있는 임시 폴더에 공식 ZIP을 다시 압축
해제하여 실행하십시오.

### Viewer 설치가 이전 상태로 복구된 경우

Viewer 설치 창에 `Viewer 설치 실패를 감지해 설치 전 상태로 되돌립니다.`가 표시되면 바로
아래의 진단 줄을 먼저 확인합니다.

```text
Cause: <최초 실패 코드>
Detail: <실패 세부 코드>
ExitCode: <표시되는 경우의 프로세스 종료 코드>
Recovery: <복구 결과>
Diagnostic: %LOCALAPPDATA%\SamsungSwitchWatch-Operations\viewer-install.json
Runtime diagnostic: %LOCALAPPDATA%\SamsungSwitchWatch\logs\viewer-diagnostic.jsonl
```

- `Recovery: PREVIOUS_VIEWER_RESTORED`이면 기존 Viewer 파일과 바로 가기가 복구된
  상태입니다. Viewer는 자동으로 다시 실행되지 않으므로 시작 메뉴의 `Samsung Switch Watch`를
  실행합니다.
- `Recovery: PARTIAL_INSTALL_REMOVED`이면 처음 설치하던 일부 파일을 제거해 설치 전
  상태로 돌아간 것입니다. `Cause`의 원인을 해결한 뒤 같은 패키지를 다시 실행합니다.
- `Recovery: ROLLBACK_INCOMPLETE (...)`이면 백업과 진단 파일을 삭제하지 말고 Windows
  관리자에게 표시된 코드와 진단 경로를 전달합니다.

v0.9.16부터 시작 메뉴 또는 시작프로그램 폴더가 아직 없는 Windows 사용자 환경에서는
설치기가 필요한 폴더를 먼저 만듭니다. 보안 정책이나 권한 때문에 만들 수 없으면
`VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE`을 표시하고 설치 전 상태로 복구합니다.

v0.9.17부터 Viewer 실행 파일뿐 아니라 매니페스트에 선언된 모든 WPF 파일의 크기와
SHA-256을 설치 전과 staging 복사 후 각각 검사합니다. 실제 Viewer 창과 Agent 연결을
사용하지 않는 20초 제한 무화면 자체점검을 통과한 뒤에만 설치를 확정합니다.

v0.9.18부터 기본 프로그램 설치 위치는 Program Files입니다. 새 설치본의 무결성과
원래 사용자 자체점검이 성공해도 기존 사용자별 프로그램 폴더는 자동 삭제하지 않고 복구용으로
보존합니다. 새 바로 가기는 Program Files 설치본을 가리킵니다. 장비·계정·감시 데이터가 있는
`%LOCALAPPDATA%\SamsungSwitchWatch`도 삭제하지 않습니다.

### 고급 설치

설치 전 검사 또는 자동 시작 상태를 직접 지정할 때만 PowerShell에서 다음 명령을 사용합니다.
기본 설치는 UAC를 거쳐 Program Files를 사용합니다.

```powershell
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows -Preflight
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows
```

`-StartWithWindows`는 Windows 로그인 시 Viewer를 자동 시작합니다. 이후 버전을 업데이트할
때 이 옵션을 생략해도 기존 자동 시작 상태는 그대로 보존됩니다. 자동 시작을 명시적으로
끄려는 경우에만 다음 옵션을 사용합니다.

```powershell
.\install-viewer.ps1 -SourceDirectory . -DisableStartWithWindows
```

더블클릭 설치는 주기 감시가 계속 시작되도록 `-StartWithWindows`를 기본 적용합니다.

사내 보안 정책상 Program Files 설치 승인을 받을 수 없어 기존 사용자별 설치를 유지해야 할
때만 다음 호환 옵션을 명시적으로 사용합니다. 이 방식은 기본 경로가 아니며 AppLocker,
WDAC 또는 EDR이 사용자 폴더 실행을 차단하는 환경에서는 동작하지 않을 수 있습니다.

```powershell
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows -PerUser
```

Viewer의 `Agent 연결`에는 **Agent를 설치한 원격 PC의 IPv4 또는 사내 DNS 이름만**
입력합니다. 스위치 IP나 Viewer PC 주소를 입력하지 마십시오. HTTPS와 고정 포트 `18443`은
자동 적용되며 인증서 SHA-256 지문과 페어링 토큰을 직접 입력하지 않습니다.

Viewer는 첫 연결에서 Agent의 영구 신원을 자동으로 저장합니다. 이후 같은 주소에서 다른
신원이 보이면 연결을 중단합니다. Agent PC가 정식으로 재설치되어 신원이 바뀐 것이 확실할
때만 `Agent 신뢰 다시 설정`을 사용하십시오.

## 5. Viewer에서 장비 등록

`장비 관리`를 열고 다음 항목을 입력합니다.

| 항목 | 필수 | 설명 |
|---|---|---|
| 장비명 | 예 | 화면에 표시할 이름 |
| 모델 | 예 | IES4224GP, IES4028XP, IES4226XP |
| 장비 IPv4 | 예 | Agent 설치 또는 허용 IP 설정 도구에 등록한 관리 주소 |
| ID | 예 | Telnet 로그인 ID |
| 로그인 PW | 예 | Telnet 로그인 비밀번호 |
| enable PW | 아니요 | 로그인 프롬프트가 `>`인 장비만 필요 |

계정은 Viewer PC의 현재 Windows 사용자 DPAPI로 암호화합니다. Agent에는 저장하지 않으며,
다른 Windows 사용자나 다른 PC로 Viewer 데이터 파일만 복사해 사용할 수 없습니다.

`접속 시험`은 다음 단계만 확인합니다.

```text
TCP/23 연결 → 로그인 → 필요하면 enable → 프롬프트 확인 → 로그아웃
```

접속 시험에 실패한 장비도 저장할 수 있지만 `미확인`으로 표시됩니다.

## 6. 명령 실행

장비를 선택하고 명령 입력란에 한 줄짜리 `show` 명령을 입력합니다.

```text
show port status
show sylog tail num 100
show syslog tail num 100
show running-config
```

공백과 대소문자는 정규화됩니다. 줄바꿈, 여러 명령 연결, `;`, `&`, `|` 같은 구분자와
설정 변경 명령은 차단됩니다.

로그인 뒤 프롬프트가 `#`이면 enable을 생략합니다. `>`이고 enable PW가 있으면
`enable → Password → #` 흐름을 처리합니다. enable PW가 없거나 승격에 실패하면 현재
권한에서 실행하지 못한 이유를 표시합니다.

명령 결과는 최대 64KiB이며 초과하면 `잘림`을 표시합니다. 결과는 현재 Viewer 프로세스의
메모리에만 있으며 다음 위치에 저장되지 않습니다.

- Agent 로그와 데이터 폴더
- Viewer DB와 변경 이력
- 진단 JSON
- 자동 내보내기 파일

결과 요약에는 이번 요청에서 사용한 `세션 n회`와, 해당하는 경우 `재연결 n회`가 표시됩니다.
재연결이 1회 표시되더라도 완료된 명령을 다시 실행했다는 의미는 아닙니다.

특히 `show running-config`에는 비밀번호 해시, SNMP 문자열, IP와 망 구성이 포함될 수
있으므로 복사한 내용도 일반 문서나 메신저에 붙여 넣지 마십시오.

## 7. 주기 감시

주기 감시는 Viewer가 실행 중일 때만 동작합니다. Agent는 자체 스케줄러나 장비 목록을
갖지 않습니다.

- Viewer 실행 중: 등록된 장비와 명령을 주기적으로 Agent에 요청
- Viewer 종료·PC 절전·네트워크 단절: 감시 중단
- Viewer 재실행: 중단 시간을 `감시 공백`으로 기록하고 현재 결과를 새 기준선으로 사용

공백 동안 스위치에서 발생하고 이미 로그 버퍼에서 사라진 사건은 복원할 수 없습니다.
24시간 무중단 감시가 필요한 환경에서는 Viewer PC도 상시 실행되어야 합니다.

## 8. 짧은 장비 세션 유지 시간

Agent는 연결을 장기간 유지하지 않습니다. 명령 요청마다 새 세션을 만들고 다음 순서가
끝나면 즉시 종료합니다.

```text
연결 → 로그인 → enable → 명령 1~8개 → exit/logout → 소켓 종료
```

명령 실행 단계에서 장비가 연결을 끊었고 실행할 명령이 남았다면 2초 뒤 새 세션으로
1회만 재연결합니다. 이미 결과를 받은 명령은 반복하지 않고 남은 명령만 실행합니다.
로그인·인증·enable 단계 실패, 명령 타임아웃과 사용자 취소는 자동 재시도하지 않습니다.

각 세션의 최대 시간은 240초입니다. 따라서 `exec-timeout 5 0`인 장비에서도 유휴 세션을
붙잡아 두지 않습니다. 성공과 모든 실패 경로에서 세션 정리를 시도합니다.

## 9. 진단과 제거

민감한 명령 결과 없이 Agent 단계 상태만 수집합니다.

```powershell
.\diagnose-agent.ps1 -OutputPath "$env:TEMP\ssw-diagnostic.json"
```

Viewer에서 `AGENT_CONNECTION_REFUSED`가 보이면 다음 순서로 확인합니다.

1. Viewer의 `Agent 연결`에 스위치 IP가 아니라 Agent를 설치한 PC의 주소가 입력됐는지
   확인합니다. Agent와 Viewer가 다른 PC라면 `localhost`를 사용하지 않습니다.
2. Agent PC에서 위 진단을 실행하고 다음 항목을 확인합니다.
   - `service.status`: `Running`
   - `listener.status`: `Listening`
   - `firewall.enabled`: `true`
   - `firewall.exact`: `true`
   - `network.activeCategories`: `DomainAuthenticated` 또는 `Private` 포함
   - `health.live`: `LIVE`
   - `health.ready`: `READY`
3. Viewer PC의 PowerShell에서 다음 명령을 실행합니다. 실제 주소는 화면에만 입력하고
   진단 파일이나 외부 문의 자료에는 기록하지 않습니다.

   ```powershell
   Test-NetConnection <Agent-PC-주소> -Port 18443
   ```

4. `TcpTestSucceeded : False`이면 설치 때 입력한 Viewer PC 허용 IP와 Agent PC의
   Domain/Private 방화벽 프로필, 사내 라우팅과 EDR 차단을 확인합니다. Viewer PC 주소가
   바뀌었다면 같은 버전 Agent ZIP의 `Configure-Agent-Allowed-IPs.cmd`로 갱신합니다. 로컬
   진단은 정상인데 원격 검사만 실패하면 스위치 접속 문제가 아니라 Viewer PC와 Agent PC
   사이의 문제입니다.

기본 제거는 프로그램과 서비스만 삭제하고 HTTPS 신원·설치 설정 데이터는 보존합니다.

```powershell
.\uninstall-agent.ps1
```

데이터까지 영구 삭제할 때만 다음 명령을 사용합니다. 이 옵션은 HTTPS 신원뿐 아니라
`legacy-v0.7-backup-*`의 과거 자격 증명·SQLite 보존 자료도 함께 삭제합니다. 사내 보존
정책과 별도 승인을 먼저 확인하십시오. 삭제한 신원은 복구되지 않으며 Viewer에서 기존
Agent 신원 불일치가 발생합니다.

`-RemoveData`는 install receipt가 SYSTEM·Administrators 전용 일반 파일이고 설치 경로와
정확한 DataDirectory에 결속된 경우에만 허용됩니다. `AGENT_RECEIPT_TRUST_INVALID`가 표시되면
영수증 ACL을 임의로 완화하거나 파일을 새로 만들어 우회하지 마십시오. 제거 과정에서 서비스
중지 또는 삭제를 확인하지 못하면 실행 파일이 사용 중일 가능성이 있으므로 후속 방화벽·프로그램·
데이터 삭제를 차단하고 journal에 실패를 남깁니다.

```powershell
.\uninstall-agent.ps1 -RemoveData
```

## 10. 주요 진단 코드

| 코드 | 의미 |
|---|---|
| `DEPLOYMENT_ALREADY_RUNNING` | 같은 제품의 설치 또는 제거가 진행 중임. 먼저 실행한 작업이 끝난 뒤 다시 실행 |
| `DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED` | 이전 설치·제거 프로세스의 비정상 종료가 감지되어 이번 실행은 변경 전에 중단됨. 서비스·설치 폴더 상태를 확인한 뒤 다시 실행 |
| `DEPLOYMENT_LOCK_UNAVAILABLE` | 배포 잠금을 열 수 없음. Agent는 관리자 권한, Viewer는 설치한 동일 Windows 계정인지 확인하고 보안 프로그램·정책 차단 여부 점검 |
| `AGENT_DEPLOYMENT_RECOVERY_REQUIRED` | 이전 Agent 설치·제거가 완료되지 않았거나 rollback 오류가 남음. 자동 복구가 아니므로 journal과 백업을 보존하고 관리자 상태 확인 후 다음 조치 결정 |
| `AGENT_DEPLOYMENT_JOURNAL_INVALID` | Agent 작업 기록이 손상됐거나 지원되지 않는 형식임. 기록을 삭제해 우회하지 말고 백업과 함께 보존하여 관리자 확인 |
| `AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID` | 작업 기록 폴더의 소유자·ACL·파일 구성이 안전하지 않음. 폴더를 임의로 새로 만들거나 삭제하지 말고 관리자와 보안 정책 확인 |
| `AGENT_DIRECTORY_TRUST_INVALID` | Agent 설치·데이터 루트 또는 하위 항목의 소유자·reparse 구성을 신뢰할 수 없어 읽기·채택·삭제를 중단함. 강제 소유권 변경이나 삭제로 우회하지 말고 Windows 관리자 확인 |
| `AGENT_RECEIPT_TRUST_INVALID` | 데이터 영구 삭제에 필요한 install receipt가 SYSTEM·Administrators 전용 일반 파일이 아님. ACL 완화·파일 재작성으로 우회하지 말고 설치 증거와 데이터 보존 |
| `AGENT_HTTPS_UNREACHABLE` | Viewer 또는 로컬 검사에서 HTTPS Agent에 도달하지 못함 |
| `AGENT_CONNECTION_REFUSED` | 입력한 주소의 TCP/18443에 listener가 없음. 실제 Agent PC 주소와 `SamsungSwitchWatchAgent` 서비스를 확인하고, Viewer PC IPv4가 바뀌었다면 Agent ZIP의 `Configure-Agent-Allowed-IPs.cmd`로 허용 IP 갱신 |
| `VIEWER_SOURCE_ACCESS_DENIED` | UAC에 사용한 관리자 계정이 압축 해제 원본을 읽을 수 없음. ACL을 완화하지 말고 관리자도 읽을 수 있는 승인된 임시 폴더에 공식 ZIP을 다시 압축 해제 |
| `VIEWER_INSTALL_PATH_EXECUTION_BLOCKED` | Program Files에 설치된 Viewer 실행이 AppLocker·WDAC·EDR 등에 의해 차단됨. 보안 정책 담당자에게 설치 진단 코드와 배포 파일 해시 전달 |
| `VIEWER_USER_PHASE_FAILED` | 원래 사용자 권한의 실행 검사 또는 바로 가기 반영 실패. 이어지는 복구 UAC를 승인하고 `Recovery` 결과 확인 |
| `VIEWER_MACHINE_ROLLBACK_INCOMPLETE` | 이전 Program Files 버전 자동 복구가 완료되지 않음. 현재 설치와 `Viewer.__rollback`을 삭제하지 말고 관리자 확인 |
| `VIEWER_ROLLBACK_ELEVATION_NOT_GRANTED` | 사용자 단계 실패 뒤 복구 UAC가 취소되었거나 시작되지 않음. rollback 슬롯을 보존하고 관리자에게 재시도 요청 |
| `VIEWER_SHORTCUT_DIRECTORY_UNAVAILABLE` | 시작 메뉴 또는 시작프로그램 폴더를 만들 수 없음. Viewer를 설치할 동일 Windows 사용자로 실행했는지와 폴더 쓰기 권한·보안 정책 확인 |
| `VIEWER_SHORTCUT_SETUP_FAILED` | Viewer 바로 가기 생성 또는 자동 시작 반영 실패. `Recovery` 결과를 확인하고 보안 프로그램의 바로 가기 생성 차단 여부 점검 |
| `VIEWER_SMOKE_CHECK_FAILED` | 새 Viewer 무화면 자체점검 실패. `Detail`, 선택적 `ExitCode`, 복구 결과, 설치 journal과 Viewer 진단 로그를 확인 |
| `VIEWER_PACKAGE_FILE_MISSING` | 압축 해제 뒤 매니페스트에 선언된 Viewer 파일이 없음. 공식 ZIP을 새 폴더에 다시 압축 해제하고 EDR 격리 여부 확인 |
| `VIEWER_PACKAGE_HASH_MISMATCH` | Viewer 파일이 매니페스트 SHA-256과 다름. 변조된 파일을 실행하지 말고 공식 ZIP과 보안 프로그램 기록 확인 |
| `VIEWER_UNSUPPORTED_ARCHITECTURE` | 32비트 Windows에서는 win-x64 Viewer를 설치할 수 없음. 64비트 Windows PC에서 실행 |
| `VIEWER_SELF_CHECK_START_FAILED` | 무화면 자체점검 프로세스를 시작하지 못함. Windows x64 여부와 AppLocker·WDAC·EDR 실행 차단 확인 |
| `VIEWER_SELF_CHECK_WAIT_FAILED` | 무화면 자체점검 프로세스의 완료 상태를 읽지 못함. 설치 journal과 Windows Application 로그를 확인한 뒤 다시 설치 |
| `VIEWER_SELF_CHECK_EXITED_NONZERO` | 무화면 자체점검이 비정상 종료됨. 표시된 `ExitCode`, Windows Application 로그와 EDR 기록 확인 |
| `VIEWER_SELF_CHECK_ACCESS_DENIED` | 설치된 Viewer 자체점검 실행 권한이 거부됨. Program Files 실행 정책과 EDR·AppLocker·WDAC 기록 확인 |
| `FILE_MISSING` | 자체점검 대상 파일이 없어짐. EDR 격리 여부와 패키지 무결성 확인 |
| `BAD_IMAGE` | 설치된 실행 파일을 현재 Windows에서 로드하지 못함. win-x64 환경과 파일 손상·격리 여부 확인 |
| `TIMEOUT` | 무화면 자체점검이 20초 안에 끝나지 않음. 남은 프로세스와 보안 프로그램 지연·차단 확인 |
| `VIEWER_UNINSTALL_ROLLBACK_PRESERVED` | 실행 중 Viewer 또는 활성 프로그램 폴더를 제거하지 못해 rollback 슬롯을 보존함. 잠긴 Viewer를 정상 종료하고 관리자 제거를 다시 실행 |
| `TARGET_NOT_ALLOWED` | 장비 IPv4가 Agent 허용 스위치 IP 또는 고급 대상 CIDR 밖임. Agent ZIP의 허용 IP 설정 도구로 갱신 |
| `TCP_TIMEOUT` | Agent에서 장비 TCP/23 연결 시간 초과 |
| `AUTH_FAILED` | Telnet 로그인 실패 |
| `ENABLE_FAILED` | enable 승격 실패 |
| `QUERY_COMMAND_BLOCKED` | 한 줄 show 정책 위반 |
| `QUERY_RATE_LIMITED` | 같은 API 클라이언트의 분당 요청 한도 초과 |
| `COMMAND_TIMEOUT` | 장비 출력 또는 프롬프트 복귀 시간 초과 |
| `OUTPUT_LIMIT_EXCEEDED` | 장비 출력이 세션 처리 안전 한도를 초과함 |
| `PROMPT_PARSE_FAILED` | 장비 프롬프트를 안전하게 판별하지 못함 |
| `VIEWER_DEVICE_STORE_UNAVAILABLE` | 장비 목록 파일을 읽지 못함. 기존 목록을 유지하고 사용자 폴더 권한과 파일 잠금 확인 |
| `VIEWER_DEVICE_STORE_WRITE_FAILED` | 장비 설정 파일 저장 실패. 사용자 폴더 권한, 파일 잠금과 디스크 여유 공간 확인 |
| `VIEWER_MONITOR_STATE_WRITE_FAILED` | 장비 설정 저장은 완료될 수 있음. 중복 등록하지 말고 감시 이력 파일 권한·잠금·디스크 확인 |

정상 명령 출력이 64KiB 응답 상한에서 잘리면 오류 코드 대신 Viewer의 `잘림` 표시를
확인합니다. 실제 IP, ID, 비밀번호, 호스트명과 원문 출력은 진단 파일에 추가하지 마십시오.

`DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED`는 직전 프로세스의 잠금 중단을 감지한 경우 한 번
fail-closed로 멈추는 보호입니다. 재부팅 뒤 남은 부분 설치를 자동으로 복구하는 기능은
아니므로 오류가 반복되거나 서비스·설치 폴더 상태가 불명확하면 임의 삭제하지 말고 관리자가
작업 journal과 설치 상태를 확인해야 합니다.

위 `AGENT_DEPLOYMENT_*` 코드가 표시되면
`%ProgramData%\SamsungSwitchWatch-Operations`, `.__staging_*`, `.__backup_*`,
transaction 백업, Agent 데이터와 `legacy-*-backup-*`을 삭제·이동·이름 변경하지 마십시오.
서비스나 제품 방화벽 규칙도 임의로 다시 만들지 마십시오. 해당 자료는 중단 지점과 안전한
복구 방향을 판단하는 증거입니다.

서비스 중지·삭제 실패 또는 legacy program/data의 부분 이동이 기록된 경우에는 설치기가
후속 파일 삭제·복구를 의도적으로 진행하지 않습니다. 원래 위치와 archive 양쪽에 자료가
남아 있어도 중복으로 단정해 정리하지 말고, snapshot과 작업 journal을 함께 보존하십시오.

이전 작업 기록이 현재 실행한 관리자와 다른 계정 소유라면 자동 이관하지 않습니다. 폐쇄망에서
장기 대기를 만들 수 있는 로컬·도메인 그룹 조회로 다른 owner의 권한을 추측하지 않으므로
`AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID`가 표시될 수 있습니다. 기록을 삭제해 우회하지 말고
사내 Windows 관리자에게 소유권과 ACL 확인을 요청하십시오.

`AGENT_DIRECTORY_TRUST_INVALID`는 작업 journal이 아니라 활성 Agent 설치·데이터 트리의
신뢰 검사가 실패했다는 뜻입니다. 정적 비신뢰 항목은 ACL을 바꾸기 전에 거부하지만, 검사 중
동시 변경이 감지된 경우 이미 안전하게 잠긴 상위 ACL이 남을 수 있습니다. 이 경우에도 임의
정리하지 말고 설치기 메시지와 `%ProgramData%\SamsungSwitchWatch-Operations`를 보존하십시오.
