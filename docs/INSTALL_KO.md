# Samsung Switch Watch 설치 및 운영 안내

## 1. 필요한 파일

공식 GitHub `v0.9.6-poc` Release의 Assets에서 다음 두 파일만 받습니다.

- `SamsungSwitchWatch-Agent-0.9.6-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.6-poc-win-x64.zip`

GitHub가 자동으로 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다.
각 ZIP에는 self-contained Windows x64 실행 파일이 있으므로 .NET이나 Python을 별도로
설치하지 않습니다.
두 ZIP 모두 루트에 `SamsungSwitchWatch_User_Manual_KO.pdf`가 포함되어 있습니다.

## 2. 설치 전 네트워크 확인

다음 두 IPv4 CIDR을 준비합니다.

| 구분 | 의미 | 예 |
|---|---|---|
| Viewer 관리 CIDR | Viewer PC에서 Agent HTTPS/18443으로 접근할 수 있는 범위 | `10.20.30.0/24` |
| 스위치 대상 CIDR | Agent가 Telnet/23으로 접속해도 되는 장비 범위 | `10.40.0.0/16` |

이 두 값은 서로 같을 수도 있고 다를 수도 있습니다. 범위를 넓게 지정할수록 같은 관리망의
다른 사용자가 Agent API를 호출하거나 Agent를 경유해 더 많은 주소에 접근할 수 있으므로,
실제 필요한 최소 범위를 사용하십시오.

필수 통신은 다음과 같습니다.

```text
Viewer PC ── HTTPS/TCP 18443 ──> Agent PC
Agent PC  ── Telnet/TCP 23 ────> 허용된 삼성 스위치
```

## 3. Agent 신규 설치 또는 업데이트

### 가장 간단한 방법

1. Agent ZIP을 원격 PC의 임시 폴더에 압축 해제합니다.
2. `Install-or-Update-Agent.cmd`를 더블클릭합니다.
3. Windows UAC 창에서 관리자 권한을 승인합니다.
4. 신규 설치라면 Viewer 관리 CIDR과 스위치 대상 CIDR을 쉼표로 구분해 입력합니다.
5. `설치/업데이트가 완료되었습니다`와 `창 없이 Windows 서비스로 실행 중입니다` 메시지를
   확인합니다.

설치기는 `SamsungSwitchWatchAgent` 서비스 존재 여부로 신규 설치와 업데이트를 자동 판별합니다.
업데이트에서는 기존 CIDR을 그대로 사용하므로 일반적인 업데이트에 재입력이 필요하지 않습니다.

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

설치 결과:

```text
프로그램: %ProgramFiles%\SamsungSwitchWatch\Agent
데이터:   %ProgramData%\SamsungSwitchWatch
서비스:   SamsungSwitchWatchAgent
수신:     HTTPS/18443
```

Agent 데이터 폴더에는 Agent의 영구 HTTPS 신원과 설치 영수증이 들어갑니다. 업데이트는 이 폴더
전체를 제한된 임시 폴더에 백업한 뒤 프로그램을 교체합니다. 새 버전이 `/health/ready` 검증을
통과하지 못하면 프로그램, 데이터, 방화벽과 이전 실행 상태를 자동 복구합니다.

### CIDR을 변경하는 경우

Agent ZIP 폴더에서 관리자 PowerShell을 열어 설치기를 직접 실행합니다.

```powershell
.\install-agent.ps1 `
  -ClientManagementCidrs 10.20.30.0/24,10.20.31.0/24 `
  -AllowedTargetCidrs 10.40.0.0/16
```

시스템 변경 없이 입력과 패키지만 검사하려면 `-Preflight`를 추가합니다.

```powershell
.\install-agent.ps1 `
  -ClientManagementCidrs 10.20.30.0/24 `
  -AllowedTargetCidrs 10.40.0.0/16 `
  -Preflight
