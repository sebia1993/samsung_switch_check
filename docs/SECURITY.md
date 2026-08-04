# Samsung Switch Watch v0.11 보안 설계

## 1. 신뢰 경계

| 경계 | 보호 방식 | 남는 위험 |
|---|---|---|
| Viewer 로컬 저장소 | DPAPI CurrentUser | 같은 Windows 사용자 세션 또는 계정 탈취 |
| Viewer → Agent | HTTPS, loopback·RFC1918 출발지 검증, RFC1918 원격 대역의 Domain·Private 방화벽 규칙 적용 시도 | Agent 인증과 애플리케이션 사용자 인증이 없으며, 도달 가능한 사설망 클라이언트가 API에 접근할 수 있음 |
| Agent → 스위치 | RFC1918 대상 제한, TCP/23 고정, 한 줄 `show` 정책 | Telnet 평문 노출 |
| Agent 서비스와 데이터 | 전용 서비스 SID, 제한된 서비스·폴더 ACL, 무창 서비스 | 로컬 관리자는 제어 가능 |

HTTPS는 전송 내용을 암호화하지만 Agent 신원을 인증하지 않습니다. Agent는 loopback 또는
RFC1918 출발지인지 확인할 뿐 Viewer 한 대를 식별하거나 로그인시키지 않습니다. Agent PC와
Viewer PC를 일반 사용자 VLAN, 공용 Wi-Fi 또는 인터넷에 노출하지 마십시오.

`FIREWALL_REMOTE_ACCESS_UNCONFIRMED`는 제품 방화벽 규칙을 적용하거나 재확인하지 못했다는
경고이고, `AGENT_LOCAL_CONNECTION_UNCONFIRMED`는 설치 뒤 로컬 HTTPS/API 준비 상태를 확인하지
못했다는 경고입니다. 두 경우 모두 Agent 파일과 서비스는 설치된 상태로 유지됩니다. Viewer 연결
시험과 조직의 방화벽·GPO 정책으로 실제 경로를 확인하기 전에는 운영 준비 완료로 간주하지
마십시오. Agent의 loopback·RFC1918 출발지 검증은 경고 상태에서도 유지됩니다.

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

Agent HTTPS 인증서는 서비스 시작마다 새로 생성합니다. Windows Schannel용 임시 UserKeySet 키
컨테이너는 Agent 프로세스 수명 동안만 유지하고 종료 시 인증서와 함께 폐기합니다. DataDirectory에
영구 인증서, 개인 키 또는 Agent 신원 파일을 만들지 않습니다.

업데이트 중 파일과 서비스 구성을 바꾸는 동안에는 새 서비스의 자동 재시작 정책을 일시적으로
비활성화합니다. 서비스 설치와 시작이 완료되어 변경을 commit할 때 정상 복구 정책을 다시
적용합니다. 그 뒤의 readiness 확인은 현재 SCM 서비스 PID와 그 PID의 TCP/18443 소유를 매
시도 확인하는 별도 진단 단계이며, 실패해도 이미 완료한 설치를 rollback하지 않습니다.

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
- 고정된 RFC1918 Viewer·스위치 정책을 포함한 운영 설정 형식

