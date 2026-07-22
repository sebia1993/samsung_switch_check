# Architecture

## 배치 구조

```text
관리 VLAN
  IES4224GP ─┐
  IES4028XP ─┼─ Telnet/23 ─> Agent host
  IES4226XP ─┘                ├─ Windows Service (관리자 권장)
                              ├─ 현재 사용자 숨김 예약 작업 (비관리자 대체)
                              ├─ 모델 프로파일 + capability 확인
                              ├─ 명령별 구조화·변경 감지
                              ├─ SQLite + DPAPI 원문 보관
                              ├─ HTTP API v1/v2/v3
                              │  (암호화·인증 없음, 고정 IP 방화벽)
                              └─ SignalR eventChanged
                                      │
                                      ▼
                                  WPF Viewer
                                  ├─ 대시보드
                                  ├─ 미니 창
                                  ├─ 트레이·알림
                                  └─ 검색·안전한 내보내기
```

주기 수집과 기존 수동 점검은 `deviceId + commandId`만 요청하고 Agent가 등록된 읽기 전용
`show` 명령으로 변환합니다. v0.7의 Viewer 장비 명령은 설치 시 명시적으로 켠 경우에만 한 줄
문자열을 받습니다. Agent가 다시 허용 목록·금지 키워드·구분자를 검사한 뒤 같은 장비 semaphore와
Telnet 수명주기를 사용합니다. Telnet 사용자명·비밀번호는 Agent PC 밖으로 전송하지 않습니다.

## Agent 호스트 모드

Agent 실행 파일은 동일하며 호스트 방식만 다음 둘 중 하나를 선택합니다.

| 모드 | 실행 주체 | 실행 범위 | 설치 위치 |
|---|---|---|---|
| Windows 서비스 | `LocalService` | 부팅 후 로그온과 무관 | `%ProgramFiles%`, `%ProgramData%` |
| 현재 사용자 숨김 | 제한된 `InteractiveToken` 예약 작업 | 같은 사용자 로그온 동안 | `%LOCALAPPDATA%` |

두 모드는 고정 서비스·작업 충돌 검사를 통해 동시에 실행되지 않습니다. 숨김 모드는
`run-agent-background.ps1`을 `-WindowStyle Hidden`으로 실행하고 Agent 프로세스를 자식으로
직접 추적합니다. 예약 작업은 중복 실행을 무시하고 비정상 종료를 1분 간격으로 최대 3회
재시작합니다. 설치 성공은 로컬 `/health/live` 응답 뒤에만 확정합니다.

숨김 모드는 방화벽을 변경하지 않으므로 기존 관리자 승인 고정 Viewer IPv4 정책을 전제로
합니다. Windows 서비스의 `LocalService` 격리나 같은 계정 사용자의 작업 종료 방지는 제공하지
않습니다.

## 수집 흐름

모델 프로파일은 공개 자료를 바탕으로 한 안전한 후보입니다.

| Command ID | 후보 명령 | 기본 주기 | 필수 여부 |
|---|---|---:|---|
| `version` | `show version` | 1시간 | 선택 |
| `system` | `show system` | 1분 | 선택 |
| `log_ram` | `show syslog tail num 100` 우선, `show sylog tail num 100` 및 모델별 대체 | 1분 | 필수 |
| `interface_status` | `show port status` 우선, 모델별 대체 | 1분 | 필수 |

Agent는 같은 장비의 due 명령을 한 Telnet 세션에서 실행합니다. 장비별 semaphore로 동시
세션을 하나로 제한하고, 다른 장비는 `MaxConcurrentDevices`(기본 4, 최대 16) 범위에서
병렬 처리합니다. Agent 한 대에는 최대 256개 장비를 등록할 수 있습니다.

명령별 성공·실패·미지원·마지막 시도·마지막 성공을 별도 collector health snapshot으로
저장합니다. 한 명령이 미지원이어도 나머지 수집기는 계속 동작하며, 필수 수집기 전체가
사용 불가능할 때만 readiness가 실패합니다.

## Viewer 읽기 전용 장비 명령

이 기능은 `Agent:EnableReadOnlyQueries=false`가 기본값이며 설치기의
`-EnableReadOnlyQueries`로만 명시적으로 켭니다. `GET /api/v3/snapshot`의
`features.readOnlyQueries`가 `enabled`, `maxCommandLength`, `maxOutputBytes`를 광고하므로
구형 Agent나 비활성 Agent에서는 Viewer 탭을 이유와 함께 비활성화합니다.

