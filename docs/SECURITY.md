# Security Model

## 보호 대상과 로컬 경계

- 스위치 Telnet 사용자명과 비밀번호
- Agent API Bearer 토큰
- Agent 서버 인증서와 개인키
- 실제 장비 IP·호스트명·MAC·시리얼 및 원문 명령 출력

장비 자격 증명은 Agent PC에만 두고 Windows DPAPI로 보호합니다. Agent 프로그램·PFX·TokenPepper와 데이터 폴더는 SYSTEM, Administrators와 `SamsungSwitchWatchAgent` 서비스 SID만 접근하도록 ACL을 제한합니다. SQLite 파일은 네트워크 공유에 두지 않습니다.

Viewer는 일회용 코드로 페어링하고 승인된 인증서 SHA-256 지문을 고정합니다. 불일치는 `TLS_PIN_MISMATCH`로 중단하며 Bearer 토큰은 Agent에서 평문으로 저장하지 않습니다.

## Telnet과 명령 통제

Telnet은 암호화되지 않으므로 다음 조건이 필수입니다.

- Agent와 스위치는 외부망이나 일반 사용자 VLAN을 통과하지 않는 IPv4 관리망에 둡니다.
- 가능한 경우 스위치 ACL로 Agent IP 한 개만 TCP/23을 허용합니다.
- 별도 읽기 전용 계정을 사용하고 계정 공유를 최소화합니다.
- 모델 프로파일에 등록된 읽기 전용 `show` 명령만 허용합니다.
- 개행, NUL, 제어문자와 명령 연결 문자를 포함한 입력은 거부합니다.
- `AUTH_FAILED` 후에는 자격 증명을 갱신·검증하기 전까지 자동 로그인을 중단합니다.

## 진단과 안정 오류 코드

- `TCP_TIMEOUT`
- `TELNET_NEGOTIATION_FAILED`
- `LOGIN_PROMPT_NOT_FOUND`
- `AUTH_FAILED`
- `COMMAND_TIMEOUT`
- `PROMPT_PARSE_FAILED`
- `OUTPUT_LIMIT_EXCEEDED`
- `INCOMPLETE_OUTPUT`
- `PARSER_UNSUPPORTED`
- `COLLECTOR_UNUSABLE`

운영 모드에서는 HTTP pairing bootstrap을 제공하지 않습니다. Viewer 페어링 코드는 Agent PC의 관리자 PowerShell에서 `new-pairing-code.ps1`로만 생성합니다.
- `IPV6_UNSUPPORTED`
- `CREDENTIAL_CORRUPT`
- `TLS_PIN_MISMATCH`
- `STORAGE_WRITE_FAILED`

로그, API와 반출 가능한 진단에는 단계, 오류 코드, 소요 시간과 비식별 버전만 포함합니다. 실제 주소·계정·원문·인증서·토큰은 포함하지 않습니다.

## POC 배포 제한

`v0.2.0-poc` Agent·Viewer·PowerShell 스크립트는 코드 서명되지 않았습니다. 공식 GitHub Release의 `SHA256SUMS.txt`로 무결성을 확인하고 사내 승인된 PC에서만 실행하십시오. 운영 전에는 사내 코드 서명, 신뢰 가능한 서버 인증서, 실제 펌웨어 검증과 장시간 soak test가 별도로 필요합니다.
