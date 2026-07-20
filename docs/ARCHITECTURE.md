# Architecture

## 신뢰 경계

```text
관리 VLAN
  IES4224GP
      | Telnet/23 (암호화되지 않음, 읽기 전용 계정)
      v
  Agent Windows Service
      - 자격 증명 보호
      - 명령 허용 목록(allowlist)
      - 원문/SQLite 로컬 보관
      - 파싱, diff, 이벤트 수명주기
      | HTTPS/18443 + Bearer 토큰 + 인증서 지문 고정
      v
  Viewer WPF
      - 구조화 상태·이벤트만 수신
      - 누락 이벤트 재동기화용 시퀀스와 SignalR 재연결
      - 대시보드, 미니 창, 팝업, 시스템 트레이
```

Viewer는 임의 CLI 문자열을 Agent에 보내지 않습니다. 수동 점검도 `deviceId + commandId`만 요청하며 Agent가 로컬 프로파일의 읽기 전용 명령으로 변환합니다.

## 기본 수집 일정

| Command ID | 후보 명령 | 주기 |
|---|---|---:|
| `version` | `show version` | 시작 시 + 1시간 |
| `system` | `show system` | 5분 |
| `log_ram` | `show log ram` | 60초 |
| `interface_status` | `show interfaces status` | 60초 |

첫 결과는 기준선입니다. 명령이 지원되지 않거나 출력 파싱에 실패해도 장비 전체 장애로 만들지 않고 해당 collector만 `PARSER_UNSUPPORTED`로 표시합니다.

## 이벤트 규칙

- 로그: 순번·시각·메시지·module/function/event 식별값으로 신규 여부를 판정합니다.
- 로그 기준점이 사라지고 가동 시간(uptime)이 유지되면 순환/초기화로 처리합니다.
- 가동 시간(uptime)이 감소하면 재부팅 이벤트로 처리하고 새 기준선을 세웁니다.
- 상태: 원문 문자열이 아니라 정규화 필드의 의미 있는 변경만 비교합니다.
- 수집 실패: `collector_health`에는 접속 주소나 계정 대신 표준화 오류 코드와 마지막 시도 시각만 저장합니다.
- 같은 수집 실패는 한 번만 알리고, 다음 정상 수집에서 복구 이벤트를 만듭니다.
- 활성 조건 key가 같으면 중복 이벤트 대신 지속 시간을 갱신합니다.
- 조건이 사라지면 별도 `Recovered` 이벤트를 생성합니다.

## API 원칙

- `/health`: Agent 프로세스와 저장소 준비 상태
- `/api/v1/status`: Agent 연결 및 최근 수집 요약
- `/api/v1/devices`: 구조화 장비 상태
- `/api/v1/events?after={sequence}`: 누락 이벤트 재동기화
- `/api/v1/events/{id}/ack`: 운영자 확인 기록
- `/api/v1/commands/{deviceId}/{commandId}`: 등록 명령 즉시 실행
- `/hubs/events`: 실시간 이벤트 전송

API 응답에는 원문 Telnet, 자격 증명, 내부 저장 경로를 포함하지 않습니다.
