# Samsung Switch Watch v0.10 아키텍처

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
                             │ Viewer 고정 IPv4 한 개만 허용
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
4. 원격 Viewer PC의 고정 IPv4 한 개를 입력합니다. 동일 PC 사전 테스트에서는
   `이 PC 주소 넣기`로 Agent PC의 실제 RFC1918 사설 IPv4를 선택합니다.
5. Setup이 찾은 RFC1918 사설 IPv4 관리망을 선택하고, 목록에 없는 승인 관리망은
   `IPv4/prefix`로 직접 추가합니다. 자동 선택과 직접 추가의 합계는 1~2개입니다.
6. `검사`로 사전 점검한 뒤 설치 또는 업데이트합니다.

Setup은 작동 중인 로컬 네트워크 어댑터의 IPv4 주소와 마스크를 읽어 관리망 후보를 자동
계산하며, loopback·tunnel·공인 주소는 후보에서 제외합니다. 자동 검색이 기본 경로이지만,
승인된 라우팅 관리망이 목록에 없으면 운영자가 직접 추가할 수 있습니다. 입력한 호스트 주소는
canonical 네트워크 주소로 정규화하며, 정규화된 전체 범위가 RFC1918 안에 있는지 확인합니다.
자동 선택과 직접 추가에서 중복을 제거한 1~2개 망만 Agent의 Telnet 대상 허용 목록이 됩니다.

설치기는 다음 작업을 수행합니다.

- Agent 패키지 매니페스트와 SHA-256 무결성 확인
- 보호된 staging을 이용한 설치 또는 업데이트
- `SamsungSwitchWatchAgent` 무창 Windows 서비스 설치와 자동 시작 구성
- `NT SERVICE\SamsungSwitchWatchAgent` 서비스 SID와 제한된 서비스 ACL 적용
- Viewer 고정 IPv4를 정확한 `/32`로 허용하는 HTTPS/18443 방화벽 규칙 적용
- 같은 Viewer IPv4를 Agent 설정에 저장하고 모든 API 요청에서 재검증
- Agent HTTPS 신원과 기존 유효 설정 보존
- 로컬 `/health/ready` 점검 후 완료 처리
- 실패 시 프로그램, 서비스와 방화벽 상태 rollback 시도

Setup 시작 시 설치 트랜잭션 작업 기록은 읽기 전용으로 먼저 검사합니다. 복구 가능한 미완료
기록이 있으면 새 설치·업데이트를 시작하지 않고 `설치/업데이트`를 비활성화하며, 운영자가
별도의 `이전 상태 복구`를 선택해야 합니다. 복구 성공 뒤 설치가 자동으로 이어지지는 않습니다.
Setup은 설치 버튼을 다시 활성화한 뒤 운영자가 사전 점검을 확인하고 새 작업을 별도로
시작하도록 합니다. 작업 기록이 손상됐거나 현재 파일·서비스 상태와 맞지 않아 복구 안전성을
증명할 수 없으면 복구와 설치를 모두 차단하고 관리자 확인을 요청합니다.

Rollback은 서비스 중지, 프로그램·ACL 복원과 검증, 데이터 정리, 이전 서비스 상태 복원 순으로
의존성을 확인합니다. 프로그램 복원이 완전하지 않으면 이전 서비스를 다시 시작하지 않습니다.
HTTPS 방화벽과 레거시 방화벽 snapshot은 서로 독립적으로 복원 결과를 기록합니다. 권위 있는
상태 복원이 끝난 뒤에만 rollback 완료 단계를 작업 기록에 쓰고, 그 다음에만 staging·backup·
failed 자료를 정리합니다. 복구가 실패하면 최초 설치·업데이트 실패 코드와 복구 단계별 코드를
별도로 유지하면서 최종 호환 코드로 `SETUP_ROLLBACK_FAILED`를 표시합니다.

방화벽 COM API는 적용한 `ViewerIPv4/32`를 다시 읽을 때 `ViewerIPv4/255.255.255.255`로
정규화할 수 있습니다. Setup 검증기는 같은 IPv4의 `IP`, `IP/32`,
`IP/255.255.255.255`만 동일한 단일 호스트 범위로 취급합니다. 다른 주소, `/0`~`/31`,
목록·범위, `Any`, `LocalSubnet`, IPv6는 거부하고 Enabled·Inbound·Allow·TCP·18443·
Domain/Private·Edge Traversal 비활성 조건은 정확히 비교합니다.

