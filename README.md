# Samsung Switch Watch

삼성 `IES4224GP` 스위치의 읽기 전용 Telnet 조회 결과를 원격 Windows PC에서 수집하고, 운영자 PC의 WPF 대시보드·항상 위 미니 창·팝업으로 변경점만 보여 주는 Windows 전용 POC입니다.

```text
IES4224GP -- Telnet/23 --> SamsungSwitchWatch.Agent
                               |  HTTPS/18443 + SignalR
                               v
                         SamsungSwitchWatch.Viewer
```

현재 구현 범위는 한 대의 `IES4224GP`, 한 명의 Viewer 운영자, 직접 HTTPS 연결입니다. 실제 장비 없이도 안전하게 확인할 수 있도록 데모/모의(Mock) 경로와 합성 Telnet 테스트를 기본으로 제공합니다.

## 핵심 동작

- 등록된 읽기 전용 명령 ID만 실행합니다. 자유 CLI 입력은 없습니다.
- 첫 수집은 기준선으로 저장하고 기존 로그 알림을 만들지 않습니다.
- 이후 새 로그, `UP -> DOWN`, `DOWN -> UP`, 재부팅, 로그 순환을 구분합니다.
- 같은 장애는 반복 알림하지 않고 `신규 -> 확인됨 -> 복구됨`으로 관리합니다.
- Viewer가 꺼져 있던 동안의 이벤트는 마지막 시퀀스 다음부터 다시 동기화합니다.
- 원문 Telnet 출력과 스위치 자격 증명은 Agent PC 밖으로 전송하지 않습니다.
- Agent 연결이 끊기면 스위치를 DOWN으로 오판하지 않고 마지막 상태를 `미확인`으로 표시합니다.

## 개발 시작

필수 환경은 Windows x64와 .NET 10 SDK입니다.

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet restore .\SamsungSwitchWatch.sln
dotnet build .\SamsungSwitchWatch.sln -c Release --no-restore
dotnet test .\SamsungSwitchWatch.sln -c Release --no-build
```

Viewer는 기본 데모 모드에서 회사망·장비·계정 없이 실행할 수 있습니다. 실제 연결 전에는 [현장 POC 체크리스트](docs/FIELD_POC_CHECKLIST_KO.md)를 따르세요.

## 릴리스 생성

```powershell
.\scripts\validate.ps1
.\scripts\build-release.ps1
```

생성물은 `artifacts` 아래에 만들어지며 Git에 포함하지 않습니다. 배포본은 `win-x64`, self-contained, single-file, trimming 비활성화 기준입니다.

## 프로젝트 구조

- `src/SamsungSwitchWatch.Core`: Telnet/IAC, 정규화, 모델별 파서, diff/event 엔진
- `src/SamsungSwitchWatch.Agent`: Windows 서비스, SQLite, HTTPS API, SignalR, 폴링
- `src/SamsungSwitchWatch.Viewer`: WPF 대시보드, 미니 창, 팝업, 시스템 트레이
- `tests`: 합성 출력과 mock transport 기반 테스트
- `scripts`: PowerShell 5.1 호환 검증·배포·서비스 운영 스크립트
- `docs`: 설치, 보안, 아키텍처, 현장 검증 문서

## 디자인

Figma 파일: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)

Figma는 한글 렌더링을 위해 Noto Sans KR을 사용하고, 실제 Windows 앱은 기본 설치 글꼴인 Segoe UI를 사용합니다. 색상·간격·크기·상태 의미는 동일하게 유지합니다.

## 보안 주의

Telnet은 암호화되지 않습니다. Agent PC와 스위치는 통제된 관리망 안에 있어야 하며, 가능하면 스위치 ACL에서 Agent IP만 TCP/23 접근을 허용해야 합니다. 비밀번호, 인증서, 실제 IP·호스트명·MAC·회사 로그를 저장소나 진단 내보내기에 넣지 마세요.

자세한 내용은 [보안 설계](docs/SECURITY.md)와 [설치 안내](docs/INSTALL_KO.md)를 참조하세요.
