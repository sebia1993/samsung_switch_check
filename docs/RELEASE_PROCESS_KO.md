# Samsung Switch Watch 릴리스 절차

## 릴리스 계약

- 대상: Windows x64
- 런타임: .NET 10 self-contained, single-file, trimming 비활성
- 현재 버전: `0.9.10-poc`
- 태그: annotated tag `v0.9.10-poc`
- GitHub Release 사용자 정의 Asset: Agent ZIP과 Viewer ZIP, 정확히 두 개
- 기존 Release와 Asset은 교체하지 않는 immutable 방식

공개 파일:

```text
SamsungSwitchWatch-Agent-0.9.10-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.9.10-poc-win-x64.zip
```

Actions 내부 검증 산출물:

```text
SamsungSwitchWatch-Agent-0.9.10-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.9.10-poc-win-x64.zip
BUILD-MANIFEST.json
SBOM.spdx.json
SBOM.cdx.json
SHA256SUMS.txt
```

매니페스트, SBOM과 SHA256SUMS는 Actions 내부 검증과 각 ZIP 내부 확인에 사용하지만
GitHub Release의 별도 Assets로 올리지 않습니다.
최종 PDF 사용설명서는 두 ZIP 내부에 포함하고, 편집용 DOCX는 저장소에만 둡니다.

## 로컬 검증

깨끗한 `main` 작업 트리에서 실행합니다.

```powershell
git status --short
git branch --show-current
git remote -v
git ls-files AGENTS.md
.\scripts\validate.ps1 -Configuration Release
```

필수 확인:

- 실제 IP, ID, 비밀번호, 인증서, 회사 로그와 원문 출력이 추적되지 않음
- `docs/manual` 또는 로컬 캡처 자료가 검증 없이 패키지에 포함되지 않음
- Agent 패키지에 `set-switch-credential.ps1`, `set-viewer-access.ps1`,
  `switches.example.json`이 없음
- Agent 패키지에 `Install-or-Update-Agent.cmd`가 있음
- Viewer 패키지에 `Install-or-Update-Viewer.cmd`가 있음
- Agent 패키지에 현재 사용자 background 설치·실행·제거 스크립트와 loose
  `appsettings*.json`이 없음
- 기본 Agent 설정이 HTTPS/18443, 대상 CIDR, 무상태 실행기 구조임
- Viewer가 자격 증명과 감시 자료를 소유함

## 패키지 생성

```powershell
.\scripts\build-release.ps1 -Version 0.9.10-poc
```

스크립트는 다음을 수행합니다.

1. locked restore, build, test, format, PowerShell 계약 검사
2. Agent와 Viewer를 self-contained single EXE로 publish
3. 현재 버전 릴리스 노트와 최종 PDF 사용설명서를 각 ZIP에 포함
4. SPDX 2.3, CycloneDX 1.6 SBOM 생성
5. ZIP별 BUILD-MANIFEST와 SHA-256 생성
6. 6개 내부 산출물의 정확한 이름 집합 검사
7. Agent/Viewer ZIP 구조, PDF 헤더와 금지 파일 검사

로컬 진단 목적으로만 더러운 작업 트리를 허용할 수 있습니다.

```powershell
.\scripts\build-release.ps1 -Version 0.9.10-poc -AllowDirty
```

`sourceDirty=true` 산출물은 공식 Release에 사용하지 않습니다.

## Agent ZIP 계약

Agent ZIP 루트에는 다음 운영 진입점이 있어야 합니다.

```text
Install-or-Update-Agent.cmd
SamsungSwitchWatch.Agent.exe
install-agent.ps1
uninstall-agent.ps1
diagnose-agent.ps1
SamsungSwitchWatch_User_Manual_KO.pdf
```

다음 파일은 공개 Agent ZIP에 포함하지 않습니다.

```text
install-agent-background.ps1
run-agent-background.ps1
uninstall-agent-background.ps1
appsettings.json
appsettings.Production.json
appsettings.Development.json
```

Agent publish 단계에서는 `SamsungSwitchWatch.Agent.exe`만 남기고 Web SDK의 IIS·정적 자산·
NuGet 잠금 부산물을 제거합니다. 그 뒤 위 운영 스크립트와 사용자 문서·SBOM을 명시적으로
추가하며, 패키지 계약은 Agent와 Viewer ZIP의 전체 파일 이름 집합이 정확히 일치하는지
검사합니다.