방화벽 적용 직후에는 즉시 검증하고, 운영체제 반영 지연을 위해 200ms 간격으로 최대 2초까지만
다시 읽습니다. 마지막 결과가 불일치이면 안전한 필드별 코드와 상위
`SETUP_FIREWALL_FAILED`를 반환하며 실제 주소와 방화벽 원문은 사용자 오류에 포함하지 않습니다.
설치 트랜잭션은 이 실패를 성공으로 간주하지 않고 기존 방화벽 snapshot을 포함해 rollback합니다.

관리자 권한은 Setup 실행과 시스템 설정 변경에만 필요합니다. 설치 후 Agent는 일반 사용자의
데스크톱 세션과 분리된 서비스이므로 Agent 창이나 트레이 아이콘이 표시되지 않습니다. 로컬
관리자는 Windows 정책상 서비스를 제어할 수 있습니다.

`이 PC 주소 넣기`는 현재 PC에서 활성 상태인 loopback·tunnel 이외 RFC1918 IPv4를 찾습니다.
후보가 하나면 바로 입력하고 여러 개이면 운영자가 정확한 주소를 선택하며, 후보가 없거나 검색이
실패하면 설치를 추측으로 계속하지 않고 안내를 표시합니다. 이 도우미는 방화벽 범위를 넓히지
않고 선택된 주소 한 개만 기존 `/32` 경계에 적용합니다.

Setup이 다른 프로그램 소유의 TCP/18443 인바운드 허용 규칙을 발견해도 이를 변경하지
않습니다. 사전 점검에 `FIREWALL_OVERLAP_PROTECTED` 경고를 남기고 설치를 계속하며, Agent의
Viewer IPv4 검증을 추가 경계로 사용합니다. 방화벽 비활성화, 기본 인바운드 허용, Public
프로필만 활성, 로컬 규칙 병합 차단 또는 제품 규칙 이름 충돌은 계속 설치를 차단합니다.

실패 화면의 `진단정보 복사`는 지속 파일을 만들지 않고 클립보드에만 안전한 요약을
복사합니다. 요약에는 제품 버전, UTC 시각, 작업 종류, 최초 실패와 복구 단계 코드, 작업 기록
형식·단계, 필수 경로의 존재 여부와 서비스 상태만 포함합니다. 실제 IP/CIDR, PC·사용자명,
절대 경로, 트랜잭션 ID, 서비스 계정, 방화벽 규칙 내용, 자격 증명, 인증서, 명령과 장비 출력은
포함하지 않습니다.

### Viewer PC

Viewer ZIP은 설치 프로그램이 없는 포터블 배포물입니다.

