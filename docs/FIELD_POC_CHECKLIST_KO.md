# Samsung Switch Watch v0.10 현장 POC 체크리스트

실제 IP, ID, 비밀번호, 호스트명, MAC, 시리얼과 원문 출력은 이 문서에 기록하지 않습니다.
결과는 `통과`, `실패`, `미검증`과 정제된 오류 코드만 기록합니다.

## 1. 반입 파일과 버전

- [ ] 동일 GitHub Release에서 Agent ZIP과 Viewer ZIP을 받음
- [ ] Agent와 Viewer 파일명이 같은 `0.10.x-poc` 버전을 표시함
- [ ] 두 ZIP의 SHA-256을 해당 GitHub Release 본문에 표시된 값과 비교함
- [ ] Agent ZIP에 `SamsungSwitchWatch.Agent.Setup.exe`와 Agent 런타임 파일이 있음
- [ ] Viewer ZIP에 `SamsungSwitchWatch.Viewer.exe`와 Viewer 런타임 파일이 있음
- [ ] 공개 ZIP에 `.ps1`, `.cmd`, 소스코드, 테스트 fixture와 불필요한 개발 파일이 없음
- [ ] 조직의 백신·EDR·SmartScreen 반입 검사를 완료함

Agent와 Viewer 버전은 달라도 되는 구성이 아닙니다. 반드시 같은 Release 조합을 사용합니다.

## 2. Agent Setup 사전 조건

- [ ] Agent PC가 스위치 관리망에 직접 연결되어 있거나 승인된 라우팅 경로가 있음
- [ ] Agent PC에서 대상 스위치 TCP/23 연결 정책이 허용됨
- [ ] Viewer PC가 고정 IPv4 또는 조직에서 관리하는 예약 IPv4를 사용함
- [ ] Viewer PC에서 Agent PC의 HTTPS/TCP 18443에 접근할 경로가 있음
- [ ] Agent PC에서 설치 시 사용할 관리자 계정 또는 UAC 승인 수단이 있음
- [ ] 실제 설정 변경 없이 읽기 전용 `show` 명령만 시험하기로 승인받음

## 3. Agent 설치와 무창 실행

- [ ] Agent ZIP을 로컬 폴더에 완전히 압축 해제함
- [ ] `SamsungSwitchWatch.Agent.Setup.exe` 실행 시 UAC를 한 번 승인함
- [ ] Setup에 Viewer PC의 고정 IPv4 한 개만 입력함
- [ ] 동일 PC 사전 테스트에서는 `이 PC 주소 넣기`가 실제 RFC1918 사설 IPv4만 제안함
- [ ] 후보가 여러 개이면 운영자가 사용할 인터페이스 주소를 직접 선택함
- [ ] 후보가 없을 때 주소를 추측하거나 loopback으로 계속하지 않고 안내가 표시됨
- [ ] Viewer 주소 입력란에는 CIDR, 서브넷 주소 또는 Viewer 대역을 입력하지 않음
- [ ] Setup이 직접 연결 RFC1918 사설 IPv4 관리망을 표시함
- [ ] 표시된 후보를 우선 사용하고, 목록에 없는 승인 관리망만 `IPv4/prefix`로 직접 추가함
- [ ] 자동 선택과 직접 추가를 합해 서로 다른 관리망 1~2개만 사용함
- [ ] 호스트 주소 입력이 canonical 네트워크 주소로 정규화됨
- [ ] 공인망, RFC1918 경계를 벗어난 범위와 정규화 후 중복되는 범위가 거부됨
- [ ] 세 번째 관리망 선택 또는 추가가 거부됨
- [ ] 공인망, 일반 사용자망 또는 관리와 무관한 어댑터를 선택하지 않음
- [ ] `검사` 결과에서 입력, 패키지와 설치 경로가 통과함
- [ ] 설치 또는 업데이트 결과가 `완료`임
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
- [ ] 원격 주소가 입력한 Viewer IPv4의 정확한 `/32`임
- [ ] 같은 Viewer IPv4의 `IP`, `IP/32`, `IP/255.255.255.255` 조회 표현만 동등하게 판정됨
- [ ] 다른 prefix, 주소 목록·범위, `Any`, `LocalSubnet`과 IPv6는 동등하게 판정되지 않음
- [ ] Domain·Private 프로필에만 규칙이 적용됨
- [ ] Public 프로필에는 제품 규칙이 적용되지 않음
- [ ] Enabled·Inbound·Allow·TCP·18443·Edge Traversal 비활성 조건 중 하나라도 다르면 실패함
- [ ] 등록한 Viewer PC에서 Agent HTTPS/18443 연결이 성공함
- [ ] 다른 시험 PC의 Agent API 요청이 `AGENT_CLIENT_NOT_ALLOWED`로 거부됨
- [ ] 다른 프로그램 소유 TCP/18443 허용 규칙이 있으면 Setup이
      `FIREWALL_OVERLAP_PROTECTED` 경고만 표시하고 해당 규칙을 변경하지 않음
