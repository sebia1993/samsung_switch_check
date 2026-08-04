# Samsung Switch Watch 0.10.13-poc 릴리스 노트

이번 버전은 Agent 설치 완료 직전의 로컬 HTTPS 준비 상태 실패를 더 정확하고 안전하게
진단합니다. 설치 절차, Viewer 허용 IPv4, 방화벽 경계, Agent API와 스위치 접근 정책은
변경하지 않았습니다.

## 로컬 HTTPS 실패 분류

Agent Setup이 `Setup → 127.0.0.1:18443 → Agent 서비스` 구간에서 준비 상태를 확인하지
못하면 다음 안전 코드 중 하나로 구분합니다.

- `HTTPS_TLS_FAILED`: TLS 협상 또는 로컬 인증서 사용 단계 실패
- `HTTPS_REQUEST_TIMEOUT`: 제한 시간 안에 로컬 HTTPS 요청이 완료되지 않음
- `HTTPS_CONNECTION_RESET`: 연결이 도중에 재설정됨
- `HTTPS_EOF`: 응답 헤더 또는 본문이 끝나기 전에 연결이 종료됨
- `HTTPS_CONNECT_FAILED`: 로컬 HTTPS 연결 자체를 만들지 못함

이 분류는 Agent PC 내부 통신 상태를 설명합니다. Viewer IP, Viewer와 Agent 사이의 원격
방화벽·라우팅 또는 스위치 관리망 설정 문제를 뜻하지 않습니다.

## 비식별 관측값

익명 진단에는 원인 분류에 필요한 다음 상태만 추가합니다.

- Agent 서비스 실행 관측 여부
- TCP/18443 수신 소유 관측 여부
- 로컬 HTTPS 요청 시도 횟수
- 마지막 전송 단계
- 설치 중 Agent 재시작 관측 여부

실제 PID, IP/CIDR, 사용자명, 경로, 인증서 정보, 예외 원문, 명령과 장비 출력은 포함하지
않습니다. 새 세부 분류는 익명 진단과 화면 설명에 사용하고, 짧은 SWD1 지원 코드는 기존
`HTTPS_REQUEST_FAILED` 범주로 유지하여 기존 해석 도구와 호환됩니다.

## 단계 순서와 시간

설치 완료 판정이 실패하면 진단 단계가 실제 작업 순서대로 기록됩니다.

```text
SERVICE_STARTED
SETUP_HEALTH_FAILED
ROLLBACK_COMPLETED
```

준비 상태 확인에 사용한 시간은 `SETUP_HEALTH_FAILED`에, 복구 시간은
`ROLLBACK_COMPLETED`에 각각 기록합니다. 이전처럼 대기 시간이 복구 단계에 합쳐져 보이지
않습니다.

## 유지되는 동작

- Agent는 창 없는 Windows 서비스로 실행됩니다.
- Viewer와 Agent는 HTTPS/TCP 18443을 사용합니다.
- Agent는 Setup에서 승인한 스위치 관리망의 Telnet/TCP 23에만 접속합니다.
- Viewer와 Agent 양쪽에서 한 줄 읽기 전용 `show` 명령만 허용합니다.
- 장비 자격 증명과 운영 이력은 Viewer가 소유하며 Agent는 저장하지 않습니다.
- GitHub Release 사용자 정의 Assets는 다음 두 파일뿐입니다.

```text
SamsungSwitchWatch-Agent-0.10.13-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.10.13-poc-win-x64.zip
```

## 검증 범위

자동 테스트는 TLS, 요청 시간 초과, 연결 재설정, 조기 종료와 연결 실패를 비식별 Mock으로
재현하고, 단계 순서·시간 분리와 민감정보 미노출을 확인합니다. 실제 사내 EDR·백신 정책,
Windows 서비스·방화벽 통합 설치와 삼성 스위치 펌웨어 동작은 승인된 시험 PC에서 별도로
확인해야 합니다.
