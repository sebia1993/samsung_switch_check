# Samsung Switch Watch v0.11 현장 POC 체크리스트

실제 IP, ID, 비밀번호, 호스트명, MAC, 시리얼과 원문 출력은 이 문서에 기록하지 않습니다.
결과는 `통과`, `실패`, `미검증`과 정제된 오류 코드만 기록합니다.

## 1. 반입 파일과 버전

- [ ] 동일 GitHub Release에서 Agent ZIP과 Viewer ZIP을 받음
- [ ] Agent와 Viewer 파일명이 같은 `0.11.4-poc` 버전을 표시함
- [ ] 두 ZIP의 SHA-256을 해당 GitHub Release 본문에 표시된 값과 비교함
- [ ] Agent ZIP에 `SamsungSwitchWatch.Agent.Setup.exe`와 Agent 런타임 파일이 있음
- [ ] Viewer ZIP에 `SamsungSwitchWatch.Viewer.Setup.exe`, `SamsungSwitchWatch.Viewer.exe`와 Viewer 런타임 파일이 있음
- [ ] 공개 ZIP에 `.ps1`, `.cmd`, 소스코드, 테스트 fixture와 불필요한 개발 파일이 없음
- [ ] 조직의 백신·EDR·SmartScreen 반입 검사를 완료함

API v4가 호환되면 Agent와 Viewer 버전이 달라도 경고 후 연결할 수 있습니다. 현장 검증과 운영은
기능 차이로 인한 혼동을 피하기 위해 같은 Release 조합을 사용합니다.

## 2. Agent Setup 사전 조건

- [ ] Agent PC가 스위치 관리망에 직접 연결되어 있거나 승인된 라우팅 경로가 있음
- [ ] Agent PC에서 대상 스위치 TCP/23 연결 정책이 허용됨
- [ ] Viewer PC에서 Agent PC의 HTTPS/TCP 18443에 접근할 경로가 있음
- [ ] Agent PC에서 설치 시 사용할 관리자 계정 또는 UAC 승인 수단이 있음
- [ ] 실제 설정 변경 없이 읽기 전용 `show` 명령만 시험하기로 승인받음

## 3. Agent 설치와 무창 실행

- [ ] Agent ZIP을 로컬 폴더에 완전히 압축 해제함
- [ ] `SamsungSwitchWatch.Agent.Setup.exe` 실행 시 UAC를 한 번 승인함
- [ ] Setup에 Viewer IP 또는 스위치 관리 CIDR 입력란이 없음
- [ ] 일반 운영 모드에는 별도 `사전 점검` 버튼이 표시되지 않고 `설치/업데이트`가 표시됨
- [ ] `설치/업데이트`를 한 번 누르면 별도 사전 점검을 중복하지 않고 하나의 설치 흐름이 이어짐
- [ ] 진단 전용 모드에는 읽기 전용 `사전 점검`만 표시되고 `설치/업데이트`는 비활성화됨
- [ ] 안내에 Viewer 요청은 loopback·RFC1918, 스위치 대상은 RFC1918·TCP/23으로 자동 제한됨이 표시됨
- [ ] 설치 진행 결과에서 입력, 패키지와 설치 경로의 내부 검사가 통과함
- [ ] 서비스 상태가 `SERVICE_NOT_INSTALLED`, `SERVICE_RUNNING` 또는 `SERVICE_STOPPED`로 표시됨
- [ ] 릴리스 자동화 검증에서 첫 서비스 조회만 일시 실패하는 시뮬레이션이 한 번 재시도 후 통과함
- [ ] 릴리스 자동화 검증에서 서비스 조회가 계속 실패하면 변경 전에 `SETUP_SERVICE_FAILED`로 중단됨
- [ ] 설치 또는 업데이트 결과가 `완료` 또는 조치 가능한 연결 확인 경고임
- [ ] `SamsungSwitchWatchAgent` 서비스가 자동 시작으로 등록됨
- [ ] 서비스가 `NT SERVICE\SamsungSwitchWatchAgent` 가상 계정으로 실행됨
- [ ] 사용자 바탕 화면·작업 표시줄·트레이에 Agent 창이 없음
- [ ] RDP를 종료해도 Agent 서비스가 계속 실행됨
- [ ] 다른 일반 사용자가 로그인해도 Agent 창이 나타나지 않음
- [ ] 일반 사용자가 서비스를 중지하거나 구성을 바꿀 수 없음
- [ ] 로컬 관리자는 Windows 정책에 따라 서비스를 관리할 수 있음을 이해함
- [ ] PC 재부팅 후 사용자 로그인 전 Agent 서비스가 자동 시작됨
- [ ] 강제 서비스 종료 후 5초·15초·60초 복구 정책을 확인함

