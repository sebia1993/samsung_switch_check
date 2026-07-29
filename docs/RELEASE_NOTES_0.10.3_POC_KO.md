# Samsung Switch Watch 0.10.3-poc 릴리스 노트

이번 버전은 Agent Setup이 다른 프로그램 소유의 TCP/18443 인바운드 허용 규칙을 발견했을 때
설치를 막던 문제를 개선합니다. 외부 규칙은 그대로 보존하고, Windows 방화벽과 Agent 내부
Viewer IPv4 검증을 함께 사용합니다.

## 사용자 흐름

1. Agent Setup에 Viewer PC의 고정 IPv4와 스위치 관리망을 입력합니다.
2. `사전 점검`에서 외부 TCP/18443 허용 규칙이 발견되면 노란색
   `FIREWALL_OVERLAP_PROTECTED` 경고를 확인합니다.
3. 기존 `설치 / 업데이트` 버튼을 그대로 사용합니다.
4. Setup은 외부 규칙을 삭제·비활성화·변경하지 않습니다.
5. 제품 소유 Viewer `/32` 방화벽 규칙과 Agent의 `AllowedViewerIpv4`를 함께 적용합니다.

별도의 자동 수정 버튼이나 인증서 지문·페어링 토큰 입력은 추가하지 않았습니다.

## Agent 접근 제한

- 운영 모드에서는 정확한 RFC1918 Viewer IPv4 한 개가 필수입니다.
- 실제 TCP 연결의 원격 주소가 등록 Viewer IPv4와 일치해야 전체 Agent API를 사용할 수
  있습니다.
- IPv4-mapped IPv6 주소는 IPv4로 정규화해 비교합니다.
- `X-Forwarded-For` 같은 전달 헤더는 신뢰하지 않습니다.
- 로컬 루프백은 `/health/live`와 `/health/ready`만 사용할 수 있습니다.
- Setup의 설치 후 점검은 `/health/ready` 응답에서 API·프로토콜·제품 버전을 함께 확인하므로
  로컬 `/api/v4/identity` 접근을 열지 않습니다.
- 등록하지 않은 주소나 주소를 확인할 수 없는 요청은
  `403 / AGENT_CLIENT_NOT_ALLOWED`로 거부합니다.
- Viewer는 이 오류를 받으면 Agent PC에서 Setup을 다시 실행하고 현재 Viewer 고정 IPv4로
  설치/업데이트하라는 안내를 표시합니다.

다른 프로그램의 넓은 허용 규칙이 남아 있으면 허용되지 않은 PC도 TLS 연결 시도 자체는 할 수
있지만 Agent API는 403으로 거부됩니다. 불필요한 외부 규칙은 해당 규칙 소유 부서에서 별도로
검토해야 합니다.

## 계속 설치를 차단하는 조건

- Windows 방화벽 서비스 또는 활성 프로필 방화벽 비활성
- 활성 프로필의 기본 인바운드 정책이 Allow
- Public 프로필만 활성
- 그룹 정책의 로컬 방화벽 규칙 병합 차단
- 제품 전용 방화벽 규칙 이름을 비소유 규칙이 사용

PowerShell 기반 구형 설치기가 만든 제품 규칙은 이름, 설명, 그룹, 방향, 프로토콜, 포트,
프로필과 적용 범위를 모두 확인한 경우에만 제품 소유 규칙으로 인정합니다.

## 호환성과 제한

- Agent와 Viewer는 같은 `0.10.3-poc` Release의 ZIP을 함께 사용하십시오.
- 장비 API, 장비 설정 저장 형식, Viewer 데이터와 Telnet 조회 흐름은 변경하지 않았습니다.
- 실제 삼성 스위치, 사내 방화벽·EDR·그룹 정책과 UAC 계정 전환은 현장 검증이 필요합니다.
- Mock과 합성 네트워크 테스트 통과를 실제 펌웨어 검증으로 간주하지 않습니다.
- 운영 장비 설정은 변경하지 않으며 읽기 전용 `show` 명령만 사용합니다.

## 공개 Assets

- `SamsungSwitchWatch-Agent-0.10.3-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.3-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 사용자 정의
Release Assets는 위 두 ZIP만 게시합니다.