```

CIDR은 IPv4 네트워크 형식만 허용됩니다. `LocalSubnet`, DNS 이름, IPv6와 포트 18443 또는
Telnet 23의 변경은 지원하지 않습니다.

### Agent 창이 보이지 않는 이유

Agent는 `LocalService` 계정의 Windows 서비스로 Session 0에서 실행됩니다. 바탕 화면, 작업
표시줄 또는 시스템 트레이에 창을 만들지 않으며 RDP 연결 종료와 사용자 로그오프 뒤에도
계속 실행됩니다. 일반 사용자는 보이는 창을 실수로 닫을 수 없습니다.

`SamsungSwitchWatch.Agent.exe`를 직접 더블클릭하면 Agent를 별도로 실행하지 않고 즉시
종료합니다. 운영할 때는 반드시 설치된 Windows 서비스를 사용하십시오.

서비스는 비정상 종료 후 5초, 15초, 60초 간격으로 재시작하도록 설치됩니다. 서비스 중지와
제거는 관리자만 수행해야 합니다.

## 4. Viewer 설치와 Agent 연결

### 가장 간단한 방법

1. Viewer ZIP을 운영자 PC의 임시 폴더에 압축 해제합니다.
2. `Install-or-Update-Viewer.cmd`를 더블클릭합니다.
3. 설치 완료 메시지를 확인합니다. Viewer는 현재 Windows 사용자에게 설치되고 바로
   실행되며, 다음 로그인부터 자동 시작됩니다.

Viewer 설치에는 관리자 권한이나 UAC 승인이 필요하지 않습니다.

### 고급 설치

설치 전 검사, 설치 위치 또는 자동 시작 상태를 직접 지정할 때만 일반 사용자 PowerShell에서
다음 명령을 사용합니다.

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

Viewer의 `Agent 연결`에서 Agent PC의 IPv4 또는 사내 DNS 이름과 고정 포트 `18443`을
입력합니다. 인증서 SHA-256 지문과 페어링 토큰을 직접 입력하지 않습니다.

Viewer는 첫 연결에서 Agent의 영구 신원을 자동으로 저장합니다. 이후 같은 주소에서 다른
신원이 보이면 연결을 중단합니다. Agent PC가 정식으로 재설치되어 신원이 바뀐 것이 확실할
때만 `Agent 신뢰 다시 설정`을 사용하십시오.

## 5. Viewer에서 장비 등록

`장비 관리`를 열고 다음 항목을 입력합니다.

| 항목 | 필수 | 설명 |
|---|---|---|
| 장비명 | 예 | 화면에 표시할 이름 |
| 모델 | 예 | IES4224GP, IES4028XP, IES4226XP |
| 장비 IPv4 | 예 | Agent 대상 CIDR 안의 관리 주소 |
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
.\diagnose-agent.ps1 -OutputPath C:\Temp\ssw-diagnostic.json
```

기본 제거는 프로그램과 서비스만 삭제하고 HTTPS 신원·설치 설정 데이터는 보존합니다.

```powershell
.\uninstall-agent.ps1
```

데이터까지 영구 삭제할 때만 다음 명령을 사용합니다. 이 옵션은 HTTPS 신원뿐 아니라
`legacy-v0.7-backup-*`의 과거 자격 증명·SQLite 보존 자료도 함께 삭제합니다. 사내 보존
정책과 별도 승인을 먼저 확인하십시오. 삭제한 신원은 복구되지 않으며 Viewer에서 기존
Agent 신원 불일치가 발생합니다.

```powershell
.\uninstall-agent.ps1 -RemoveData
```

## 10. 주요 진단 코드

| 코드 | 의미 |
|---|---|
| `AGENT_HTTPS_UNREACHABLE` | Viewer 또는 로컬 검사에서 HTTPS Agent에 도달하지 못함 |
| `TARGET_NOT_ALLOWED` | 장비 IPv4가 Agent 허용 대상 CIDR 밖임 |
| `TCP_TIMEOUT` | Agent에서 장비 TCP/23 연결 시간 초과 |
| `AUTH_FAILED` | Telnet 로그인 실패 |
| `ENABLE_FAILED` | enable 승격 실패 |
| `QUERY_COMMAND_BLOCKED` | 한 줄 show 정책 위반 |
| `QUERY_RATE_LIMITED` | 같은 API 클라이언트의 분당 요청 한도 초과 |
| `COMMAND_TIMEOUT` | 장비 출력 또는 프롬프트 복귀 시간 초과 |
| `OUTPUT_LIMIT_EXCEEDED` | 장비 출력이 세션 처리 안전 한도를 초과함 |
| `PROMPT_PARSE_FAILED` | 장비 프롬프트를 안전하게 판별하지 못함 |

정상 명령 출력이 64KiB 응답 상한에서 잘리면 오류 코드 대신 Viewer의 `잘림` 표시를
확인합니다. 실제 IP, ID, 비밀번호, 호스트명과 원문 출력은 진단 파일에 추가하지 마십시오.
