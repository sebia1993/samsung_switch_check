# Samsung Switch Watch 설치 및 운영 안내

## 1. 준비

공식 GitHub `v0.11.2-poc` Release의 Assets에서 다음 두 파일만 받습니다.

- `SamsungSwitchWatch-Agent-0.11.2-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.11.2-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 두 ZIP은 Windows
x64용 self-contained 빌드이므로 Python, PowerShell 모듈 또는 .NET을 온라인으로 설치하지
않습니다. API v4가 호환되면 버전이 달라도 경고 후 연결하지만, 기능 차이와 운영 혼동을 줄이기
위해 Agent와 Viewer는 같은 Release 조합을 권장합니다.

`0.11.2-poc`는 코드 서명되지 않은 시험판입니다. SmartScreen, EDR, AppLocker 또는 WDAC가
경고하거나 차단할 수 있으며, 보안 정책을 우회하지 말고 공식 Release와 파일 해시를 확인한
뒤 사내 보안 담당자의 승인 절차를 따르십시오.

Agent 설치·업데이트 실패 뒤 미완료 작업이 감지되면 Setup은 상태를 읽기 전용으로 확인하고
`설치/업데이트`를 비활성화합니다. 구형 Setup을 실행하거나 설치를 반복하지 말고, 같은
`0.11.2-poc` Agent ZIP의 Setup에서 별도의 `이전 상태 복구`를 사용하십시오. 복구 성공 뒤에는
운영자가 `설치/업데이트`를 한 번 눌러 내부 사전 점검부터 새 작업을 별도로 시작해야 합니다.
복구가 자동으로 설치를 이어서 실행하지는 않습니다.

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

Agent 설치에는 Viewer IP나 스위치 관리 CIDR 입력이 필요하지 않습니다. Viewer에서 연결할 때
다음 정보만 준비합니다.

| 항목 | 의미 | 조건 |
|---|---|---|
| Agent 주소 | Viewer에 입력할 Agent PC IPv4 또는 사내 DNS 이름 | Viewer에서 접근 가능 |
| 장비 주소 | Viewer에서 등록할 삼성 스위치 관리 IPv4 | RFC1918 사설 IPv4 |

Agent는 loopback과 RFC1918 사설 IPv4 Viewer 요청만 받고, 스위치 대상도 RFC1918 사설 IPv4와
Telnet/TCP 23으로 자동 제한합니다. Agent PC에서 장비까지 승인된 라우팅과 TCP/23 경로가
있는지는 사내 네트워크 관리자가 별도로 확인해야 합니다.

## 3. Agent 설치 또는 업데이트

### 실행 순서

1. Agent ZIP을 Agent PC의 로컬 임시 폴더에 압축 해제합니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행합니다.
3. Windows UAC에서 관리자 권한을 승인합니다.
4. 별도 주소나 CIDR 입력 없이 `설치/업데이트`를 한 번 누릅니다. Setup이 내부 사전 점검을
   먼저 수행한 뒤 설치 또는 업데이트를 계속합니다.
5. 이전 작업이 감지되면 `설치/업데이트`가 비활성화됩니다. 복구 가능 상태에서만
   `이전 상태 복구`를 누르고, 복구 완료 뒤 `설치/업데이트`를 한 번 다시 누릅니다.
6. 설치 결과 또는 연결 확인 경고를 확인한 뒤 Viewer에서 연결 진단을 실행합니다.

Agent Setup은 다음 항목을 구성합니다.

- `SamsungSwitchWatchAgent` Windows 서비스
- 자동 시작과 서비스 실패 복구 정책
- HTTPS/TCP 18443 수신
- RFC1918 원격 IPv4만 허용하도록 시도하는 Domain·Private 방화벽 규칙
- loopback과 RFC1918 Viewer 요청만 허용하는 애플리케이션 접근 제한
- RFC1918 스위치 IPv4와 Telnet/TCP 23만 허용하는 대상 정책
- `%ProgramData%\SamsungSwitchWatch`의 Agent 설정

설치가 끝나면 Agent는 서비스로만 실행합니다. 일반 사용자의 바탕 화면, 작업 표시줄과
시스템 트레이에는 창이 나타나지 않습니다. RDP를 끊거나 다른 사용자가 로그인해도 서비스는
계속 실행됩니다. 로컬 관리자는 Windows 보안 모델상 서비스를 중지할 수 있으므로 관리자
계정을 다른 사용자에게 제공하지 마십시오.

`SamsungSwitchWatch.Agent.exe`를 직접 더블클릭하는 것은 설치나 진단 방법이 아닙니다.
직접 실행하면 사용자 세션에 Agent 창을 남기지 않고 종료하는 것이 정상입니다.

### 설치 내부 사전 점검과 진단 전용 모드

일반 운영 모드에는 별도의 `사전 점검` 버튼이 표시되지 않습니다. 운영자가
`설치/업데이트`를 누르면 Setup이 설정 변경 전에 다음 단계를 내부적으로 확인하고, 통과하거나
계속 가능한 경고인 경우에만 설치를 진행합니다.

1. 운영체제와 관리자 권한
2. 패키지 파일과 BUILD-MANIFEST
3. 서비스 상태
4. HTTPS/TCP 18443 수신 상태
5. 제품 소유 방화벽 규칙
6. Agent 준비 상태

진단 전용 모드에서는 `설치/업데이트`가 비활성화되고 읽기 전용 `사전 점검` 버튼만 표시됩니다.
지원 담당자가 Agent PC 내부 상태를 따로 확인하도록 안내한 경우에만 이 모드를 사용합니다.
일반 설치나 연결 확인을 위해 진단 전용 모드를 먼저 실행할 필요는 없습니다.

실행 중인 구형 API v4 Agent를 내부 사전 점검 또는 진단 전용 모드에서 확인할 때는
`status=ready`와 `apiVersion=4`인 최소 응답도
준비 상태로 확인합니다. 새 Agent 설치·업데이트 뒤에는 새 패키지의 HTTPS 프로토콜과 정확한
제품 버전까지 확인해 정상 readiness와 `AGENT_LOCAL_CONNECTION_UNCONFIRMED` 경고를 구분합니다.
구형 Agent 응답을 새 버전의 정상 준비 상태로 오인하지 않으며, 이 확인 실패만으로 설치를
되돌리지는 않습니다.

다른 프로그램이 만든 TCP/18443 인바운드 허용 규칙이 발견되면 노란색
`FIREWALL_OVERLAP_PROTECTED` 경고를 표시하지만 설치를 중단하지 않습니다. Setup은 그 규칙을
삭제·비활성화·변경하지 않습니다. `설치/업데이트`를 계속하면 제품 소유 RFC1918 규칙을
적용하려고 시도하고 Agent도 사설 IPv4 요청을 다시 확인합니다.

제품 규칙은 Enabled·Inbound·Allow·TCP·18443·Domain/Private·Edge Traversal 비활성과 세
RFC1918 원격 대역을 기준으로 확인합니다. `Any`, `LocalSubnet`, Public 프로필과 IPv6를 제품
규칙으로 만들지 않습니다.

규칙을 적용한 직후 Windows가 아직 새 값을 반환하지 않을 수 있어 즉시 한 번 확인한 뒤 200ms
간격으로 최대 2초까지만 재확인합니다. 이 제한 안에 정확한 규칙을 확인하지 못하거나 Windows
방화벽 서비스·활성 프로필·로컬 규칙 병합 GPO·제품 규칙 소유권을 확인하지 못하면
`FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고를 표시합니다. Setup은 방화벽 변경분만 설치 전
상태로 복원하려고 시도하고 복원 확인 여부를 경고에 포함하며, Agent 서비스와 프로그램은
유지합니다. Agent의 사설 IPv4 접근 제한도 유지합니다.