- [ ] 다른 프로그램 소유 규칙이 있어도 등록하지 않은 시험 PC의 API 요청은 계속 거부됨
- [ ] 선택하거나 직접 추가한 스위치 관리망의 IPv4와 TCP/23 요청은 허용됨
- [ ] 확정한 관리망 밖의 시험 주소는 `TARGET_NOT_ALLOWED`로 거부됨
- [ ] DNS 이름, IPv6, loopback, link-local과 포트 23 이외 값은 거부됨

Viewer 주소가 바뀌면 Setup을 다시 실행하여 새 고정 IPv4를 입력하고 방화벽과 Agent 내부
허용 주소를 함께 갱신합니다. 방화벽 규칙을 넓은 CIDR로 수동 확장하지 않습니다.

## 5. ProgramData와 Agent 신원

민감한 파일 이름이나 내용을 수집하지 않고 ACL과 동작만 확인합니다.

- [ ] `%ProgramData%\SamsungSwitchWatch`가 일반 사용자에게 직접 열리지 않음
- [ ] SYSTEM과 Administrators가 FullControl을 가짐
- [ ] Agent 서비스 SID가 필요한 데이터 Modify 권한을 가짐
- [ ] Viewer 최초 연결에서 지문 또는 페어링 토큰 입력 화면이 나타나지 않음
- [ ] 최초 HTTPS 연결과 Agent identity 확인 후 자동 TOFU가 완료됨
- [ ] Viewer 재실행 후 같은 Agent에 계속 연결됨
- [ ] Agent 신원이 임의로 바뀐 시험에서는 Viewer가 연결을 차단함
- [ ] 신원 불일치를 지문이나 토큰 입력으로 우회할 수 없음

TOFU 첫 연결은 중앙 인증기관 검증이 아닙니다. 최초 연결 전에 Viewer 주소, Agent 주소와 `/32`
방화벽이 정확한지 확인합니다.

## 6. Viewer 포터블 실행

- [ ] 0.9 설치형 Viewer를 사용했다면 트레이 메뉴에서 기존 프로그램을 완전히 종료함
- [ ] `shell:startup`에서 기존 `Samsung Switch Watch` 자동 시작 바로 가기를 제거함
- [ ] `shell:programs`에서 같은 이름의 기존 시작 메뉴 바로 가기를 제거함
- [ ] 이전 Viewer가 실행 중일 때 새 Viewer가 동시 실행되지 않고 전환 안내를 표시함
- [ ] 창이 바로 보이지 않으면 트레이에서 대시보드를 열고 실행 경로가 새 v0.10 폴더인지 확인함
- [ ] Viewer ZIP을 사용할 로컬 폴더에 완전히 압축 해제함
- [ ] `SamsungSwitchWatch.Viewer.exe`를 더블클릭해 실행함
- [ ] Viewer 실행에 UAC가 나타나지 않음
- [ ] Viewer가 PowerShell 또는 CMD를 실행하지 않음
- [ ] Viewer가 `Program Files`에 자신을 설치하지 않음
- [ ] Viewer가 시작 메뉴·바탕 화면·자동 시작을 임의 등록하지 않음
- [ ] 같은 폴더에서 재실행 가능함
- [ ] Viewer 설정과 자격 증명은 현재 Windows 사용자 범위로 보존됨
- [ ] 다른 Windows 사용자로 Viewer 데이터를 복사해도 비밀번호가 복호화되지 않음