Agent는 128자 이하 한 줄 `show` 명령 중 `port`, `ports`, `interface`, `interfaces`, `system`,
`version`, `syslog`, `sylog`, `log`, `spanning-tree`, `lacp`, `power`, `memory` 첫 토큰만
허용하고, 설정·계정·인증·암호 관련 키워드와 제어문자 및 shell 구분자를 거부합니다. 요청은
같은 장비의 주기 수집과 공통 잠금을 사용하며 잠금 대기는 최대 5초, 전체 요청은 최대 60초입니다.
Viewer IPv4별 분당 최대 12회이고 대기열 없이 초과 요청을 거부합니다.

정규화된 UTF-8 결과는 최대 65,536바이트에서 유효한 문자 경계로 잘라 `truncated` 상태와 함께
요청한 Viewer에 한 번 반환합니다. 결과는 Agent snapshot/event/raw DB, Viewer 설정, 명령 이력,
CSV/JSON 내보내기에 저장하지 않습니다. 감사에는 장비 ID, Viewer IP, 명령 SHA-256, 소요 시간,
결과 코드와 출력 크기만 남깁니다.

## 변경 감지

- 첫 로그 조회는 기준선만 만들고 기존 로그를 신규 알림으로 만들지 않습니다.
- 빈 출력·부분 출력·프롬프트 미복귀는 정상 결과를 덮어쓰지 않습니다.
- 최초 조회부터 중요 업링크가 Down이면 활성 Critical을 생성합니다.
- 동일 condition의 미복구 활성 이벤트는 DB unique 제약으로 하나만 유지합니다.
- 이벤트 생성·확인·복구마다 당시의 불변 snapshot을 append-only change feed에 기록합니다.
- uptime 감소와 로그 기준선 초기화를 영속 snapshot으로 상관 분석합니다.
- DB integrity 실패 시 liveness만 유지하고 수집·수동 점검·쓰기 작업을 중단합니다.

## 저장소

SQLite는 WAL 모드를 사용합니다. 구조화 snapshot, 이벤트, 변경 feed, 감사 이력과
Agent 전용 원문을 한 트랜잭션으로 커밋합니다.

- 원문: DPAPI LocalMachine 보호, 기본 7일·500MB, 256개 단위 trim
- 이벤트: 기본 90일, 미복구 활성 condition은 보존
- 감사 이력: 기본 180일
- schema v5: snapshot/event/raw/audit는 보존하고 제거된 pairing/token 테이블만 폐기

서비스 설치 스크립트는 서비스 계정과 관리자만 데이터에 접근하도록 ACL을 설정합니다.
현재 사용자 숨김 설치는 사용자 프로필의 별도 데이터 폴더를 사용합니다.

## API v3

- `GET /health/live`: Agent 프로세스 응답
- `GET /health/ready`: DB·스키마·자격 증명·스케줄러·필수 수집기 상태
- `GET /api/v3/snapshot`: authoritative counts, high-watermark, 장비·capability·채널 및 기능 협상 상태
- `GET /api/v3/events/recent?limit=500`: 최신 이벤트 snapshot
- `GET /api/v3/events/changes?after={cursor}&limit=500`: 불변 변경 이력 page
- `POST /api/v3/check-runs`: 등록된 장비 ID와 명령 ID만 즉시 실행
- `POST /api/v3/read-only-queries`: 옵트인된 한 줄 `show` 조회 실행과 메모리 전용 결과 반환
- `/hubs/events`: 같은 `eventChanged` 계약의 실시간 전달

기존 `/api/v1`, `/api/v2`는 v1.0 전까지 읽기 호환 경로로 유지합니다. Viewer는 v3를
우선 사용하고 404에서만 v2로 fallback합니다.

v0.7 API와 SignalR에는 애플리케이션 인증이 없습니다. Agent는 `0.0.0.0:18443`에서
수신하고 Windows 방화벽이 설치 때 지정한 1~32개의 고정 Viewer IPv4만 허용합니다.

## Viewer 동기화

Viewer는 먼저 최신 snapshot과 authoritative count를 가져온 뒤 저장한 cursor부터 변경
page를 순서대로 적용합니다. 동기화 중 들어온 SignalR 변경은 버퍼링하고, 재연결 시 누락된
여러 이벤트를 한 catch-up 요약으로 알립니다. retention 때문에 cursor가 유효하지 않으면
현재 기준선을 다시 만들되 과거 이벤트를 신규 팝업으로 만들지 않습니다.

HTTP 상태와 SignalR 상태는 분리됩니다. 실시간 채널이 재연결 중이어도 API 조회가 정상이면
기존 상태를 표시하고 `실시간 저하`로 알립니다. Agent 자체가 끊기면 마지막 정상 cache를
유지하되 현재 상태는 `미확인`으로 표시합니다.
