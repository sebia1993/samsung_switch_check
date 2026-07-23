# Samsung Switch Watch 보안 설계

## 신뢰 경계

이 POC에는 세 가지 서로 다른 경계가 있습니다.

| 경계 | 보호 방식 | 남는 위험 |
|---|---|---|
| Viewer 로컬 저장소 | DPAPI CurrentUser | 같은 Windows 사용자 권한 탈취 |
| Viewer → Agent | HTTPS, 자동 Agent 신원 고정, 관리 CIDR 방화벽 | 애플리케이션 사용자 인증 없음 |
| Agent → 스위치 | 대상 CIDR 제한, TCP/23 고정, 한 줄 show 정책 | Telnet 평문 노출 |

HTTPS를 사용해도 같은 허용 관리 CIDR의 API 클라이언트는 Agent를 호출할 수 있습니다.
Agent PC를 사용자 VLAN, 공용 Wi-Fi, 인터넷 또는 신뢰하지 않는 프록시에 노출하면 안 됩니다.

## 자격 증명

장비 ID, 로그인 PW와 enable PW는 Viewer PC의 현재 Windows 사용자 범위 DPAPI로 암호화합니다.

- Viewer가 명령을 실행할 때만 메모리에서 복호화합니다.
- HTTPS 요청으로 Agent에 전달한 뒤 Agent 메모리에서만 사용합니다.
- Agent 설정, 파일, DB, 로그, 이벤트와 진단 JSON에 저장하지 않습니다.
- API 응답이나 오류 메시지에 반사하지 않습니다.
- Viewer 편집 화면은 기존 비밀번호를 다시 표시하지 않습니다.

DPAPI 파일을 다른 PC나 다른 Windows 사용자에게 복사해도 복호화할 수 없는 것이 정상입니다.
운영자 계정이 탈취되면 해당 사용자의 DPAPI 자료도 보호할 수 없으므로 Windows 로그인, 화면
잠금과 원격접속 권한을 별도로 관리해야 합니다.

## Agent HTTPS 신원

Agent는 최초 정상 시작 때 ECDSA P-256 키와 자체 서명 인증서를 생성합니다. 개인 키가 포함된
PFX 자료는 DataDirectory 아래에 영구 저장하고 DPAPI LocalMachine으로 보호합니다.

DPAPI LocalMachine만으로는 같은 컴퓨터의 다른 사용자가 파일을 읽는 상황을 충분히 막지
못합니다. 설치기는 `%ProgramData%\SamsungSwitchWatch` 전체에 폐쇄형 ACL을 적용합니다.

- `SYSTEM`: FullControl
- 로컬 `Administrators`: FullControl
- `SamsungSwitchWatchAgent` 서비스 SID: Modify
- 일반 Users와 로그인 사용자의 직접·상속 읽기 권한: 제거

설치기는 루트와 기존 하위 파일의 ACE를 다시 구성하고, 예상하지 않은 명시적·상속 규칙이
남아 있으면 설치를 실패시킵니다. 따라서 일반 PC 사용자는 PFX나 설치 영수증을 읽을 수
없습니다.

업데이트는 DataDirectory 전체를 동일하게 제한된 트랜잭션 폴더에 백업합니다. HTTPS
readiness 실패 시 이전 자료를 복구한 뒤 폐쇄형 ACL을 다시 적용합니다. 성공 시 설치
트랜잭션용 복제본만 제거합니다.

v0.7에서 v0.8로 이관할 때 기존 Agent 장비 목록 설정 사본, 자격 증명과 SQLite 원문·이력
자료는 자동 삭제하지 않습니다. `legacy-v0.7-backup-*` 폴더로 이동한 뒤 루트와 모든 하위
항목의 ACL을 SYSTEM과
Administrators 전용으로 다시 구성합니다. Agent 서비스 SID는 이 백업 ACL에 포함하지
않습니다. 보존 기간 종료 뒤 정리는 관리자가 사내 정책과 별도 승인을 확인해 수동으로
수행해야 합니다.

이전 현재 사용자 예약 작업을 서비스로 전환할 때도 작업 이름만 신뢰하지 않습니다. 현재
사용자 SID, 작업 설명, 실행 경로·인수, 설치 영수증, 패키지 매니페스트와 실행 파일·숨김
실행기·보존 설정 해시를 모두 확인합니다.
완전한 DPAPI LocalMachine HTTPS 신원 쌍은 새 DataDirectory로 복사하고, 이전 프로그램과
데이터 전체는 `legacy-background-backup-*`으로 이동해 SYSTEM·Administrators 전용 ACL을
적용합니다. 전환 실패 시 원래 ACL과 예약 작업 실행 상태를 복구합니다. 서비스와 예약 작업이
동시에 등록된 모호한 상태나 소유권이 불완전한 상태는 자동 변경하지 않습니다.

Viewer는 처음 연결한 Agent의 공개 신원을 자동 고정합니다. 이후 신원이 달라지면 연결을
중단하며, SHA-256 지문이나 페어링 토큰을 입력해 우회할 수 없습니다. 관리자가 Agent를
정식 재설치했다는 사실을 별도로 확인한 경우에만 Viewer의 `Agent 신뢰 다시 설정`을 사용합니다.

