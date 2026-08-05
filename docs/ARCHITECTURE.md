# Samsung Switch Watch v0.11 아키텍처

## 1. 목적과 구성 요소

Samsung Switch Watch는 스위치에 직접 접근할 수 있는 Agent PC와 운영자가 사용하는 Viewer PC를
분리합니다. Viewer가 장비 정보와 감시 업무를 소유하고, Agent는 요청받은 Telnet 조회를 실행하는
무상태(stateless) 중계기입니다.

```text
┌──────────────────────── Viewer PC ────────────────────────┐
│ SamsungSwitchWatch.Viewer.exe                             │
│ - 장비명·모델·IPv4                                       │
│ - ID·로그인 PW·enable PW: DPAPI CurrentUser              │
│ - 수동 한 줄 show 명령과 원문 결과                       │
│ - 주기 감시, 상태 비교, 변경 이력                         │
└────────────────────────────┬──────────────────────────────┘
                             │ HTTPS/TCP 18443
                             │ loopback 또는 RFC1918 Viewer만 허용
┌────────────────────────────▼──────────────────────────────┐
│ Agent PC                                                  │
│ SamsungSwitchWatchAgent Windows Service                   │
│ - 창 없이 자동 시작                                      │
│ - 요청 검증, Telnet 실행, 응답 반환                       │
│ - 장비·계정·명령·출력·감시 이력을 보관하지 않음           │
└────────────────────────────┬──────────────────────────────┘
                             │ Telnet/TCP 23
                             ▼
              IES4224GP / IES4028XP / IES4226XP
```

Agent는 독립 수집 서버가 아닙니다. Viewer가 종료되면 새 감시 요청도 발생하지 않습니다.
Agent는 요청마다 새 Telnet 세션을 만들고 성공, 실패 또는 취소 경로에서 세션을 정리합니다.

## 2. 배포와 설치 흐름

### Agent PC

1. Agent ZIP을 로컬 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행합니다.
3. Windows UAC를 한 번 승인합니다.
4. Viewer IP나 스위치 관리 CIDR을 입력하지 않고 `설치/업데이트`를 한 번 누릅니다.
5. Setup이 같은 작업의 내부 검사와 설치 또는 업데이트를 차례로 수행한 뒤 완료 또는 연결 확인 경고를
   표시합니다.

일반 운영 모드에는 별도의 `사전 점검` 버튼이 표시되지 않습니다. 읽기 전용 진단이 필요한
진단 전용 모드에서만 `사전 점검` 버튼을 표시하고 `설치/업데이트`는 비활성화합니다.

v0.11 Setup에는 Viewer 주소와 관리망 선택 입력란이 없습니다. Agent 런타임이 Viewer 요청은
loopback과 세 RFC1918 사설 대역으로, 스위치 대상은 세 RFC1918 사설 대역과 TCP/23으로 자동
제한합니다. 이전 설정의 `AllowedViewerIpv4`와 `AllowedTargetCidrs` 필드는 업데이트 호환성을
위해 읽거나 기록할 수 있지만 런타임 접근 권한으로 사용하지 않습니다.

설치기는 다음 작업을 수행합니다.

- Agent 패키지 매니페스트와 SHA-256 무결성 확인
- 보호된 staging을 이용한 설치 또는 업데이트
- `SamsungSwitchWatchAgent` 무창 Windows 서비스 설치와 자동 시작 구성
- `NT SERVICE\SamsungSwitchWatchAgent` 서비스 SID와 제한된 서비스 ACL 적용
- 세 RFC1918 원격 대역만 허용하는 Domain·Private HTTPS/18443 방화벽 규칙을 최선 노력 방식으로 적용
- Agent 미들웨어에서 loopback과 RFC1918 Viewer 출발지를 다시 검증
- 로컬 `/health/ready`를 제한 시간 안에서 확인
- 패키지·파일·서비스 구성 같은 설치 변경 실패에서만 이전 상태 rollback 시도
- 로컬 준비 상태 또는 방화벽 확인 실패는 Agent 설치를 유지하고 조치 가능한 경고로 표시

