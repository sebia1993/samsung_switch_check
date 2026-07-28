# Samsung Switch Watch 0.10.0-poc 릴리스 노트

## 요약

이번 버전은 실행 흐름을 다음 두 역할로 단순화합니다.

```text
Agent PC: 최초 1회 Agent Setup → 창 없는 Windows 서비스
Viewer PC: 포터블 Viewer 실행 → 장비 등록·show 명령·결과 확인
```

Agent는 장비와 계정 정보를 저장하지 않는 실행 중계이고, Viewer가 장비·자격 증명·감시
일정과 결과를 소유하는 기존 API v4 구조를 유지합니다.

## 주요 변경

### Agent Setup

- `SamsungSwitchWatch.Agent.Setup.exe`를 공개 설치·업데이트 진입점으로 제공합니다.
- 설치 시 UAC를 한 번 승인하고 고정 Viewer IPv4 한 개를 입력합니다.
- 활성 어댑터에서 자동 검색한 직접 연결 사설 관리망 중 1~2개를 선택합니다.
- 서비스, HTTPS/18443, 방화벽과 준비 상태를 같은 화면에서 단계별로 검사합니다.
- 설치 뒤 Agent는 창이나 트레이 아이콘이 없는 `SamsungSwitchWatchAgent` 서비스로만
  실행합니다.
- PowerShell 실행 정책에 의존하지 않습니다.

### 포터블 Viewer

- Viewer는 ZIP을 풀고 `SamsungSwitchWatch.Viewer.exe`를 직접 실행합니다.
- 별도 설치, UAC, 시작 메뉴 등록과 Windows 로그인 자동 시작을 수행하지 않습니다.
- 0.9 설치형 Viewer가 실행 중이면 동시 실행하지 않고 기존 트레이 종료와
  `shell:startup`·`shell:programs` 바로 가기 제거 순서를 안내합니다.
- Agent 주소 연결 진단을 주소, TCP/18443, HTTPS, API, 버전 단계로 나눠 표시합니다.
- 같은 Release의 Agent와 Viewer 조합을 명시적으로 확인합니다.
- 인증서 SHA-256 지문과 페어링 토큰을 사용자가 입력하지 않습니다.

### 공개 패키지

- GitHub Release Assets는 버전이 붙은 Agent ZIP과 Viewer ZIP 정확히 두 개입니다.
- 게시 워크플로가 두 ZIP의 SHA-256을 이 Release 본문 끝에 자동으로 추가합니다.
- 두 ZIP에서 PowerShell, CMD와 레거시 설치 파일을 제외했습니다.
- Agent ZIP에는 Agent Setup, Agent 서비스 실행 파일, 필요한 네이티브 런타임,
  BUILD-MANIFEST, SBOM과 사용자 문서만 포함합니다.
- Viewer ZIP에는 Viewer 실행 파일, 필요한 네이티브 런타임, BUILD-MANIFEST, SBOM과
  사용자 문서만 포함합니다.

## 유지되는 동작과 보안 경계

- Viewer가 장비 IPv4, 모델, ID, 로그인 PW와 선택적 enable PW를 입력합니다.
- 자격 증명은 Viewer PC의 현재 Windows 사용자 DPAPI로 보호합니다.
- Agent는 장비·자격 증명·명령·원문 결과·감시 이력을 저장하지 않습니다.
- Viewer가 종료되면 주기 감시도 중단됩니다.
- 한 줄 `show` 명령만 허용하고 설정 변경 명령과 구분자는 Viewer와 Agent 양쪽에서
  차단합니다.
- Viewer→Agent는 HTTPS/TCP 18443, Agent→스위치는 선택 관리망의 Telnet/TCP 23만
  사용합니다.
- 애플리케이션 로그인은 추가하지 않았습니다. 고정 Viewer IPv4 방화벽 제한이 Agent API의
  접근 경계입니다.

## 현장 확인 필요

다음 항목은 Mock·Fixture로만 확인했으며 실제 사내 장비 검증이 필요합니다.

- IES4224GP, IES4028XP, IES4226XP의 로그인·enable·프롬프트 차이
- `show port status` 출력 형식
- `show syslog tail num 100`과 `show sylog tail num 100` 지원 여부
- 5분 VTY timeout 환경의 세션 종료와 제한적 재접속 동작
- 사내 방화벽, EDR, AppLocker 또는 WDAC에서 서명되지 않은 POC 실행 파일 허용 여부

첫 적용에서는 Agent 한 대, 고정 Viewer 한 대와 영향이 적은 스위치 한 대만 연결한 뒤
읽기 전용 명령으로 단계적으로 확대하십시오.