이 경고는 설치 실패가 아니라 **원격 Viewer 연결을 아직 확인하지 못했다**는 뜻입니다. 설치
결과는 `설치 완료 · 원격 Viewer 연결 확인 필요`로 표시되며, Viewer에서 연결 테스트를 실행해
TCP/18443 경로를 최종 확인해야 합니다. 다음 환경에서 경고가 발생할 수 있습니다.

- Windows 방화벽 서비스 또는 활성 프로필 방화벽이 꺼짐
- 활성 프로필의 기본 인바운드 정책이 허용
- Public 프로필만 활성
- 그룹 정책이 로컬 방화벽 규칙 병합을 차단
- 제품 전용 규칙 이름을 다른 프로그램이 사용
- 제품 규칙 적용 또는 재조회가 실패하거나 정확한 RFC1918 규칙으로 확인되지 않음

Setup은 이런 상황에서도 `Any`, `LocalSubnet` 또는 Public 프로필 규칙을 대체로 만들지 않습니다.
Viewer TCP 연결에 실패하면 Windows 관리자에게 승인된 방화벽·GPO 정책을 요청하십시오.

설치 후 Viewer 연결이 안 되면 Viewer의 연결 진단을 먼저 실행하십시오. Agent PC 내부 상태를
추가로 확인해야 하고 지원 담당자가 안내한 경우에만 Setup 진단 전용 모드의 `사전 점검`을
사용합니다. 명령줄 PowerShell을 실행하거나 실행 정책을 변경할 필요가 없습니다.