새 서비스 파일을 활성화한 뒤 서비스 설치와 시작까지 완료하면 파일·서비스 변경을 commit합니다.
그 다음 readiness 검사는 매 시도마다 SCM의 현재 서비스 PID와 그 프로세스의 TCP/18443 소유,
HTTPS 응답, API v4와 제품 버전을 확인합니다. 준비 상태를 확인하지 못해도 이미 설치된 Agent를
되돌리지 않고 `AGENT_LOCAL_CONNECTION_UNCONFIRMED` 경고를 표시합니다. 따라서 로컬 HTTPS
응답 문제 때문에 설치와 rollback을 반복하지 않습니다. 일반 운영자는 Viewer 연결 진단으로
서비스·TLS·API 경로를 확인하고, 지원 담당자가 진단 전용 실행을 안내한 경우에만 Setup의
읽기 전용 `사전 점검`을 사용합니다.

Setup 시작 시 설치 트랜잭션 작업 기록은 읽기 전용으로 먼저 검사합니다. 복구 가능한 미완료
기록이 있으면 새 설치·업데이트를 시작하지 않고 `설치/업데이트`를 비활성화하며, 운영자가
별도의 `이전 상태 복구`를 선택해야 합니다. 복구 성공 뒤 설치가 자동으로 이어지지는 않습니다.
Setup은 설치 버튼을 다시 활성화한 뒤 운영자가 `설치/업데이트`를 다시 한 번 눌러 내부 사전
점검부터 새 작업을 별도로 시작하도록 합니다. 작업 기록이 손상됐거나 현재 파일·서비스 상태와
맞지 않아 복구 안전성을 증명할 수 없으면 복구와 설치를 모두 차단하고 관리자 확인을 요청합니다.

Rollback은 서비스 중지, 프로그램·ACL 복원과 검증, 데이터 정리, 이전 서비스 상태 복원 순으로
의존성을 확인합니다. 서비스 중지는 SCM의 `STOPPED` 상태와 중지 과정에서 관찰한 서비스
프로세스의 실제 종료를 같은 제한 시간 안에서 모두 확인하며 프로세스를 강제 종료하지 않습니다.
프로그램 폴더 이동은 일시적 파일 잠금에 한해 최대 5회 제한적으로 재시도하고, 원본·대상 상태가
모호하면 즉시 중단합니다. 프로그램 복원이 완전하지 않으면 이전 서비스를 다시 시작하지 않습니다.
HTTPS 방화벽과 레거시 방화벽 snapshot은 서로 독립적으로 복원 결과를 기록합니다. 권위 있는
상태 복원이 끝난 뒤에만 rollback 완료 단계를 작업 기록에 쓰고, 그 다음에만 staging·backup·
failed 자료와 journal을 정리합니다. 정리는 작업 기록으로 검증된 각 대상만 최대 3회 시도하고
실패한 시도 사이에 250ms 대기하며, 삭제 뒤 대상 부재와 새 journal 부재를 다시 확인합니다.
복구가 실패하면 최초 설치·업데이트 실패 코드와 대상별 복구 단계 코드를 별도로 유지하면서
최종 호환 코드로 `SETUP_ROLLBACK_FAILED`를 표시합니다.

제품 소유 방화벽 규칙은 Enabled·Inbound·Allow·TCP·18443, Domain·Private 프로필,
Edge Traversal 비활성과 다음 세 원격 범위를 정확히 사용합니다.

```text
10.0.0.0/8
172.16.0.0/12
192.168.0.0/16
```

`Any`, `LocalSubnet`, Public 프로필과 IPv6는 제품 규칙으로 만들지 않습니다.