## 네트워크 제한

### 인바운드

설치기는 Windows Defender Firewall에 다음 제품 소유 규칙을 만듭니다.

```text
Name:       SamsungSwitchWatchAgent-Https
Direction:  Inbound
Protocol:   TCP
LocalPort:  18443
Remote:     설치 시 입력한 Viewer 관리 CIDR
Profiles:   Domain, Private
```

Public 프로필은 허용하지 않습니다. 설치기는 활성 방화벽 서비스, 각 프로필의 기본 인바운드
차단 정책과 TCP/18443에 겹치는 외부 Allow 규칙을 점검합니다. 제품 소유권을 확인할 수 없는
동일 이름 규칙은 수정하거나 삭제하지 않습니다.

### Agent의 Telnet 대상

Agent는 요청의 대상이 다음 조건을 모두 만족할 때만 연결합니다.

- canonical dotted IPv4
- 설치 설정의 `AllowedTargetCidrs` 안에 포함
- TCP 포트 23
- loopback, link-local, multicast와 기타 특수 범위가 아님

이 검증은 Viewer UI 검증과 별도로 Agent에서 매 요청 수행합니다. 이는 OS 아웃바운드
방화벽 규칙이 아니라 Agent 실행기의 필수 대상 allowlist입니다.

## 명령 정책

Agent는 Viewer가 보낸 문자열을 그대로 신뢰하지 않습니다. 정규화 후 다음 조건을 모두
만족하는 한 줄 `show` 명령만 실행합니다.

- `show` 단어로 시작
- 128자 이하
- CR/LF와 제어문자 없음
- `;`, `&`, `|` 및 여러 명령 연결 없음
- configure, interface, shutdown, reload, erase, write, copy 등 설정 문맥으로 전환하지 않음

`show running-config`는 읽기 명령으로 허용하지만 매우 민감한 결과를 만들 수 있습니다.
명령 문자열과 원문 출력은 Agent/Viewer DB, 파일 로그, 감사 이력과 내보내기에 저장하지
않습니다. 출력은 요청 Viewer 프로세스의 메모리에 최대 64KiB만 유지됩니다.

## 로그 및 진단

허용되는 진단 정보:

- 요청 ID
- 단계와 소요 시간
- 성공·실패와 sanitized 오류 코드
- 출력 바이트 수와 잘림 여부
- 서비스, HTTPS listener와 CIDR 설정 유효성

금지되는 진단 정보:

- 실제 장비 IP와 호스트명
- 계정 ID, 로그인 PW, enable PW
- 실행한 명령 문자열
- Telnet 원문과 `show running-config`
- 장비 MAC, 시리얼과 고객 식별 정보

대표 오류 코드는 `TARGET_NOT_ALLOWED`, `TCP_TIMEOUT`, `AUTH_FAILED`, `ENABLE_FAILED`,
`QUERY_COMMAND_BLOCKED`, `QUERY_RATE_LIMITED`, `COMMAND_TIMEOUT`,
`OUTPUT_LIMIT_EXCEEDED`, `PROMPT_PARSE_FAILED`입니다. 정상 응답이 64KiB에서 잘린 경우에는
오류로 바꾸지 않고 명령별 `truncated` 값을 표시합니다.

## 가용성과 세션

- Agent는 무창 `LocalService` Windows 서비스로 실행합니다.
- 일반 사용자가 닫을 창이나 트레이 종료 메뉴가 없습니다.
- 서비스 실패 시 5초, 15초, 60초 재시작 정책을 적용합니다.
- 장비마다 동시 세션 1개, 전체 최대 2개로 제한합니다.
- 요청 IP 기준 분당 최대 60회로 제한합니다.
- 요청 본문은 최대 32KiB이며 초과 시 바인딩 전에 거부합니다.
- 요청마다 새 Telnet 세션을 사용하고 각 세션을 최대 240초 뒤 강제 정리합니다.
- 명령 실행 중 원격 종료가 발생하면 완료된 명령은 반복하지 않고 남은 명령만 새 세션에서
  1회 재시도합니다.
- 인증·enable 실패, 명령 타임아웃과 사용자 취소는 자동 재시도하지 않습니다.

Viewer가 종료되면 감시도 중단되며 Agent는 독립적으로 장비를 조회하지 않습니다. 이 공백은
보안상 예상된 동작이지만 운영 가용성 요구와 별도로 평가해야 합니다.

## 알려진 POC 한계

- Telnet 구간은 암호화되지 않습니다.
- Agent API에는 Windows/AD 로그인이나 별도 애플리케이션 토큰이 없습니다.
- 자체 서명 Agent 신원의 첫 연결은 관리 CIDR과 운영자 판단을 신뢰합니다.
- 세 모델의 실제 펌웨어별 프롬프트와 페이징 처리는 현장 검증이 필요합니다.
- 코드 서명 인증서가 없는 `-poc` 패키지는 Windows 게시자 신뢰를 제공하지 않습니다.

이 한계를 수용할 수 없는 환경에는 배포하지 마십시오.
