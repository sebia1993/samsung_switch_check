# Samsung Switch Watch 릴리스 절차

## 릴리스 계약

- 대상: Windows x64
- 런타임: .NET 10 self-contained, single-file, trimming 비활성
- 현재 버전: `0.9.16-poc`
- 태그: annotated tag `v0.9.16-poc`
- GitHub Release 사용자 정의 Asset: Agent ZIP과 Viewer ZIP, 정확히 두 개
- 기존 Release와 Asset은 교체하지 않는 immutable 방식

공개 파일:

```text
SamsungSwitchWatch-Agent-0.9.16-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.9.16-poc-win-x64.zip
```

Actions 내부 검증 산출물:

```text
SamsungSwitchWatch-Agent-0.9.16-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.9.16-poc-win-x64.zip
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
.\scripts\build-release.ps1 -Version 0.9.16-poc
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
.\scripts\build-release.ps1 -Version 0.9.16-poc -AllowDirty
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
검증 → 기존 install/data 루트 owner·reparse 신뢰 검사
→ 패키지를 보호 staging에 복사·재해시
→ 정확히 소유한 이전 예약 작업 중지·제거 → 서비스 정지
→ 정확한 DataDirectory 읽기 전용 전수 검사 → 루트 선잠금·부모 우선 ACL 이관
→ ProgramData 전체 snapshot → 프로그램 원자 교체
→ 전용 가상 서비스 계정·설정·ACL·방화벽 적용
→ 서비스 시작 → HTTPS /health/ready
→ v0.7 자료와 이전 예약 작업 자료를 제한된 legacy-*-backup-*으로 보존
→ Administrators 전용 설치 영수증 확정 → 설치 트랜잭션용 백업 제거
```

Agent 설치와 제거는 `Global\SamsungSwitchWatch.Agent.Deployment.v1` 잠금을 공유합니다.
Viewer 설치와 제거는 같은 Windows 사용자 SID가 포함된 전역 잠금을 공유합니다. 두 잠금은
`WaitOne(0)`으로 즉시 판정하며, 이미 실행 중인 작업을 기다리거나 겹쳐서 변경하지 않습니다.
잠금 ACL은 Agent의 경우 SYSTEM·Administrators, Viewer의 경우 SYSTEM·현재 사용자로
제한합니다. `.v1`은 앱 버전이 아니라 이후 설치기와도 공유해야 하는 잠금 프로토콜
식별자이므로 릴리스마다 바꾸지 않습니다.

readiness 실패 시 프로그램, ProgramData의 HTTPS 신원, CIDR 설정, 제품 방화벽과 이전 서비스
실행 상태를 복구해야 합니다. 이전 예약 작업을 건드린 경우 작업 XML, 실행 상태, 원래 파일
위치와 ACL도 복구해야 합니다. 서비스 중지·삭제 또는 선행 복구가 확인되지 않으면 후속 파일
삭제·복구를 진행하지 않습니다. legacy program/data 이동이 일부만 끝났다면 원래 위치와
archive를 보존하고 활성 DataDirectory 복구도 차단합니다. rollback 오류가 남은 transaction
snapshot, program backup, legacy archive와 journal은 관리자 판단 전까지 정리하지 않습니다.

DataDirectory는 정확히 `%ProgramData%\SamsungSwitchWatch`만 허용합니다. 신규 설치에서는
빈 선점 폴더도 거부하며 `New-Item -Force`를 사용하지 않습니다. 사전 검사 뒤 다른 프로세스가
같은 경로를 만들면 기존 폴더를 채택하거나 rollback에서 삭제하지 않고 실패해야 합니다.
서비스 SID는 결정론적으로 먼저 계산하고, 신규·기존 DataDirectory 모두 HTTPS 신원 복사나
snapshot 전에 ACL 적용을 완료해야 합니다. ACL 성공 뒤에만 신규 폴더를 설치기 소유 rollback
항목으로 표시합니다.