방화벽 적용 직후에는 즉시 검증하고, 운영체제 반영 지연을 위해 200ms 간격으로 최대 2초까지만
다시 읽습니다. 마지막 결과가 불일치하거나 방화벽·GPO 정책을 확인하지 못하면 안전한 필드별
코드와 `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고를 반환하며 실제 주소와 방화벽 원문은 사용자
오류에 포함하지 않습니다. 방화벽 변경분은 가능한 경우 기존 snapshot으로 되돌리고 결과를
경고에 남기되 Agent 서비스와 프로그램은 유지합니다. 로컬 준비 상태도 별도의
`AGENT_LOCAL_CONNECTION_UNCONFIRMED` 경고이므로 두 확인 실패가 설치 rollback을 유발하지 않습니다.

관리자 권한은 Setup 실행과 시스템 설정 변경에만 필요합니다. 설치 후 Agent는 일반 사용자의
데스크톱 세션과 분리된 서비스이므로 Agent 창이나 트레이 아이콘이 표시되지 않습니다. 로컬
관리자는 Windows 정책상 서비스를 제어할 수 있습니다.

Setup이 다른 프로그램 소유의 TCP/18443 인바운드 허용 규칙을 발견해도 이를 변경하지
않습니다. 같은 설치 작업의 내부 검사에 `FIREWALL_OVERLAP_PROTECTED` 경고를 남기고 설치를 계속하며, Agent의
사설 출발지 검증을 추가 경계로 사용합니다. 방화벽 비활성화, 기본 인바운드 허용, Public
프로필만 활성, 로컬 규칙 병합 차단 또는 제품 규칙 이름 충돌 때문에 정확한 제품 규칙을
확인할 수 없으면 `FIREWALL_REMOTE_ACCESS_UNCONFIRMED`로 설치를 완료합니다. Agent는 요청마다
loopback 또는 RFC1918 출발지인지 계속 재검증합니다. 원격 도달 가능성은 Viewer 연결 진단으로
최종 확인합니다.

실패 화면의 `진단정보 복사`는 지속 파일을 만들지 않고 클립보드에만 안전한 요약을
복사합니다. 요약에는 제품 버전, UTC 시각, 작업 종류, 최초 실패와 복구 단계 코드, 작업 기록
형식·단계, 필수 경로의 존재 여부와 서비스 상태만 포함합니다. 실제 IP/CIDR, PC·사용자명,
절대 경로, 트랜잭션 ID, 서비스 계정, 방화벽 규칙 내용, 자격 증명, 인증서, 명령과 장비 출력은
포함하지 않습니다.

진단 전용 사전 점검·설치·복구 작업이 성공 또는 실패로 끝나면 `익명 진단 저장`을 눌러
`SSW_FIELD_DIAGNOSTIC/2` UTF-8 BOM 텍스트를 수동으로 저장할 수 있습니다. 사진 한 장으로
전달할 수 있도록 최대 12줄, 줄당 88자로 제한하면서 제품과 Windows 버전, 작업·결과·실패
단계·안전한 오류/권장 조치 코드, 패키지·작업 기록·서비스·방화벽·TCP/18443·readiness 핵심
상태를 보존합니다. 자동 저장하지 않으며 IP/CIDR, PC·사용자명, 계정, 인증서 정보, 절대 경로,
방화벽 원문, 예외 원문, 명령과 장비 출력은 포함하지 않습니다. 기존 실패 화면의 클립보드 복사
기능과 과거 `/1` 재현 호환성은 그대로 유지합니다.

실패 결과에는 별도로 `SWD1-XXXX-XXXX-XXXX-XXXX` 형식의 짧은 지원 코드를 만듭니다.
Agent Setup 포매터가 이미 허용 목록으로 정규화한 제품 버전, 작업·오류 분류, 복구·서비스·
TCP/18443·readiness 하위 원인·패키지·방화벽의 제한된 상태를 72비트 payload로 만들고 CRC-8을 더해
Crockford Base32로 표시합니다. 코드는 읽기 전용으로 선택할 수 있으며 새 작업 시작, 입력 변경,
성공 상태에서는 숨기고 기존 값을 지웁니다.

### Viewer PC

Viewer ZIP은 설치 프로그램이 없는 포터블 배포물입니다.

1. Viewer ZIP을 항상 사용할 로컬 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Viewer.exe`를 실행합니다.
3. Agent PC의 IPv4 또는 사내 DNS 이름을 입력하고 연결을 확인합니다. 같은 PC에서는
   `localhost` 또는 `127.0.0.1`을 입력합니다.

