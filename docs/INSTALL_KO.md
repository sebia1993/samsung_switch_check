# Samsung Switch Watch 설치 안내

## 1. 배포 파일

공식 GitHub `v0.6.0-poc` Release의 사용자 정의 Assets에서 아래 ZIP 두 개만 받습니다.
GitHub가 자동 표시하는 소스 코드 ZIP·tar.gz는 설치 파일이 아닙니다.

- `SamsungSwitchWatch-Agent-0.6.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.6.0-poc-win-x64.zip`

```powershell
$repo = 'sebia1993/samsung_switch_check'
$tag = 'v0.6.0-poc'
$releaseFiles = @(
  'SamsungSwitchWatch-Agent-0.6.0-poc-win-x64.zip',
  'SamsungSwitchWatch-Viewer-0.6.0-poc-win-x64.zip'
)

$tagRef = gh api "repos/$repo/git/ref/tags/$tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $tagRef.object.type -ne 'tag') { throw '검증된 annotated tag를 찾지 못했습니다.' }
$tagObject = gh api "repos/$repo/git/tags/$($tagRef.object.sha)" | ConvertFrom-Json
$sourceCommit = [string]$tagObject.object.sha
gh release verify $tag --repo $repo
foreach ($name in $releaseFiles) {
  gh attestation verify ".\$name" --repo $repo `
    --signer-workflow "$repo/.github/workflows/release.yml" `
    --source-digest $sourceCommit --source-ref "refs/tags/$tag" `
    --deny-self-hosted-runners
  gh release verify-asset $tag ".\$name" --repo $repo
  if ($LASTEXITCODE -ne 0) { throw "릴리스 검증 실패: $name" }
}
```

각 ZIP에는 패키지 `BUILD-MANIFEST.json`과 SPDX/CycloneDX SBOM이 들어 있습니다.

## 2. 보안 전제

Agent–Viewer 통신은 `HTTP/18443`이며 암호화와 사용자 인증이 없습니다. 설치기가 만든
Windows 방화벽의 **고정 Viewer IPv4 허용 목록이 유일한 Agent API 접근 통제**입니다.
인터넷, 일반 사용자 VLAN, 공용 Wi-Fi 또는 신뢰하지 않는 중계망을 통과시키지 마십시오.
설치 전 Windows Defender Firewall 서비스와 Domain/Private/Public 프로필을 모두 켜고,
각 프로필의 기본 인바운드 정책을 차단 상태로 유지하십시오. TCP/18443과 겹치는 별도
인바운드 Allow 규칙이 있으면 설치기가 안전을 위해 중단합니다.

준비할 값:

- Agent PC에서 접근 가능한 스위치 관리 IPv4와 읽기 전용 Telnet 계정
- Viewer PC마다 변경되지 않는 고정 IPv4 주소(최대 32개)
- Viewer에서 접근할 Agent IPv4 또는 DNS 이름

## 3. Agent 설치

관리자 PowerShell에서 먼저 시스템을 바꾸지 않는 사전 검사를 실행합니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -MockMode `
  -SwitchModel IES4224GP `
  -SwitchHost 192.0.2.10 `
  -ViewerRemoteAddress 192.0.2.50 `
  -Preflight
```

실제 설치에서는 `-MockMode`를 빼고 문서용 주소를 회사망의 실제 값으로 바꿉니다.
여러 Viewer를 허용할 때는 고정 IPv4를 쉼표로 나열합니다. CIDR·서브넷·DNS는 방화벽
입력으로 허용하지 않습니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -SwitchesJsonPath C:\Temp\switches.json `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42
```

설치기는 표준 4-octet 십진 IPv4만 받아 중복 제거·정렬하고 정확히 1~32개만 받습니다.
축약형, 16진수, 8진수로 해석될 수 있는 선행 0 표기는 거부합니다. `-SkipFirewall`은
없습니다. Agent는 `http://0.0.0.0:18443`에서 수신하지만 Domain/Private 프로필의 허용
Viewer 주소만 인바운드로 통과합니다.

[switches.example.json](examples/switches.example.json)에는 최대 256대의 등록 형식이 있습니다.
비밀번호는 JSON에 넣지 않습니다.

## 4. 스위치 자격 증명

각 `CredentialId`를 관리자 PowerShell에서 대화형으로 저장합니다.

```powershell
.\set-switch-credential.ps1 `
  -CredentialId samsung-switch-readonly-01 `
  -Username monitor-readonly

.\diagnose-agent.ps1 -OutputPath C:\Temp\ssw-diagnostic.json
```

비밀번호와 Telnet 원문은 Agent PC의 DPAPI·제한 ACL 경계 안에 남습니다.

## 5. Viewer 설치와 연결

운영자 PC의 일반 사용자 PowerShell에서 설치합니다.

```powershell
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows -Preflight
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows
```

