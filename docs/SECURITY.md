# Security

## 보호 대상

- 스위치 Telnet 사용자명·비밀번호
- Agent HTTPS 인증서 개인키
- Viewer Bearer 토큰과 인증서 pin
- Telnet 원문, 실제 장비 주소·호스트명·MAC·시리얼·회사 로그
- Agent SQLite DB, WAL/SHM과 설치 설정

이 값은 저장소, GitHub 이슈, 테스트 fixture, 일반 진단 JSON과 Viewer export에 넣지 않습니다.

## 네트워크 경계

Telnet은 암호화되지 않습니다. Agent와 스위치는 격리된 관리 VLAN에 두고, 가능하면 스위치
ACL에서 Agent IPv4 주소만 TCP/23 접근을 허용하십시오. 인터넷·일반 사용자 VLAN·공용
Wi-Fi를 통과하는 구성은 지원하지 않습니다.

Agent API는 실환경에서 HTTPS가 필수입니다. 설치 스크립트는 Viewer 단일 IPv4 주소로
Windows 방화벽 범위를 제한할 수 있으며, 자신이 만든 규칙만 receipt에 기록하고 제거합니다.

## 최소 권한과 명령 안전성

- 스위치 계정은 설정 변경이 불가능한 조회 전용 계정을 사용합니다.
- Agent는 모델 프로파일에 등록된 `show` 명령만 실행합니다.
- Viewer와 API는 임의 CLI 문자열을 받을 수 없습니다.
- `configure`, `interface`, `shutdown`, `reload`, `erase`, `write`, `copy`, `delete`,
  `debug`, `test`, shell separator와 제어문자는 차단됩니다.
- Agent 서비스는 가능한 경우 `LocalService`와 전용 ACL로 실행합니다.
- Viewer와 TCP/23 연결에는 관리자 권한이 필요하지 않습니다.

## 저장 보호

스위치 자격 증명과 Telnet 원문은 Windows DPAPI `LocalMachine` scope로 보호합니다.
데이터 디렉터리 ACL은 서비스 SID와 관리자로 제한합니다. DB만 다른 PC로 복사해서는
복호화할 수 없습니다.

원문은 Agent 전용이며 API와 Viewer로 반환하지 않습니다. 기본 보존은 7일·500MB이고,
schema v4 업그레이드에서는 이전 버전이 평문으로 저장했을 가능성이 있는 원문을 폐기합니다.

Viewer 설정의 Bearer 토큰은 현재 Windows 사용자 DPAPI로 보호합니다. 내보내기에는 원문,
토큰, 인증서, 내부 URL과 실제 device ID를 포함하지 않고 `DEVICE-xxxxxxxx` 별칭을 사용합니다.

## 페어링과 토큰

- Agent의 `new-viewer-pairing.ps1`은 `SSW1:` 문자열에 Agent 주소, 공개 인증서 지문,
  일회용 코드와 만료 시각만 넣으며 최종 Bearer 토큰은 넣지 않습니다.
- Viewer는 연결 문자열에 포함된 pin으로 최초 HTTPS 응답을 검증한 뒤에만 코드를 교환합니다.
  원격 Agent 응답에서 받은 새 지문을 자동 신뢰하지 않습니다.
- 최종 토큰은 Viewer 내부에서 바로 DPAPI로 보호하며 기본 화면이나 로그에 표시하지 않습니다.
- 토큰 발급 뒤 HTTPS 사전 점검이 실패해도 DPAPI 저장을 먼저 완료하고 같은 창에서 재시도하여
  일회용 코드 재소비와 보이지 않는 토큰 한도 소진을 방지합니다.
- 페어링 HTTP는 redirect와 시스템 proxy를 사용하지 않고 연결 문자열의 Agent endpoint에만
  직접 전송하며, 성공 응답은 크기와 JSON 필드 집합을 제한합니다.
- 연결 문자열 생성 CLI는 Kestrel 인증서를 로드하지 않으므로 PFX 비밀번호를 읽지 않습니다.
- 일회용 페어링 코드는 기본 10분 뒤 만료되고 한 번만 소비됩니다.
- 코드 소비와 토큰 저장은 같은 DB 트랜잭션입니다.
- Viewer 토큰은 최대 5개, 절대 180일, 마지막 사용 후 60일이 기본입니다.
- 로컬 관리자 CLI에서 안전한 token ID만 조회·폐기·교체할 수 있습니다.
- 폐기는 validation cache 없이 다음 요청부터 즉시 적용됩니다.
- pairing exchange는 IP별 분당 5회 제한하며 오래된 bucket을 정리합니다.

```powershell
SamsungSwitchWatch.Agent.exe token list
SamsungSwitchWatch.Agent.exe token revoke <16자리-token-id>
SamsungSwitchWatch.Agent.exe token rotate <16자리-token-id>
```

교체 명령이 표시하는 새 토큰은 복구용 고급 설정에 즉시 적용하고 화면이나 파일에 남기지 마십시오.

## 인증서와 pin

신규 설치는 가능한 경우 `LocalMachine\My`의 비내보내기 개인키 인증서를 사용합니다.
기존 PFX는 복구 호환 경로로만 유지합니다.

- 만료 60일·30일·7일 상태를 별도 경고로 표시
- 만료되면 `/health/ready`가 `CERTIFICATE_EXPIRED`로 실패
- 회전 시 현재/예정 SHA-256 pin을 Viewer에 최대 2개까지 사전 등록
- 이전 인증서 pin 중첩 기간은 최대 14일
- Agent 응답에 나온 새 pin을 Viewer가 자동 신뢰하지 않음
- HTTPS와 SignalR WebSocket이 같은 pin 집합을 검증

인증서 지문은 공개 식별값이지만, 개인키·PFX 비밀번호는 비밀입니다.

## 진단과 오류

외부로 전달 가능한 진단에는 단계, 안정된 오류 코드, 소요 시간, 비식별 버전만 포함합니다.
예: `TCP_TIMEOUT`, `LOGIN_PROMPT_NOT_FOUND`, `AUTH_FAILED`, `COMMAND_TIMEOUT`,
`PROMPT_PARSE_FAILED`, `PARSER_UNSUPPORTED`, `TLS_PIN_MISMATCH`,
`CERTIFICATE_EXPIRED`, `STORAGE_WRITE_FAILED`.

원문이 꼭 필요한 현장 분석은 회사 PC에만 보관하고 외부 반출 전에 별도 승인을 받으십시오.

## POC 공급망 제한

`-poc` 패키지는 코드 서명 인증서가 없으면 서명되지 않습니다. 공식 GitHub Release의
Agent·Viewer ZIP만 받고 각 ZIP의 build provenance와 release attestation을 확인하십시오.
패키지 내부의 빌드 매니페스트와 SPDX/CycloneDX SBOM도 함께 확인할 수 있습니다. 실제 운영
전에는 사내 코드 서명, 신뢰 가능한 서버 인증서, 3모델 실제 펌웨어 검증과 장시간 soak
test가 별도로 필요합니다.