### 중단된 설치·업데이트 복구

Setup은 시작할 때 미완료 설치·업데이트 작업 기록을 변경하지 않고 먼저 확인합니다.

- 안전하게 되돌릴 수 있는 상태이면 `이전 상태 복구`만 활성화하고 `설치/업데이트`는
  비활성화합니다.
- 작업 기록이 손상됐거나 파일·서비스 상태가 기록과 맞지 않아 복구 안전성을 증명할 수 없으면
  복구와 설치를 모두 비활성화하고 Windows 관리자 확인을 요청합니다.
- 검증된 staging·backup·failed·journal 경로의 정리가 잠시 실패하면 Setup이 최대 3회
  시도하고, 실패한 시도 사이에만 250ms 대기합니다. 다른 경로나 넓은 상위 폴더는 정리하지
  않습니다.
- 각 정리 대상이 실제로 사라졌고 새로 검사한 작업 기록에도 미완료 상태가 없을 때만 복구
  성공과 설치 버튼 활성화를 표시합니다. 설치를 자동으로 시작하지 않으며, 운영자가
  `설치/업데이트`를 다시 한 번 눌러 내부 사전 점검부터 새 작업을 별도로 실행합니다.
- 복구가 실패하면 최초 설치·업데이트 실패 원인과 복구 단계별 원인을 구분해 표시합니다.
  하나의 `SETUP_ROLLBACK_FAILED`만 반복 표시되는 것으로 원인을 판단하지 마십시오.
- 로컬 준비 상태 확인 실패에는 서비스, TCP/18443, HTTPS, 응답 형식과 버전 중 마지막으로
  확인하지 못한 안전한 `AgentHealthCode`가 함께 표시됩니다. v0.11 자동 설치 경로에서는
  `AGENT_LOCAL_CONNECTION_UNCONFIRMED` 경고로 표시하며 설치를 되돌리지 않습니다. 호환용
  진단 전용 사전 점검 경로에서만 `SETUP_HEALTH_FAILED`가 남을 수 있습니다. 실제 PID, IP,
  경로와 예외 원문은 포함하지 않습니다.
- 로컬 준비 상태 요청 실패는 `HTTPS_TLS_FAILED`, `HTTPS_REQUEST_TIMEOUT`,
  `HTTPS_CONNECTION_RESET`, `HTTPS_EOF`, `HTTPS_CONNECT_FAILED`로 구분합니다. 화면의
  `Setup → 127.0.0.1:18443 → Agent 서비스`는 Agent PC 내부 통신 경로입니다. 이 분류는
  Viewer IP나 스위치 관리망 설정 문제를 뜻하지 않으므로 해당 값을 임의로 변경하지 마십시오.
- 익명 진단에는 서비스 실행, TCP/18443 수신 소유, HTTPS 시도 횟수·마지막 전송 단계와
  Agent 재시작 관측 여부만 기록합니다. 실제 PID, 주소, 경로, 인증서와 예외 원문은 기록하지
  않습니다. SWD1은 하위 호환을 위해 세부 실패를 기존 `HTTPS_REQUEST_FAILED`로 요약합니다.
- 서비스 설치 뒤 준비 상태 확인이 실패하면 설치를 되돌리지 않고
  `AGENT_LOCAL_CONNECTION_UNCONFIRMED` 경고를 표시합니다. 파일·서비스 구성 실패와 구분하여
  반복 설치와 불필요한 rollback을 방지합니다.
- `0.11.2-poc` Agent는 서비스 시작마다 새 RSA 인증서를 생성합니다. Windows Schannel용 임시
  사용자 키 컨테이너는 프로세스 수명 동안만 사용하고 Agent 종료 시 제거합니다. 영구 Agent
  신원 파일, 인증서 지문과 페어링 토큰은 사용하지 않습니다.
- rollback 프로그램 폴더 이동은 일시적 파일 잠금에 한해 최대 5회 제한적으로 다시
  시도합니다. 계속 잠겨 있거나 원본·대상 상태가 모호하면 이전 서비스를 다시 시작하지 않고
  작업 기록을 보존합니다.
- `ROLLBACK_EVIDENCE_CLEANUP_FAILED` 범주에 해당하는 정리 실패는 화면 상단의
  `SETUP_ROLLBACK_FAILED`와 staging·backup·failed·journal 대상별 전용 코드로 구분해
  표시합니다. 실제 경로나 파일명은 진단에 넣지 않습니다.
- 실패 화면에만 나타나는 `진단정보 복사`는 제품 버전, UTC 시각, 작업 종류, 안전한 오류 코드,
  작업 기록 형식·단계, 필요한 파일의 존재 여부와 서비스 상태만 클립보드에 복사합니다.