## 4. Agent 네트워크 경계

- [ ] `SamsungSwitchWatchAgent-Https` 인바운드 규칙이 TCP/18443에 적용됨
- [ ] 원격 주소가 `10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`임
- [ ] `Any`, `LocalSubnet`, Public 프로필과 IPv6는 제품 규칙에 포함되지 않음
- [ ] Domain·Private 프로필에만 규칙이 적용됨
- [ ] Public 프로필에는 제품 규칙이 적용되지 않음
- [ ] Enabled·Inbound·Allow·TCP·18443·Edge Traversal 비활성 조건 중 하나라도 다르면 방화벽 확인 경고가 표시됨
- [ ] loopback 또는 RFC1918 Viewer에서 Agent HTTPS/18443 연결이 성공함
- [ ] 공인·link-local·IPv6 출발지 요청이 `AGENT_CLIENT_NOT_ALLOWED`로 거부됨
- [ ] 다른 프로그램 소유 TCP/18443 허용 규칙이 있으면 Setup이
      `FIREWALL_OVERLAP_PROTECTED` 경고만 표시하고 해당 규칙을 변경하지 않음
- [ ] 다른 프로그램 소유 규칙이 있어도 Agent의 loopback·RFC1918 출발지 검증이 유지됨
- [ ] 세 RFC1918 대역의 스위치 IPv4와 TCP/23 요청은 허용됨
- [ ] RFC1918 밖 시험 주소는 `TARGET_NOT_ALLOWED`로 거부됨
- [ ] DNS 이름, IPv6, loopback, link-local과 포트 23 이외 값은 거부됨