Viewer에는 UAC, PowerShell, CMD, 자동 시작 등록과 `Program Files` 설치 단계가 없습니다. 공개
ZIP에는 PowerShell·CMD 설치 스크립트를 넣지 않으며, 저장소의 유지보수용 스크립트는 source-only
자료입니다.

## 3. 사용자 입력부터 결과까지

### Agent 연결

Viewer는 연결할 때 다음 단계를 순서대로 확인합니다.

```text
입력 형식
→ DNS 또는 IPv4 확인
→ TCP/18443
→ HTTPS 암호화 연결과 임시 자체 서명 인증서 자동 수락
→ Agent API v4와 제품 버전
```

인증서 SHA-256 지문이나 페어링 토큰을 사용자가 입력하는 화면은 없습니다. Viewer는 인증서
신원을 저장·비교하지 않습니다. API v4가 호환되면 Agent와 Viewer 제품 버전이 달라도 경고를
표시하고 연결하며, 주소·TCP·HTTPS·API 실패는 안정적인 오류 코드와 사용자용 설명으로
구분합니다.

연결 검사가 성공 또는 실패로 끝나면 `익명 진단 저장`을 눌러
`SSW_FIELD_DIAGNOSTIC/2` UTF-8 BOM 텍스트를 최대 12줄로 수동 저장할 수 있습니다. 일반
연결과 같은 PC 시험 여부, 주소·DNS·TCP·HTTPS·API/버전 단계별 상태와 제한된 소요 시간,
후보 수, 확인된 Agent/API 버전만 보존합니다. 입력한 주소, DNS 이름, IP/CIDR, PC·사용자명,
계정, 인증서 정보, 절대 경로, 예외 원문, 명령과 장비 출력은 기록하지 않습니다.

Viewer의 실패 지원 코드도 같은 SWD1 codec을 사용하되 Viewer 포매터가 일반/같은-PC 모드,
실패 단계, 단계별 상태, 제한된 후보 수와 확인된 Agent/API 버전만 전달합니다. 지원 코드는
네트워크 전송 없이 로컬에서 생성되며 성공 상태에서는 만들지 않습니다.

Agent와 Viewer가 같은 PC이면 Agent 주소에 `localhost` 또는 `127.0.0.1`을 입력해 같은 연결
검사를 수행할 수 있습니다. 이 성공은 로컬 서비스·TCP/18443·HTTPS와 API까지만 증명하며,
스위치 접속과 원격 Viewer 경로는 증명하지 않습니다. 실제 원격 배치에서는 원격 Viewer에서
Agent PC의 실제 IPv4 또는 사내 DNS 이름으로 연결 진단을 다시 수행합니다.

### 장비 로그인 확인

```text
Viewer가 장비·계정을 메모리에서 복호화
→ POST /api/v4/telnet/test
→ Agent가 대상 관리망과 TCP/23 검증
→ 로그인
→ 프롬프트가 > 이면 선택적으로 enable
→ 최종 권한 프롬프트 확인
→ exit/logout과 연결 정리
→ 성공 여부·최종 권한·소요 시간 또는 정제된 오류 코드 반환
```

이 단계는 조회 명령을 실행하지 않습니다. 실제 포트·로그 명령 지원 여부와 출력 종료 처리는
Viewer의 수집 진단에서 확인합니다.

### 명령 실행과 감시

```text
Viewer에서 한 줄 show 명령 선택 또는 입력
→ POST /api/v4/telnet/execute
→ Agent가 대상과 명령을 다시 검증
→ 로그인·enable·단일 조회 명령 실행
→ 30초 무응답·90초 전체 제한과 페이징 처리 후 최대 64 KiB 결과 반환
→ 세션 정리
→ Viewer가 원문 표시 또는 이전 상태와 비교
```

주요 명령은 `show port status`와 `show sylog tail num 100`입니다. 펌웨어에 따라
`show syslog tail num 100` 또는 다른 조회 명령이 필요할 수 있으므로, 특정 명령 실패를 장비 전체
장애로 바꾸지 않습니다.

