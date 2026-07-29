# Samsung Switch Watch v0.10 보안 설계

## 1. 신뢰 경계

| 경계 | 보호 방식 | 남는 위험 |
|---|---|---|
| Viewer 로컬 저장소 | DPAPI CurrentUser | 같은 Windows 사용자 세션 또는 계정 탈취 |
| Viewer → Agent | HTTPS, 자동 TOFU 신원 고정, Viewer 고정 IPv4 `/32` 방화벽, Agent의 동일 IPv4 재검증 | 애플리케이션 사용자 인증 없음 |
| Agent → 스위치 | Setup에서 확정한 RFC1918 관리망 제한, TCP/23 고정, 한 줄 `show` 정책 | Telnet 평문 노출 |
| Agent 서비스와 데이터 | 전용 서비스 SID, 제한된 서비스·폴더 ACL, 무창 서비스 | 로컬 관리자는 제어 가능 |

HTTPS를 사용하더라도 등록된 Viewer IPv4를 사용하는 클라이언트는 Agent API에 접근할 수
있습니다. Agent PC와 Viewer PC를 일반 사용자 VLAN, 공용 Wi-Fi 또는 인터넷에 노출하지
마십시오.

## 2. 자격 증명

장비 ID, 로그인 PW와 enable PW는 Viewer PC의 현재 Windows 사용자 범위 DPAPI로 암호화합니다.

- Viewer가 접속 시험 또는 명령을 실행할 때만 메모리에서 복호화합니다.
- HTTPS 요청으로 Agent에 전달된 값은 해당 요청의 Telnet 세션에서만 사용합니다.
- Agent 설정, 파일, 데이터베이스, 로그 또는 진단 자료에 저장하지 않습니다.
- API 응답과 오류 메시지에 되돌려 보내지 않습니다.
- Viewer 편집 화면은 저장된 비밀번호를 다시 평문으로 표시하지 않습니다.

DPAPI 파일을 다른 PC나 다른 Windows 사용자에게 복사해도 복호화할 수 없는 것이 정상입니다.
Windows 계정과 원격 접속 권한이 탈취되면 DPAPI만으로 보호할 수 없으므로 화면 잠금과 계정
권한을 별도로 관리해야 합니다.

수동 명령과 원문 출력은 Viewer 프로세스 메모리에서만 사용합니다. 특히
`show running-config` 결과에는 비밀정보가 포함될 수 있으므로 캡처, 메일, 이슈 첨부 또는 외부
반출을 금지합니다.

## 3. Agent 설치와 권한

`SamsungSwitchWatch.Agent.Setup.exe`는 Windows 서비스, 방화벽과 보호된 폴더를 구성하기 위해
최초 설치 또는 업데이트 때 UAC 승인이 필요합니다. 설치 완료 후 Setup 창을 계속 실행할 필요는
없습니다.

Agent는 다음 특성을 갖습니다.

- `SamsungSwitchWatchAgent` 이름의 자동 시작 Windows 서비스
- `NT SERVICE\SamsungSwitchWatchAgent` 가상 계정과 서비스 SID
- 사용자 데스크톱에 창이나 트레이 아이콘 없음
- 서비스 실패 후 5초, 15초, 60초 재시작 정책
- 일반 사용자에게 서비스 정지·구성 권한을 주지 않는 제한 ACL

Windows 로컬 관리자는 운영체제 정책상 서비스를 중지하거나 제거할 수 있습니다. 이 설계의
목표는 다른 일반 사용자의 실수로 Agent 창을 닫는 일을 방지하는 것이지, 로컬 관리자를 막는
것이 아닙니다.

Setup은 설치 폴더와 `%ProgramData%\SamsungSwitchWatch`에 폐쇄형 ACL을 적용합니다.

- `SYSTEM`: FullControl
- 로컬 `Administrators`: FullControl
- Agent 서비스 SID: 프로그램은 ReadAndExecute, 데이터는 Modify
- 일반 Users: 직접 접근 권한 없음

HTTPS 개인 키는 Agent DataDirectory에 저장하고 DPAPI LocalMachine으로 보호합니다. DPAPI만으로
같은 PC의 다른 사용자를 모두 차단할 수 없으므로 파일 ACL도 함께 필요합니다.