## 7. Viewer → Agent 연결 진단

- [ ] Viewer에서 실제 Agent PC의 IPv4만 입력함
- [ ] 원격 구성에서는 스위치 IP나 Viewer 자신의 IP를 Agent 주소로 입력하지 않음
- [ ] 동일 PC 구성에서도 `localhost`, `localhost.`와 `127.x.x.x`가 거부됨
- [ ] 입력 형식 단계가 통과함
- [ ] DNS·IPv4 단계가 통과함
- [ ] TCP/18443 단계가 통과함
- [ ] HTTPS·Agent 신원 단계가 통과함
- [ ] Agent·Viewer 버전 단계가 통과함
- [ ] 연결 성공 후 과거 `AGENT_CONNECTION_REFUSED` 경고가 화면에서 제거됨
- [ ] 버전을 다르게 한 시험은 `AGENT_VERSION_MISMATCH`로 중단됨
- [ ] 연결 거부 시 Setup의 `검사`로 서비스·listener·방화벽 상태를 구분할 수 있음
- [ ] `AGENT_CLIENT_NOT_ALLOWED`가 표시되면 Agent Setup에 현재 Viewer 고정 IPv4를 다시
      입력하라는 안내가 표시됨

### 동일 PC 사전 테스트

- [ ] Agent와 Viewer를 같은 PC에 설치한 경우에만 `이 PC에서 사전 테스트`를 직접 누름
- [ ] Viewer를 열거나 연결 창을 여는 것만으로 사전 테스트가 자동 실행되지 않음
- [ ] 활성 loopback·tunnel 이외 RFC1918 IPv4만 후보가 됨
- [ ] 후보가 최대 6개, 후보당 최대 7초, 전체 최대 30초로 제한됨
- [ ] 성공 결과에 Agent/API는 정상, 스위치와 원격 Viewer 경로는 미확인으로 표시됨
- [ ] 사전 테스트 중 장비 자격 증명 복호화, Telnet 접속 또는 show 명령 실행이 없음
- [ ] 저장 후 `장비 관리 → 접속 시험`에서 스위치를 별도로 검증함
- [ ] 실제 원격 배치 전 Agent Setup에 원격 Viewer 고정 IPv4를 다시 적용함
- [ ] 원격 Viewer PC에서 Agent 연결 진단을 다시 수행함

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
- [ ] enable PW가 없는 장비의 접속 시험이 성공함
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

- [ ] `exec-timeout 5 0` 장비에서 접속 시험이 성공함
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

- [ ] 기존 설치에서 v0.10 Setup이 업데이트를 수행함
- [ ] 업데이트 후 Agent ID가 유지됨
- [ ] 업데이트 후 HTTPS 신원이 유지되어 Viewer 재신뢰 입력이 없음
- [ ] 기존 유효 실행 한도 설정이 보존됨
- [ ] 현재 입력한 Viewer IPv4가 정확한 `/32` 방화벽 규칙으로 적용됨
- [ ] 방화벽 적용이 늦게 보이는 시험에서 Setup이 200ms 간격, 최대 2초 안에서만 재확인함
- [ ] 2초 안에도 규칙이 불일치하면 설치가 실패하고 이전 방화벽 snapshot이 복구됨
- [ ] 실패 Cause에는 실제 IP나 규칙 원문 없이 안전한 방화벽 불일치 코드만 표시됨
- [ ] 현재 입력한 Viewer IPv4가 Agent `AllowedViewerIpv4`에도 적용됨
- [ ] 현재 선택하거나 직접 추가한 서로 다른 관리망 1~2개가 대상 허용 목록으로 적용됨
- [ ] 기존 설정의 유효한 canonical RFC1918 관리망 1~2개가 Setup 목록에 복원됨
- [ ] 기존 대상 목록이 불완전하거나 중복되면 `SETUP_EXISTING_NETWORKS_NOT_LOADED`
      경고가 표시되고 아무 관리망도 미리 선택되지 않음