활성 install/data 트리는 ACL 변경 전에 읽기 전용으로 owner와 reparse를 전수 검사하고,
루트를 먼저 잠근 뒤 부모 우선 순회에서 같은 검사를 반복합니다. 서비스는
`NT SERVICE\SamsungSwitchWatchAgent` 가상 계정으로 등록합니다. 정확한 서비스 SID owner는
DataDirectory 하위 항목에서만 허용하며, 기존 `LocalService` owner는 그 서비스를 중지한
업데이트의 1회 이관에만 허용합니다. 마지막 재열거에서 Administrators owner, 허용 SID ACL과
reparse 부재를 확인합니다. 실패 코드는 `AGENT_DIRECTORY_TRUST_INVALID`이며 관리자 확인 없이
폴더 삭제나 강제 소유권 변경으로 우회하지 않습니다.

소스 패키지 검증 뒤 SYSTEM·Administrators 전용 staging을 만들고, 복사된 모든 파일과 Agent
EXE를 in-memory 매니페스트 SHA-256과 다시 비교해야 합니다. 보호 staging 재검증 전에는 기존
프로그램을 교체하지 않습니다.

install receipt는 Administrators owner이며 SYSTEM·Administrators만 접근할 수 있어야 합니다.
영수증에 CIDR 필드가 있어도 업데이트 권한원으로 사용하지 않습니다. 스위치 대상 CIDR은 검증된
설정, Viewer 관리 CIDR은 정확한 제품 소유 방화벽 규칙에서 가져옵니다. 데이터 영구 제거는
`AGENT_RECEIPT_TRUST_INVALID`가 발생하면 변경 전에 중단해야 합니다.

`legacy-v0.7-backup-*`은 설치 트랜잭션용 임시 복제본이 아닙니다. 과거 자격 증명과
SQLite 원문·이력의 보존 자료이므로 설치기와 릴리스 자동화가 삭제해서는 안 됩니다.
보존 기간 종료 뒤 삭제는 관리자 승인과 사내 정책에 따라 별도로 수행합니다.

## Viewer ZIP 계약

Viewer ZIP 루트에는 `Install-or-Update-Viewer.cmd`, `install-viewer.ps1`,
`uninstall-viewer.ps1`과 실행 파일이 있어야 합니다. 초급 사용자는 CMD 진입점을
더블클릭하고, 고급 관리자는 PowerShell 설치 옵션을 직접 사용합니다. Viewer는 현재
Windows 사용자 범위에 설치되므로 CMD 진입점은 UAC를 요청하지 않습니다.

abandoned mutex 감지는 해당 커널 객체가 남아 있을 때 이전 프로세스 중단을 한 번
fail-closed로 보고합니다. Agent 설치기와 제거기는 별도로 두 영구 journal을 잠금 직후
교차 검사하며, 재부팅 뒤 남은 `running` 기록, rollback 오류와 손상 기록을 감지하면
설치 상태를 읽거나 변경하기 전에 중단합니다. 이 검사는 불완전한 백업을 추측해
rollback·roll-forward하거나 임시 자료를 정리하는 자동 복구 계약이 아닙니다. journal과
transaction·legacy 백업은 관리자 판단 전까지 보존해야 합니다.
기존 작업 기록 루트의 관리자 소유권과 reparse 여부를 검증한 뒤 루트 ACL을 먼저 잠급니다.
그 안에서 각 하위 항목의 기존 관리자 소유권과 reparse 여부를 부모부터 순서대로 검증·이관하고,
전체 ACL 이관 뒤 허용된 journal·transaction 항목만 있는지 다시 검사합니다. 최종 소유자는
로컬 Administrators, ACL은 SYSTEM·Administrators 전용입니다. 신뢰 경계 검증에 실패하면
`AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID`로 중단하고, 64KiB를 초과한 journal은 파싱하지
않습니다.

## GitHub 게시

`.github/workflows/release.yml`은 `v*` 태그 push에서만 게시합니다.

```powershell
git tag -a v0.9.16-poc -m "Samsung Switch Watch v0.9.16-poc"
git push origin v0.9.16-poc
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
$tag = 'v0.9.16-poc'
$assets = @(
  'SamsungSwitchWatch-Agent-0.9.16-poc-win-x64.zip',
  'SamsungSwitchWatch-Viewer-0.9.16-poc-win-x64.zip'
)

gh release verify $tag --repo $repo
foreach ($asset in $assets) {
  gh release verify-asset $tag ".\$asset" --repo $repo
}
```

Release 화면의 사용자 정의 Assets가 두 ZIP뿐인지 마지막으로 확인합니다. GitHub가 제공하는
Source code 링크는 이 개수에 포함하지 않습니다.
