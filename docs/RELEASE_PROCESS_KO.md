# Samsung Switch Watch 릴리스 절차

## 릴리스 계약

- 현재 버전: `0.11.1-poc`
- 태그: annotated tag `v0.11.1-poc`
- 대상: Windows x64, self-contained, single-file managed publish, untrimmed
- GitHub Release 사용자 정의 Asset: Agent ZIP과 Viewer ZIP 정확히 두 개
- 공개 패키지: PowerShell·CMD·개발 설정·DB·인증정보 제외
- 내부 Actions artifact: 검증 파일 정확히 여섯 개

공개 Asset:

```text
SamsungSwitchWatch-Agent-0.11.1-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.1-poc-win-x64.zip
```

내부 검증 파일:

```text
SamsungSwitchWatch-Agent-0.11.1-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.1-poc-win-x64.zip
BUILD-MANIFEST.json
SBOM.spdx.json
SBOM.cdx.json
SHA256SUMS.txt
```

내부 네 파일은 Actions artifact와 검증에만 사용하고 GitHub Release의 사용자 정의 Asset으로
올리지 않습니다. 게시 워크플로는 두 공개 ZIP의 SHA-256을 계산해 Release 본문 끝에 자동으로
추가하므로, 운영자는 별도 해시 파일 없이 해당 본문과 다운로드 파일을 비교합니다.

## 릴리스 전 확인

깨끗한 `main` 작업 트리에서 실행합니다.

```powershell
git status --short
git ls-files AGENTS.md
dotnet --version
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
.\scripts\validate.ps1 -Configuration Release
```

추가 확인:

- 실제 IP, 호스트명, ID, PW, 인증서, 장비 출력과 회사 로그가 추적되지 않음
- Agent Setup, Agent와 Viewer가 같은 제품 버전을 가짐
- 사용자 매뉴얼 생성 소스의 버전과 흐름이 현재 릴리스와 일치함
- 최종 DOCX/PDF를 새로 생성하고 화면과 페이지를 확인함
- 공개 ZIP에 `.ps1`, `.cmd`, `.bat`, 개발 설정, PDB, DOCX가 없음
- `BUILD-MANIFEST.json`이 ZIP의 모든 파일 해시를 포함함
- Agent ZIP의 기본 진입점이 `SamsungSwitchWatch.Agent.Setup.exe`임
- Viewer ZIP은 `SamsungSwitchWatch.Viewer.exe`를 직접 실행하는 포터블 패키지임
- 미완료 Agent 트랜잭션에서 설치가 차단되고 별도 `이전 상태 복구`만 허용됨
- 복구 정리는 검증된 staging·backup·failed·journal 경로만 최대 3회 시도하고 실패한 시도 사이 250ms 대기함
- 각 정리 대상 삭제와 새 작업 기록 검사가 모두 통과해야만 복구 성공과 설치 활성화로 표시됨
- 복구 성공 뒤 설치가 자동 실행되지 않고, 실패 시 최초 원인과 복구 대상별 단계 원인이 분리됨
- 복구 대상별 진단이 `ROLLBACK_STAGING_CLEANUP_FAILED`,
  `ROLLBACK_BACKUP_CLEANUP_FAILED`, `ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`,
  `ROLLBACK_JOURNAL_CLEANUP_FAILED` 중 하나로 표시되고 실제 경로는 노출하지 않음
- 실패 전용 `진단정보 복사`에 실제 IP·경로·사용자·자격 증명·방화벽 원문이 없음
- Agent Setup과 Viewer의 `익명 진단 저장`이 수동으로만 동작하고
  `SSW_FIELD_DIAGNOSTIC/2` 허용 필드만 최대 12줄·줄당 88자로 기록함
- 과거 `SSW_FIELD_DIAGNOSTIC/1` 파일도 재현 도구에서 계속 검증함
- 익명 진단에 IP/CIDR·PC/사용자명·계정·인증서·경로·예외 원문·장비 명령/출력이 없음
- Agent Setup과 Viewer 실패 화면에서만 유효한 SWD1 지원 코드가 표시되고 성공·재시도·
  입력 변경 때 이전 코드가 지워짐
- SWD1이 오프라인 생성·해석되고 CRC 오타 검사를 통과하며 인증·페어링·비밀값으로 사용되지 않음
- 실행 중인 API v4 Agent의 최소 readiness 응답은 사전 점검에서 호환되며, Viewer는 같은
  API v4의 제품 버전 차이를 경고로 표시한 뒤 연결함