제품 방화벽 규칙은 설치 성공 조건이 아닌 최선 노력(best effort) 보호입니다. 적용·재확인 실패는
`FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고로 남고 Agent 설치는 유지되어야 합니다. 현장에서는
조직 방화벽·VLAN·ACL과 Viewer 연결 진단으로 실제 접근 범위를 확인합니다.

## 5. ProgramData와 임시 HTTPS 인증서

민감한 파일 이름이나 내용을 수집하지 않고 ACL과 동작만 확인합니다.

- [ ] `%ProgramData%\SamsungSwitchWatch`가 일반 사용자에게 직접 열리지 않음
- [ ] SYSTEM과 Administrators가 FullControl을 가짐
- [ ] Agent 서비스 SID가 필요한 데이터 Modify 권한을 가짐
- [ ] Viewer 연결에서 지문, 페어링 토큰 또는 TOFU 승인 화면이 나타나지 않음
- [ ] Agent 서비스가 시작할 때마다 새 RSA 자체 서명 인증서를 생성함
- [ ] Agent 종료 뒤 임시 Windows UserKeySet 키 컨테이너가 정리됨
- [ ] DataDirectory에 새 영구 인증서·개인 키·신원 파일이 생성되지 않음
- [ ] Agent 재시작으로 인증서가 바뀌어도 Viewer가 자동 수락하고 연결함

HTTPS는 전송 내용을 암호화하지만 Agent 신원을 인증하지 않습니다. 이 위험을 승인한 격리된 사내
사설망에서만 시험합니다.

## 6. Viewer 사용자 전용 설치

- [ ] Viewer ZIP을 로컬 임시 폴더에 완전히 압축 해제함
- [ ] v0.11.4 이후 Viewer 실행 중 업데이트에서 Setup이 안전 종료와 실제 종료 확인을 수행함
- [ ] v0.11.3 포터블 Viewer가 실행 중이면 Setup이 강제 종료하지 않고 수동 종료를 안내함
- [ ] `SamsungSwitchWatch.Viewer.Setup.exe` 실행에 UAC가 나타나지 않음
- [ ] Viewer Setup이 PowerShell, CMD 또는 인터넷을 사용하지 않음
- [ ] Viewer가 `%LOCALAPPDATA%\Programs\SamsungSwitchWatch\Viewer`에 설치됨
- [ ] 바탕 화면과 시작 메뉴 바로 가기가 고정 설치 경로를 가리킴
- [ ] 제품 소유의 이전 시작프로그램 바로 가기는 제거되고 새 자동 시작은 등록되지 않음
- [ ] 설치 완료 전에 smoke/실행 확인이 실패하면 기존 설치는 복구되고 최초 설치는 미설치 상태로 정리됨
- [ ] 설치 완료 전에 Viewer가 자동 실행되고 정상 실행 유지가 확인됨
- [ ] 설치 성공 후 압축 해제한 임시 폴더를 삭제해도 Viewer가 바로 가기로 실행됨
- [ ] 업데이트가 임의의 다운로드·압축 해제 폴더를 삭제하지 않음
- [ ] Viewer 설정과 자격 증명은 현재 Windows 사용자 범위로 보존됨
- [ ] 장비·연결·DPAPI·감시 데이터 파일은 보존되고 `Setup` 하위의 journal·증거 파일만 변경됨
- [ ] 정상 미완료 journal에서는 설치가 잠기고 복구 완료 후 설치를 별도로 눌러야 함
- [ ] 손상되거나 안전하지 않은 journal에서는 복구와 설치가 모두 차단됨
- [ ] 다른 Windows 사용자로 Viewer 데이터를 복사해도 비밀번호가 복호화되지 않음
- [ ] 인터넷과 Python/.NET 설치 없이 `익명 진단 저장` TXT를 생성하고 메모장에서 한글을 읽을 수 있음

## 7. Viewer → Agent 연결 진단

- [ ] Viewer에서 실제 Agent PC의 IPv4 또는 사내 DNS 이름만 입력함
- [ ] 원격 구성에서는 스위치 IP나 Viewer 자신의 IP를 Agent 주소로 입력하지 않음
- [ ] 동일 PC 구성에서 `localhost` 또는 `127.0.0.1` 연결이 성공함
- [ ] 입력 형식 단계가 통과함
- [ ] DNS·IPv4 단계가 통과함
- [ ] TCP/18443 단계가 통과함
- [ ] HTTPS 보호 단계에서 임시 자체 서명 인증서가 자동 수락됨
- [ ] Agent API v4와 버전 단계가 통과함
- [ ] 연결 성공 후 과거 `AGENT_CONNECTION_REFUSED` 경고가 화면에서 제거됨
- [ ] API v4 Agent와 Viewer 버전을 다르게 한 시험은 경고를 표시하고 연결됨
- [ ] 연결 거부 시 Viewer 연결 진단으로 주소·TCP·HTTPS·API 단계를 구분할 수 있음
- [ ] 지원 담당자가 안내한 경우 진단 전용 `사전 점검`으로 서비스·listener·방화벽 상태를
      구분할 수 있음
- [ ] `AGENT_CLIENT_NOT_ALLOWED`가 표시되면 Viewer 출발지가 loopback 또는 RFC1918인지
      확인하라는 안내가 표시됨
- [ ] 연결 성공과 실패 후 `익명 진단 저장`을 사용할 수 있고 파일이 자동 생성되지는 않음
- [ ] 진단 첫 줄이 `SSW_FIELD_DIAGNOSTIC/2`이고 전체가 최대 12줄·줄당 88자임
- [ ] 진단에 IP/CIDR·PC/사용자명·계정·경로·예외 원문·명령/출력이 없음
- [ ] 과거 `SSW_FIELD_DIAGNOSTIC/1` Fixture도 재현 도구에서 정상 분석됨
- [ ] 연결 실패 때만 `SWD1-XXXX-XXXX-XXXX-XXXX` 지원 코드가 표시됨
- [ ] 지원 코드를 선택해 `Ctrl+C`로 복사할 수 있고 별도 복사 버튼이 추가되지 않음
- [ ] 연결 성공, 새 연결 확인 또는 Agent 주소 변경 시 이전 지원 코드가 숨겨지고 지워짐
- [ ] SWD1 한 글자를 바꾼 값은 CRC 검사에서 거부되고 원래 값은 오프라인 해석됨

### 동일 PC 연결 확인

- [ ] Agent와 Viewer를 같은 PC에 설치한 경우 Agent 주소에 `localhost`를 입력함
- [ ] 성공 결과가 Agent 서비스·TCP/18443·HTTPS·API까지만 확인함
- [ ] 연결 확인 중 장비 자격 증명 복호화, Telnet 접속 또는 show 명령 실행이 없음
- [ ] 저장 후 `장비 관리 → 로그인 확인`에서 계정과 프롬프트를 검증함
- [ ] 수집 진단에서 `show port status`와 시스템 로그 명령을 각각 검증함
- [ ] 실제 원격 Viewer PC에서 Agent PC의 실제 주소로 연결 진단을 다시 수행함

동일 PC 성공은 Agent 서비스, TCP/18443, HTTPS, API와 버전까지만 증명합니다. 원격 PC 사이의
방화벽·라우팅이나 스위치 접속을 증명하지 않습니다.

## 8. Viewer 장비와 자격 증명

각 모델에서 아래 항목을 반복합니다.

| 모델 | 장비 등록 | 로그인 | enable | 결과 |
|---|---|---|---|---|
| IES4224GP | 미검증 | 미검증 | 미검증 | |
| IES4028XP | 미검증 | 미검증 | 미검증 | |
| IES4226XP | 미검증 | 미검증 | 미검증 | |

- [ ] Viewer에서 장비명, 모델, IPv4, ID와 로그인 PW를 입력함
- [ ] 필요한 장비에만 enable PW를 입력함
- [ ] enable PW가 없는 장비의 로그인 확인이 성공함
- [ ] enable PW가 필요한 장비에서 `>` → `enable` → `#`를 확인함
- [ ] 잘못된 ID 또는 PW가 `AUTH_FAILED`로 표시됨
- [ ] 잘못된 enable PW가 `ENABLE_FAILED`로 표시됨
- [ ] 편집 화면과 API 오류에 기존 비밀번호가 노출되지 않음
- [ ] Agent PC에 장비·계정 정보가 영구 저장되지 않음

