# Samsung Switch Watch 0.10.6-poc 릴리스 노트

이번 버전은 Agent와 Viewer를 한 PC에 함께 설치해 원격 배치 전에 연결 구조를 확인하는
사전 테스트 흐름을 추가합니다. 테스트를 위해 `localhost`나 넓은 방화벽 범위를 허용하지
않으며, 기존 `/32` 방화벽과 Agent 내부 Viewer IPv4 검증을 그대로 사용합니다.

## 동일 PC 사전 테스트

Agent Setup에는 `이 PC 주소 넣기`, Viewer의 Agent 연결 창에는
`이 PC에서 사전 테스트`가 추가되었습니다.

```text
같은 PC
Agent Setup에서 실제 사설 IPv4 선택
→ Agent 설치/업데이트
→ Viewer에서 동일 PC 사전 테스트를 명시적으로 실행
→ Agent 서비스·TCP/18443·HTTPS·API·버전 확인
```

- Setup은 활성 상태인 loopback·tunnel 이외 RFC1918 IPv4만 제안합니다.
- 주소가 하나이면 바로 입력하고, 여러 개이면 운영자가 사용할 주소를 선택합니다.
- Viewer 사전 테스트는 자동으로 실행되지 않고 사용자가 버튼을 눌렀을 때만 시작합니다.
- Viewer는 사설 IPv4 후보를 최대 6개, 후보당 최대 7초, 전체 최대 30초 동안 확인합니다.
- 첫 성공 후보만 연결 저장 대상으로 제안합니다.
- Agent와 Viewer는 같은 `0.10.6-poc` 버전이어야 합니다.

## 테스트가 확인하는 범위

성공 결과가 확인하는 항목:

- Agent Windows 서비스 응답
- TCP/18443 연결
- HTTPS 보호와 Agent 신원
- Agent API 준비 상태
- Agent·Viewer 제품 버전 일치

확인하지 않는 항목:

- 스위치 Telnet/TCP 23 연결
- 장비 ID·PW·enable PW
- `show` 명령과 출력
- 원격 Viewer PC에서 Agent PC까지의 라우팅
- 원격 Viewer 고정 IPv4의 방화벽 `/32` 경로

사전 테스트 중에는 장비 자격 증명을 복호화하거나 스위치에 접속하지 않으며 명령을
자동 실행하지 않습니다. 스위치는 연결 저장 후 Viewer의
`장비 관리 → 접속 시험`에서 별도로 확인합니다.

## localhost 처리

`localhost`, `localhost.`와 `127.0.0.0/8`은 동일 PC 시험에서도 Agent API 연결 주소로
허용하지 않습니다. 이전 Viewer 설정에 loopback 주소가 남아 있으면 삭제하지 않고
마이그레이션 안내를 표시하지만, 새 연결 저장과 실제 연결 시도는 차단합니다.

동일 PC 테스트는 Agent PC의 실제 RFC1918 사설 IPv4를 사용합니다. Agent의 제품 API는
등록한 `AllowedViewerIpv4`와 실제 연결의 원격 IPv4를 계속 정확히 비교합니다. loopback은
Setup의 로컬 설치 상태 확인용 `/health/live`와 `/health/ready`에만 유지됩니다.

## 원격 배치 전 필수 전환

동일 PC 사전 테스트 성공은 원격 배치 완료를 뜻하지 않습니다.

1. 원격 Viewer PC에서 사용할 고정 IPv4를 확인합니다.
2. Agent PC에서 Setup을 다시 열어 해당 원격 Viewer IPv4를 입력합니다.
3. `검사`와 `설치/업데이트`를 완료해 제품 방화벽 `/32`와 `AllowedViewerIpv4`를 갱신합니다.
4. 실제 원격 Viewer PC에서 Agent 연결 진단을 다시 실행합니다.
5. 영향이 적은 스위치 한 대에서 `접속 시험`과 읽기 전용 명령을 별도로 검증합니다.

## 유지되는 보안 경계

- Agent는 사용자 창이나 트레이 아이콘 없이 Windows 서비스로 실행됩니다.
- Viewer→Agent는 HTTPS/TCP 18443을 사용합니다.
- 제품 소유 방화벽 규칙은 Viewer IPv4 한 개의 `/32`만 허용합니다.
- Agent API는 실제 원격 IPv4가 `AllowedViewerIpv4`와 같은지 다시 확인합니다.
- Agent→스위치는 Setup에서 확정한 RFC1918 관리망과 Telnet/TCP 23만 허용합니다.
- 인증서 SHA-256 지문과 페어링 토큰을 사용자가 입력하지 않습니다.
- 동일 PC 테스트를 위해 API, 방화벽, 명령 정책을 완화하지 않습니다.
- 사전 테스트가 스위치 자격 증명이나 명령 원문을 저장하거나 전송하지 않습니다.

## 검증 범위

- Setup의 사설 IPv4 단일 후보 자동 입력과 복수 후보 선택
- loopback·tunnel·공인 IPv4 제외와 검색 실패 안내
- Viewer 사전 테스트의 명시적 실행과 제한된 후보·시간
- 동일한 5단계 Agent 연결 검사 재사용
- 성공 시 Agent/API 정상과 스위치·원격 경로 미확인 표시
- loopback 설정 마이그레이션 안내와 새 연결 거부
- 사전 테스트 중 Telnet 실행 없음
- 기존 Agent `/32` 방화벽, API 허용 주소와 패키지 계약 회귀

합성 네트워크 인터페이스와 Mock Agent 응답을 사용한 검증은 실제 사내 방화벽·라우팅과
스위치 펌웨어 검증을 대신하지 않습니다. 실제 PC의 네트워크 어댑터 선택, Windows 방화벽,
EDR·백신, 원격 Viewer 연결과 장비 접속은 현장에서 단계적으로 확인해야 합니다.

## 공개 Assets

- `SamsungSwitchWatch-Agent-0.10.6-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.6-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 사용자 정의
Release Assets는 위 두 ZIP만 게시합니다.