Setup은 공개 ZIP 안에서 네이티브 코드로 설치를 수행합니다. 공개 ZIP에 PowerShell 또는 CMD
설치 스크립트를 포함하지 않으므로 실행 정책 때문에 설치가 중단되는 흐름에 의존하지 않습니다.
저장소에 남은 유지보수 스크립트는 개발·CI용 source-only 자료입니다.

## 4. 설치 무결성과 rollback

Setup은 패키지를 변경하기 전에 다음을 확인합니다.

- 패키지 매니페스트 형식과 버전
- 포함 파일 SHA-256
- Agent 실행 파일 SHA-256
- Program Files와 ProgramData 사용 가능 여부
- 관리자 권한
- Viewer IPv4와 관리망 선택의 유효성

검증한 파일은 보호된 staging에 복사한 뒤 설치 폴더와 교체합니다. 서비스, 방화벽 또는 readiness
확인이 실패하면 기존 프로그램, 서비스 상태와 방화벽 규칙의 rollback을 시도합니다. rollback이
완전히 끝나지 않으면 성공으로 처리하지 않고 안정적인 Setup 오류 코드로 관리자 확인을
요청합니다.

Setup은 시작 시 미완료 트랜잭션 작업 기록을 읽기 전용으로 검사합니다. 안전하게 복구 가능한
상태이면 새 설치·업데이트를 차단하고 `이전 상태 복구`만 허용합니다. 복구 성공 뒤에는 설치
버튼만 다시 활성화하며 설치를 자동으로 시작하지 않습니다. 작업 기록 손상이나 상태 불일치로
안전성을 증명할 수 없으면 복구와 설치를 모두 차단합니다.

Rollback은 선행 복구가 확인된 단계만 계속 진행합니다. 서비스 중지가 확인되지 않으면 실행
파일을 바꾸지 않고, 프로그램 복원과 검증이 끝나지 않으면 이전 서비스를 다시 시작하지
않습니다. 각 방화벽 snapshot은 독립적으로 복원 결과를 남깁니다. 최초 설치·업데이트 실패
원인과 복구 단계별 실패 원인은 별도로 보존하고, 완전한 복구가 확인된 뒤에만 완료 기록과
증거 정리를 진행합니다. 작업 기록과 `Agent.__staging_*`, `Agent.__backup_*`,
`Agent.__failed_*` 폴더를 사용자가 삭제·이동·이름 변경해 이 검사를 우회해서는 안 됩니다.

v0.10 업데이트는 기존 DataDirectory를 유지하여 Agent ID와 HTTPS 신원을 보존합니다. 대상
관리망은 Setup에서 자동 선택하거나 직접 추가해 확정한 서로 다른 1~2개 망으로, Viewer 방화벽
경계는 현재 입력한 고정 IPv4 `/32`로 명시적으로 다시 적용합니다.

릴리스는 서명 인증서가 없는 `-poc` 배포물일 수 있습니다. SHA-256은 전송 중 변경을 확인할 수
있지만 게시자 신원을 증명하지 않습니다. 사내 반입 전에 조직의 백신·EDR·SmartScreen 정책에
맞는 승인과 검사를 받아야 합니다.

## 5. Viewer 방화벽 경계

Setup은 Windows Defender Firewall에 제품 소유 규칙을 만듭니다.

```text
Name:       SamsungSwitchWatchAgent-Https
Direction:  Inbound
Protocol:   TCP
LocalPort:  18443
Remote:     Setup에 입력한 Viewer 고정 IPv4/32
Profiles:   Domain, Private
```

Public 프로필은 허용하지 않습니다. Viewer IPv4는 CIDR 또는 대역이 아니라 정확한 IPv4 한 개로
입력하며 Setup이 `/32` 규칙을 만듭니다. Viewer 주소가 DHCP로 바뀌면 연결이 거부되므로 고정
주소 또는 조직에서 관리하는 예약 주소를 사용해야 합니다.

Windows 방화벽 COM API의 조회 표현은 생성 요청 문자열과 다를 수 있습니다. 같은 단일
호스트는 다음 세 형식만 동등하게 인정합니다.

```text
ViewerIPv4
ViewerIPv4/32
ViewerIPv4/255.255.255.255
```

