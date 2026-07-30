# Samsung Switch Watch 0.10.11-poc 릴리스 노트

이번 버전은 Agent 설치 중 `SETUP_HEALTH_FAILED` 뒤에
`ROLLBACK_FILE_RESTORE_FAILED`가 연이어 발생할 수 있던 Windows 서비스 시작·복구 경쟁을
수정한 안정성 릴리스입니다. 화면 배치, Agent API v4, Viewer 장비 입력·명령 흐름, 저장 형식과
보안 경계는 변경하지 않습니다.

## 설치 준비 상태 확인 개선

- 설치 중에는 새 Agent의 자동 서비스 복구 정책을 일시적으로 비활성화하고, Windows SCM에서
  재시작 작업이 0개로 다시 읽히는지 확인합니다.
- `/health/ready`를 확인할 때 최초 PID를 60초 동안 고정하지 않고 매 시도마다 Windows SCM의
  현재 서비스 PID를 다시 확인합니다.
- 시작 도중 서비스가 한 번 다시 실행돼 PID가 바뀌어도, 현재 서비스 프로세스가
  TCP/18443을 소유하고 올바른 HTTPS 준비 응답을 반환하면 정상으로 인정합니다.
- readiness가 성공한 뒤에만 5초·15초·60초 자동 재시작 정책을 최종 적용하고 설치를
  commit합니다.
- 로컬 readiness 요청은 시스템 프록시를 사용하지 않습니다.

## 실패 복구 개선

- 서비스 중지는 SCM의 `STOPPED` 표시만 확인하지 않고, 중지 과정에서 관찰한 서비스
  프로세스가 실제로 종료될 때까지 기존 제한 시간 안에서 기다립니다.
- 프로세스를 강제로 종료하지 않으며 제한 시간을 넘으면 파일을 이동하지 않고 안전하게
  실패합니다.
- rollback의 프로그램 폴더 이동은 일시적인 파일 잠금이나 EDR 검사 지연을 고려해 최대 5회만
  제한적으로 다시 시도합니다.
- 이동이 완료된 정확한 상태만 인정하며 원본과 대상이 함께 존재하는 등 모호한 상태에서는
  계속 진행하지 않습니다.
- 일시적 잠금은 기존 설치 상태로 복구하고, 지속 잠금은 작업 기록과 복구 근거를 보존한 채
  `ROLLBACK_FILE_RESTORE_FAILED`로 표시합니다.

## 더 구체적인 비식별 진단

`SETUP_HEALTH_FAILED`는 이제 다음 범주의 안전한 하위 원인을 구분합니다.

- 서비스 미실행 또는 서비스 상태 확인 실패
- TCP/18443 미수신, 다른 프로세스 점유 또는 소유 정보 확인 실패
- 로컬 HTTPS 요청 또는 HTTP 상태 실패
- 준비 응답 크기·형식 오류
- API 버전, HTTPS 프로토콜 또는 제품 버전 불일치
- 준비 확인 제한 시간 초과

실패 화면의 `진단정보 복사`, `익명 진단 저장`과 SWD1 지원 코드는 이 분류와 서비스 재시작
관찰 여부만 기록합니다. 실제 PID, IP/CIDR, 경로, 예외 원문, 인증정보, 장비 명령과 출력은
포함하지 않습니다. 기존 SWD1 코드는 그대로 해석되며, 이전 코드에서 예약값 0이던 4비트에
새 health 분류만 추가했습니다.

## 호환성과 유지되는 동작

- Agent와 Viewer는 같은 `0.10.11-poc` Release 조합을 사용합니다.
- Agent는 창이나 트레이 아이콘 없이 Windows 서비스로 실행됩니다.
- Viewer는 관리자 권한과 설치 스크립트가 필요 없는 포터블 실행 파일입니다.
- Agent API는 HTTPS/TCP 18443, Viewer 고정 IPv4 `/32`, 자동 TOFU 신뢰를 유지합니다.
- Viewer가 장비 IPv4·ID·PW·선택적 enable PW와 감시 이력을 소유합니다.
- Agent는 허용된 한 줄 읽기 전용 `show` 명령을 요청마다 새 Telnet 세션으로 실행하고
  종료합니다.
- 사용자가 인증서 SHA-256 지문이나 페어링 토큰을 입력하는 절차는 없습니다.

## 검증 범위

자동 검증은 현재 PID 전환, 다른 프로세스의 포트 점유 거부, 서비스·TCP·HTTPS·payload·버전
실패 분류, 서비스 복구 정책 적용 순서, 서비스 프로세스 종료 판정, 일시적·지속적 파일 잠금,
기존 journal 호환성, SWD1 health 분류와 전체 회귀 테스트를 포함합니다.

Mock과 단위 테스트는 실제 사내 EDR, Windows SCM 타이밍, 방화벽 COM, 원격 Viewer 경로와
삼성 스위치 Telnet을 증명하지 않습니다. 사내에서는 시험 PC 한 대에서 기존 미완료 작업을
먼저 복구한 뒤 이 Release의 Agent Setup을 실행하고, 단일 Viewer 연결과 읽기 전용 명령부터
확인해 단계적으로 확대하십시오.

## 공개 Assets

- `SamsungSwitchWatch-Agent-0.10.11-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.11-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 사용자 정의
Release Assets는 위 두 ZIP만 게시합니다.
