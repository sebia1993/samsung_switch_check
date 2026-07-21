# Security

## v0.6 Agent API의 명시적 제한

`v0.6.0-poc`의 Agent–Viewer 연결은 일반 `HTTP`이며 TLS 암호화와 사용자 인증이 없습니다.
인증서 SHA-256 pin, 페어링 코드, Bearer 토큰 기능은 제거되었습니다. 따라서 Agent API에
도달할 수 있는 호스트는 상태 조회뿐 아니라 수동 점검 요청과 이벤트 확인 API도 사용할 수
있습니다.

Windows 방화벽의 정확한 고정 Viewer IPv4 허용 목록이 유일한 API 접근 통제입니다.

- Agent를 격리된 사내 관리망에만 배치합니다.
- 인터넷, 일반 사용자 VLAN, 공용 Wi-Fi와 신뢰하지 않는 라우팅 경로에 노출하지 않습니다.
- DHCP로 자주 바뀌는 Viewer 주소를 쓰지 않습니다.
- 설치기의 제품 소유 방화벽 규칙을 수동으로 `Any` 또는 서브넷 전체로 넓히지 않습니다.
- 허용 주소 변경에는 `set-viewer-access.ps1`을 사용합니다.

이 단순화는 사용성 우선 POC 결정입니다. 통신 기밀성·서버 신원·사용자별 권한이나 감사가
필요한 운영 환경에서는 이 버전을 사용하지 말고 TLS와 인증을 다시 설계해야 합니다.

## 보호 대상

- 스위치 Telnet 사용자명·비밀번호
- Telnet 원문, 실제 장비 주소·호스트명·MAC·시리얼·회사 로그
- Agent SQLite DB, WAL/SHM, 설치 설정과 장비 인벤토리

이 값은 저장소, GitHub 이슈, 테스트 fixture, 일반 진단 JSON과 Viewer export에 넣지 않습니다.

## Telnet 경계

Telnet도 암호화되지 않습니다. Agent와 스위치는 격리된 관리 VLAN에 두고 스위치 ACL에서
Agent 고정 IPv4만 TCP/23 접근을 허용하십시오. 인터넷·일반 사용자 VLAN·공용 Wi-Fi를
통과하는 구성은 지원하지 않습니다.

## 최소 권한과 명령 안전성

- 스위치 계정은 설정 변경이 불가능한 조회 전용 계정을 사용합니다.
- Agent는 모델 프로파일에 등록된 `show` 명령만 실행합니다.
- Viewer와 API는 임의 CLI 문자열을 받을 수 없습니다.
- `configure`, `interface`, `shutdown`, `reload`, `erase`, `write`, `copy`, `delete`,
  `debug`, `test`, shell separator와 제어문자는 차단합니다.
- Agent 서비스는 `LocalService`와 서비스 SID 전용 ACL로 실행합니다.

## 저장 보호

스위치 자격 증명과 Telnet 원문은 Windows DPAPI `LocalMachine` scope로 보호합니다. 데이터
디렉터리 ACL은 서비스 SID와 관리자로 제한합니다. DB만 다른 PC로 복사해서는 복호화할 수
없습니다.

원문은 Agent 전용이며 API와 Viewer로 반환하지 않습니다. 기본 보존은 7일·500MB입니다.
Viewer export는 실제 device ID 대신 익명 별칭을 사용하며 IP, 사용자명과 원문을 제외합니다.

schema v5 마이그레이션은 모니터링 snapshot, event, raw, audit와 자격 증명을 보존하면서
더 이상 쓰지 않는 pairing/token 테이블만 제거합니다. 설치 Repair는 DB·WAL/SHM을 먼저
백업하고 readiness 검증 실패 시 복원합니다.

## 설치와 방화벽 소유권

- 신규·실환경 설치는 1~32개의 고정 Viewer IPv4 입력 없이는 진행되지 않습니다.
- CIDR, 서브넷, DNS 이름, 축약형·16진수·선행 0 IPv4 표기는 방화벽 입력으로 거부합니다.
- Agent는 HTTP/18443에서 수신하고 제품 규칙은 Domain/Private 프로필의 고정 IPv4만 허용합니다.
- Windows Defender Firewall 서비스와 Domain/Private/Public 프로필이 모두 활성화되어야 하며,
  기본 인바운드 정책이 `Allow`인 프로필이 있으면 설치·수정·최초 시작을 중단합니다.
- 같은 포트에 적용될 수 있는 제품 외 인바운드 Allow 규칙이 하나라도 있으면 자동 작업을 중단합니다.
- 설치기와 제거기는 이름·그룹·설명이 일치하는 제품 소유 규칙만 변경합니다.
- 설치·수정·최초 시작은 설정과 receipt의 Agent ID·장비 수·인벤토리 hash를 확인하고,
  적용 직후에도 방화벽 범위와 프로필을 다시 검증합니다.
- `set-viewer-access.ps1`은 설치 설정의 사용자 지정 데이터 경로를 따르고 방화벽 범위 검증과
  receipt 원자 교체를 수행하며 실패 시 복원합니다.
- v0.5 Repair는 검증 성공 뒤에만 구 서비스 인증서 secret을 제거합니다.
- v0.5의 현재 인증서와 rotation 이전 인증서는 receipt·설정의 thumbprint와 SHA-256,
  installer-owned 표식, 저장소 FriendlyName이 모두 맞을 때만 Repair 또는 제거 과정에서 삭제하며
  사용자 소유 인증서는 보존합니다.

## 진단과 오류

외부 전달 가능한 진단에는 단계, 안정 오류 코드, 소요 시간, 비식별 버전만 포함합니다.
예: `TCP_TIMEOUT`, `LOGIN_PROMPT_NOT_FOUND`, `AUTH_FAILED`, `COMMAND_TIMEOUT`,
`PROMPT_PARSE_FAILED`, `PARSER_UNSUPPORTED`, `AGENT_HTTP_UNREACHABLE`,
`STORAGE_WRITE_FAILED`.

원문이 꼭 필요한 현장 분석은 회사 PC에만 보관하고 외부 반출 전에 별도 승인을 받으십시오.

## 공급망 제한

여기서 제거한 인증서는 Agent–Viewer 통신용 TLS 인증서입니다. 릴리스 파일의 Authenticode
코드 서명 인증서와 GitHub build provenance/release attestation은 별도 공급망 보호이며 계속
유지합니다. `-poc` 패키지는 코드 서명 인증서가 없으면 서명되지 않을 수 있으므로 공식
GitHub Release의 Agent·Viewer ZIP만 받고 각 ZIP의 provenance와 attestation을 확인하십시오.
공개 Release Assets는 ZIP 두 개뿐이며, 내부 Actions artifact는 ZIP 2개와 매니페스트·SBOM
2종·SHA256SUMS를 포함한 정확히 6개 파일입니다.