- 진단 전용 사전 점검·설치·복구가 끝나면 성공 여부와 관계없이 `익명 진단 저장`을 눌러
  사내에서 외부로 전달 가능한 진단 TXT를 수동 저장할 수 있습니다. 자동으로 파일을 만들지
  않습니다.

익명 진단 파일은 `SSW_FIELD_DIAGNOSTIC/2`로 시작하는 UTF-8 BOM 텍스트입니다. 메모장을
사진 한 장으로 전달할 수 있도록 최대 12줄, 줄당 88자로 제한합니다. 제품·Windows 버전,
작업 결과, 실패 단계, 안전한 오류·권장 조치 코드와 핵심 상태는 보존하며 실제 IP/CIDR,
PC·사용자명, 계정, 인증서 정보, 절대 경로, 방화벽 원문, 예외 원문, 명령과 장비 출력은
포함하지 않습니다. 저장에 실패하면 `DIAGNOSTIC_WRITE_FAILED`가 표시되며 성공한 것으로
처리하지 않습니다. 과거 `/1` 파일은 저장 재현 도구에서 계속 분석할 수 있습니다.

예상하지 못한 권한·파일·시간 초과·Windows API 오류도 예외 원문 대신 마지막 안전 단계,
오류 범주, 해당 단계와 전체 작업의 제한된 소요 시간만 기록합니다. 오류를 진단에 남겼다는
이유로 설치 성공으로 처리하지 않으며 화면의 실패 상태가 유지됩니다.

실패 화면의 `지원 코드 · 이 코드만 전달하세요` 아래에는
`SWD1-XXXX-XXXX-XXXX-XXXX` 형식의 짧은 코드가 표시됩니다. 별도 버튼 없이 코드를 마우스나
키보드로 선택한 뒤 `Ctrl+C`로 복사해 전화·메신저 지원에 전달합니다. 코드는 오프라인으로
생성되며 계정·주소·경로·명령·출력 원문을 포함하지 않습니다. CRC는 입력 오타 확인용일 뿐
인증·페어링·암호화 기능이 아니므로 코드로 접속을 승인하거나 신원을 확인하지 마십시오.

`SETUP_PATH_NOT_WRITABLE`이 표시되면 패키지나 로컬 HTTPS가 아니라 기존 Agent 제품 폴더의
권한 또는 파일 상태를 확인하지 못한 것입니다. 잠시 후 한 번만 다시 시도하고 반복되면 화면의
지원 코드만 전달하십시오. `SETUP_PATH_UNTRUSTED` 또는 `SETUP_PATH_INVALID`는 반복 설치하지
마십시오. 어떤 경우에도 제품 폴더나 ACL을 수동으로 삭제·변경하지 마십시오. `0.11.2-poc`는
검사 중 잠시 사라진 하위 파일을 한 번만 다시 확인합니다. 다시 확인해도 실제로 사라진 비루트
항목은 건너뛰며, 그 밖의 계속되는 접근·I/O 오류는 위 코드로 분류합니다.

복구 대기 또는 실패 상태에서는 `%ProgramFiles%\SamsungSwitchWatch` 아래의
`Agent.__staging_*`, `Agent.__backup_*`, `Agent.__failed_*` 폴더와
`%ProgramData%\SamsungSwitchWatch-Operations`의 작업 기록을 수동으로 삭제·이동·이름
변경하지 마십시오. 제품이 보존한 복구 근거가 사라지면 안전한 복구 여부를 판단할 수 없습니다.
안전하지 않거나 손상된 상태는 `진단정보 복사` 결과를 사내 Windows 관리자에게 전달하고,
승인된 현장 절차로 확인해야 합니다.

복구 완료 메시지가 나타났다면 같은 실패 화면에서 설치를 자동으로 다시 시작하지 않습니다.
Setup을 닫지 않아도 되지만, 상태가 `복구 필요 없음`으로 바뀌고 설치 버튼이 다시 활성화됐는지
확인한 뒤 `0.11.2-poc` 패키지의 `설치/업데이트`를 한 번만 다시 실행하십시오. Setup이 내부
사전 점검부터 새 설치를 수행합니다. 같은 readiness 분류가 반복되면 재설치를 계속 반복하지
말고 SWD1 코드 또는 `진단정보 복사` 결과를 전달하십시오.

### 업데이트

