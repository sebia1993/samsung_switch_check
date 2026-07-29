# Samsung Switch Watch 설치 및 운영 안내

## 1. 준비

공식 GitHub `v0.10.3-poc` Release의 Assets에서 다음 두 파일만 받습니다.

- `SamsungSwitchWatch-Agent-0.10.3-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.3-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 두 ZIP은 Windows
x64용 self-contained 빌드이므로 Python, PowerShell 모듈 또는 .NET을 온라인으로 설치하지
않습니다. Agent와 Viewer는 반드시 같은 Release의 조합을 사용합니다.

`0.10.3-poc`는 코드 서명되지 않은 시험판입니다. SmartScreen, EDR, AppLocker 또는 WDAC가
경고하거나 차단할 수 있으며, 보안 정책을 우회하지 말고 공식 Release와 파일 해시를 확인한
뒤 사내 보안 담당자의 승인 절차를 따르십시오.

Agent 업데이트 실패 뒤 `RECOVERY_REQUIRED`가 표시되거나 복구 자료가 남아 있으면 구형
Setup을 실행하거나 임시 폴더를 직접 지우지 말고, 동일한 `0.10.3-poc` Agent ZIP의 Setup을
다시 실행하십시오.

압축을 풀기 전에 각 ZIP의 SHA-256을 GitHub Release 본문에 표시된 값과 비교하십시오.
`SHA256SUMS.txt`는 내부 검증용이라 별도 Asset으로 배포하지 않습니다.

다운로드한 ZIP은 네트워크 공유에서 직접 실행하지 말고 각 PC의 로컬 폴더에 완전히 압축
해제하십시오. ZIP 내부 또는 메일 첨부 미리 보기에서 실행하면 함께 제공된 파일을 찾지 못할
수 있습니다.

## 2. 필요한 네트워크 정보

```text
Viewer PC ── HTTPS/TCP 18443 ──> Agent PC
Agent PC  ── Telnet/TCP 23 ────> 삼성 스위치 관리망
```

Agent 설치 전에 다음 정보만 준비합니다.

| 항목 | 의미 | 조건 |
|---|---|---|
| Viewer PC IPv4 | Viewer가 Agent에 접속할 때 사용하는 출발지 주소 | 고정 IPv4 한 개 |
| 관리망 | Agent가 스위치에 Telnet 접속할 직접 연결 사설망 | 자동 검색 결과 중 1~2개 |
| Agent 주소 | Viewer에 입력할 Agent PC IPv4 또는 사내 DNS 이름 | Viewer에서 접근 가능 |

사용자가 CIDR을 계산하거나 입력하지 않습니다. Agent Setup이 활성 네트워크 어댑터에서 직접
연결된 RFC1918 사설망을 검색하고 선택 결과를 내부 CIDR 정책으로 저장합니다.

Viewer PC 주소가 DHCP로 자주 바뀌거나 Agent PC가 스위치 관리망에 직접 연결되어 있지 않다면
임의로 사설망 전체를 허용하지 마십시오. 이 버전의 단순 연결 모델과 맞지 않으므로 고정 주소와
관리망 구성을 사내 네트워크 관리자에게 요청해야 합니다.

## 3. Agent 설치 또는 업데이트

### 실행 순서

1. Agent ZIP을 Agent PC의 로컬 임시 폴더에 압축 해제합니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행합니다.
3. Windows UAC에서 관리자 권한을 승인합니다.
4. Viewer PC의 고정 IPv4를 입력합니다.
5. 자동 검색된 목록에서 스위치 관리망 1~2개를 선택합니다.
6. `검사`를 눌러 입력값과 현재 서비스·포트·방화벽 상태를 확인합니다.
7. `설치/업데이트`를 누르고 모든 단계가 성공인지 확인합니다.

Agent Setup은 다음 항목을 구성합니다.

- `SamsungSwitchWatchAgent` Windows 서비스
- 자동 시작과 서비스 실패 복구 정책
- HTTPS/TCP 18443 수신
- 입력한 Viewer IPv4 한 개만 허용하는 Domain·Private 방화벽 규칙
- 입력한 Viewer IPv4만 Agent API에서 다시 허용하는 애플리케이션 접근 제한
- 선택한 관리망만 허용하는 Telnet 대상 정책
- `%ProgramData%\SamsungSwitchWatch`의 Agent 설정과 HTTPS 신원

설치가 끝나면 Agent는 서비스로만 실행합니다. 일반 사용자의 바탕 화면, 작업 표시줄과
시스템 트레이에는 창이 나타나지 않습니다. RDP를 끊거나 다른 사용자가 로그인해도 서비스는
계속 실행됩니다. 로컬 관리자는 Windows 보안 모델상 서비스를 중지할 수 있으므로 관리자
계정을 다른 사용자에게 제공하지 마십시오.

`SamsungSwitchWatch.Agent.exe`를 직접 더블클릭하는 것은 설치나 진단 방법이 아닙니다.
직접 실행하면 사용자 세션에 Agent 창을 남기지 않고 종료하는 것이 정상입니다.

### 설치 전 검사와 재진단

Agent Setup의 `검사`는 설정을 변경하지 않고 다음 단계를 확인합니다.

1. 운영체제와 관리자 권한
2. 패키지 파일과 BUILD-MANIFEST
3. Viewer IPv4와 선택 관리망
4. 서비스 상태
5. HTTPS/TCP 18443 수신 상태
6. 제품 소유 방화벽 규칙
7. Agent 준비 상태

다른 프로그램이 만든 TCP/18443 인바운드 허용 규칙이 발견되면 노란색
`FIREWALL_OVERLAP_PROTECTED` 경고를 표시하지만 설치를 중단하지 않습니다. Setup은 그 규칙을
삭제·비활성화·변경하지 않습니다. `설치/업데이트`를 계속하면 제품 소유 Viewer `/32` 규칙을
적용하고 Agent도 입력한 Viewer IPv4를 API 요청마다 다시 확인합니다.

다음 조건은 경고가 아니라 설치 중단 사유입니다.

- Windows 방화벽 서비스 또는 활성 프로필 방화벽이 꺼짐
- 활성 프로필의 기본 인바운드 정책이 허용
- Public 프로필만 활성
- 그룹 정책이 로컬 방화벽 규칙 병합을 차단
- 제품 전용 규칙 이름을 다른 프로그램이 사용

설치 후 Viewer 연결이 안 되면 Agent Setup을 다시 열어 `검사`부터 실행하십시오. 명령줄
PowerShell을 실행하거나 실행 정책을 변경할 필요가 없습니다.

### 업데이트

같은 폴더에서 새 Release의 Agent Setup을 실행하면 기존 설치를 검사한 후 업데이트합니다.
HTTPS 신원과 유효한 네트워크 정책은 보존합니다. 파일 교체, 서비스 시작 또는 준비 상태
검사에 실패하면 기존 프로그램·설정·방화벽을 복구하고 실패 단계를 표시해야 합니다.

Agent를 먼저 업데이트하고 준비 상태를 확인한 뒤 같은 Release의 Viewer를 사용하십시오.

Viewer 주소가 바뀌어 `AGENT_CLIENT_NOT_ALLOWED`가 표시되면 Agent PC에서 같은 Release의
Agent Setup을 다시 실행하고 현재 Viewer PC의 고정 IPv4를 입력한 뒤 `설치/업데이트`를
수행하십시오. 방화벽 규칙과 Agent 내부 허용 주소가 함께 갱신됩니다.

## 4. Viewer 실행

Viewer는 설치 프로그램이 없는 포터블 프로그램입니다.

### 0.9 설치형 Viewer에서 처음 전환할 때

이전 Viewer가 자동 시작으로 실행 중이면 새 포터블 Viewer를 동시에 실행하지 않습니다. 두
프로그램이 같은 사용자 데이터에 동시에 접근하지 않도록 새 Viewer가 실행을 차단하고 전환
안내를 표시합니다.

1. 작업 표시줄 알림 영역의 기존 Viewer 아이콘을 우클릭해 `프로그램 종료`를 선택합니다.
   창의 X만 누르면 트레이에 계속 남으므로 완전히 종료해야 합니다.
2. `Win+R`을 누르고 `shell:startup`을 입력합니다.
3. 열린 폴더의 `Samsung Switch Watch` 바로 가기를 삭제합니다.
4. `Win+R` → `shell:programs`에서도 같은 이름의 이전 시작 메뉴 바로 가기를 삭제합니다.
5. 새 Viewer ZIP을 안정적인 로컬 폴더에 압축 해제하고 실행합니다.

기존 `%LOCALAPPDATA%\SamsungSwitchWatch`의 Agent 연결, 장비 목록과 암호화된 자격 증명은
같은 Windows 사용자에서 유지됩니다. 이전 `Program Files` 사본은 두 바로 가기를 제거한
뒤 직접 실행하지 않으면 동작하지 않으며, 삭제가 필요할 때는 기존 버전의 승인된 제거
절차를 사용합니다. 기존 `트레이로 최소화` 설정도 유지되므로 창이 바로 보이지 않으면 알림
영역의 Viewer 아이콘을 열고, 작업 관리자에서 실행 경로가 새로 압축 해제한 폴더인지
확인하십시오.

1. Viewer ZIP을 운영자 PC의 항상 사용할 로컬 폴더에 압축 해제합니다.
2. `SamsungSwitchWatch.Viewer.exe`를 더블클릭합니다.
3. 필요하면 사용자가 직접 바탕 화면 바로 가기를 만듭니다.

Viewer 실행에는 UAC와 관리자 권한이 필요하지 않습니다. 시작 메뉴 등록과 Windows 로그인
자동 시작도 수행하지 않습니다. 자동 감시가 필요할 때는 Viewer를 직접 실행해 둡니다.

Viewer 데이터는 `%LOCALAPPDATA%\SamsungSwitchWatch`에 저장됩니다.

- Agent 주소와 화면 설정
- 장비명·모델·IPv4
- DPAPI CurrentUser로 암호화한 ID·PW·enable PW
- 장비별 감시 설정, 기준값, 이벤트와 감시 공백

수동으로 입력한 명령과 원문 출력은 Viewer 메모리에서만 사용하고 저장하거나 내보내지
않습니다. Viewer 폴더를 다른 PC로 복사해도 해당 Windows 사용자의 암호화된 비밀번호를
복호화할 수 없습니다.

## 5. Agent 연결

1. Viewer 상단에서 `Agent 연결`을 엽니다.
2. Agent를 설치한 PC의 IPv4 또는 사내 DNS 이름을 입력합니다.
3. `연결` 또는 `진단`을 실행합니다.

스위치 IP, Viewer PC 주소 또는 `localhost`를 입력하지 마십시오. Agent와 Viewer가 서로 다른
PC이면 실제 Agent PC 주소를 사용합니다. 포트와 경로는 HTTPS/18443으로 자동 정규화됩니다.

연결 진단은 다음 순서로 진행됩니다.

| 단계 | 확인 내용 | 실패 시 우선 확인 |
|---|---|---|
| 주소 | 입력 형식과 DNS | Agent PC 주소 오입력 |
| TCP/18443 | Viewer에서 Agent까지 연결 | 서비스, 라우팅, 방화벽, EDR |
| HTTPS | 암호화와 Agent 공개 신원 | Agent 재설치 여부, 보안 프로그램 |
| API | Agent identity와 준비 상태 | Agent Setup의 검사 결과 |
| 버전 | Agent·Viewer 제품 버전 | 같은 Release ZIP 사용 |

인증서 SHA-256 지문이나 페어링 토큰을 사용자가 입력하지 않습니다. 최초 정상 연결에서 Viewer가
Agent 공개 신원을 내부적으로 자동 저장합니다. 같은 주소의 신원이 바뀌면 중간자 공격 또는
Agent PC 교체 가능성이 있으므로 연결을 차단합니다. 정상적인 재설치·PC 교체가 확실할 때만
Viewer의 신뢰 재설정을 사용합니다.

## 6. 장비 등록과 명령 실행

`장비 관리`에서 다음 항목을 입력합니다.

| 입력 | 필수 | 설명 |
|---|---:|---|
| 장비명 | 예 | 운영자가 알아볼 이름 |
| 모델 | 예 | IES4224GP, IES4028XP, IES4226XP 등 |
| 장비 IPv4 | 예 | Agent Setup에서 선택한 관리망 내부 주소 |
| ID | 예 | Telnet 로그인 ID |
| 로그인 PW | 예 | Telnet 로그인 비밀번호 |
| enable PW | 아니요 | 장비가 enable 전환을 요구할 때만 |

먼저 `접속 시험`을 실행하고 성공한 장비에서만 명령을 실행합니다. 수동 입력은 한 줄 `show`
명령 하나만 허용하며 Viewer와 Agent가 각각 다시 검증합니다.

주로 확인할 명령 후보:

```text
show port status
show syslog tail num 100
show sylog tail num 100
```

`syslog`와 `sylog` 중 지원하는 표기는 모델·펌웨어마다 다를 수 있습니다. 미지원 명령은 해당
수집 항목 실패로 표시하고 다른 장비 또는 전체 Viewer를 정상으로 오판하지 않습니다.

줄바꿈, `;`, `|`, `&` 같은 구분자와 configure·shutdown·reload 등 설정 변경 명령은
차단합니다. 이 POC는 운영 장비 설정을 변경하는 도구가 아닙니다.

## 7. 감시 동작

- Viewer가 실행 중일 때만 등록 주기로 Agent에 조회를 요청합니다.
- Agent는 요청마다 새 Telnet 세션을 열고 제한 시간 안에 종료합니다.
- Viewer가 꺼지거나 절전 상태이면 감시가 중단되고 다음 실행에서 `감시 공백`으로 표시합니다.
- Agent 연결 실패를 모든 스위치 Down으로 바꾸지 않습니다. 마지막 상태는 유지하되 현재
  상태를 `확인 불가`로 표시합니다.
- 같은 장애가 유지될 때 팝업을 반복하지 않고 복구 시 별도 이벤트를 표시합니다.

## 8. 연결 오류

### AGENT_CONNECTION_REFUSED

Viewer가 Agent PC의 TCP/18443에 연결하지 못했습니다.

1. Viewer에 실제 Agent PC 주소를 입력했는지 확인합니다.
2. Agent PC에서 Agent Setup의 `검사`를 실행합니다.
3. `SamsungSwitchWatchAgent` 서비스와 HTTPS/18443 수신 상태를 확인합니다.
4. 설치 시 입력한 고정 Viewer IPv4가 현재 Viewer 주소와 같은지 확인합니다.
5. Windows 방화벽 프로필이 Domain 또는 Private인지 확인합니다.
6. EDR·백신·사내 방화벽 차단은 보안 담당자에게 확인합니다.

### AGENT_IDENTITY_CHANGED

같은 Agent 주소에서 이전과 다른 HTTPS 신원이 확인됐습니다. Agent PC 교체 또는 데이터를
삭제한 재설치가 맞는지 관리자에게 확인하기 전에는 신뢰를 초기화하지 마십시오.

### AGENT_VERSION_MISMATCH

Agent와 Viewer가 서로 다른 Release입니다. 두 PC 모두 같은 버전의 공식 ZIP으로 맞춥니다.

### TARGET_NOT_ALLOWED

장비 IPv4가 Agent Setup에서 선택한 관리망에 포함되지 않습니다. 주소 오입력을 먼저 확인하고
정말 다른 관리망 장비라면 Agent Setup을 관리자 권한으로 다시 실행하여 정책을 검토합니다.

### TCP_TIMEOUT / AUTH_FAILED / ENABLE_FAILED

- `TCP_TIMEOUT`: Agent PC→장비 TCP/23, ACL과 Telnet 활성 상태 확인
- `AUTH_FAILED`: ID·로그인 PW와 장비의 `login local` 적용 확인
- `ENABLE_FAILED`: enable 필요 여부, enable PW와 로그인 직후 프롬프트 확인

### COMMAND_TIMEOUT / PROMPT_PARSE_FAILED / OUTPUT_LIMIT_EXCEEDED

장비 응답 지연, 페이징 문자열 또는 프롬프트가 예상 형식과 다르거나 출력이 안전 한도를
넘었습니다. 더 좁은 `show` 명령으로 확인하고 실제 IP·계정·원문을 제거한 오류 코드와 단계만
개발자에게 전달하십시오.

## 9. 사내 첫 적용 순서

1. Agent PC에서 Setup의 검사와 설치를 완료합니다.
2. Viewer PC 한 대에서 Agent 연결만 확인합니다.
3. 영향이 적은 스위치 한 대를 등록합니다.
4. 접속 시험을 실행합니다.
5. 부하가 작은 읽기 전용 명령 한 개를 실행합니다.
6. 결과 수신 뒤 Telnet 세션이 종료되는지 확인합니다.
7. 짧은 주기로 반복하지 말고 한 대의 주기 감시를 확인합니다.
8. 소수 장비로 확대하고 오류·세션·장비 부하를 확인합니다.
9. 검증이 끝난 뒤 전체 대상에 단계적으로 적용합니다.

운영 장비에 설정 변경 명령을 실행하거나 첫 실행부터 모든 장비에 동시에 접속하지 마십시오.

## 10. 공개 패키지와 개발자 파일

공개 Agent ZIP에는 Agent Setup, Agent 서비스 실행 파일, 필요한 WPF 네이티브 런타임,
BUILD-MANIFEST, SBOM과 사용자 문서만 포함합니다. 공개 Viewer ZIP에는 Viewer 실행 파일,
필요한 WPF 네이티브 런타임, BUILD-MANIFEST, SBOM과 사용자 문서만 포함합니다.

PowerShell/CMD 설치·제거·진단 파일은 개발과 레거시 복구를 위해 소스 저장소에만 유지하며
공개 ZIP에 포함하지 않습니다. 실행 정책을 우회하거나 사용자가 PowerShell 정책을 변경하게
하는 설치 절차는 사용하지 않습니다.
