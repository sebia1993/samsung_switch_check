# Samsung Switch Watch 0.10.5-poc 릴리스 노트

이번 버전은 Agent Setup이 Viewer 전용 Windows 방화벽 규칙을 만든 뒤 같은 규칙을 잘못
불일치로 판단하여 설치를 rollback하던 호환성 문제를 수정합니다. Agent와 Viewer의 운영 흐름,
장비 접속 정책과 저장 형식은 변경하지 않습니다.

## 문제 원인

Setup은 입력한 Viewer IPv4를 `ViewerIPv4/32`로 제한해 방화벽 규칙을 만듭니다. Windows
방화벽 COM API는 같은 단일 호스트 규칙을 다시 읽을 때 다음처럼 dotted netmask 형식으로
정규화할 수 있습니다.

```text
적용 요청: ViewerIPv4/32
조회 결과: ViewerIPv4/255.255.255.255
```

두 값은 같은 한 대의 Viewer만 허용하지만, 이전 Setup은 문자열이 완전히 같은지만 확인하여
`SETUP_FIREWALL_FAILED`로 설치를 중단할 수 있었습니다. 이 실패는 Agent 서비스가 정상
가동되기 전에 rollback되므로 Viewer에는 이어서 `AGENT_CONNECTION_REFUSED`가 보일 수
있었습니다.

## 변경된 검증

Setup은 현재 입력한 Viewer IPv4와 정확히 같은 단일 호스트의 다음 세 표현만 동등하게
인정합니다.

```text
ViewerIPv4
ViewerIPv4/32
ViewerIPv4/255.255.255.255
```

다음 값은 계속 거부합니다.

- 다른 IPv4
- `/0`부터 `/31`까지의 넓은 prefix
- `255.255.255.255` 이외의 dotted mask
- 여러 주소와 주소 범위
- `Any`, `LocalSubnet`
- IPv6와 잘못된 주소

원격 주소 표현 외에도 다음 조건은 모두 정확히 일치해야 합니다.

- 규칙 활성
- Inbound
- Allow
- TCP
- LocalPort 18443
- Domain과 Private 프로필만
- Edge Traversal 비활성

## Windows 반영 지연과 실패 처리

규칙 적용 직후에는 즉시 확인하고, Windows가 변경을 늦게 반환하는 경우를 위해 200ms
간격으로 최대 2초까지만 다시 읽습니다. 무한 대기하거나 제한 없이 규칙을 다시 만들지
않습니다.

제한 시간 안에 안전 기준을 만족하지 못하면 Setup은 성공으로 표시하지 않습니다.

1. 상위 오류 `SETUP_FIREWALL_FAILED`를 유지합니다.
2. `FIREWALL_REMOTE_ADDRESS_MISMATCH` 같은 안전한 불일치 코드만 표시합니다.
3. 실제 Viewer IPv4, 다른 규칙 주소와 방화벽 원문은 오류에 포함하지 않습니다.
4. 프로그램·서비스·방화벽을 설치 전 snapshot으로 rollback합니다.

사용자는 오류를 피하려고 Windows 방화벽 규칙을 `Any`, `LocalSubnet`, 사설망 전체 또는
넓은 prefix로 직접 변경하지 않아야 합니다. rollback 완료와 오류 코드를 확인한 뒤 같은
Release의 Agent Setup에서 `검사`를 다시 실행합니다.

## 유지되는 동작

- Agent PC에는 창이나 트레이 아이콘 없이 `SamsungSwitchWatchAgent` Windows 서비스가
  실행됩니다.
- Viewer에서 Agent PC 주소, 장비 IP·ID·PW·enable PW와 한 줄 `show` 명령을 관리합니다.
- Viewer→Agent는 HTTPS/TCP 18443을 사용합니다.
- 제품 소유 방화벽 규칙과 Agent API는 같은 Viewer IPv4 한 개를 각각 확인합니다.
- 다른 프로그램 소유의 TCP/18443 인바운드 규칙은 삭제·비활성화·변경하지 않습니다.
- Agent→스위치는 Setup에서 확정한 관리망과 Telnet/TCP 23만 허용합니다.
- 장비 설정 변경 명령, 인증서 지문 입력과 페어링 토큰 입력은 추가하지 않았습니다.
- 장비·자격 증명·명령 요청·응답과 Viewer 저장 형식은 변경하지 않았습니다.

## 검증 범위

- 단일 호스트 세 표현의 동등성
- 다른 prefix, 주소 목록·범위와 특수 범위 거부
- 나머지 방화벽 필드의 strict 비교
- 즉시 확인과 제한된 지연 재확인
- 지연 후 성공과 제한 시간 초과 rollback
- 오류 코드의 실제 IP와 규칙 원문 미포함
- Windows 메모리 내 `HNetCfg.FWRule` 객체의 `/32` 적용과 dotted netmask 조회
- 기존 Agent Setup, Viewer, Core 회귀 테스트와 Windows 패키지 계약

메모리 내 COM 객체 검증은 실제 방화벽 정책에 규칙을 등록하지 않습니다. 실제 사내 PC의
그룹 정책, 방화벽 서비스, EDR·백신, UAC 계정 전환과 Viewer 원격 연결은 현장에서
읽기 전용 절차로 확인해야 합니다. Mock과 로컬 검증 통과를 현장 검증 완료로 간주하지
않습니다.

## Figma 화면 기준

- 기본 Agent Setup: node `46:72`
- 방화벽 규칙 검증 실패와 자동 rollback: node `49:72`

## 호환성과 적용 순서

- Agent와 Viewer는 같은 `0.10.5-poc` Release의 ZIP을 함께 사용하십시오.
- Agent PC에서 새 Setup을 실행하고 `검사`와 `설치/업데이트` 완료를 확인합니다.
- Viewer PC에서 같은 Release의 Viewer를 실행하고 Agent 연결 진단을 수행합니다.
- 먼저 Viewer 한 대와 영향이 적은 장비 한 대에서 확인한 뒤 대상을 단계적으로 확대합니다.

## 공개 Assets

- `SamsungSwitchWatch-Agent-0.10.5-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.5-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 사용자 정의
Release Assets는 위 두 ZIP만 게시합니다.