- production Agent가 시작마다 새 RSA 인증서와 PFX 바이트를 만들고 Exportable·PersistKeySet
  없이 UserKeySet으로 가져오며, 프로세스 종료 뒤 임시 사용자 키 컨테이너가 남지 않는지
  Schannel TLS 서버 통합 테스트로 검증함
- Setup 준비 상태 재시도는 매번 새 HTTP handler/client, 정확한 HTTP/1.1과 `Connection: close`를
  사용하며 실패한 TLS 연결 상태를 다음 시도에 재사용하지 않음
- 파일과 서비스 설치를 commit한 뒤 로컬 HTTPS 준비 상태를 확인하지 못해도 rollback하지 않고
  `AGENT_LOCAL_CONNECTION_UNCONFIRMED` 성공·경고로 남기며 Viewer 연결 진단을 안내함
- 제품 소유 Domain/Private·TCP/18443·RFC1918 원격 주소 방화벽 규칙을 best effort로 적용하며,
  방화벽·GPO·적용·재조회만 실패하면 변경한 방화벽 상태 복원을 시도하고 결과를 경고 단계에 남긴 뒤
  `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고로
  Agent 프로그램과 서비스를 유지함
- 방화벽 경고 완료 화면은 `설치 완료 · 원격 Viewer 연결 확인 필요`, 정확한 규칙까지 확인한
  완료 화면은 `설치 완료 · 원격 연결 준비됨`으로 서로 구분됨
- 방화벽 실패 경로가 `Any`, `LocalSubnet` 또는 RFC1918보다 넓은 규칙을 만들거나 Agent 내부의
  loopback/RFC1918 Viewer 출발지 제한을 완화하지 않음
- 예상하지 못한 Setup 실패가 안전한 단계·범주와 제한된 시간으로 기록되며 예외 원문·경로·
  PID를 노출하거나 성공으로 처리하지 않음
- 로컬 준비 상태 실패가 `HTTPS_TLS_FAILED`, `HTTPS_REQUEST_TIMEOUT`,
  `HTTPS_CONNECTION_RESET`, `HTTPS_EOF`, `HTTPS_CONNECT_FAILED`로 구분되고 화면에서
  `Setup → 127.0.0.1:18443 → Agent 서비스` 내부 구간임을 명확히 안내함
- 익명 진단에는 서비스 실행, TCP/18443 수신 소유, HTTPS 시도 횟수·마지막 전송 단계와
  재시작 관측 여부만 추가되고 PID·주소·경로·인증서·예외 원문은 없음
- 설치 후 준비 상태 실패 단계가 `SERVICE_STARTED` → `SETUP_HEALTH_FAILED` →
  `ROLLBACK_COMPLETED` 순으로 기록되고 readiness 대기와 rollback 시간이 분리됨
- 세부 HTTPS 실패의 SWD1 값은 하위 호환 `HTTPS_REQUEST_FAILED` 범주를 유지함
- Viewer가 첫 자동 수집 전과 작업 경합으로 미뤄진 수집을 `확인 대기`로 표시하고, 연결
  단절 시 현재 확인 불가와 마지막 확인 상태를 구분함
- Viewer 연결 교체·종료 시 진행 중 요청을 취소하고 이전 HTTP 자원을 제한된 시간 안에서
  정리하며 정리 실패를 비식별 코드로 구분함
- Viewer TCP 단계 실패는 방화벽·GPO·라우팅 확인을, TCP 성공 뒤 HTTPS 단계 실패는 Agent PC의
  로컬 TLS/readiness 확인을 안내함

## 사용자 매뉴얼 갱신

운영 흐름, 화면 또는 버전이 바뀌면 다음 순서로 사용자 매뉴얼을 다시 만듭니다.

1. `tools/build-user-manual.py`의 현재 버전과 내용을 갱신합니다.
2. 비식별 WPF 화면 9개를 다시 캡처합니다.
3. 생성 스크립트로 DOCX를 만듭니다.
4. 프로젝트의 문서 렌더링 절차로 PDF와 QA 페이지 PNG를 만듭니다.
5. 모든 페이지를 100% 배율로 확인하여 잘림, 깨짐과 오래된 설치 흐름이 없는지 검사합니다.
6. 최종 DOCX, PDF와 문서에 사용한 비식별 화면 9개를 저장소에 반영합니다.

Agent Setup 흐름이 바뀐 Release에서는 매뉴얼과 캡처가 다음 내용을 모두 보여야 합니다.

- 미완료 작업을 읽기 전용으로 감지하고 설치를 비활성화하는 상태
- 복구 가능한 상태에서만 표시되는 별도 `이전 상태 복구`
- 복구 성공 뒤 설치가 자동으로 시작되지 않는 상태
- 삭제 API 성공 뒤 대상 잔존 또는 새 작업 기록 잔존을 복구 성공으로 표시하지 않는 상태
- 복구 자료 정리 실패에서 작업 기록 보존, 복구 재시도와 익명 진단 저장을 안내하는 상태
- 손상되거나 안전성을 증명할 수 없는 상태의 관리자 안내
- 최초 설치 실패와 staging·backup·failed·journal 복구 단계 실패가 분리된 결과
- 실패 화면에서만 사용할 수 있는 `진단정보 복사`와 민감정보 제외 범위
- 성공 또는 실패한 점검 뒤 사용할 수 있는 `익명 진단 저장`과 자동 저장 금지
- Agent Setup 실패와 Viewer 연결 실패 화면의 선택 가능한 SWD1 지원 코드
- SWD1, `진단정보 복사`, `익명 진단 저장`의 용도 차이
- staging·backup·failed 폴더와 작업 기록을 수동으로 정리하지 말라는 안내

```powershell
dotnet restore .\tools\SamsungSwitchWatch.ManualCapture\SamsungSwitchWatch.ManualCapture.csproj --locked-mode
dotnet run --project .\tools\SamsungSwitchWatch.ManualCapture\SamsungSwitchWatch.ManualCapture.csproj `
  -c Release --no-restore -- .\docs\manual\images
python .\tools\build-user-manual.py `
  --output .\docs\SamsungSwitchWatch_User_Manual_KO.docx `
  --images .\docs\manual\images
python .\tools\render-user-manual-pdf.py `
  --input .\docs\SamsungSwitchWatch_User_Manual_KO.docx `
  --output .\docs\SamsungSwitchWatch_User_Manual_KO.pdf `
  --render-dir .\tmp\manual-render-0.11.1
