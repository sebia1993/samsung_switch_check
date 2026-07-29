# Samsung Switch Watch 릴리스 절차

## 릴리스 계약

- 현재 버전: `0.10.5-poc`
- 태그: annotated tag `v0.10.5-poc`
- 대상: Windows x64, self-contained, single-file managed publish, untrimmed
- GitHub Release 사용자 정의 Asset: Agent ZIP과 Viewer ZIP 정확히 두 개
- 공개 패키지: PowerShell·CMD·개발 설정·DB·인증정보 제외
- 내부 Actions artifact: 검증 파일 정확히 여섯 개

공개 Asset:

```text
SamsungSwitchWatch-Agent-0.10.5-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.10.5-poc-win-x64.zip
```

내부 검증 파일:

```text
SamsungSwitchWatch-Agent-0.10.5-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.10.5-poc-win-x64.zip
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

## 사용자 매뉴얼 갱신

운영 흐름, 화면 또는 버전이 바뀌면 다음 순서로 사용자 매뉴얼을 다시 만듭니다.

1. `tools/build-user-manual.py`의 현재 버전과 내용을 갱신합니다.
2. 비식별 WPF 화면 7개를 다시 캡처합니다.
3. 생성 스크립트로 DOCX를 만듭니다.
4. 프로젝트의 문서 렌더링 절차로 PDF와 QA 페이지 PNG를 만듭니다.
5. 모든 페이지를 100% 배율로 확인하여 잘림, 깨짐과 오래된 설치 흐름이 없는지 검사합니다.
6. 최종 DOCX, PDF와 문서에 사용한 비식별 화면 7개를 저장소에 반영합니다.

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
  --render-dir .\tmp\manual-render-0.10.5
```

DOCX는 저장소 편집 원본이고 공개 패키지에는 넣지 않습니다. PDF는 두 ZIP에 포함합니다.
QA 페이지 PNG는 시각 검사 후 임시 폴더에만 두며 커밋하지 않습니다.

## 로컬 패키지 생성

```powershell
.\scripts\build-release.ps1 -Version 0.10.5-poc
```

진단용 dirty 빌드는 게시하지 않습니다.

```powershell
.\scripts\build-release.ps1 -Version 0.10.5-poc -AllowDirty
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
RELEASE_NOTES_0.10.5_POC_KO.md
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
RELEASE_NOTES_0.10.5_POC_KO.md
BUILD-MANIFEST.json
SBOM.spdx.json
SBOM.cdx.json
```

Viewer는 설치하지 않고 압축 해제한 폴더에서 EXE를 직접 실행합니다. 공개 패키지는 UAC,
시작 메뉴, 바로 가기와 자동 시작을 구성하지 않습니다.

## 패키지 계약 검증

```powershell
$commit = (git rev-parse HEAD).Trim()
.\scripts\test-package-contract.ps1 `
  -ReleaseDirectory .\artifacts\release `
  -Version 0.10.5-poc `
  -ExpectedSourceCommit $commit
.\scripts\test-release-workflow-contract.ps1
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

## 태그와 게시

```powershell
git tag -a v0.10.5-poc -m "Samsung Switch Watch v0.10.5-poc"
git push origin v0.10.5-poc
```

Release workflow는 태그가 `origin/main`에 포함되고 annotated tag의 객체와 peeled commit이
변하지 않았는지 확인합니다. 두 공개 ZIP에 provenance를 발급한 뒤 draft에 정확히 한 번
업로드하고 크기·SHA-256을 검증한 후 게시합니다.

기존 태그나 Release Asset을 교체하지 않습니다. 같은 버전에 문제가 있으면 새 버전과 새
불변 태그를 만듭니다.

## 게시 후 확인

```powershell
$tag = 'v0.10.5-poc'
$expected = @(
  'SamsungSwitchWatch-Agent-0.10.5-poc-win-x64.zip',
  'SamsungSwitchWatch-Viewer-0.10.5-poc-win-x64.zip'
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

현장에는 공식 Release의 두 ZIP만 전달합니다. Agent를 먼저 업데이트하고 Setup 검사 성공을
확인한 뒤 같은 Release의 Viewer를 실행합니다.
