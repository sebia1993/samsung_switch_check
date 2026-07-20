# Security Model

## 보호 대상

- 스위치 Telnet 사용자명과 비밀번호
- Agent API Bearer 토큰
- Agent 서버 인증서와 개인키
- 실제 장비 IP·호스트명·MAC·시리얼 및 원문 명령 출력

## 적용 통제

- 장비 자격 증명은 Agent PC에만 보관하고 Windows DPAPI를 사용합니다.
- Agent 프로그램·PFX·TokenPepper와 데이터 폴더 ACL은 SYSTEM, Administrators, LocalService로 제한합니다.
- Viewer 페어링은 일회용 코드로 수행하고 완료된 코드는 재사용할 수 없습니다.
- Viewer는 최초 승인된 인증서 SHA-256 지문을 고정하고 불일치 시 `TLS_PIN_MISMATCH`로 연결을 중단합니다.
- Bearer 토큰은 Agent에서 평문으로 저장하지 않습니다.
- 원문 명령 결과는 Agent의 로컬 보관 정책 안에서만 유지합니다.
- 로그, API, 진단 번들은 비밀정보와 네트워크 식별자를 마스킹합니다.
- 명령 실행은 모델 프로파일의 허용 목록(allowlist)에 등록된 `show` 명령 ID로 제한합니다.

## Telnet 제약

Telnet 세션은 암호화되지 않으므로 다음 조건이 필수입니다.

- Agent와 스위치는 외부망이나 일반 사용자 VLAN을 통과하지 않는 관리망에 둡니다.
- 가능한 경우 스위치 ACL로 Agent IP 한 개만 TCP/23을 허용합니다.
- 별도 읽기 전용 계정을 사용하고 계정 공유를 최소화합니다.
- 운영 PC에 패킷 캡처·디버그 로그·원문 내보내기를 남기지 않습니다.

## 안정 오류 코드

- `TCP_TIMEOUT`
- `TELNET_NEGOTIATION_FAILED`
- `LOGIN_PROMPT_NOT_FOUND`
- `AUTH_FAILED`
- `COMMAND_TIMEOUT`
- `PROMPT_PARSE_FAILED`
- `OUTPUT_LIMIT_EXCEEDED`
- `PARSER_UNSUPPORTED`
- `TLS_PIN_MISMATCH`
- `STORAGE_WRITE_FAILED`

지원 요청 시 실제 출력 대신 단계, 오류 코드, 소요 시간, 모델/펌웨어의 비식별 버전만 전달합니다.

## 운영 배포 전 남은 통제

POC 배포본은 코드 서명이 없습니다. 실제 운영 배포 전 사내 코드 서명 인증서로 Agent·Viewer·PowerShell 스크립트를 서명하고, 배포 해시(hash)를 변경 관리 기록에 남기세요.