같은 폴더에서 새 Release의 Agent Setup을 실행하면 기존 설치를 검사한 후 업데이트합니다.
파일 교체 또는 서비스 구성에 실패하면 기존 프로그램·설정을 복구하고 실패 단계를 표시합니다.
이전 설정의 Viewer IP, 대상 CIDR과 인증서 신뢰 필드는 호환성을 위해 읽을 수 있지만 v0.11
접근 정책이나 Viewer TLS 판단에는 사용하지 않습니다.

Agent를 먼저 업데이트하고 준비 상태를 확인한 뒤 같은 Release의 Viewer를 사용하십시오.

`AGENT_CLIENT_NOT_ALLOWED`가 표시되면 Viewer 출발지 주소가 loopback 또는 RFC1918 사설 IPv4인지
확인하십시오. Agent Setup에 Viewer 주소를 입력하는 절차는 없습니다.

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

스위치 IP나 Viewer PC 주소를 입력하지 마십시오. Agent와 Viewer가 같은 PC이면 `localhost` 또는
`127.0.0.1`을 사용할 수 있습니다. 포트와 경로는 HTTPS/18443으로 자동 정규화됩니다.

### 동일 PC에서 먼저 확인하는 경우

Agent와 Viewer를 한 PC에 함께 설치해 반입 전에 확인할 수 있습니다.

1. Agent Setup에서 `설치/업데이트`를 한 번 눌러 내부 사전 점검과 설치를 완료합니다.
2. Viewer의 `Agent 연결`에 `localhost`를 입력합니다.
3. 연결이 성공하면 같은 PC의 Agent 서비스와 API 동작이 확인된 것입니다.

이 테스트는 스위치 자격 증명을 읽거나 명령을 실행하지 않습니다. 스위치 연결은 저장 후
`장비 관리 → 로그인 확인`에서 계정과 프롬프트를 확인하고, 수집 진단에서 실제 명령을 별도로
확인합니다.

동일 PC 연결 성공은 원격 Viewer PC에서 Agent PC까지의 라우팅과 방화벽을 증명하지 않습니다.
실제 원격 Viewer에서도 Agent PC 주소를 입력해 연결 진단을 다시 실행하십시오.

연결 진단은 다음 순서로 진행됩니다.

| 단계 | 확인 내용 | 실패 시 우선 확인 |
|---|---|---|
| 주소 | 입력 형식과 DNS | Agent PC 주소 오입력 |
| TCP/18443 | Viewer에서 Agent까지 연결 | 서비스, 라우팅, 방화벽, EDR |
| HTTPS | 암호화 연결 | Agent 서비스, TLS 정책, 보안 프로그램 |
| API | Agent 준비 상태와 API v4 | Viewer 연결 진단, 필요하면 Setup 진단 전용 사전 점검 |
| 버전 | Agent·Viewer 제품 버전 | 다르면 경고, 같은 Release 권장 |

연결 검사가 성공 또는 실패로 끝나면 `익명 진단 저장`으로 단계별 상태와 제한된 소요 시간을
최대 12줄의 사진 한 장용 TXT로 저장할 수 있습니다. 파일은 사용자가 선택한 위치에만 생성되며
입력한 Agent 주소, DNS 이름, IP/CIDR, PC·사용자명, 계정, 인증서 정보, 경로, 예외 원문과
장비 명령/출력은 포함하지 않습니다. 사내에서 오류를 재현하기 어려울 때 이 TXT와 화면에
표시된 오류 코드를 전달합니다.

연결 실패 때만 단계 목록 아래에 짧은 SWD1 지원 코드가 표시됩니다. 코드를 선택해
`Ctrl+C`로 복사할 수 있으며 성공, 새 연결 확인 또는 Agent 주소 변경 시 이전 코드는
사라집니다. Viewer의 추가 확인에는 화면의 실패 단계와 오류 코드를 함께 사용합니다. Agent
Setup 실패에서 더 긴 화면용 원인이 필요할 때만 기존 `진단정보 복사`를 추가로 사용합니다.

인증서 SHA-256 지문이나 페어링 토큰을 사용자가 입력하지 않습니다. Viewer는 Agent의 임시
자체 서명 인증서를 자동 수락하고 신원을 저장하거나 비교하지 않습니다. HTTPS는 전송 내용을
암호화하지만 Agent 신원을 인증하지 않으므로 신뢰할 수 있는 사내 사설망에서만 사용합니다.

## 6. 장비 등록과 명령 실행

`장비 관리`에서 다음 항목을 입력합니다.

| 입력 | 필수 | 설명 |
|---|---:|---|
| 장비명 | 예 | 운영자가 알아볼 이름 |
| 모델 | 예 | IES4224GP, IES4028XP, IES4226XP 등 |
| 장비 IPv4 | 예 | RFC1918 사설 IPv4 |
| ID | 예 | Telnet 로그인 ID |
| 로그인 PW | 예 | Telnet 로그인 비밀번호 |
| enable PW | 아니요 | 장비가 enable 전환을 요구할 때만 |

