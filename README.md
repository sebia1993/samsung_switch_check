# Samsung Switch Watch

삼성 `IES4224GP` 스위치의 읽기 전용 Telnet 조회 결과를 원격 Windows PC에서 수집하고, 운영자 PC의 WPF 대시보드·항상 위 미니 창·팝업으로 변경점만 보여 주는 Windows 전용 POC입니다.

```text
IES4224GP -- Telnet/23 --> SamsungSwitchWatch.Agent
                               |  HTTPS/18443 + SignalR
                               v
                         SamsungSwitchWatch.Viewer
```

`v0.2.0-poc` 범위는 한 대의 `IES4224GP`, 한 명의 Viewer 운영자, 직접 HTTPS 연결입니다. 실제 장비 없이 검증할 수 있는 데모·Mock 경로와 합성 Telnet 테스트를 제공합니다.

## 안정성 원칙

- 등록된 읽기 전용 명령 ID만 실행하며 자유 CLI 입력을 허용하지 않습니다.
- 최초 수집은 기준선으로 저장하고 기존 로그를 신규 알림으로 만들지 않습니다.
- 불완전하거나 파싱할 수 없는 출력은 마지막 정상 상태를 덮어쓰지 않습니다.
- 같은 장애는 반복 팝업하지 않고 생성·확인·복구 변경을 순서대로 기록합니다.
- Viewer는 재연결 후 v2 변경 피드를 페이지 단위로 읽어 누락 이벤트를 복구합니다.
- Agent 연결 단절은 스위치 Down과 구분하며 마지막 상태를 `현재 미확인`으로 표시합니다.
- 원문 Telnet 출력과 스위치 자격 증명은 Agent PC 밖으로 전송하지 않습니다.

## 개발과 검증

Windows x64와 .NET 10 SDK가 필요합니다.

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet restore .\SamsungSwitchWatch.sln
.\scripts\validate.ps1 -Configuration Release
```

Viewer는 기본 데모 모드로 회사망·장비·계정 없이 실행할 수 있습니다. 실제 연결 전에는 [현장 POC 체크리스트](docs/FIELD_POC_CHECKLIST_KO.md)를 따르세요.

## v0.2.0-poc 릴리스 생성

```powershell
.\scripts\build-release.ps1
```

`artifacts\release`에 다음 파일이 생성되고 패키지 구조와 SHA-256이 자동 검증됩니다.

- `SamsungSwitchWatch-Agent-0.2.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.2.0-poc-win-x64.zip`
- `SHA256SUMS.txt`

생성물은 Git에 포함하지 않습니다. 배포본은 `win-x64`, self-contained, single-file, trimming 비활성화 기준입니다. 자세한 변경점은 [릴리스 노트](docs/RELEASE_NOTES_0.2.0_POC_KO.md)를 참조하세요.

## 프로젝트 구조

- `src/SamsungSwitchWatch.Core`: Telnet/IAC, 정규화, 모델별 파서, diff/event 엔진
- `src/SamsungSwitchWatch.Agent`: Windows 서비스, SQLite, HTTPS API, SignalR, 폴링
- `src/SamsungSwitchWatch.Viewer`: WPF 대시보드, 미니 창, 팝업, 시스템 트레이
- `tests`: 합성 출력과 mock transport 기반 테스트
- `scripts`: PowerShell 5.1 호환 검증·패키징·설치·진단 스크립트
- `docs`: 설치, 보안, 아키텍처, 현장 검증 문서

## 디자인

Figma 파일: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)

## POC 보안 제한

Telnet은 암호화되지 않으며 현재 패키지는 코드 서명되지 않았습니다. Agent PC와 스위치를 통제된 IPv4 관리망에 두고, 가능하면 스위치 ACL에서 Agent IP만 TCP/23 접근을 허용하십시오. SmartScreen 경고가 표시될 수 있으므로 반드시 공식 GitHub Release의 SHA-256과 내려받은 파일을 대조하십시오.

비밀번호, 인증서, 실제 IP·호스트명·MAC·회사 로그를 저장소나 외부 진단 자료에 넣지 마세요. 자세한 내용은 [보안 설계](docs/SECURITY.md)와 [설치 안내](docs/INSTALL_KO.md)를 참조하세요.