```

DOCX는 저장소 편집 원본이고 공개 패키지에는 넣지 않습니다. PDF는 두 ZIP에 포함합니다.
QA 페이지 PNG는 시각 검사 후 임시 폴더에만 두며 커밋하지 않습니다.

## 로컬 패키지 생성

```powershell
.\scripts\build-release.ps1 -Version 0.11.1-poc
```

진단용 dirty 빌드는 게시하지 않습니다.

```powershell
.\scripts\build-release.ps1 -Version 0.11.1-poc -AllowDirty
```

빌드 스크립트는 다음 순서로 실행됩니다.

1. 잠금 복원과 전체 검증
2. Agent, Agent Setup과 Viewer의 win-x64 self-contained publish
3. Agent Setup의 필요한 WPF 네이티브 런타임을 Agent 패키지에 병합
4. 정확한 버전의 릴리스 노트, 설치 안내와 최종 PDF 포함
5. SPDX·CycloneDX SBOM 생성
6. 선택적 Authenticode 서명
7. 패키지 BUILD-MANIFEST와 루트 매니페스트·해시 생성
8. 두 ZIP 생성과 `test-package-contract.ps1` 실행

서명되지 않은 빌드는 `-poc` 버전만 허용합니다. 코드 서명 인증서와 암호는 저장소나 명령줄에
기록하지 않습니다.

## Agent ZIP 계약

Agent ZIP의 정확한 파일 집합:

```text
SamsungSwitchWatch.Agent.Setup.exe
SamsungSwitchWatch.Agent.exe
D3DCompiler_47_cor3.dll
PenImc_cor3.dll
PresentationNative_cor3.dll
vcruntime140_cor3.dll
wpfgfx_cor3.dll
INSTALL_KO.md
SamsungSwitchWatch_User_Manual_KO.pdf
RELEASE_NOTES_0.11.1_POC_KO.md
BUILD-MANIFEST.json
SBOM.spdx.json
SBOM.cdx.json
```

`SamsungSwitchWatch.Agent.Setup.exe`는 공개 설치·업데이트·검사 진입점입니다. Agent 서비스
실행 파일은 운영자가 직접 실행하지 않습니다. PowerShell/CMD 배포 스크립트는 레거시 복구와
개발 테스트를 위해 소스에만 유지합니다.

BUILD-MANIFEST의 기본 실행 파일은 Agent Setup이며 Agent 서비스 실행 파일도 같은
`version+commit` 제품 버전을 가져야 합니다.

## Viewer ZIP 계약

Viewer ZIP의 정확한 파일 집합:

```text
SamsungSwitchWatch.Viewer.exe
D3DCompiler_47_cor3.dll
PenImc_cor3.dll
PresentationNative_cor3.dll
vcruntime140_cor3.dll
wpfgfx_cor3.dll
INSTALL_KO.md
SamsungSwitchWatch_User_Manual_KO.pdf
RELEASE_NOTES_0.11.1_POC_KO.md
BUILD-MANIFEST.json
SBOM.spdx.json
SBOM.cdx.json
```

Viewer는 설치하지 않고 압축 해제한 폴더에서 EXE를 직접 실행합니다. 공개 패키지는 UAC,
시작 메뉴, 바로 가기와 자동 시작을 구성하지 않습니다.

## 패키지 계약 검증

패키지 계약과 워크플로 계약은 일반 개발 셸에서도 실행할 수 있습니다.

```powershell
$commit = (git rev-parse HEAD).Trim()
.\scripts\test-package-contract.ps1 `
  -ReleaseDirectory .\artifacts\release `
  -Version 0.11.1-poc `
  -ExpectedSourceCommit $commit
.\scripts\test-release-workflow-contract.ps1
```