이 호환 처리는 범위를 넓히지 않습니다. 주소가 다르거나 `/0`~`/31`, 여러 주소, 범위,
`Any`, `LocalSubnet`, IPv6이면 거부합니다. 원격 주소 외에도 Enabled, Inbound, Allow, TCP,
LocalPort 18443, Domain+Private만, Edge Traversal 비활성을 모두 만족해야 합니다.

적용 직후 Windows의 규칙 조회 반영이 늦을 수 있으므로 즉시 확인 후 200ms 간격으로 최대
2초까지만 다시 확인합니다. 계속 불일치하면 Setup은 `SETUP_FIREWALL_FAILED`와
`FIREWALL_REMOTE_ADDRESS_MISMATCH` 같은 안전한 필드별 코드를 표시하고 설치 전 snapshot으로
rollback합니다. 오류 메시지에는 Viewer IPv4, 방화벽 원문 또는 다른 규칙 주소를 넣지 않습니다.

Agent는 운영 설정의 `AllowedViewerIpv4`와 실제 TCP 연결의 원격 주소를 정확히 비교합니다.
일치하지 않거나 원격 주소를 확인할 수 없으면 모든 Agent API를
`403 / AGENT_CLIENT_NOT_ALLOWED`로 거부합니다. `X-Forwarded-For` 같은 전달 헤더는 신뢰하지
않습니다. 로컬 상태 점검은 `/health/live`와 `/health/ready`에만 허용합니다.

Agent와 Viewer가 같은 PC에 있어도 제품 API에는 `localhost`, `localhost.` 또는
`127.0.0.0/8`을 허용하지 않습니다. 동일 PC 사전 테스트는 현재 PC의 실제 RFC1918 사설 IPv4를
사용하며 제품 방화벽 `/32`와 `AllowedViewerIpv4` 검증을 그대로 통과해야 합니다. Setup의 로컬
설치 확인을 위한 loopback 허용 범위는 `/health/live`와 `/health/ready`에만 유지됩니다.

따라서 제품 소유 `/32` 규칙과 Agent 내부 검증이 함께 접근 경계를 구성합니다. Viewer PC
주소를 넓은 대역으로 허용하거나 규칙을 수동 확장하지 마십시오.

동일 PC 사전 테스트는 운영자가 Viewer에서 명시적으로 시작할 때만 동작하고, 활성 상태인
loopback·tunnel 이외 RFC1918 IPv4 후보를 최대 6개로 제한합니다. 후보당 최대 7초, 전체 최대
30초로 Agent 연결의 주소·TCP/18443·HTTPS·Agent API·버전만 확인합니다. 장비 자격 증명을
복호화하거나 스위치 접속·명령 실행을 하지 않으므로 이 테스트의 성공을 스위치 검증 또는 원격
Viewer 방화벽·라우팅 검증으로 해석하면 안 됩니다.

다른 프로그램이 만든 TCP/18443 인바운드 허용 규칙은 Setup이 소유하지 않으므로 삭제,
비활성화 또는 변경하지 않습니다. 해당 규칙을 발견하면
`FIREWALL_OVERLAP_PROTECTED` 경고를 표시하고, Agent 내부 Viewer IPv4 검증으로 API 접근을
계속 제한합니다. 다만 허용되지 않은 PC도 TLS 연결 시도 자체는 할 수 있으므로 불필요한 외부
규칙은 소유 부서에서 별도로 검토해야 합니다.

## 6. 스위치 대상 경계

Setup은 Agent PC에서 작동 중인 직접 연결 네트워크 어댑터의 RFC1918 사설 IPv4 주소와 마스크를
읽어 선택 후보를 만듭니다. 자동 검색 결과가 기본이며, 승인된 관리망이 목록에 없으면 운영자가
`IPv4/prefix` 형식으로 직접 추가할 수 있습니다. 호스트 주소를 입력해도 네트워크 주소로
정규화합니다. 예를 들어 `10.50.0.10/24`는 `10.50.0.0/24`가 됩니다.

