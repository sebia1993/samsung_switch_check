# 불변 릴리스 절차

Samsung Switch Watch의 GitHub 릴리스는 검증된 **annotated tag push**에서만 게시됩니다. `workflow_dispatch`는 동일한 패키지를 빌드하고 다운로드 검증까지 수행하지만 GitHub Release를 만들거나 기존 자산을 변경하지 않습니다.

## 릴리스 전 조건

- `global.json`의 정확한 .NET SDK 버전(현재 `10.0.302`)을 사용합니다.
- 모든 `packages.lock.json`과 `packages.win-x64.lock.json`이 현재 프로젝트 파일과 일치해야 합니다.
- 작업 트리가 깨끗해야 하며 `scripts/validate.ps1 -Configuration Release`가 통과해야 합니다.
- 저장소의 GitHub Immutable Releases 설정이 관리자에 의해 활성화되어 있어야 합니다.
- `refs/tags/v*`의 업데이트와 삭제를 제한하는 활성 태그 ruleset이 있어야 합니다. 새 태그
  생성은 허용하되, 생성된 릴리스 태그를 이동하거나 삭제할 수 없어야 합니다.
- `docs/RELEASE_NOTES_<VERSION>_KO.md`가 현재 버전과 정확히 일치해야 합니다. 과거 버전
  릴리스 노트를 대신 사용하는 fallback은 허용하지 않습니다.
- 서명 인증서가 없으면 버전에는 반드시 `-poc`가 포함되어야 합니다. 이 경우 릴리스는 prerelease로 게시됩니다.
- 정식 버전은 `release-signing` GitHub Environment의 `SSW_SIGNING_PFX_BASE64`, `SSW_SIGNING_CERTIFICATE_PASSWORD` secret으로 Authenticode 서명과 타임스탬프를 완료해야 합니다.

## 게시 방법

예를 들어 `0.6.0-poc`를 게시할 때는 검증된 커밋에 annotated tag를 만들고 한 번만 push합니다.

```powershell
git tag -a v0.6.0-poc -m "Samsung Switch Watch 0.6.0 POC"
git push origin v0.6.0-poc
```

워크플로는 같은 태그의 GitHub Release가 이미 존재하면 즉시 실패합니다. `--clobber`를 사용하지 않으며, 기존 자산을 교체하거나 추가하는 경로도 제공하지 않습니다. 변경이 필요하면 소스와 버전을 올려 새 태그로 게시합니다.

## 산출물 계약

GitHub Release의 사용자 정의 Assets에는 다음 두 파일만 포함됩니다.

- `SamsungSwitchWatch-Agent-<VERSION>-win-x64.zip`
- `SamsungSwitchWatch-Viewer-<VERSION>-win-x64.zip`

빌드 과정의 내부 Actions artifact에는 ZIP 2개와 `BUILD-MANIFEST.json`, SPDX/CycloneDX
SBOM, `SHA256SUMS.txt`까지 정확히 6개 파일을 유지합니다. 이 내부 파일로 업로드 전 계약을
검사하고, Actions artifact를 다시 다운로드해 digest와 기대한 소스 커밋을 재검증합니다.
각 ZIP 안에도 실행 파일과 스크립트의 SHA-256, 소스 커밋, SDK, 서명 상태가 기록된 빌드
매니페스트와 두 SBOM이 포함됩니다.

태그 릴리스에서는 공개 ZIP 두 개만 build provenance 대상으로 지정하고 signer workflow,
source commit, source tag를 검증합니다. 검증된 ZIP은 먼저 draft에 업로드하고 GitHub가
계산한 두 자산의 digest·크기와 로컬
파일을 대조합니다. draft 생성 직후 목록 API 반영이 늦을 수 있으므로 생성 자체는 다시
시도하지 않고, 생성 시 반환된 URL과 일치하는 항목의 조회만 제한된 시간 동안 재시도합니다.
찾은 draft는 숫자 release ID로 다시 조회해 태그·URL·draft 상태를 고정한 뒤 모든 후속
검증과 게시에 같은 ID만 사용합니다. 그 뒤 태그 객체와 peeled commit을 다시 확인하고
draft를 게시합니다.
마지막으로 `immutable=true`, GitHub release attestation, 두 ZIP의 `verify-asset` 결과와 게시 후
태그를 확인합니다. 게시를 시도하기 전에 실패한 경우에도 숫자 ID·태그·생성 URL·draft
상태가 모두 다시 일치할 때만 그 ID를 자동 삭제합니다. 정확한 ID를 확정하지 못했거나 게시
API를 시도한 뒤에는 응답이나 조회 상태가 불확실해도 자동 삭제하지 않습니다. 실패 뒤
release 또는 draft가 남았다면 불변성과 attestation을 관리자 권한으로 먼저 확인하고, 삭제
가능 여부와 관계없이 원인을 수정한 새 버전 태그를 사용합니다.

## 사용자 검증

다운로드한 파일은 같은 폴더에서 다음처럼 확인할 수 있습니다.

```powershell
Get-FileHash .\SamsungSwitchWatch-*-win-x64.zip -Algorithm SHA256
gh attestation verify .\SamsungSwitchWatch-Agent-0.6.0-poc-win-x64.zip `
  --repo sebia1993/samsung_switch_check `
  --signer-workflow sebia1993/samsung_switch_check/.github/workflows/release.yml `
  --source-digest <릴리스-커밋-SHA> `
  --source-ref refs/tags/v0.6.0-poc `
  --deny-self-hosted-runners
gh release verify v0.6.0-poc --repo sebia1993/samsung_switch_check
gh release verify-asset v0.6.0-poc `
  .\SamsungSwitchWatch-Agent-0.6.0-poc-win-x64.zip `
  --repo sebia1993/samsung_switch_check
```

해시가 다르거나 attestation의 repository/commit이 기대값과 다르면 설치하지 않습니다.