검증한 파일은 보호된 staging에 복사한 뒤 설치 폴더와 교체합니다. 패키지·파일·서비스 구성 같은
설치 변경이 실패하면 기존 프로그램과 서비스 상태의 rollback을 시도합니다. 서비스 설치와 시작이
완료된 뒤의 로컬 HTTPS/API/버전 readiness 실패는 `AGENT_LOCAL_CONNECTION_UNCONFIRMED`,
방화벽·GPO·규칙 적용/재조회 실패는 `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고로 분리합니다.
이 두 확인 실패 때문에 작동 가능한 설치를 되돌리지 않습니다.
rollback이 완전히 끝나지 않으면 성공으로 처리하지 않고 안정적인 Setup 오류 코드로 관리자
확인을 요청합니다.

Setup은 시작 시 미완료 트랜잭션 작업 기록을 읽기 전용으로 검사합니다. 안전하게 복구 가능한
상태이면 새 설치·업데이트를 차단하고 `이전 상태 복구`만 허용합니다. 복구 성공 뒤에는 설치
버튼만 다시 활성화하며 설치를 자동으로 시작하지 않습니다. 작업 기록 손상이나 상태 불일치로
안전성을 증명할 수 없으면 복구와 설치를 모두 차단합니다.

Rollback은 선행 복구가 확인된 단계만 계속 진행합니다. SCM 중지 상태와 관찰한 서비스
프로세스의 실제 종료가 모두 확인되지 않으면 실행 파일을 바꾸지 않습니다. 폴더 이동은 일시적
파일 잠금만 최대 5회 제한적으로 재시도하며 모호한 원본·대상 상태에서는 실패로 보존합니다.
프로그램 복원과 검증이 끝나지 않으면 이전 서비스를 다시 시작하지 않습니다. 각 방화벽
snapshot은 독립적으로 복원 결과를 남깁니다. 최초 설치·업데이트 실패
원인과 복구 단계별 실패 원인은 별도로 보존하고, 완전한 복구가 확인된 뒤에만 완료 기록과
증거 정리를 진행합니다. 작업 기록과 `Agent.__staging_*`, `Agent.__backup_*`,
`Agent.__failed_*` 폴더를 사용자가 삭제·이동·이름 변경해 이 검사를 우회해서는 안 됩니다.

v0.11 업데이트는 기존 DataDirectory와 호환 설정을 유지할 수 있지만, 과거 Viewer IP·대상 CIDR과
인증서 신뢰 값은 접근 권한 또는 TLS 신뢰 판단에 사용하지 않습니다. 새 Agent는 시작할 때마다
임시 HTTPS 인증서를 만들고 고정된 RFC1918 정책을 적용합니다.

릴리스는 서명 인증서가 없는 `-poc` 배포물일 수 있습니다. SHA-256은 전송 중 변경을 확인할 수
있지만 게시자 신원을 증명하지 않습니다. 사내 반입 전에 조직의 백신·EDR·SmartScreen 정책에
맞는 승인과 검사를 받아야 합니다.

## 5. Viewer 네트워크 경계

Setup은 Windows Defender Firewall에 제품 소유 규칙을 만듭니다.

```text
Name:       SamsungSwitchWatchAgent-Https
Direction:  Inbound
Protocol:   TCP
LocalPort:  18443
Remote:     세 RFC1918 사설 IPv4 대역
Profiles:   Domain, Private
```

위 `Remote` 값은 실제로 다음 세 범위를 한 규칙에 사용합니다.

```text
10.0.0.0/8,172.16.0.0/12,192.168.0.0/16
```

Setup에는 Viewer IP 또는 CIDR 입력란이 없습니다. 제품 규칙은 Enabled·Inbound·Allow·TCP,
LocalPort 18443, Domain·Private 프로필과 Edge Traversal 비활성 조건을 사용합니다. `Any`,
`LocalSubnet`, Public 프로필과 IPv6는 제품 규칙으로 만들지 않습니다.

적용 직후 Windows의 규칙 조회 반영이 늦을 수 있으므로 즉시 확인 후 200ms 간격으로 최대
2초까지만 다시 확인합니다. 계속 불일치하거나 GPO 때문에 로컬 규칙을 확인할 수 없으면 Setup은
`FIREWALL_REMOTE_ACCESS_UNCONFIRMED`와 `FIREWALL_REMOTE_ADDRESS_MISMATCH` 같은 안전한 필드별
코드를 표시하고 방화벽 변경분을 설치 전 snapshot으로 되돌리려고 시도합니다. 복원 확인 여부는
경고 단계에 남기며 Agent 프로그램과 서비스는 유지합니다. 오류 메시지에는 실제 주소,
방화벽 원문 또는 다른 규칙 주소를 넣지 않습니다.

Agent는 실제 TCP 연결의 원격 주소가 loopback 또는 RFC1918인지 매 요청 확인합니다. 그 밖의
주소이거나 원격 주소를 확인할 수 없으면 모든 Agent API를
`403 / AGENT_CLIENT_NOT_ALLOWED`로 거부합니다. `X-Forwarded-For` 같은 전달 헤더는 신뢰하지
않습니다. 같은 PC에서는 `localhost` 또는 `127.0.0.1`로 API 연결을 확인할 수 있습니다.

이 정책은 사설 대역의 특정 Viewer 한 대를 인증하지 않습니다. 같은 사설망에서 Agent PC의
TCP/18443에 도달할 수 있는 다른 클라이언트도 API 요청을 시도할 수 있으므로 조직 방화벽, VLAN,
ACL과 PC 접근 통제가 실제 경계입니다.

다른 프로그램이 만든 TCP/18443 인바운드 허용 규칙은 Setup이 소유하지 않으므로 삭제,
비활성화 또는 변경하지 않습니다. 해당 규칙을 발견하면
`FIREWALL_OVERLAP_PROTECTED` 경고를 표시하고, Agent 내부 사설 출발지 검증을 계속 적용합니다.
불필요하거나 더 넓은 외부 규칙은 해당 소유 부서에서 별도로 검토해야 합니다.

## 6. 스위치 대상 경계

Agent는 매 요청에서 다음 조건을 모두 확인합니다.

- canonical dotted IPv4
- 10/8, 172.16/12 또는 192.168/16 안에 포함
- TCP 포트 23
- loopback, link-local, multicast 또는 기타 특수 범위가 아님

Viewer UI 검증과 별개로 Agent가 다시 검증하므로 변조된 API 요청도 같은 정책을 통과해야 합니다.
이는 Agent 실행기의 필수 대상 allowlist이며, Windows 아웃바운드 방화벽 규칙은 아닙니다.

Setup에서 예외 CIDR을 추가하는 기능은 없습니다. Agent PC에서 대상까지 실제 TCP/23 경로와
조직 승인이 있는지는 별도로 확인해야 합니다. 기존 `AllowedTargetCidrs` 값은 호환성을 위해 파일에
남을 수 있지만 Agent 시작 시 세 RFC1918 대역으로 정규화됩니다.

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

## 8. HTTPS 전송 보호와 신원 한계

Agent는 서비스 시작마다 RSA 2048 자체 서명 인증서를 만들고 프로세스 종료 때 폐기합니다.
Viewer는 인증서를 자동 수락하며 인증서 지문, TOFU pin 또는 페어링 토큰을 저장·비교하지
않습니다. API v4의 기존 신원 hash 필드는 wire 호환을 위해 남을 수 있지만 Viewer 신뢰 판단에
사용하지 않습니다.

이 방식은 사용자 입력과 인증서 수명 문제를 줄이고 전송 내용을 암호화하지만 Agent 신원을
인증하지 않습니다. DNS 또는 라우팅이 변조된 환경에서는 다른 HTTPS 종단을 Agent로 오인할 수
있습니다. 격리된 사내 사설망, 정확한 Agent 주소, 조직 방화벽·VLAN·ACL과 운영자 PC 통제를
전제로 사용합니다.

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
- 수동 저장한 최대 12줄·줄당 88자의 `SSW_FIELD_DIAGNOSTIC/2` 컴포넌트, Windows
  빌드·아키텍처, 작업 결과, 실패 단계, 권장 조치 코드와 압축된 핵심 상태
- 실패 화면의 `SWD1-XXXX-XXXX-XXXX-XXXX` 지원 코드에 매핑된 제품 버전, 컴포넌트,
  작업·오류·단계, readiness 하위 원인과 제한된 상태 분류

진단에 기록하지 않는 정보:

- 장비 IP와 호스트명
- 계정 ID, 로그인 PW와 enable PW
- 실행한 명령 문자열
- Telnet 원문과 `show running-config`
- 장비 MAC, 시리얼과 고객 식별정보
- Agent Setup의 실제 IP/CIDR, PC·사용자명, 절대 경로, 트랜잭션 ID, 서비스 계정,
  서비스 PID, 방화벽 규칙 원문, 인증서와 설치 명령
- Viewer에 입력한 Agent 주소와 DNS 이름, 연결 후보 주소, 예외 원문과 로컬 저장 경로

Agent Setup의 `진단정보 복사`는 실패 화면에서만 표시하고 진단 파일을 만들지 않으며,
위 허용 범위의 요약만 클립보드에 복사합니다. 대표 오류 코드는
`SETUP_ROLLBACK_FAILED`, `ROLLBACK_EVIDENCE_CLEANUP_FAILED`,
`ROLLBACK_STAGING_CLEANUP_FAILED`, `ROLLBACK_BACKUP_CLEANUP_FAILED`,
`ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`, `ROLLBACK_JOURNAL_CLEANUP_FAILED`,
`SETUP_EXISTING_NETWORKS_NOT_LOADED`, `TARGET_NOT_ALLOWED`,
`TCP_TIMEOUT`, `AUTH_FAILED`, `ENABLE_FAILED`,
`QUERY_COMMAND_BLOCKED`, `QUERY_RATE_LIMITED`, `COMMAND_TIMEOUT`,
`OUTPUT_LIMIT_EXCEEDED`, `PROMPT_PARSE_FAILED`, `AGENT_CONNECTION_REFUSED`입니다.
`AGENT_VERSION_MISMATCH`는 API v4 호환 연결에서 차단 오류가 아니라 사용자에게 표시하는 호환
경고입니다. 실제 실패를 로그만 남기고 정상으로 표시하지 않습니다.

Agent Setup 작업과 Viewer 연결 검사가 끝난 뒤에는 운영자가 명시적으로
`익명 진단 저장`을 누른 경우에만 UTF-8 BOM 텍스트를 저장합니다. 자동 저장하거나 기존 로그를
원문으로 복제하지 않습니다. 저장 전 formatter가 고정된 필드·값 목록만 허용하고, 저장 실패는
`DIAGNOSTIC_WRITE_FAILED`로 표시합니다. Viewer 장비 명령 원문과 출력은 이 진단에 절대
포함하지 않습니다.

SWD1 지원 코드는 실패 화면에서만 로컬 생성하며 파일 저장이나 Agent↔Viewer 전송을 자동으로
수행하지 않습니다. IP/CIDR, PC·사용자명, 자격 증명, 인증서, 경로, 예외 원문, 스위치 명령과
출력은 codec 입력에 포함하지 않습니다. 끝의 CRC-8은 전화·메신저 전달 과정의 오타를 찾기 위한
무결성 검사일 뿐 암호화, 서명 또는 신원 인증이 아닙니다. 따라서 코드는 비밀값, 로그인 토큰,
페어링 토큰, 인증서 지문이나 접근 승인 값으로 취급하지 않습니다.

자동화·Mock 검증은 rollback 단계 순서, 오류 분리와 민감정보 제외 계약을 확인할 수 있지만,
Windows SCM, 방화벽 COM, 실제 ACL, EDR 파일 잠금과 전원 중단 조합을 모두 증명하지는
않습니다. 실제 배포 전 관리자 시험 PC 한 대에서 실패와 복구 흐름을 확인한 뒤 단계적으로
확대해야 합니다.

## 11. 알려진 POC 한계와 배포 금지 조건

- Agent와 스위치 사이 Telnet은 암호화되지 않아 ID, 비밀번호와 명령 결과가 평문으로 노출될 수
  있습니다.
- Agent API에는 Windows/AD 로그인이나 별도 애플리케이션 인증 토큰이 없습니다.
- Viewer는 Agent 자체 서명 인증서를 자동 수락하므로 서버 신원을 인증하지 않습니다.
- Agent API는 loopback과 RFC1918 출발지만 구분하며 특정 Viewer 사용자나 PC를 인증하지 않습니다.
- 제품 방화벽 규칙은 최선 노력(best effort) 방식입니다. `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 상태에서는 조직
  방화벽 정책과 실제 Viewer 연결 경로를 별도로 확인해야 합니다.
- 코드 서명 없는 `-poc` 실행 파일은 사내 보안 제품에 의해 차단될 수 있습니다.
- 실제 세 모델과 펌웨어별 프롬프트·페이징 처리는 현장 읽기 전용 검증이 필요합니다.

다음 조건에서는 배포하지 마십시오.

- Agent 또는 Telnet 구간이 일반 사용자망·공용망·인터넷을 통과함
- Agent PC의 TCP/18443을 신뢰할 수 있는 사설 관리 경로로 제한할 수 없음
- Agent 서버 신원 인증 또는 Viewer 사용자 인증이 필수인 환경
- Telnet 평문 위험을 조직이 수용하지 않음
- 애플리케이션 사용자 인증이 필수인 환경
- 24시간 Viewer 비의존 감시가 필수인 환경