실행 파일 smoke는 관리자 권한이 있는 Windows CI 또는 승인된 시험 PC에서 실행합니다.

```powershell
.\scripts\test-release-executable-smoke.ps1 `
  -ReleaseDirectory .\artifacts\release `
  -Version 0.11.1-poc
```

검사는 다음 조건을 fail-closed로 확인합니다.

- 내부 파일 정확히 여섯 개와 공개 ZIP 정확히 두 개
- ZIP 경로 traversal 부재
- Agent·Viewer ZIP의 정확한 파일 이름 집합
- PowerShell/CMD와 민감 파일 부재
- 제품 버전, 소스 commit, 파일 크기와 SHA-256 일치
- SPDX 2.3과 CycloneDX 1.6 형식
- 서명 상태와 POC 버전 정책
- GitHub Release의 공개 allowlist와 내부 검증 파일 분리
- 압축 해제한 Viewer, Mock Agent와 Agent Setup 실행 파일의 제한된 시작·종료 smoke 검사

실행 파일 smoke는 실제 삼성 스위치 Telnet, 사내 EDR/백신, 원격 PC 사이의 방화벽·라우팅을
검증하지 않습니다. Agent Setup의 전체 Windows 서비스·방화벽 설치 및 rollback 통합도 이
패키지 smoke의 범위가 아니므로 승인된 시험 PC에서 별도로 확인합니다. 로컬 비관리자 실행
결과를 전체 설치 검증으로 표현하지 않습니다.

## 태그와 게시

```powershell
git tag -a v0.11.1-poc -m "Samsung Switch Watch v0.11.1-poc"
git push origin v0.11.1-poc
```

Release workflow는 태그가 `origin/main`에 포함되고 annotated tag의 객체와 peeled commit이
변하지 않았는지 확인합니다. 두 공개 ZIP에 provenance를 발급한 뒤 draft에 정확히 한 번
업로드하고 크기·SHA-256을 검증한 후 게시합니다.

기존 태그나 Release Asset을 교체하지 않습니다. 같은 버전에 문제가 있으면 새 버전과 새
불변 태그를 만듭니다.

## 게시 후 확인

```powershell
$tag = 'v0.11.1-poc'
$expected = @(
  'SamsungSwitchWatch-Agent-0.11.1-poc-win-x64.zip',
  'SamsungSwitchWatch-Viewer-0.11.1-poc-win-x64.zip'
) | Sort-Object
$release = gh release view $tag --json isDraft,isPrerelease,assets,url | ConvertFrom-Json
$actual = @($release.assets | ForEach-Object { $_.name } | Sort-Object)
if ($release.isDraft -or -not $release.isPrerelease) { throw 'Release 상태가 올바르지 않습니다.' }
if (($actual -join '|') -ne ($expected -join '|')) { throw 'Release Asset 계약 위반입니다.' }
gh release verify $tag
foreach ($name in $expected) {
  gh release verify-asset $tag (Join-Path .\artifacts\release $name)
}
```

현장에는 공식 Release의 두 ZIP만 전달합니다. Agent를 먼저 설치 또는 업데이트한 뒤 Viewer의
`Agent 연결 테스트`로 연결을 확인하고, 같은 Release의 Viewer를 사용합니다. Setup이 연결 확인
경고를 표시해도 Agent 서비스 설치는 유지되므로 Viewer 진단 결과를 기준으로 판단합니다.