1. Viewer ZIP을 항상 사용할 로컬 폴더에 완전히 압축 해제합니다.
2. `SamsungSwitchWatch.Viewer.exe`를 실행합니다.
3. Agent PC의 IPv4를 입력하고 연결을 확인합니다. 동일 PC 사전 검증은 운영자가
   `이 PC에서 사전 테스트`를 눌렀을 때만 실행합니다.

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
→ HTTPS 및 자동 TOFU 신뢰
→ Agent identity와 Agent·Viewer 버전
```

인증서 SHA-256 지문이나 페어링 토큰을 사용자가 입력하는 화면은 없습니다. 단계별 실패는
`AGENT_CONNECTION_REFUSED`, `AGENT_VERSION_MISMATCH`와 같은 안정적인 오류 코드와 사용자용
설명으로 표시합니다.

동일 PC 사전 테스트는 `localhost`나 loopback을 우회 경로로 열지 않습니다. Viewer는 활성
loopback·tunnel 이외 RFC1918 IPv4를 최대 6개로 제한하고, 후보당 7초·전체 30초 안에서 위의
동일한 5단계 연결 검사를 수행합니다. 첫 성공 후보만 저장 대상으로 제안하며 스위치 자격
증명이나 Telnet 명령은 전송하지 않습니다. 성공은 Agent 서비스·TCP/18443·HTTPS·Agent API·
제품 버전만 증명하고, 스위치 접속과 원격 Viewer 경로는 각각 미확인으로 표시합니다.

실제 원격 배치 전에는 Setup의 `AllowedViewerIpv4`와 제품 방화벽 `/32`를 원격 Viewer의 고정
IPv4로 다시 적용하고, 그 원격 Viewer에서 동일한 연결 진단을 수행해야 합니다. `localhost`,
`localhost.`와 `127.0.0.0/8`은 기존 설정 마이그레이션 안내를 위해 감지할 수는 있지만 새 연결과
실제 연결 시도에는 허용하지 않습니다.

### 장비 접속 시험

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

### 명령 실행과 감시

```text
Viewer에서 한 줄 show 명령 선택 또는 입력
→ POST /api/v4/telnet/execute
→ Agent가 대상과 명령을 다시 검증
→ 한 Telnet 세션에서 로그인·enable·명령 실행
→ 페이징 처리와 최대 64 KiB 결과 반환
→ 세션 정리
→ Viewer가 원문 표시 또는 이전 상태와 비교
```

주요 명령은 `show port status`와 `show sylog tail num 100`입니다. 펌웨어에 따라
`show syslog tail num 100` 또는 다른 조회 명령이 필요할 수 있으므로, 특정 명령 실패를 장비 전체
장애로 바꾸지 않습니다.

명령 실행 중 원격 종료가 발생하면 완료된 명령은 반복하지 않고 남은 명령만 새 세션에서 최대 한
번 재시도합니다. 인증·enable 실패, 명령 시간 초과와 사용자 취소는 자동 재시도하지 않습니다.

## 4. 소유권과 저장 위치

### Viewer가 소유하는 정보

- 장비명, 모델, IPv4와 표시 설정
- 현재 Windows 사용자 DPAPI로 암호화된 ID·로그인 PW·enable PW
- 장비별 감시 설정과 마지막 실행 시각
- 상태 기준선, 변경 이벤트와 Viewer 비실행 시간에 따른 감시 공백
- 자동으로 고정한 Agent HTTPS 신원

수동 명령 문자열과 수동 원문 출력은 Viewer 프로세스 메모리에서만 사용하고 파일, 데이터베이스,
진단 로그 또는 내보내기에 보관하지 않습니다.

### Agent가 소유하는 정보

- Agent ID, HTTPS listener와 실행 한도
- Setup에서 선택한 대상 관리망
- DPAPI LocalMachine으로 보호한 HTTPS 개인 키
- 서비스 실행에 필요한 최소 설정과 정제된 진단

Agent는 장비 목록, 장비 자격 증명, 감시 일정, 상태 기준선 또는 명령 원문을 영구 저장하지
않습니다.

## 5. Agent 설정

Setup이 만드는 대표 운영 설정은 다음과 같습니다. 운영자는 자동 검색 결과를 선택하거나
승인된 CIDR을 Setup에서 직접 추가하며, 운영 설정 파일을 직접 편집하지 않습니다.

```json
{
  "Agent": {
    "AgentId": "agent-REMOTE-PC",
    "ListenUrl": "https://0.0.0.0:18443",
    "DataDirectory": "C:\\ProgramData\\SamsungSwitchWatch",
    "MockMode": false,
    "AllowedViewerIpv4": "10.20.30.41",
    "AllowedTargetCidrs": [
      "10.40.0.0/16"
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

`AllowedViewerIpv4`는 Agent API가 실제 TCP 연결의 원격 IPv4와 비교하는 단일 주소입니다.
운영 모드에서는 RFC1918 사설 IPv4가 반드시 필요합니다. 일치하지 않는 요청은
`AGENT_CLIENT_NOT_ALLOWED`로 거부되며 전달 헤더는 주소 판정에 사용하지 않습니다.
동일 PC 사전 테스트도 이 규칙의 예외가 아니며 실제 사설 IPv4가 정확히 일치해야 합니다.

`AllowedTargetCidrs`는 요청마다 Agent가 적용하는 SSRF/Telnet 대상 허용 목록입니다. Setup은
직접 입력한 `IPv4/prefix`를 canonical 네트워크 주소로 정규화하고, 전체 네트워크가 RFC1918
범위 안에 있는지 확인합니다. 자동 선택과 직접 추가에서 중복을 제거한 1~2개만 저장합니다.
Agent는 canonical dotted IPv4와 고정 TCP/23만 허용하며 DNS 이름, IPv6, loopback,
link-local, multicast와 허용 범위 밖 주소를 거부합니다.

## 6. API v4

### 신원과 상태

- `GET /api/v4/identity`
  - Agent ID, 제품 버전, API 버전, HTTPS 공개 신원, 실행 한도
  - 비밀번호와 대상 CIDR 전체 목록은 반환하지 않음
- `GET /health/live`
  - 프로세스 생존 상태
- `GET /health/ready`
  - HTTPS 신원과 실행기 초기화 완료 상태
  - Setup의 로컬 설치 후 검증에 필요한 상태, API 버전, 프로토콜과 제품 버전을 반환
  - 운영 모드에서 loopback `/api/v4/identity`를 열지 않고 이 응답만 16KiB 한도로 검증

### 접속 시험

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
포함하지 않는 오류 코드와 설명만 반환합니다.

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
- 세션 수명: 최대 240초

한 장비의 인증 실패, 명령 미지원 또는 시간 초과가 다른 장비 작업을 중단시키지 않습니다.

## 8. HTTPS 신뢰

Agent는 최초 정상 시작 때 ECDSA P-256 자체 서명 신원을 생성하고 DataDirectory에 보관합니다.
개인 키는 DPAPI LocalMachine으로 보호됩니다. 정상 업데이트에서는 DataDirectory를 유지하여
신원도 보존합니다.

Viewer는 최초 연결에서 `/api/v4/identity`와 TLS 공개 키 신원을 자동으로 대조하고 TOFU
(Trust On First Use) 방식으로 고정합니다. 이후 같은 Agent 주소의 신원이 바뀌면 재설치 또는
중간자 공격 가능성으로 보고 연결을 차단합니다. 수동 지문 또는 페어링 토큰 입력으로 우회하지
않습니다.

TOFU는 최초 연결 상대를 별도 중앙 인증기관으로 검증하는 방식이 아닙니다. 최초 연결은 Setup에서
구성한 Viewer `/32` 방화벽, 관리망 격리와 운영자 확인을 신뢰합니다.

## 9. 버전과 업데이트

Agent와 Viewer는 같은 Release 버전을 사용합니다. 버전이 다르면 연결 진단에서
`AGENT_VERSION_MISMATCH`로 중단하고 두 ZIP을 같은 Release 조합으로 교체합니다.

v0.10 Agent Setup은 기존 설치를 감지하여 Agent ID, HTTPS 신원과 유효 실행 한도를 보존하고,
현재 입력한 Viewer `/32`와 확정한 관리망으로 접근 경계를 다시 구성합니다. 기존
`AllowedTargetCidrs`가 서로 다른 canonical RFC1918 CIDR 1~2개이면 Setup 목록에 복원합니다.
대상 목록을 안전하게 복원할 수 없으면 `SETUP_EXISTING_NETWORKS_NOT_LOADED` 경고를 표시하고
아무 관리망도 미리 선택하지 않으므로 운영자가 다시 선택하거나 직접 추가해야 합니다. 이
경고만으로 설치를 영구 차단하지는 않지만, 전체 운영 설정 JSON 손상은 별도의 기존 배포 설정
검증에서 차단할 수 있습니다. v0.7 계열의 Agent 장비 목록, Agent 자격 증명 저장소, 자체 Poll
Scheduler, 이벤트 DB와 SignalR 수집 흐름은 v0.10 구조에 포함되지 않습니다.

미완료 설치 작업이 있으면 Setup은 이를 자동 복구하거나 새 설치와 함께 처리하지 않습니다.
읽기 전용 판정 뒤 복구 가능한 상태에서만 `이전 상태 복구`를 허용하고, 성공 뒤에도 설치는
운영자가 별도로 시작합니다. staging·backup·failed 폴더와 작업 기록은 안전성 판단 근거이므로
운영자가 수동으로 삭제·이동·이름 변경하지 않습니다.

## 10. 의도적으로 하지 않는 동작

- Agent PC에서 사용자 창이나 트레이 아이콘 표시
- Viewer 자동 시작 또는 시스템 전체 설치
- Agent가 Viewer 없이 독립적으로 장비 감시
- Viewer에서 설정 변경 명령 실행
- 공인망 또는 승인받지 않은 CIDR 허용
- 관리망을 세 개 이상 허용
- 지문 또는 페어링 토큰 수동 입력
- `localhost` 또는 loopback을 이용한 Agent API 연결
- 자동 실행되는 동일 PC 사전 테스트나 사전 테스트 중 스위치 조회
- 미완료 설치 작업의 자동 복구 또는 복구 성공 직후 자동 설치
- 설치·복구 근거인 staging·backup·failed 폴더와 작업 기록의 사용자 수동 정리
- 공개 ZIP에서 PowerShell·CMD 스크립트 실행