먼저 `로그인 확인`을 실행합니다. 이 단계는 TCP/23, 계정, 선택적 enable과 최종 프롬프트까지만
검사합니다. 실제 포트·로그 명령은 수집 진단 또는 수동 명령에서 확인합니다. 수동 입력은 한 줄
`show` 명령 하나만 허용하며 Viewer와 Agent가 각각 다시 검증합니다.

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
- 포트 상태와 시스템 로그는 순차적인 단일 명령 세션으로 실행합니다. 명령 시간 초과·출력 한도는
  실패한 항목만 `확인 불가`로 표시하고 다른 항목을 계속 수집합니다. 실패 명령은 같은 주기에 즉시
  재시도하지 않으며, 인증·enable·TCP·세션 종료는 반복 로그인을 막기 위해 해당 장비 주기를 중단합니다.
- 명령 출력은 30초 무응답 제한과 90초 전체 제한을 적용합니다. Agent는 넉넉한 Telnet 화면
  크기를 협상하고 알려진 페이징 문구를 처리하지만 페이지 진행이 32회를 넘으면 중단합니다.
- 이번 POC의 자동 감시 검증·지원 범위는 등록 장비 10대 이하입니다. 그보다 많은 장비는 한꺼번에
  등록하지 말고 별도 성능 검증 후 확대하십시오.
- Viewer가 꺼지거나 절전 상태이면 감시가 중단되고 다음 실행에서 `감시 공백`으로 표시합니다.
- Agent 연결 실패를 모든 스위치 Down으로 바꾸지 않습니다. 마지막 상태는 유지하되 현재
  상태를 `확인 불가`로 표시합니다.
- 감시를 켠 뒤 첫 자동 수집 전에는 `확인 대기`로 표시하며 정상 장비 수에 넣지 않습니다.
- 같은 장비의 로그인 확인·수동 명령 때문에 자동 수집이 미뤄지면 `확인 대기`로 표시하고 다음
  주기에 다시 수집합니다. 이전 정상 결과를 현재 정상으로 단정하지 않습니다.
- Agent 연결을 바꾸거나 Viewer를 종료할 때 진행 중인 요청을 취소한 뒤 제한된 시간 안에서
  이전 연결 자원을 정리합니다. 정리 실패는 사용자용 상태와 비식별 진단 로그에서 구분합니다.
- 같은 장애가 유지될 때 팝업을 반복하지 않고 복구 시 별도 이벤트를 표시합니다.

## 8. 연결 오류

### AGENT_CONNECTION_REFUSED

Viewer가 Agent PC의 TCP/18443에 연결하지 못했습니다.

1. Viewer에 실제 Agent PC 주소를 입력했는지 확인합니다.
2. Viewer의 연결 진단에서 실패 단계가 주소, TCP/18443, HTTPS 또는 API 중 어디인지 확인합니다.
3. Agent PC의 Windows 서비스에서 `SamsungSwitchWatchAgent`가 실행 중인지 확인합니다.
4. Viewer 출발지와 Agent 주소가 loopback 또는 RFC1918 사설 IPv4인지 확인합니다.
5. Windows 방화벽 프로필이 Domain 또는 Private인지 확인합니다.
6. Agent PC 내부 상태를 더 확인해야 하고 지원 담당자가 안내한 경우에만 진단 전용
   `사전 점검`을 실행합니다.
7. EDR·백신·사내 방화벽 차단은 보안 담당자에게 확인합니다.

이 오류는 HTTPS 인증서 오류가 아니라 Viewer에서 Agent PC의 TCP/18443까지 연결되지 않은
상태입니다. Agent 설치가 `설치 완료 · 원격 Viewer 연결 확인 필요`로 끝났다면 방화벽 또는 회사
GPO가 로컬 제품 규칙을 허용하는지 Windows 관리자에게 먼저 확인하십시오.

### AGENT_PROTOCOL_MISMATCH / TLS_IDENTITY_INVALID

TCP/18443 연결은 성공했지만 Agent의 HTTPS/TLS 응답을 확인하지 못했습니다. Viewer 연결
진단의 HTTPS 단계를 확인하고, 지원 담당자가 안내한 경우에만 Agent PC에서 Setup 진단 전용
`사전 점검`으로 `Setup → 127.0.0.1:18443 → Agent 서비스` 로컬 HTTPS 준비 상태를 확인하십시오.
TCP 단계가 성공한 경우 Viewer PC의 방화벽 규칙을 넓혀도 이 오류는 해결되지 않습니다.

