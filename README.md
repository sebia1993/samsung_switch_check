# Samsung Switch Watch

삼성 `IES4224GP`, `IES4028XP`, `IES4226XP` 스위치의 읽기 전용 Telnet 점검 결과를
원격 Windows PC에서 수집하고, 운영자 PC의 WPF 대시보드·미니 창·알림으로 변경점만
보여 주는 Windows 전용 모니터링 도구입니다.

```text
삼성 스위치 여러 대 ── Telnet/23 ──> SamsungSwitchWatch.Agent
                                          │ HTTPS/18443 + SignalR
                                          ▼
                                  SamsungSwitchWatch.Viewer
```

현재 버전은 `v0.5.1-poc`입니다. 실제 펌웨어 3종을 회사망에서 검증하기 전까지는
운영 확정판이 아닌 현장 검증용 프리릴리스로 취급해야 합니다.

## 핵심 기능

- Agent 하나에 최대 256개 스위치 등록, 기본 최대 4개 장비 병렬 점검
- 장비별 Telnet 세션 1개와 등록된 `show` 명령 ID만 실행
- `show port status`, `show syslog tail num 100` 우선 실행과 모델별 자동 대체 명령
- 짧은 VTY 유지시간을 고려한 세션 분할, 완료 결과 보존과 남은 명령 1회 재접속
- 신규 로그, 상태 변경, 장애 지속, 복구, 재시작과 로그 기준선 초기화 감지
- 모델·펌웨어별 미지원 명령을 장비 장애와 분리한 capability 상태
- API v3 cursor catch-up과 SignalR 실시간 이벤트, v1/v2 호환 유지
- Agent/API/실시간/DB/인증서/수집기 상태를 구분한 Figma 기반 대시보드
- 항상 위 미니 창, 시스템 트레이, Critical 우선 Windows 알림과 인앱 fallback
- 검색 및 전체·미확인·새 로그·장애·복구 필터
- IP·호스트·사용자·MAC·원문을 제외하고 장비 ID를 익명화한 CSV·JSON 내보내기
- DPAPI 자격 증명·원문 보호, 토큰 수명/폐기/교체, 인증서 만료·dual-pin 관리
- `SSW1:` 연결 문자열 한 번으로 주소·인증서 검증·일회용 페어링을 끝내는 Viewer 마법사
- 설치 receipt, 작업 journal, rollback, 패키지 매니페스트, SBOM과 SHA-256 검증

## 개발과 검증

Windows x64와 `global.json`에 고정된 .NET 10 SDK가 필요합니다.

```powershell
dotnet restore .\SamsungSwitchWatch.sln --locked-mode
.\scripts\validate.ps1 -Configuration Release
```

Viewer는 설정이 없으면 민감정보가 없는 데모 경로로 실행할 수 있습니다. 실제 회사망
자료 없이도 합성 Telnet 출력, mock Agent와 패키지 계약으로 대부분의 동작을 검증합니다.

## 릴리스 패키지

깨끗한 Git 작업 트리에서 다음 명령을 실행합니다.

```powershell
.\scripts\build-release.ps1 -Version 0.5.1-poc
```

`artifacts\release`에는 패키지와 내부 검증 파일 6개가 만들어집니다.

- `SamsungSwitchWatch-Agent-0.5.1-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.5.1-poc-win-x64.zip`
- `BUILD-MANIFEST.json`
- `SBOM.spdx.json`, `SBOM.cdx.json`
- `SHA256SUMS.txt`

공식 GitHub Release의 사용자 정의 Assets에는 설치에 필요한 Agent ZIP과 Viewer ZIP만
게시합니다. 나머지 4개 파일은 Actions 내부 검증에만 사용하며, 각 ZIP 안에는 해당 패키지의
빌드 매니페스트와 SBOM이 포함됩니다. Agent ZIP 루트에는 다중 장비 등록용
`switches.example.json` 예제도 포함됩니다.

배포본은 Windows x64 self-contained 단일 실행 파일이므로 대상 PC에 .NET이나 Python을
별도로 설치하지 않습니다. `-poc` 산출물은 서명 인증서가 없으면 서명되지 않으므로
공식 GitHub Release의 build provenance와 release attestation을 반드시 확인하십시오.

## 문서

- [설치·복구·페어링](docs/INSTALL_KO.md)
- [아키텍처와 API](docs/ARCHITECTURE.md)
- [보안 설계](docs/SECURITY.md)
- [현장 POC 체크리스트](docs/FIELD_POC_CHECKLIST_KO.md)
- [릴리스 절차](docs/RELEASE_PROCESS_KO.md)
- [0.5.1-poc 릴리스 노트](docs/RELEASE_NOTES_0.5.1_POC_KO.md)
- [Figma handoff](docs/figma/README.md)

## 보안 경계

Telnet 자체는 암호화되지 않습니다. Agent와 스위치는 격리된 관리망에 두고 스위치 ACL로
Agent IPv4 주소만 TCP/23 접근을 허용하십시오. 비밀번호, 토큰, 인증서 개인키, 실제 IP,
호스트명, MAC, 시리얼과 회사 로그를 저장소·이슈·외부 진단 자료에 넣지 마십시오.

Viewer는 임의 CLI 문자열이나 Telnet 자격 증명을 받지 않으며, Telnet 원문은 Agent PC
밖으로 전송하지 않습니다. 실제 스위치 설정 변경·재부팅·로그 삭제 기능은 포함하지 않습니다.

## 디자인

Figma source of truth: [Samsung Switch Watch](https://www.figma.com/design/JueYiLj18xFE7enHvGlU2s)

구현 기준 노드와 검증 상태는 [docs/figma/figma-state.json](docs/figma/figma-state.json)에
기록되어 있습니다.