자동 감시는 포트 상태와 시스템 로그를 순차적인 단일 명령 요청으로 분리합니다. 한 수집기가
명령 시간 초과·출력 한도로 실패해도 다른 수집기를 계속 실행하고 성공 결과를 보존합니다. 같은
실패 명령은 현재 수집 주기에서 즉시 재시도하지 않습니다. 인증·enable·TCP·세션 종료는 반복
로그인을 막기 위해 해당 장비의 현재 수집 주기를 중단합니다.

명령 실행 중 원격 종료가 발생하면 완료된 명령은 반복하지 않고 남은 명령만 새 세션에서 최대 한
번 재시도합니다. 인증·enable 실패, 명령 시간 초과와 사용자 취소는 자동 재시도하지 않습니다.

Viewer가 Agent 연결 설정을 바꿀 때는 새 Agent의 준비 상태와 API v4 호환을 먼저 확인하고 설정을
저장한 뒤 현재 클라이언트를 교체합니다. 교체가 완료된 뒤 이전 클라이언트 정리가 지연되거나
실패해도 이미 성공한 연결을 실패로 되돌리지 않으며, 정리 결과는 주소·예외 원문이 없는 안정적인
진단 코드로만 남깁니다. 새 연결 사전 검증이 실패한 경우에는 기존 설정과 기존 클라이언트를
유지하고 최초 실패 원인을 보존합니다.

주기 수집 화면은 현재 세션의 수집 결과와 과거 마지막 정상값을 구분합니다. 현재 수집 중에는
`Loading`, 장비별 동시 실행 제한이나 선행 작업 때문에 대기하면 `Deferred`, 이번 수집이 실패하면
확인 불가 상태를 표시합니다. 과거 정상값은 참고 정보로 남길 수 있지만 현재 정상으로 합산하지
않습니다. 따라서 Agent 전환 직후의 늦은 이전 응답이나 일부 장비 수집 실패가 전체 정상 수치에
섞이지 않습니다.

## 4. 소유권과 저장 위치

### Viewer가 소유하는 정보

- 장비명, 모델, IPv4와 표시 설정
- 현재 Windows 사용자 DPAPI로 암호화된 ID·로그인 PW·enable PW
- 장비별 감시 설정과 마지막 실행 시각
- 상태 기준선, 변경 이벤트와 Viewer 비실행 시간에 따른 감시 공백
- Agent 연결 주소와 API 호환 상태

수동 명령 문자열과 수동 원문 출력은 Viewer 프로세스 메모리에서만 사용하고 파일, 데이터베이스,
진단 로그 또는 내보내기에 보관하지 않습니다.

### Agent가 소유하는 정보

- Agent ID, HTTPS listener와 실행 한도
- RFC1918 Viewer·스위치 자동 접근 정책과 실행 한도
- 프로세스 시작 때 생성해 종료 시 폐기하는 임시 HTTPS 인증서
- 서비스 실행에 필요한 최소 설정과 정제된 진단

Agent는 장비 목록, 장비 자격 증명, 감시 일정, 상태 기준선 또는 명령 원문을 영구 저장하지
않습니다.

## 5. Agent 설정

Setup이 만드는 대표 운영 설정은 다음과 같습니다. 호환 필드는 과거 설정 형식을 유지하지만
v0.11 런타임은 고정된 RFC1918 정책으로 정규화합니다. 운영 설정 파일을 직접 편집해 접근 범위를
바꾸는 방식은 지원하지 않습니다.

