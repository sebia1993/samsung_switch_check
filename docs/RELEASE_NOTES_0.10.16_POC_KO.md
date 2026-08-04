# Samsung Switch Watch 0.10.16-poc 릴리스 노트

릴리스 날짜: 2026-08-04

이번 버전은 Agent의 로컬 HTTPS/API 준비 상태와 원격 Viewer 방화벽 준비 상태를 분리합니다.
로컬 HTTPS가 정상인데 회사 GPO 또는 Windows 방화벽 규칙 적용·재조회만 실패하는 경우에는
Agent 전체 설치를 되돌리지 않고, 원격 연결 확인이 필요한 경고 상태로 설치를 완료합니다.

## 설치 완료 판정 개선

- `SamsungSwitchWatchAgent` 서비스, 로컬 HTTPS `/health/ready`, API v4, HTTPS 프로토콜과
  정확한 제품 버전은 계속 설치 필수 조건입니다.
- 위 로컬 준비 상태가 실패하면 기존처럼 `SETUP_HEALTH_FAILED`로 처리하고 설치 전 상태로
  rollback합니다.
- Viewer 고정 IPv4의 정확한 `/32` 제품 방화벽 규칙은 계속 자동 적용하고 재확인합니다.
- 방화벽 서비스·GPO·규칙 적용 또는 재조회만 실패하면 방화벽 변경분 복원을 시도하고
  복원 확인 여부를 경고 단계에 남긴 뒤
  `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고를 표시합니다.
- 이 경고 상태에서는 Agent 프로그램과 서비스가 유지되며 화면에
  `설치 완료 · 원격 Viewer 연결 확인 필요`가 표시됩니다.
- 정확한 제품 규칙까지 확인되면 `설치 완료 · 원격 연결 준비됨`으로 표시됩니다.

## Viewer 연결 안내 개선

- TCP/18443 단계가 실패하면 Agent 서비스, 네트워크 경로, Windows 방화벽 또는 회사 GPO를
  확인하도록 안내합니다.
- TCP 연결은 성공하고 HTTPS 단계가 실패하면 Agent PC의 로컬 HTTPS/TLS 준비 상태를
  확인하도록 안내합니다.
- 방화벽 문제와 로컬 TLS 문제를 같은 재설치 안내로 묶지 않습니다.

## 유지되는 보안 경계

- Viewer→Agent는 HTTPS/TCP 18443을 계속 사용합니다.
- Viewer는 인증서 지문이나 페어링 토큰을 입력하지 않으며 기존 자동 TOFU 신원 확인을
  유지합니다.
- Agent는 Setup에 입력한 Viewer IPv4를 모든 업무 API 요청에서 정확히 재검증합니다.
- Setup은 `Any`, `LocalSubnet`, 주소 목록 또는 넓은 대역 방화벽 규칙을 만들지 않습니다.
- Agent→스위치의 관리망 CIDR, Telnet/TCP 23과 읽기 전용 `show` 명령 검증은 변경하지
  않습니다.

## 운영 확인 순서

1. 같은 릴리스의 Agent와 Viewer ZIP을 사용합니다.
2. Agent Setup에서 설치 또는 업데이트를 한 번 실행합니다.
3. `원격 Viewer 연결 확인 필요`가 표시되면 재설치하지 말고 Viewer 연결 테스트를 실행합니다.
4. TCP/18443이 실패할 때만 Windows 관리자에게 방화벽·GPO 허용 경로를 요청합니다.
5. TCP가 성공하고 HTTPS가 실패하면 Agent PC에서 Setup의 로컬 readiness 진단을 확인합니다.

## 배포 파일

GitHub Release의 사용자 정의 Assets에는 다음 두 ZIP만 게시합니다.

```text
SamsungSwitchWatch-Agent-0.10.16-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.10.16-poc-win-x64.zip
```

두 ZIP은 Windows x64 self-contained 패키지이며 Python 또는 별도 .NET 런타임 설치를 요구하지
않습니다. 코드 서명되지 않은 POC이므로 사내 EDR·AppLocker·WDAC 승인 여부는 별도 확인해야
합니다.

자동 테스트와 패키지 smoke 검사는 방화벽 경고 완료 정책, 로컬 HTTPS fail-closed 판정과
Viewer 단계별 오류 안내를 검증합니다. 실제 사내 GPO, EDR, Windows 방화벽 병합 정책,
Viewer–Agent 라우팅과 삼성 스위치 펌웨어 동작은 현장 검증이 필요합니다.