## 9. 명령과 원문 출력

각 모델과 실제 펌웨어에서 지원 여부를 기록합니다.

| 명령 | IES4224GP | IES4028XP | IES4226XP |
|---|---|---|---|
| `show port status` | 미검증 | 미검증 | 미검증 |
| `show sylog tail num 100` | 미검증 | 미검증 | 미검증 |
| `show syslog tail num 100` | 미검증 | 미검증 | 미검증 |

- [ ] 지원 명령의 원문이 Viewer에 표시됨
- [ ] 미지원 명령은 장비 Down이 아니라 명령 미지원으로 구분됨
- [ ] 한 줄 `show running-config`가 정책상 실행 가능함
- [ ] 줄바꿈, `;`, `&`, `|`, configure, shutdown, reload 요청이 차단됨
- [ ] 128자를 넘는 명령이 차단됨
- [ ] 64 KiB를 넘는 출력에 잘림 상태가 표시됨
- [ ] 수동 명령과 원문 출력이 Agent 로그·DB·진단에 없음
- [ ] Viewer 재실행 후 이전 수동 원문이 복원되지 않음

`show running-config` 원문은 이 체크리스트, 화면 캡처, 메일 또는 이슈에 첨부하지 않습니다.

## 10. 세션 수명과 정리

- [ ] `exec-timeout 5 0` 장비에서 로그인 확인이 성공함
- [ ] IES4224GP 한 대에서 포트·로그 수집을 10회 연속 실행해 `COMMAND_TIMEOUT`이 재발하지 않음
- [ ] 한 수집 항목을 `COMMAND_TIMEOUT`으로 실패시켜도 다른 항목 결과가 유지되고 장비 전체 Down으로 표시되지 않음
- [ ] 명령 완료 후 Telnet 세션이 즉시 종료됨
- [ ] 명령 중 원격 종료 시 완료된 명령은 반복하지 않음
- [ ] 원격 종료 시 남은 명령만 새 세션에서 최대 한 번 재시도함
- [ ] 인증 또는 enable 실패를 자동 재시도하지 않음
- [ ] 명령 시간 초과를 자동 재시도하지 않음
- [ ] Viewer 취소 후 세션이 남지 않음
- [ ] 장비 한 대에서 중복 실행이 직렬화됨
- [ ] Agent 전체 동시 실행이 기본 최대 두 건임
- [ ] 한 세션이 240초를 넘지 않음
- [ ] 실패한 한 장비가 다른 장비 작업을 중단시키지 않음