```json
{
  "Agent": {
    "AgentId": "agent-REMOTE-PC",
    "ListenUrl": "https://0.0.0.0:18443",
    "DataDirectory": "C:\\ProgramData\\SamsungSwitchWatch",
    "MockMode": false,
    "AllowedViewerIpv4": "127.0.0.1",
    "AllowedTargetCidrs": [
      "10.0.0.0/8",
      "172.16.0.0/12",
      "192.168.0.0/16"
    ],
    "MaxConcurrentExecutions": 2,
    "RateLimitPerMinute": 60,
    "MaxRequestBodyBytes": 32768,
    "MaxCommandsPerRequest": 8,
    "MaxCommandLength": 128,
    "MaxOutputBytes": 65536,
    "Telnet": {
      "MaxSessionSeconds": 240,
      "ImmediateSessionCloseRetryCount": 1,
      "ImmediateSessionCloseRetryDelaySeconds": 2
    }
  }
}
```

`AllowedViewerIpv4`와 `AllowedTargetCidrs`는 이전 설정 파일과의 호환을 위해 남아 있습니다.
Agent가 시작할 때 단일 Viewer 값은 비우고 대상 목록은 세 RFC1918 대역으로 정규화합니다.
실제 Viewer 요청은 TCP 원격 주소가 loopback 또는 RFC1918인지 확인하며 전달 헤더는 사용하지
않습니다. Telnet 대상은 strict dotted RFC1918 IPv4와 고정 TCP/23만 허용하고 DNS 이름, IPv6,
loopback, link-local, multicast와 사설 범위 밖 주소를 거부합니다.

## 6. API v4

### 호환 정보와 상태

- `GET /api/v4/identity`
  - Agent ID, 제품 버전, API 버전, 호환용 HTTPS 공개 키 hash, 실행 한도
  - 공개 키 hash는 v4 응답 호환을 위해 남아 있으며 Viewer 신뢰 판단에는 사용하지 않음
  - 비밀번호와 대상 CIDR 전체 목록은 반환하지 않음
- `GET /health/live`
  - 프로세스 생존 상태
- `GET /health/ready`
  - HTTPS listener와 실행기 초기화 완료 상태
  - Setup의 로컬 설치 후 검증에 필요한 상태, API 버전, 프로토콜과 제품 버전을 반환
  - Setup은 loopback에서 이 응답을 16KiB 한도로 검증

### 로그인 확인

`POST /api/v4/telnet/test`

```json
{
  "requestId": "7df5b77d-a5fb-45db-bc93-96f719b04b36",
  "purpose": "test",
  "host": "10.40.0.10",
  "port": 23,
  "model": "IES4224GP",
  "username": "<memory-only>",
  "password": "<memory-only>",
  "enablePassword": null,
  "commands": []
}
```

### 명령 실행

`POST /api/v4/telnet/execute`

```json
{
  "requestId": "daaf99ea-c2aa-49e0-a296-88f5f818190f",
  "purpose": "manual",
  "host": "10.40.0.10",
  "port": 23,
  "model": "IES4224GP",
  "username": "<memory-only>",
  "password": "<memory-only>",
  "enablePassword": "<optional-memory-only>",
  "commands": [
    "show port status"
  ]
}
```

- `purpose`: `test`, `manual`, `monitor`
- `host`: canonical dotted IPv4
- `port`: 항상 23
- `model`: 지원 모델
- `commands`: execute 요청에서 1~8개

요청 본문은 최대 32 KiB입니다. 성공 응답은 최종 권한, 시작·완료 시각, 소요 시간,
`sessionCount`, `reconnectCount`, 명령별 출력과 잘림 여부를 포함합니다. 실패 응답은 비밀정보를
포함하지 않는 오류 코드와 설명을 반환합니다. `COMMAND_TIMEOUT`에는 하위 호환 가능한 선택적
`details`가 붙을 수 있으며, 안전한 단계, 제한된 소요 시간, 출력 수신 여부와 페이지 진행 횟수만
포함합니다. 대상 주소, 계정, 명령과 출력은 포함하지 않습니다.

## 7. 명령, 동시성 및 세션 정책

Agent는 다음 조건을 모두 만족하는 한 줄 조회 명령만 실행합니다.

- 정규화 후 `show`로 시작
- 줄바꿈 또는 제어문자 없음
- `;`, `&`, `|` 같은 명령 연결 문법 없음
- 128자 이하