- [ ] 위 경고 후 운영자가 관리망을 다시 선택하거나 직접 추가하여 검사와 설치를 계속할 수 있음
- [ ] 전체 운영 설정 JSON 손상은 별도의 기존 배포 검증에서 차단됨
- [ ] 업데이트 후 Agent가 `/health/ready` 상태임
- [ ] 강제 readiness 실패 시험에서 이전 프로그램과 서비스가 rollback됨
- [ ] rollback 실패를 완료로 표시하지 않고 Setup 오류 코드로 표시함
- [ ] 미완료 작업 기록 감지 시 Setup이 상태를 읽기 전용으로 검사하고 설치 버튼을 비활성화함
- [ ] 복구 가능한 상태에서만 별도 `이전 상태 복구` 버튼이 활성화됨
- [ ] 작업 기록 손상 또는 현재 상태 불일치에서는 복구와 설치가 모두 차단되고 관리자 안내가 표시됨
- [ ] `이전 상태 복구` 성공 뒤 설치 버튼은 다시 활성화되지만 설치가 자동으로 시작되지 않음
- [ ] 복구 성공 뒤 운영자가 검사를 다시 확인하고 설치 또는 업데이트를 별도로 실행함
- [ ] 설치·업데이트 최초 실패 원인과 rollback 단계별 실패 원인이 서로 구분되어 표시됨
- [ ] `SETUP_ROLLBACK_FAILED`가 같은 결과 행에 중복 표시되지 않음
- [ ] 프로그램 복원이 불완전하면 이전 Agent 서비스를 다시 시작하지 않음
- [ ] HTTPS와 레거시 방화벽 snapshot 복원 결과가 서로 독립적으로 판정됨
- [ ] 완료 상태를 기록하기 전 staging·backup·failed 자료가 정리되지 않음
- [ ] 복구 대기·실패 중 `Agent.__staging_*`, `Agent.__backup_*`, `Agent.__failed_*` 폴더와
      작업 기록을 운영자가 삭제·이동·이름 변경하지 않음
- [ ] 업데이트 후 Agent와 Viewer가 같은 Release 버전임

## 13. 진단과 민감정보

- [ ] Agent Setup의 `검사`가 서비스·listener·방화벽·readiness 단계를 구분함
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
- [ ] `AGENT_CONNECTION_REFUSED`, `TCP_TIMEOUT`, `AUTH_FAILED`,
      `COMMAND_TIMEOUT`, `PROMPT_PARSE_FAILED`가 서로 구분됨

위 복구 검증은 실제 운영 Agent에서 바로 수행하지 않습니다. 관리자 시험 PC와 비식별 시험
패키지로 Windows 서비스, 방화벽, ACL과 파일 잠금 조건을 먼저 확인한 뒤, 운영 환경에서는
영향이 적은 Agent 한 대에 단계적으로 적용합니다. Mock·CI 통과만으로 사내 EDR과 Windows
서비스 복구가 검증됐다고 간주하지 않습니다.

## 14. POC 한계 승인

- [ ] Telnet 구간의 ID, 비밀번호와 결과가 평문이라는 위험을 승인함
- [ ] Agent API에 Windows/AD 로그인과 애플리케이션 토큰이 없음을 승인함
- [ ] Viewer `/32` 방화벽과 Agent 내부 고정 IPv4 검증이 현재 API 접근 경계임을 승인함
- [ ] TOFU 첫 연결이 중앙 인증기관 검증이 아님을 승인함
- [ ] 코드 서명 없는 `-poc` 배포물의 EDR·SmartScreen 위험을 승인함
- [ ] Viewer가 꺼지면 감시가 중단됨을 승인함
- [ ] 실제 모델·펌웨어 검증은 읽기 전용 명령으로만 수행함

모든 필수 항목이 통과하고 남은 `미검증`을 운영 책임자가 승인하기 전에는 `현장 검증 완료` 또는
`운영 안정화 완료`로 표시하지 않습니다.
