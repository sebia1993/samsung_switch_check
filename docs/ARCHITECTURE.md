# Architecture

## 배치 구조

```text
관리 VLAN
  IES4224GP ─┐
  IES4028XP ─┼─ Telnet/23 ─> Agent Windows Service
  IES4226XP ─┘                ├─ 모델 프로파일 + capability 확인
                              ├─ 명령별 구조화·변경 감지
                              ├─ SQLite + DPAPI 원문 보관
                              ├─ HTTPS API v1/v2/v3
                              └─ SignalR eventChanged
                                      │
                                      ▼
                                  WPF Viewer
                                  ├─ 대시보드
                                  ├─ 미니 창
                                  ├─ 트레이·알림
                                  └─ 검색·안전한 내보내기
```

Viewer는 자유 CLI 문자열을 보내지 않습니다. `deviceId + commandId`만 요청하고 Agent가
등록된 읽기 전용 `show` 명령으로 변환합니다. Telnet 사용자명·비밀번호와 명령 원문은
Agent PC 밖으로 전송하지 않습니다.

## 수집 흐름

모델 프로파일은 공개 자료를 바탕으로 한 안전한 후보입니다.

| Command ID | 후보 명령 | 기본 주기 | 필수 여부 |
|---|---|---:|---|
| `version` | `show version` | 1시간 | 선택 |
| `system` | `show system` | 1분 | 선택 |
| `log_ram` | `show log ram` | 1분 | 필수 |
| `interface_status` | `show interfaces status` | 1분 | 필수 |

Agent는 같은 장비의 due 명령을 한 Telnet 세션에서 실행합니다. 장비별 semaphore로 동시
세션을 하나로 제한하고, 다른 장비는 `MaxConcurrentDevices`(기본 4, 최대 16) 범위에서
병렬 처리합니다. Agent 한 대에는 최대 256개 장비를 등록할 수 있습니다.

명령별 성공·실패·미지원·마지막 시도·마지막 성공을 별도 collector health snapshot으로
저장합니다. 한 명령이 미지원이어도 나머지 수집기는 계속 동작하며, 필수 수집기 전체가
사용 불가능할 때만 readiness가 실패합니다.

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
- schema v4: 기존 평문 가능 원문을 폐기하고 보호 버전을 명시

설치 스크립트는 서비스 계정과 관리자만 데이터에 접근하도록 ACL을 설정합니다.

## API v3

- `GET /health/live`: Agent 프로세스 응답
- `GET /health/ready`: DB·스키마·인증서·자격 증명·스케줄러·필수 수집기 상태
- `GET /api/v3/snapshot`: authoritative counts, high-watermark, 장비·capability·채널 상태
- `GET /api/v3/events/recent?limit=500`: 최신 이벤트 snapshot
- `GET /api/v3/events/changes?after={cursor}&limit=500`: 불변 변경 이력 page
- `POST /api/v3/check-runs`: 등록된 장비 ID와 명령 ID만 즉시 실행
- `/hubs/events`: 같은 `eventChanged` 계약의 실시간 전달

기존 `/api/v1`, `/api/v2`는 v1.0 전까지 읽기 호환 경로로 유지합니다. Viewer는 v3를
우선 사용하고 404에서만 v2로 fallback합니다.

## Viewer 동기화

Viewer는 먼저 최신 snapshot과 authoritative count를 가져온 뒤 저장한 cursor부터 변경
page를 순서대로 적용합니다. 동기화 중 들어온 SignalR 변경은 버퍼링하고, 재연결 시 누락된
여러 이벤트를 한 catch-up 요약으로 알립니다. retention 때문에 cursor가 유효하지 않으면
현재 기준선을 다시 만들되 과거 이벤트를 신규 팝업으로 만들지 않습니다.

HTTP 상태와 SignalR 상태는 분리됩니다. 실시간 채널이 재연결 중이어도 API 조회가 정상이면
기존 상태를 표시하고 `실시간 저하`로 알립니다. Agent 자체가 끊기면 마지막 정상 cache를
유지하되 현재 상태는 `미확인`으로 표시합니다.