Viewer와 Agent가 모두 같은 정책을 검증합니다. `show running-config`도 읽기 명령이라 허용되지만
매우 민감한 결과를 만들 수 있으며, 결과를 저장하거나 외부로 반출해서는 안 됩니다.

기본 실행 한도는 다음과 같습니다.

- 전체 동시 Telnet 실행: 최대 2건
- 장비 한 대: 동시 한 세션
- 요청 IP별 API 호출: 분당 최대 60회
- 요청당 명령: 최대 8개
- 명령당 반환 결과: 최대 64 KiB
- 명령 무응답 제한: 30초
- 명령 전체 제한: 90초
- 명령당 페이지 진행: 최대 32회
- 세션 수명: 최대 240초

한 장비의 인증 실패, 명령 미지원 또는 시간 초과가 다른 장비 작업을 중단시키지 않습니다.

## 8. HTTPS 전송 보호

Agent는 서비스 시작마다 RSA 2048 자체 서명 인증서를 새로 생성합니다. Windows Schannel과
Kestrel이 사용할 임시 UserKeySet 키 컨테이너는 Agent 프로세스 수명 동안만 유지하며, 호스트가
종료될 때 인증서를 폐기합니다. DataDirectory에 영구 인증서나 신원 파일을 만들지 않습니다.

Viewer는 이 임시 인증서를 자동 수락하며 인증서 지문, TOFU pin 또는 페어링 토큰을 저장·비교하지
않습니다. 따라서 HTTPS는 Viewer와 Agent 사이의 전송 내용을 암호화하지만 접속한 Agent의 신원을
인증하지 않습니다. 신뢰할 수 있는 사내 사설망과 정확한 Agent 주소에서만 사용합니다.

## 9. 버전과 업데이트

Agent와 Viewer는 같은 Release 버전을 권장합니다. API v4가 호환되면 제품 버전이 달라도 연결을
중단하지 않고 화면에 경고합니다. 기능 차이와 운영 혼동을 줄이려면 가능한 한 두 ZIP을 같은
Release 조합으로 교체합니다.

v0.11 Agent Setup은 기존 설치를 감지하여 Agent ID와 유효 실행 한도를 보존합니다. 이전
`AllowedViewerIpv4`, `AllowedTargetCidrs`와 인증서 신뢰 데이터는 파일 호환을 위해 남을 수 있지만
런타임 접근·TLS 신뢰 판단에는 사용하지 않습니다. v0.7 계열의 Agent 장비 목록, Agent 자격 증명 저장소, 자체 Poll
Scheduler, 이벤트 DB와 SignalR 수집 흐름은 v0.11 구조에 포함되지 않습니다.

미완료 설치 작업이 있으면 Setup은 이를 자동 복구하거나 새 설치와 함께 처리하지 않습니다.
읽기 전용 판정 뒤 복구 가능한 상태에서만 `이전 상태 복구`를 허용하고, 성공 뒤에도 설치는
운영자가 별도로 시작합니다. staging·backup·failed 폴더와 작업 기록은 안전성 판단 근거이므로
운영자가 수동으로 삭제·이동·이름 변경하지 않습니다.

## 10. 의도적으로 하지 않는 동작

- Agent PC에서 사용자 창이나 트레이 아이콘 표시
- Viewer 자동 시작 또는 시스템 전체 설치
- Agent가 Viewer 없이 독립적으로 장비 감시
- Viewer에서 설정 변경 명령 실행
- 공인 IPv4, IPv6 또는 TCP/23 이외의 스위치 대상 허용
- Setup에서 Viewer IP나 관리 CIDR을 직접 입력
- 지문 또는 페어링 토큰 수동 입력
- 지원 코드를 인증, 페어링 또는 접근 승인 값으로 사용
- 영구 Agent 인증서 신원 또는 TOFU pin 저장
- 동일 PC 연결 확인 중 스위치 조회
- 미완료 설치 작업의 자동 복구 또는 복구 성공 직후 자동 설치
- 설치·복구 근거인 staging·backup·failed 폴더와 작업 기록의 사용자 수동 정리
- 공개 ZIP에서 PowerShell·CMD 스크립트 실행
