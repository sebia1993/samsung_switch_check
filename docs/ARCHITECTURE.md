# Samsung Switch Watch v0.9 아키텍처

## 구성요소와 소유권

```text
┌──────────────────────── Viewer PC ────────────────────────┐
│ SamsungSwitchWatch.Viewer                                │
│ - 장비 IP·모델                                           │
│ - ID·로그인 PW·enable PW: DPAPI CurrentUser              │
│ - 감시 일정·기준선·변경 이벤트·감시 공백                 │
│ - 수동 명령과 원문 출력: 프로세스 메모리만               │
└──────────────────────────┬────────────────────────────────┘
                           │ HTTPS/18443
                           ▼
┌──────────────────────── Agent PC ─────────────────────────┐
│ SamsungSwitchWatch.Agent Windows Service                 │
│ - 영구 HTTPS 신원: ECDSA P-256 + DPAPI LocalMachine      │
│ - 허용 대상 IPv4 CIDR                                    │
│ - 요청 검증·Telnet 실행·응답 반환                        │
│ - 장비·계정·명령·출력·감시 이력은 저장하지 않음           │
└──────────────────────────┬────────────────────────────────┘
                           │ Telnet/23
                           ▼
              IES4224GP / IES4028XP / IES4226XP
```

Agent는 수집 서버가 아니라 네트워크 경계 안에서 Telnet 접속을 수행하는 무상태 실행기입니다.
Viewer가 종료되면 감시 요청도 발생하지 않습니다.

## 실행 흐름

### 접속 시험

```text
Viewer가 장비·계정을 메모리에서 복호화
→ POST /api/v4/telnet/test
→ Agent가 대상 CIDR과 포트 23 검증
→ TCP 연결
→ ID/PW 로그인
→ 프롬프트가 > 이면 선택적으로 enable
→ 권한 프롬프트 확인
→ exit/logout
→ 성공 여부·최종 권한·소요 시간 반환, 실패하면 sanitized 오류 코드 반환
```

### 명령 실행

```text
Viewer 수동 UI는 show 명령 1개, 주기 감시는 한 요청에 최대 8개 구성
→ POST /api/v4/telnet/execute
→ Agent가 요청·대상·명령을 다시 검증
→ 새 Telnet 세션에서 로그인·enable
→ 명령 실행과 페이징 처리
→ 명령 단계에서 원격 종료 시 완료분을 제외하고 남은 명령만 새 세션으로 1회 재시도
→ 최대 64KiB 결과 반환
→ 세션 종료와 메모리 폐기
→ Viewer가 화면 표시 또는 상태 비교
```

장비 세션은 요청 사이에 재사용하지 않습니다. 각 세션의 최대 시간은 240초이며 성공, 실패,
취소와 타임아웃 경로 모두에서 연결을 정리합니다. 로그인·인증·enable 단계 실패와 명령
타임아웃은 자동 재시도하지 않습니다.

## Agent 설정

운영 설정은 설치기가 다음 형태로 만듭니다.

```json
{
  "Agent": {
    "AgentId": "agent-REMOTE-PC",
    "ListenUrl": "https://0.0.0.0:18443",
    "DataDirectory": "C:\\ProgramData\\SamsungSwitchWatch",
    "MockMode": false,
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

`AllowedTargetCidrs`는 단순 문서 값이 아니라 Agent가 각 요청마다 적용하는 SSRF/Telnet
대상 허용 목록입니다. 명시적 canonical dotted IPv4와 고정 TCP/23만 허용하며 DNS 이름,
IPv6, loopback, link-local, multicast와 허용 범위 밖 주소는 거부합니다.

## API v4

### 신원과 상태

- `GET /api/v4/identity`
  - Agent ID, 프로토콜 버전, HTTPS 공개 신원 식별값
  - 비밀번호, CIDR 전체 목록과 런타임 경로는 반환하지 않음
- `GET /health/live`
  - 프로세스 생존 여부
- `GET /health/ready`
  - HTTPS 신원과 실행기 초기화 완료 여부

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
- `model`: 지원 모델 3종
- `commands`: execute 요청에서 1~8개

성공 응답은 성공 여부, 최종 권한 프롬프트, 시작·완료 시각, 전체 소요 시간, `sessionCount`,
`reconnectCount`, 명령별 출력과 잘림 여부를 포함합니다. 실패 응답은 안정적인 오류 코드와
민감하지 않은 설명만 반환합니다.
비밀번호는 어떤 응답에도 포함하지 않습니다.
요청 본문은 최대 32KiB이며 초과 요청은 JSON 바인딩 전에 `413 / REQUEST_TOO_LARGE`로
거부합니다.

## 명령 정책

다음 조건을 모두 만족하는 문자열만 실행합니다.

- 정규화 후 `show`로 시작
- 한 줄
- 제어문자와 줄바꿈 없음
- `;`, `&`, `|` 등 명령 연결 문법 없음
- 128자 이하

`show running-config`를 포함한 한 줄 `show` 명령은 허용됩니다. Viewer와 Agent는 해당
명령이나 결과를 DB, 파일 로그, 감사 이벤트 또는 내보내기에 저장하지 않습니다.

## Viewer 저장소

Viewer 로컬 저장소가 보관하는 항목:

- 장비명, 모델, IPv4, 표시 설정
- 현재 Windows 사용자 DPAPI로 암호화한 ID·PW·enable PW
- 장비별 감시 설정과 마지막 실행 시각
- 파싱된 상태 기준선과 변경 이벤트
- Viewer 비실행·절전·Agent 단절에 따른 감시 공백
- 자동 신뢰한 Agent 신원

보관하지 않는 항목:

- 수동 명령 문자열
- 수동 원문 출력
- `show running-config` 결과
- Agent로 보낸 평문 비밀번호

## HTTPS 신원

Agent는 최초 시작 때 ECDSA P-256 키와 자체 서명 인증서를 생성해 DataDirectory 아래에
보관합니다. 개인 키 자료는 Windows DPAPI LocalMachine으로 보호됩니다. 설치기는 인증서
경로나 비밀번호를 설정하지 않으며 업데이트 때 DataDirectory 전체를 보존합니다.

Viewer는 최초 연결에서 `/api/v4/identity`의 공개 신원을 자동 고정합니다. 같은 Agent 주소에서
신원이 바뀌면 중간자 공격 또는 재설치 가능성으로 보고 연결을 차단합니다. 사용자가 SHA-256
지문이나 페어링 토큰을 입력하는 화면은 없습니다.

## 동시성 및 실패 격리

- 전체 동시 Telnet 실행: 최대 2건
- API 클라이언트 기준: 분당 최대 60회
- 장비 1대에는 동시에 한 세션만 허용
- 한 요청: 명령 최대 8개, 결과 최대 64KiB
- 각 세션: 최대 240초
- 명령 실행 중 원격 종료: 완료된 명령을 제외한 나머지만 새 세션으로 최대 1회 재시도

한 장비의 인증 실패나 명령 미지원은 다른 장비 요청에 영향을 주지 않습니다.
`show sylog tail num 100`과 `show syslog tail num 100`처럼 펌웨어별 후보 명령은 Viewer가
순서대로 시험하며, 실패를 스위치 전체 장애로 바꾸지 않습니다.

## 버전 호환

v0.9 Agent와 Viewer는 API v4를 기준으로 함께 배포합니다. v0.7의 Agent 저장 장비 목록,
자격 증명 저장소, 자체 Poll Scheduler, 이벤트 DB와 SignalR 수집 흐름은 v0.9 운영 구조가
아닙니다. 업데이트 순서는 Agent 먼저, Viewer 다음입니다.
