# Samsung Switch Watch 0.9.8-poc 릴리스 노트

## 핵심 변경

이번 버전은 Viewer가 Agent에 보내는 HTTP 식별 버전을 실제 실행 빌드와 자동으로
일치시키는 진단 정확성 개선 릴리스입니다.

- 제어 API와 Telnet 조회 API의 `User-Agent`에서 오래된 고정 버전 `0.8`을 제거했습니다.
- .NET 빌드의 Informational Version을 사용해 `SamsungSwitchWatch.Viewer/0.9.8-poc`
  형식으로 전송합니다.
- 빌드 메타데이터의 커밋 SHA는 User-Agent에 포함하지 않습니다.
- 비정상적인 버전 문자열이 들어오면 유효한 숫자 Assembly Version으로 안전하게
  대체해 Viewer 시작 실패를 방지합니다.
- Agent 제어 요청과 Telnet 조회 요청이 같은 User-Agent를 사용하는지 자동화 테스트로
  검증합니다.
- 패키지 내부 매니페스트의 실행 파일 Product Version이 릴리스 버전과 소스 커밋에
  정확히 일치하는지 검증합니다.

## 호환성

- Agent API, HTTPS 신뢰 방식, Telnet 연결, 장비 설정과 감시 데이터 형식은 변경하지
  않았습니다.
- User-Agent는 Agent의 인증이나 허용 판단에 사용되지 않으므로 기존 Agent와 그대로
  연결됩니다.
- `CurrentUserSecretProtector`의 `v0.8` 문자열은 기존 암호화 데이터 복호화 호환성
  값이므로 변경하지 않았습니다.
- 장비에 전달되는 명령, 접속 주기와 세션 제한은 변경하지 않았습니다.

## 검증

- Informational Version의 `+커밋` 메타데이터 제거 테스트
- 빈 값, 공백과 잘못된 버전의 안전한 폴백 테스트
- 제어 채널과 Telnet 조회 채널 User-Agent 일치 테스트
- 패키지 매니페스트 Product Version 계약 테스트
- Core, Agent, Viewer 전체 자동화 테스트
- Mock Agent EXE 통신 테스트
- Windows CI 패키지 재다운로드 및 해시 검증

## 알려진 제한

- 실제 IES4224GP, IES4028XP, IES4226XP 펌웨어에서 검증하기 전까지는 POC 상태입니다.
- 실제 사내 EDR, 방화벽, 사용자 권한과 장시간 운영은 사내 단계에서 확인해야 합니다.
- Telnet 구간은 평문이므로 격리된 사내 관리망에서만 사용하십시오.
- 공개 `poc` 빌드는 조직 코드 서명이 없으며 사내 EDR 또는 WDAC 승인이 필요할 수
  있습니다.

## 배포 파일

공식 GitHub Release의 Assets에는 다음 두 파일만 게시합니다.

- `SamsungSwitchWatch-Agent-0.9.8-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.8-poc-win-x64.zip`