직접 추가 값은 strict dotted IPv4와 prefix 형식이어야 하고, 정규화된 네트워크 전체가
RFC1918 범위 안에 있어야 합니다. 공인망, RFC1918 경계를 벗어나는 넓은 범위, 정규화 후 중복되는
범위와 자동 선택·직접 추가를 합해 세 번째가 되는 범위는 거부합니다. 최종 허용 목록은 서로
다른 canonical CIDR 1~2개입니다.

Agent는 매 요청에서 다음 조건을 모두 확인합니다.

- canonical dotted IPv4
- Setup에서 확정한 관리망 안에 포함
- TCP 포트 23
- loopback, link-local, multicast 또는 기타 특수 범위가 아님

Viewer UI 검증과 별개로 Agent가 다시 검증하므로 변조된 API 요청도 같은 정책을 통과해야 합니다.
이는 Agent 실행기의 필수 대상 allowlist이며, Windows 아웃바운드 방화벽 규칙은 아닙니다.

자동 검색에서 RFC1918 관리망을 찾지 못하면 PC의 네트워크 구성과 어댑터 상태를 먼저
확인합니다. 승인된 라우팅 관리망을 직접 추가할 때도 Agent PC에서 대상까지 실제 TCP/23 경로가
있는지 확인해야 합니다. 보안정책을 우회하기 위해 넓은 가상 어댑터, 임시 라우팅이나
승인받지 않은 사설망 범위를 추가하지 마십시오.

기존 운영 설정의 `AllowedTargetCidrs`가 서로 다른 canonical RFC1918 CIDR 1~2개이면 Setup이
이를 복원합니다. 대상 목록을 안전하게 복원할 수 없으면
`SETUP_EXISTING_NETWORKS_NOT_LOADED` 경고와 함께 아무 관리망도 미리 선택하지 않으며,
운영자가 다시 선택하거나 직접 추가해야 합니다. 이 경고만으로 영구 차단하지는 않지만, 전체
운영 설정 JSON이 손상된 경우에는 별도의 기존 배포 설정 검증이 설치를 차단할 수 있습니다.

## 7. 명령 정책

Viewer와 Agent는 다음 조건을 모두 만족하는 한 줄 `show` 명령만 실행합니다.

- 정규화 후 `show` 단어로 시작
- 128자 이하
- CR/LF와 제어문자 없음
- `;`, `&`, `|` 같은 명령 연결 문법 없음
- configure, interface, shutdown, reload, erase, write, copy 같은 설정 흐름으로 전환하지 않음

Viewer가 검증했더라도 Agent가 같은 정책을 다시 검증합니다. 자유 형식 명령 입력은 허용되지만
위 범위 밖 명령은 `QUERY_COMMAND_BLOCKED`로 거부합니다.

`show running-config`는 읽기 명령이라 정책상 허용되지만 민감도가 높습니다. 명령 문자열과 원문
출력은 Agent 로그·DB·진단 또는 Viewer 영구 저장소에 기록하지 않으며, 결과는 요청한 Viewer
메모리에서 최대 64 KiB만 유지합니다.

## 8. HTTPS 신원과 TOFU

Agent는 최초 정상 시작 때 ECDSA P-256 자체 서명 신원을 생성합니다. Viewer는 첫 연결에서 TLS
공개 키와 `/api/v4/identity`의 공개 신원을 자동으로 대조한 뒤 해당 Agent 주소에 TOFU 방식으로
고정합니다.

- 사용자가 SHA-256 지문을 입력하지 않습니다.
- 페어링 토큰을 만들거나 입력하지 않습니다.
- 저장된 신원과 달라지면 연결을 차단합니다.
- 토큰 또는 지문 입력으로 신원 불일치를 우회할 수 없습니다.

TOFU는 첫 연결 상대를 공인 CA나 AD로 인증하지 않습니다. 첫 연결의 안전성은 정확한 Viewer
`/32` 방화벽과 Agent의 동일 주소 검증, 격리된 관리망, Agent PC 주소 확인과 운영자 통제에
의존합니다.

## 9. 세션, 부하와 가용성

- 장비 한 대에 동시 Telnet 세션 한 개
- Agent 전체 동시 실행 기본 최대 두 개
- 요청 IP별 분당 기본 최대 60회
- 요청 본문 최대 32 KiB
- 요청당 명령 최대 8개
- 반환 출력 최대 64 KiB
- Telnet 세션 최대 240초
- 원격 종료 시 완료된 명령을 제외한 남은 명령만 최대 한 번 재시도
- 인증·enable 실패, 명령 시간 초과와 사용자 취소는 자동 재시도하지 않음