Viewer의 **Agent 연결** 창에는 다음 두 값만 입력합니다.

1. Agent 주소: Agent IPv4 또는 사내 DNS 이름
2. 포트: 기본 `18443`

인증서 SHA-256, 페어링 문자열, 페어링 코드, Bearer 토큰은 사용하지 않습니다. 주소 입력 후
**연결 확인 및 저장**을 누릅니다. Viewer 설정의 기존 `https://` 주소는 v0.6 첫 실행에서 같은
authority의 `http://` 주소로 전환되고, 구 fingerprint/token 필드는 다음 저장 때 제거됩니다.

## 6. Viewer 고정 IPv4 변경

Agent PC의 관리자 PowerShell에서 전체 허용 목록을 한 번에 교체합니다.

```powershell
.\set-viewer-access.ps1 `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42 `
  -Preflight

.\set-viewer-access.ps1 `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42
```

스크립트는 설치 설정의 `Agent.DataDirectory`를 기준으로 제품 소유 방화벽 규칙과 설치 receipt를
함께 갱신합니다. 명시한 `-DataDirectory`가 설정과 다르거나 Agent ID·장비 인벤토리가 일치하지
않으면 중단합니다. 검증이나 receipt 저장이 실패하면 이전 방화벽 범위로 되돌립니다.

## 7. v0.5.x에서 v0.6.0으로 복구 설치

Agent와 Viewer는 **Agent 먼저, Viewer 다음** 순서로 같은 작업 시간에 올립니다. v0.5 Viewer는
v0.6 Agent와 호환되지 않습니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -Repair -ReuseData `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42
```

`-ViewerRemoteAddress`를 생략하면 유효한 v0.6 receipt 또는 기존 제품 소유 방화벽 규칙의
주소를 가져옵니다. 명시 입력을 권장합니다.

Repair는 프로그램을 staging한 뒤 DB·WAL/SHM·receipt·서비스 환경·방화벽을 백업합니다.
기존 서비스가 중지 상태이거나 `-DoNotStart`여도 새 Agent를 임시 시작해 HTTP readiness와
DB schema migration을 검증하고, 원래 중지 상태였으면 다시 중지합니다. 검증 성공 뒤에만
구 인증서 환경 변수를 제거하고, receipt·설정의 thumbprint/SHA-256과 저장소 FriendlyName이
모두 맞는 현재 및 rotation 이전 설치기 소유 인증서만 제거합니다. 사용자 소유 인증서는
제거하지 않습니다. 인증서 정리 실패는 되돌릴 수 없는 키 삭제 뒤의 잘못된 rollback을 피하기
위해 설치 성공을 유지하고 경고로 남깁니다. 그 밖의 검증 실패 시 프로그램, DB,
receipt, 서비스 환경, 방화벽과 원래 서비스 상태를 복원합니다.

그 다음 Viewer ZIP에서 `install-viewer.ps1`을 실행합니다. 최초 연결 때 주소와 포트를 확인하십시오.

## 8. 제거

```powershell
.\uninstall-agent.ps1
.\uninstall-viewer.ps1
```

기본 Agent 제거는 데이터와 자격 증명을 보존합니다. 완전 삭제는 명시적으로 실행합니다.

```powershell
.\uninstall-agent.ps1 -RemoveData
```

제거기는 설치 설정의 사용자 지정 `Agent.DataDirectory`를 자동으로 따릅니다. 다른
`-DataDirectory`를 명시하거나 설정·receipt로 정확한 설치 소유권을 확인할 수 없으면
`-RemoveData`를 거부합니다. `-RemoveData`는 복구하기 어렵습니다. 제품 경계와 백업을 먼저
확인하십시오. 유효한 v0.5 receipt가 남아 있으면 현재 및 rotation 이전 설치기 소유 인증서도
엄격한 thumbprint·SHA-256·FriendlyName 확인 뒤 제거합니다.

## 9. 정상 확인

- 서비스 `SamsungSwitchWatchAgent`가 의도한 실행/중지 상태
- `http://<Agent>:18443/health/live` 응답
- `/health/ready`가 ready 또는 원인을 나타내는 안정 오류 코드
- Windows 방화벽 `Samsung Switch Watch Agent HTTP`의 RemoteAddress가 허용 Viewer와 정확히 일치
- Viewer의 API 상태와 SignalR 상태가 별도로 정상
- RDP 종료 후에도 Agent 수집 지속
- Viewer 재연결 뒤 누락 이벤트 catch-up

`agentLive=OK`, `agentReady=AGENT_NOT_READY_503`이면 프로세스는 동작하지만 DB·자격 증명·
스케줄러·필수 수집기 중 하나가 준비되지 않은 상태입니다. 모든 스위치를 Down으로 판정하지
말고 readiness 코드와 마지막 정상 수신 시각을 확인하십시오.