## 11. 주기 감시와 변경 감지

- [ ] Viewer 실행 중 설정한 주기로 감시 요청이 발생함
- [ ] Viewer 종료 후 Agent가 독립적으로 스위치를 조회하지 않음
- [ ] Viewer 종료 시 불필요한 Telnet 세션이 남지 않음
- [ ] Viewer 재실행 시 감시 공백이 표시됨
- [ ] 공백 후 기존 로그 100개를 모두 신규 이벤트로 오인하지 않음
- [ ] 포트 `Up → Down` 변경이 장애로 표시됨
- [ ] 포트 `Down → Up` 변경이 복구로 표시됨
- [ ] 같은 상태의 반복 점검이 중복 이벤트를 만들지 않음
- [ ] 펌웨어별 syslog 명령 대체가 장비별로 동작함

Viewer가 종료되면 감시도 중단되는 구조가 현장 운영 요구와 맞는지 별도로 승인합니다.

## 12. 업데이트와 rollback

- [ ] 기존 설치에서 v0.11 Setup이 업데이트를 수행함
- [ ] 업데이트 후 Agent ID가 유지됨
- [ ] 업데이트 또는 서비스 재시작 뒤 새 임시 인증서가 생성되어도 Viewer 입력 없이 연결됨
- [ ] 기존 유효 실행 한도 설정이 보존됨
- [ ] 세 RFC1918 원격 대역이 제품 방화벽 규칙으로 적용됨
- [ ] 방화벽 적용이 늦게 보이는 시험에서 Setup이 200ms 간격, 최대 2초 안에서만 재확인함
- [ ] 2초 안에도 규칙이 불일치하면 경고가 표시되고 Agent 설치는 유지됨
- [ ] 실패 Cause에는 실제 IP나 규칙 원문 없이 안전한 방화벽 불일치 코드만 표시됨
- [ ] 기존 `AllowedViewerIpv4` 값이 실제 Viewer 허용 판단에 사용되지 않음
- [ ] 기존 `AllowedTargetCidrs` 값과 무관하게 세 RFC1918 대상 대역으로 정규화됨
- [ ] 전체 운영 설정 JSON 손상은 별도의 기존 배포 검증에서 차단됨
- [ ] 정상 업데이트 경로에서는 Agent가 `/health/ready` 상태임
- [ ] readiness 중 서비스 PID가 바뀌어도 현재 SCM PID가 TCP/18443을 소유하고 올바른
      응답을 주면 성공으로 판정됨