Viewer가 종료되면 주기 감시도 중단됩니다. Agent는 독립적으로 장비를 조회하지 않습니다. 이
감시 공백은 정상 동작이지만, 24시간 무중단 감시가 필요한 환경에는 현재 구조가 맞지 않습니다.

## 10. 로그와 진단

진단에 허용하는 정보:

- 제품 버전과 오류 코드
- 요청 ID
- 단계별 성공·실패와 소요 시간
- 서비스, HTTPS listener, 방화벽과 readiness 상태
- 출력 바이트 수와 잘림 여부
- Agent Setup 실패 시 UTC 시각, 작업 종류, 최초 실패와 rollback 단계 코드, 작업 기록
  형식·단계, 필요한 자료의 존재 여부와 서비스 상태

진단에 기록하지 않는 정보:

- 장비 IP와 호스트명
- 계정 ID, 로그인 PW와 enable PW
- 실행한 명령 문자열
- Telnet 원문과 `show running-config`
- 장비 MAC, 시리얼과 고객 식별정보
- Agent Setup의 실제 IP/CIDR, PC·사용자명, 절대 경로, 트랜잭션 ID, 서비스 계정,
  방화벽 규칙 원문, 인증서와 설치 명령

Agent Setup의 `진단정보 복사`는 실패 화면에서만 표시하고 진단 파일을 만들지 않으며,
위 허용 범위의 요약만 클립보드에 복사합니다. 대표 오류 코드는
`SETUP_ROLLBACK_FAILED`, `SETUP_EXISTING_NETWORKS_NOT_LOADED`, `TARGET_NOT_ALLOWED`,
`TCP_TIMEOUT`, `AUTH_FAILED`, `ENABLE_FAILED`,
`QUERY_COMMAND_BLOCKED`, `QUERY_RATE_LIMITED`, `COMMAND_TIMEOUT`,
`OUTPUT_LIMIT_EXCEEDED`, `PROMPT_PARSE_FAILED`, `AGENT_CONNECTION_REFUSED`,
`AGENT_VERSION_MISMATCH`, `LOCAL_PRIVATE_IPV4_NOT_FOUND`,
`LOCAL_AGENT_PREFLIGHT_TIMEOUT`입니다. 실패를 로그만 남기고 정상으로 표시하지 않습니다.

자동화·Mock 검증은 rollback 단계 순서, 오류 분리와 민감정보 제외 계약을 확인할 수 있지만,
Windows SCM, 방화벽 COM, 실제 ACL, EDR 파일 잠금과 전원 중단 조합을 모두 증명하지는
않습니다. 실제 배포 전 관리자 시험 PC 한 대에서 실패와 복구 흐름을 확인한 뒤 단계적으로
확대해야 합니다.

## 11. 알려진 POC 한계와 배포 금지 조건

- Agent와 스위치 사이 Telnet은 암호화되지 않아 ID, 비밀번호와 명령 결과가 평문으로 노출될 수
  있습니다.
- Agent API에는 Windows/AD 로그인이나 별도 애플리케이션 인증 토큰이 없습니다.
- 자체 서명 신원의 첫 연결은 TOFU이며 중앙 인증기관 검증이 아닙니다.
- Viewer 고정 IPv4 `/32` 방화벽이 훼손되면 API 접근 경계가 약화됩니다.
- 코드 서명 없는 `-poc` 실행 파일은 사내 보안 제품에 의해 차단될 수 있습니다.
- 실제 세 모델과 펌웨어별 프롬프트·페이징 처리는 현장 읽기 전용 검증이 필요합니다.

다음 조건에서는 배포하지 마십시오.

- Agent 또는 Telnet 구간이 일반 사용자망·공용망·인터넷을 통과함
- Viewer 고정 IPv4 한 개로 방화벽을 제한할 수 없음
- Telnet 평문 위험을 조직이 수용하지 않음
- 애플리케이션 사용자 인증이 필수인 환경
- 24시간 Viewer 비의존 감시가 필수인 환경
