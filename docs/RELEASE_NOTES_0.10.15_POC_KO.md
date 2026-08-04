# Samsung Switch Watch 0.10.15-poc 릴리스 노트

이번 버전은 Agent Setup이 서비스 설치와 TCP/18443 수신까지 완료한 뒤에도 일부 Windows
환경에서 로컬 HTTPS 준비 검사를 통과하지 못해 전체 설치가 rollback되던 문제를 수정합니다.
Agent·Viewer 역할, 장비 조회 흐름, 방화벽 범위와 읽기 전용 명령 정책은 변경하지 않았습니다.

## Agent 로컬 HTTPS 수정

- Agent의 ECDSA P-256 신원은 기존처럼 `%ProgramData%\SamsungSwitchWatch`에 저장하고
  DPAPI LocalMachine으로 보호합니다.
- production Agent는 보호된 PFX를 `Exportable` 또는 `PersistKeySet` 없이 서비스 계정의
  `UserKeySet`으로 Agent 프로세스 수명 동안 불러옵니다. 일부 Windows Schannel 환경에서
  일시 키로 불러온 ECDSA 개인 키를 TLS 서버가 사용하지 못하던 경로를 피하면서 키를
  내보내거나 사용자 키 저장소에 별도 컨테이너로 영구 유지하지 않도록 합니다.
- Kestrel에 전달한 인증서도 Agent 호스트가 소유하도록 등록하여 정상 종료 때 키 컨테이너가
  정리됩니다. 실제 Kestrel HTTPS 통합 테스트는 실행 전후 사용자 키 파일 집합이 같음을
  확인합니다.
- Setup의 로컬 준비 상태 재시도는 매번 새 HTTP handler와 client를 사용합니다.
- 각 요청은 정확한 HTTP/1.1과 `Connection: close`를 사용하여 실패한 TLS 연결과 연결 풀
  상태를 다음 재시도에 재사용하지 않습니다.

## 성공 기준은 그대로 유지

이번 수정은 설치 성공 게이트를 완화하거나 실패를 경고로 바꾸지 않습니다. Setup은 다음을
모두 확인해야 설치 또는 업데이트를 완료합니다.

1. `SamsungSwitchWatchAgent` 서비스가 실행 중임
2. TCP/18443 수신 포트가 해당 Agent 프로세스 소유임
3. 로컬 HTTPS `/health/ready`가 성공 응답을 반환함
4. Agent API가 v4임
5. 응답 프로토콜이 HTTPS임
6. 응답 제품 버전이 설치 패키지와 정확히 일치함

하나라도 확인하지 못하면 `SETUP_HEALTH_FAILED`로 처리하고 기존 transactional rollback을
수행합니다. 인증서 검증, 방화벽 범위, Viewer 고정 IPv4, 관리망 CIDR 또는 Agent API 접근
제한을 우회하지 않습니다.

## 운영자 확인 순서

1. 기존 실패 화면에서 `이전 상태 복구`가 필요하면 먼저 복구를 완료합니다.
2. 공식 `0.10.15-poc` Agent ZIP을 새 로컬 폴더에 완전히 압축 해제합니다.
3. 같은 ZIP의 `SamsungSwitchWatch.Agent.Setup.exe`에서 `검사` 후 `설치/업데이트`를 한 번
   실행합니다.
4. 성공하면 같은 Release의 Viewer로 Agent 연결을 확인합니다.
5. 실패가 반복되면 재설치를 반복하지 말고 화면의 SWD1 지원 코드 또는 익명 진단을
   전달합니다.

## 유지되는 동작

- Agent는 창 없는 Windows 서비스로 실행됩니다.
- Viewer와 Agent는 HTTPS/TCP 18443을 사용합니다.
- Agent는 Setup에서 승인한 관리망의 Telnet/TCP 23에만 접속합니다.
- Viewer와 Agent 양쪽에서 한 줄 읽기 전용 `show` 명령만 허용합니다.
- 장비 자격 증명과 운영 이력은 Viewer가 소유하며 Agent는 저장하지 않습니다.
- GitHub Release 사용자 정의 Assets는 다음 두 파일뿐입니다.

```text
SamsungSwitchWatch-Agent-0.10.15-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.10.15-poc-win-x64.zip
```

## 검증 범위

자동 테스트는 production `AgentIdentityStore`가 만든 ECDSA 신원을 실제 Kestrel HTTPS
서버 소스와 통합해 Windows Schannel 연결을 검증하고, Setup 준비 상태가 일시 연결 실패 뒤
새 연결로 재시도하는지 확인합니다. 또한 서비스·포트 소유·HTTP 응답·API·프로토콜·제품
버전의 기존 fail-closed 완료 조건을 회귀 검증합니다.

이 자동 검증은 사내 EDR·백신·방화벽·Windows 서비스 전체 설치나 실제 삼성 스위치 펌웨어를
증명하지 않습니다. 공식 두 ZIP을 승인된 사내 시험 PC에서 단계적으로 확인해야 합니다.