- [ ] 다른 프로세스가 TCP/18443을 점유하면 readiness 성공으로 오판하지 않음
- [ ] 강제 readiness 실패 시험에서 설치가 유지되고 `AGENT_LOCAL_CONNECTION_UNCONFIRMED`가 표시됨
- [ ] readiness 경고 뒤 Viewer 연결 진단으로 재확인할 수 있음
- [ ] 지원 담당자가 안내한 경우 진단 전용 `사전 점검`으로 Agent PC 내부 상태를 재확인할 수
      있음
- [ ] readiness 실패 진단이 서비스·TCP·HTTPS·payload·API·protocol·제품 버전 또는
      제한 시간 범주로 구분되고 실제 PID와 예외 원문은 포함하지 않음
- [ ] rollback 실패를 완료로 표시하지 않고 Setup 오류 코드로 표시함
- [ ] 미완료 작업 기록 감지 시 Setup이 상태를 읽기 전용으로 검사하고 설치 버튼을 비활성화함
- [ ] 복구 가능한 상태에서만 별도 `이전 상태 복구` 버튼이 활성화됨
- [ ] 작업 기록 손상 또는 현재 상태 불일치에서는 복구와 설치가 모두 차단되고 관리자 안내가 표시됨
- [ ] `이전 상태 복구` 성공 뒤 설치 버튼은 다시 활성화되지만 설치가 자동으로 시작되지 않음
- [ ] 복구 성공 뒤 운영자가 `설치/업데이트`를 한 번 눌러 같은 설치 작업의 내부 검사부터 새 작업을
      실행함
- [ ] staging·backup·failed·journal 정리는 정확한 검증 대상만 최대 3회 시도하고 실패한 시도 사이 250ms 대기함
- [ ] 삭제 API가 성공해도 대상이 남아 있으면 복구 성공으로 표시하지 않음
- [ ] 복구 호출 성공 뒤 새 작업 기록 검사에서 journal이 남아 있으면 설치 버튼이 계속 비활성화됨
- [ ] staging·backup·failed·journal 중 정리 실패 대상이 실제 경로 없이 안전 단계로 구분됨
- [ ] 대상별 단계가 `ROLLBACK_STAGING_CLEANUP_FAILED`, `ROLLBACK_BACKUP_CLEANUP_FAILED`,
      `ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`, `ROLLBACK_JOURNAL_CLEANUP_FAILED`로 구분됨
- [ ] 설치·업데이트 최초 실패 원인과 rollback 단계별 실패 원인이 서로 구분되어 표시됨
- [ ] `SETUP_ROLLBACK_FAILED`가 같은 결과 행에 중복 표시되지 않음
- [ ] 프로그램 복원이 불완전하면 이전 Agent 서비스를 다시 시작하지 않음
- [ ] 서비스 중지 뒤 관찰한 서비스 프로세스 종료가 확인되기 전에는 프로그램 폴더를 이동하지 않음
- [ ] 일시적 프로그램 폴더 잠금은 최대 5회의 제한 재시도로 복구됨
- [ ] 지속 잠금 또는 모호한 폴더 상태는 journal과 복구 자료를 보존한 실패로 남음
- [ ] HTTPS와 레거시 방화벽 snapshot 복원 결과가 서로 독립적으로 판정됨
- [ ] 완료 상태를 기록하기 전 staging·backup·failed 자료가 정리되지 않음
- [ ] 복구 대기·실패 중 `Agent.__staging_*`, `Agent.__backup_*`, `Agent.__failed_*` 폴더와
      작업 기록을 운영자가 삭제·이동·이름 변경하지 않음
