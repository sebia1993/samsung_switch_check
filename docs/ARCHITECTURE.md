# Architecture

## 신뢰 경계

```text
관리 VLAN
  IES4224GP
      | Telnet/23 (암호화되지 않음, 읽기 전용 계정)
      v
  Agent Windows Service
      - 서비스 SID로 프로그램·데이터 ACL 격리
      - 자격 증명 DPAPI 보호와 인증 실패 회로 차단
      - 명령 허용 목록과 명령별 상태
      - 원문/SQLite 로컬 보관, 마지막 정상값 유지
      - 원자적 수집 저장과 append-only 변경 피드
      | HTTPS/18443 + Bearer 토큰 + 인증서 지문 고정
      v
  Viewer WPF
      - 구조화 상태·이벤트만 수신
      - 변경 피드 catch-up 후 SignalR 실시간 적용
      - Agent별 연속 적용 커서
      - 대시보드, 미니 창, 팝업, 시스템 트레이
```

Viewer는 임의 CLI 문자열을 Agent에 보내지 않습니다. 수동 점검도 `deviceId + commandId`만 요청하며 Agent가 로컬 프로파일의 읽기 전용 명령으로 변환합니다.

## 수집과 상태 판정

| Command ID | 후보 명령 | 기본 주기 |
|---|---|---:|
| `version` | `show version` | 시작 시 + 1시간 |
| `system` | `show system` | 60초 |
| `log_ram` | `show log ram` | 60초 |
| `interface_status` | `show interfaces status` | 60초 |

`system`, `log_ram`, `interface_status`는 같은 완료 시각을 기준으로 다음 실행을 예약하고 한 Telnet 세션에서 배치 조회합니다. 이로써 재부팅 직후 Uptime 감소와 RAM 로그 기준점 초기화를 하나의 재부팅 사건으로 묶습니다.

- 동시에 도래한 명령은 한 Telnet 세션에서 실행하지만 결과와 상태는 명령별로 저장합니다.
- 일시적 실패는 같은 명령에서 3회 연속 발생해야 장애가 됩니다. `AUTH_FAILED`는 첫 실패에 자동 접속을 차단합니다.
- 파싱 실패·불완전 출력·중요 업링크 행 누락은 마지막 정상값을 덮어쓰지 않습니다.
- 최초 로그는 기준선이며, 로그 순환·초기화·재부팅 시 전체 로그를 신규로 만들지 않습니다.
- 활성 장애는 수집 불능과 중요 업링크 Down입니다. 재부팅은 이력성 Warning입니다.

수집 결과, 명령 상태, 마지막 정상 스냅샷, 이벤트 변경과 감사 기록은 한 SQLite 트랜잭션으로 커밋합니다. SignalR 전송 실패는 커밋을 취소하지 않으며 Viewer는 변경 피드에서 누락분을 복구합니다.

## v2 API 계약

- `/health/live`: Agent 프로세스 응답성
- `/health/ready`: DB·스키마·인증서·자격 증명·스케줄러·필수 수집기 준비 상태
- `/api/v2/snapshot`: 준비 상태, 장비·명령별 상태, 마지막 시도·성공 시각, 변경 high-watermark
- `/api/v2/events/recent?limit=500`: Viewer 시작 화면의 최신 이벤트 상태
- `/api/v2/events/changes?after={cursor}&limit=500`: 생성·확인·복구 변경의 페이지 조회
- `/hubs/events`: 동일한 `eventChanged` 계약의 실시간 전송

Viewer는 먼저 최근 상태를 표시하고, 캡처한 high-watermark까지 변경 페이지를 순서대로 적용합니다. 이 동안 도착한 실시간 변경은 버퍼링한 뒤 연속 번호로 적용합니다. 커서는 Agent ID, URI와 인증서 지문별로 저장합니다.

기존 v1 조회 API는 `v0.2.0-poc`에서 읽기 전용 호환 경로로 유지하지만 Viewer는 v2만 사용합니다. 모든 API 응답은 원문 Telnet, 자격 증명과 내부 저장 경로를 제외합니다.