### AGENT_LOCAL_CONNECTION_UNCONFIRMED

Agent 파일과 Windows 서비스 설치는 완료됐지만 Setup이 제한 시간 안에 로컬 HTTPS/API 준비
상태를 확인하지 못했습니다. 이 경고는 설치 실패나 rollback 실패가 아닙니다.

1. 같은 PC의 Viewer에서 `localhost`로 연결 진단을 실행합니다.
2. 서비스가 실행 중이고 TCP/18443을 Agent 프로세스가 수신하는지 확인합니다.
3. 지원 담당자가 안내한 경우에만 Setup 진단 전용 `사전 점검`을 실행합니다.
4. 계속 실패하면 화면의 AgentHealthCode 또는 SWD1 지원 코드만 전달합니다.

재설치를 반복하거나 방화벽 범위를 넓히지 마십시오. TCP는 열리지만 HTTPS 단계만 실패하면 TLS
정책, EDR·백신의 로컬 통신 검사와 Agent 서비스 로그를 Windows 관리자와 확인합니다.

### AGENT_VERSION_MISMATCH

API v4가 호환되면 Agent와 Viewer가 서로 다른 Release여도 경고 후 연결합니다. 기능 차이로 인한
혼동을 줄이기 위해 두 PC 모두 같은 버전의 공식 ZIP으로 맞추는 것을 권장합니다.

### FIREWALL_REMOTE_ACCESS_UNCONFIRMED

Agent 설치는 완료됐지만, Setup이 제품 소유 RFC1918 방화벽 규칙의 적용 또는 재확인을 완료하지
못했습니다. Cause에는 실제 Viewer 주소나
규칙 원문 대신 `FIREWALL_REMOTE_ADDRESS_MISMATCH` 같은 안전한 불일치 코드만 표시됩니다.

1. Viewer에서 Agent 연결 테스트를 실행합니다.
2. TCP/18443이 연결되면 별도 조치 없이 사용할 수 있습니다. Agent 내부 사설 IPv4 검증은
   계속 적용됩니다.
3. TCP 단계가 실패하면 Agent PC의 Windows 방화벽 서비스, Domain/Private 프로필과 회사 GPO의
   로컬 규칙 병합 정책을 Windows 관리자에게 확인합니다.
4. Setup을 반복 설치하거나 제품 규칙을 수동으로 넓히지 않습니다.

`SETUP_FIREWALL_FAILED`는 이전 버전 또는 복구가 필요한 예외 경로에서 보일 수 있는 호환 코드입니다.
현재 버전에서 단순 방화벽·GPO·적용·재조회 문제는 Agent 전체 설치 rollback 대신 위 경고로
처리합니다. `Any`, `LocalSubnet` 또는 Public 프로필 규칙을 만들어 우회하지 마십시오.

### SETUP_RECOVERY_REQUIRED / SETUP_ROLLBACK_FAILED

- `SETUP_RECOVERY_REQUIRED`: 이전 설치·업데이트가 끝나지 않아 새 설치를 시작할 수 없습니다.
  Setup이 복구 가능 상태로 표시할 때만 `이전 상태 복구`를 누릅니다.
- `SETUP_ROLLBACK_FAILED`: 설치·업데이트 실패 뒤 이전 상태 복구도 완전히 끝나지 않았습니다.
  화면의 최초 실패 원인과 복구 단계별 원인을 함께 확인합니다.
- `복구 완료`는 정리 대상 삭제 결과와 새 작업 기록 검사가 모두 성공한 경우에만 표시됩니다.
  여전히 미완료 작업이 확인되면 설치 버튼은 비활성 상태로 유지됩니다.
- 복구 성공은 설치 성공이 아닙니다. 설치 버튼이 다시 활성화되면 운영자가
  `설치/업데이트`를 한 번 눌러 내부 사전 점검부터 새 작업을 별도로 시작합니다.
- `진단정보 복사`는 실패 화면에서만 사용합니다. 복사된 내용에는 실제 IP/CIDR, PC·사용자명,
  절대 경로, 작업 ID, 서비스 계정, 방화벽 규칙 원문, 자격 증명, 인증서, 명령과 장비 출력이
  포함되지 않아야 합니다.
- 복구 자료와 작업 기록을 직접 정리하거나, 구형 Setup을 실행하거나, 설치 버튼을 반복해서
  눌러 우회하지 마십시오.

복구 단계 코드는 다음 위치를 뜻합니다.