- [ ] API v4 버전 불일치 경고가 연결을 중단하지 않음
- [ ] 실제 운영에는 Agent와 Viewer를 같은 Release 버전으로 맞춤

## 13. 진단과 민감정보

- [ ] 일반 운영 모드에는 `사전 점검` 버튼이 노출되지 않음
- [ ] 진단 전용 모드의 `사전 점검`이 서비스·listener·방화벽·readiness 단계를 구분함
- [ ] Viewer 연결 진단이 입력·DNS·TCP·HTTPS·버전 단계를 구분함
- [ ] 진단에 제품 버전, 단계, 소요 시간과 오류 코드가 있음
- [ ] 진단에 ID, PW, enable PW가 없음
- [ ] 진단에 장비 IP, 호스트명, MAC과 시리얼이 없음
- [ ] 진단에 명령 문자열과 원문 출력이 없음
- [ ] Agent Setup의 `진단정보 복사`는 실패 화면에서만 표시됨
- [ ] `진단정보 복사`가 파일을 만들지 않고 클립보드에만 안전한 요약을 복사함
- [ ] 복사 결과에 버전, UTC 시각, 작업 종류, 최초 실패·rollback 단계 코드, 작업 기록
      형식·단계, 필요한 자료의 존재 여부와 서비스 상태가 있음
- [ ] 복사 결과에 실제 IP/CIDR, PC·사용자명, 절대 경로, 트랜잭션 ID, 서비스 계정,
      방화벽 규칙 원문, 자격 증명, 인증서, 명령과 장비 출력이 없음
- [ ] Agent Setup 실패 때만 SWD1 지원 코드가 표시되고 성공·실행 중에는 표시되지 않음
- [ ] 새 진단 전용 사전 점검·설치·복구를 시작하면 이전 SWD1 지원 코드가 즉시 지워짐
- [ ] SWD1 코드가 계정, 인증 토큰, 페어링 토큰 또는 인증서 지문으로 안내되지 않음
- [ ] SWD1 코드와 `진단정보 복사`, `익명 진단 저장`의 역할이 화면·매뉴얼에서 구분됨
- [ ] `AGENT_CONNECTION_REFUSED`, `TCP_TIMEOUT`, `AUTH_FAILED`,
      `COMMAND_TIMEOUT`, `PROMPT_PARSE_FAILED`가 서로 구분됨

위 복구 검증은 실제 운영 Agent에서 바로 수행하지 않습니다. 관리자 시험 PC와 비식별 시험
패키지로 Windows 서비스, 방화벽, ACL과 파일 잠금 조건을 먼저 확인한 뒤, 운영 환경에서는
영향이 적은 Agent 한 대에 단계적으로 적용합니다. Mock·CI 통과만으로 사내 EDR과 Windows
서비스 복구가 검증됐다고 간주하지 않습니다.

## 14. POC 한계 승인

- [ ] Telnet 구간의 ID, 비밀번호와 결과가 평문이라는 위험을 승인함
- [ ] Agent API에 Windows/AD 로그인과 애플리케이션 토큰이 없음을 승인함
- [ ] 제품 방화벽 규칙이 세 RFC1918 대역을 허용하는 최선 노력 방식임을 승인함
- [ ] Agent가 loopback·RFC1918 출발지만 구분하고 특정 Viewer를 인증하지 않음을 승인함
- [ ] Viewer가 Agent 임시 자체 서명 인증서를 자동 수락하여 서버 신원을 인증하지 않음을 승인함
- [ ] 코드 서명 없는 `-poc` 배포물의 EDR·SmartScreen 위험을 승인함
- [ ] Viewer가 꺼지면 감시가 중단됨을 승인함
- [ ] 실제 모델·펌웨어 검증은 읽기 전용 명령으로만 수행함

모든 필수 항목이 통과하고 남은 `미검증`을 운영 책임자가 승인하기 전에는 `현장 검증 완료` 또는
`운영 안정화 완료`로 표시하지 않습니다.