`Install-or-Update-Agent.cmd`는 UAC를 요청하고 `install-agent.ps1`을 실행합니다. 설치기는
신규/업데이트를 자동 판별하고 다음 트랜잭션을 완료해야 합니다.

```text
검증 → 정확히 소유한 이전 예약 작업 중지·제거 → 서비스 정지 → ProgramData 전체 백업
→ 프로그램 원자 교체 → 설정·ACL·방화벽 적용
→ 서비스 시작 → HTTPS /health/ready
→ v0.7 자료와 이전 예약 작업 자료를 제한된 legacy-*-backup-*으로 보존
→ 설치 영수증 확정 → 설치 트랜잭션용 백업 제거
```

readiness 실패 시 프로그램, ProgramData의 HTTPS 신원, CIDR 설정, 제품 방화벽과 이전 서비스
실행 상태를 복구해야 합니다. 이전 예약 작업을 건드린 경우 작업 XML, 실행 상태, 원래 파일
위치와 ACL도 복구해야 합니다.

`legacy-v0.7-backup-*`은 설치 트랜잭션용 임시 복제본이 아닙니다. 과거 자격 증명과
SQLite 원문·이력의 보존 자료이므로 설치기와 릴리스 자동화가 삭제해서는 안 됩니다.
보존 기간 종료 뒤 삭제는 관리자 승인과 사내 정책에 따라 별도로 수행합니다.

## Viewer ZIP 계약

Viewer ZIP 루트에는 `Install-or-Update-Viewer.cmd`, `install-viewer.ps1`,
`uninstall-viewer.ps1`과 실행 파일이 있어야 합니다. 초급 사용자는 CMD 진입점을
더블클릭하고, 고급 관리자는 PowerShell 설치 옵션을 직접 사용합니다. Viewer는 현재
Windows 사용자 범위에 설치되므로 CMD 진입점은 UAC를 요청하지 않습니다.

## GitHub 게시

`.github/workflows/release.yml`은 `v*` 태그 push에서만 게시합니다.

```powershell
git tag -a v0.9.10-poc -m "Samsung Switch Watch v0.9.10-poc"
git push origin v0.9.10-poc
```

워크플로는 다음 조건을 fail-closed로 확인합니다.

- 태그가 annotated이며 `origin/main`에서 도달 가능
- 태그 object와 peeled commit이 패키징 후에도 바뀌지 않음
- 같은 태그의 Release 또는 draft가 이미 없음
- 두 공개 ZIP에 build provenance attestation이 있음
- draft의 원격 Asset 이름·크기·SHA-256이 로컬 두 ZIP과 동일
- 공개 직전 태그가 동일
- 게시된 Release가 immutable이며 release/asset verification 성공

게시 시 `gh release create`에는 명시적인 두 ZIP allowlist만 전달합니다. wildcard로
`artifacts/release/*`를 게시하지 않습니다.

## 설치 순서와 rollback

현장 업데이트 순서는 Agent 먼저, Viewer 다음입니다.

1. Agent ZIP의 `Install-or-Update-Agent.cmd`
2. HTTPS readiness와 Viewer 연결 확인
3. Viewer ZIP의 `Install-or-Update-Viewer.cmd`
4. Viewer 장비·DPAPI 계정 보존 확인
5. 접속 시험과 수동 `show port status`

Agent 업데이트가 실패하면 설치기가 이전 버전을 자동 복구합니다. Viewer가 새 API와 연결되지
않으면 Agent 진단 JSON을 먼저 확인하고, 이전 Release Asset을 덮어쓰거나 삭제하지 않습니다.

## 게시 후 검증

```powershell
$repo = 'sebia1993/samsung_switch_check'
$tag = 'v0.9.10-poc'
$assets = @(
  'SamsungSwitchWatch-Agent-0.9.10-poc-win-x64.zip',
  'SamsungSwitchWatch-Viewer-0.9.10-poc-win-x64.zip'
)

gh release verify $tag --repo $repo
foreach ($asset in $assets) {
  gh release verify-asset $tag ".\$asset" --repo $repo
}
```

Release 화면의 사용자 정의 Assets가 두 ZIP뿐인지 마지막으로 확인합니다. GitHub가 제공하는
Source code 링크는 이 개수에 포함하지 않습니다.