| 코드 | 확인 위치 |
|---|---|
| `ROLLBACK_STATE_MISMATCH` | 작업 기록과 현재 복구 대상 상태 |
| `ROLLBACK_SERVICE_STOP_FAILED` | 새 Agent 서비스 중지 |
| `ROLLBACK_FILE_RESTORE_FAILED` | 이전 프로그램 파일과 ACL 복원·검증 |
| `ROLLBACK_DATA_CLEANUP_FAILED` | 설치 도중 생성한 데이터 정리 |
| `ROLLBACK_SERVICE_RESTORE_FAILED` | 이전 Agent 서비스 구성과 실행 상태 복원 |
| `ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED` | HTTPS/18443 방화벽 snapshot 복원 |
| `ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED` | 이전 버전 방화벽 snapshot 복원 |
| `ROLLBACK_JOURNAL_WRITE_FAILED` | 복구 상태 작업 기록 저장 |
| `ROLLBACK_EVIDENCE_CLEANUP_FAILED` | 복구 완료 뒤 staging·backup·failed·journal 정리와 삭제 결과 확인 |
| `ROLLBACK_STAGING_CLEANUP_FAILED` | 현재 작업의 staging 자료 정리 |
| `ROLLBACK_BACKUP_CLEANUP_FAILED` | 현재 작업의 backup 자료 정리 |
| `ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED` | 현재 작업의 failed 자료 정리 |
| `ROLLBACK_JOURNAL_CLEANUP_FAILED` | 현재 작업 journal 정리와 삭제 결과 확인 |

### TARGET_NOT_ALLOWED

장비 주소가 RFC1918 사설 IPv4가 아니거나 포트가 Telnet/TCP 23이 아닙니다. 주소 오입력과
사내 관리망 설계를 확인하십시오. Setup에서 예외 CIDR을 추가하는 절차는 없습니다.

### TCP_TIMEOUT / AUTH_FAILED / ENABLE_FAILED

- `TCP_TIMEOUT`: Agent PC→장비 TCP/23, ACL과 Telnet 활성 상태 확인
- `AUTH_FAILED`: ID·로그인 PW와 장비의 `login local` 적용 확인
- `ENABLE_FAILED`: enable 필요 여부, enable PW와 로그인 직후 프롬프트 확인

### COMMAND_TIMEOUT / PROMPT_PARSE_FAILED / OUTPUT_LIMIT_EXCEEDED

`COMMAND_TIMEOUT`은 30초 동안 새 응답이 없거나, 출력은 계속되지만 전체 90초 안에 종료
프롬프트를 확인하지 못했음을 뜻합니다. Viewer는 포트 상태와 시스템 로그 중 실패한 항목만
`확인 불가`로 표시하고 다른 결과는 유지합니다. 페이징 반복, 프롬프트 형식 차이 또는 출력 안전
한도 초과가 계속되면 실제 IP·계정·원문 대신 Viewer가 표시하는 오류 코드와 안전한 단계만
전달하십시오.

## 9. 사내 첫 적용 순서

1. Agent PC에서 Setup의 `설치/업데이트`를 한 번 눌러 내부 사전 점검과 설치를 완료합니다.
2. 필요하면 동일 PC 사전 테스트로 Agent 서비스와 API까지만 확인합니다.
3. 원격 Viewer PC 한 대에서 Agent 연결만 확인합니다.
4. 영향이 적은 스위치 한 대를 등록합니다.
5. 로그인 확인을 실행합니다.
6. 수집 진단에서 부하가 작은 읽기 전용 명령 한 개를 실행합니다.
7. 결과 수신 뒤 Telnet 세션이 종료되는지 확인합니다.
8. 짧은 주기로 반복하지 말고 한 대의 주기 감시를 확인합니다.
9. 소수 장비로 확대하고 오류·세션·장비 부하를 확인합니다.
10. 검증이 끝난 뒤 전체 대상에 단계적으로 적용합니다.

운영 장비에 설정 변경 명령을 실행하거나 첫 실행부터 모든 장비에 동시에 접속하지 마십시오.

## 10. 공개 패키지와 개발자 파일

공개 Agent ZIP에는 Agent Setup, Agent 서비스 실행 파일, 필요한 WPF 네이티브 런타임,
BUILD-MANIFEST, SBOM과 사용자 문서만 포함합니다. 공개 Viewer ZIP에는 Viewer 실행 파일,
필요한 WPF 네이티브 런타임, BUILD-MANIFEST, SBOM과 사용자 문서만 포함합니다.

PowerShell/CMD 설치·제거·진단 파일은 개발과 레거시 복구를 위해 소스 저장소에만 유지하며
공개 ZIP에 포함하지 않습니다. 실행 정책을 우회하거나 사용자가 PowerShell 정책을 변경하게
하는 설치 절차는 사용하지 않습니다.
