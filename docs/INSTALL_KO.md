# Samsung Switch Watch 설치 안내

## 1. 배포 파일

공식 GitHub `v0.7.0-poc` Release의 사용자 정의 Assets에서 아래 ZIP 두 개만 받습니다.
GitHub가 자동 표시하는 소스 코드 ZIP·tar.gz는 설치 파일이 아닙니다.

- `SamsungSwitchWatch-Agent-0.7.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.7.0-poc-win-x64.zip`

```powershell
$repo = 'sebia1993/samsung_switch_check'
$tag = 'v0.7.0-poc'
$releaseFiles = @(
  'SamsungSwitchWatch-Agent-0.7.0-poc-win-x64.zip',
  'SamsungSwitchWatch-Viewer-0.7.0-poc-win-x64.zip'
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
공식 파일 검증을 마친 뒤 Windows가 다운로드 스크립트 실행을 차단하면 ZIP 속성의 **차단 해제**를
선택하고 다시 압축을 풉니다. 회사 실행 정책(GPO)이 차단한 경우에는 우회하지 말고 관리자에게
승인을 요청하십시오.

## 2. 보안 전제

Agent–Viewer 통신은 `HTTP/18443`이며 암호화와 사용자 인증이 없습니다. Windows 방화벽의
**고정 Viewer IPv4 허용 목록이 유일한 Agent API 접근 통제**입니다. 관리자용 서비스 설치기는
이 규칙을 만들고 검증하지만, 아래의 비관리자 숨김 설치는 현재 방화벽 정책을 변경하지 않습니다.
인터넷, 일반 사용자 VLAN, 공용 Wi-Fi 또는 신뢰하지 않는 중계망을 통과시키지 마십시오.
설치 전 Windows Defender Firewall 서비스와 Domain/Private/Public 프로필을 모두 켜고,
각 프로필의 기본 인바운드 정책을 차단 상태로 유지하십시오. TCP/18443과 겹치는 별도
인바운드 Allow 규칙이 있으면 설치기가 안전을 위해 중단합니다.

준비할 값:

- Agent PC에서 접근 가능한 스위치 관리 IPv4와 읽기 전용 Telnet 계정
- Viewer PC마다 변경되지 않는 고정 IPv4 주소(최대 32개)
- Viewer에서 접근할 Agent IPv4 또는 DNS 이름

## 3. Agent 설치 방식 선택

두 방식은 동시에 사용할 수 없습니다.

- 관리자 승인을 받을 수 있으면 **Windows 서비스 방식**을 권장합니다. 로그온 여부와 관계없이
  실행되고 `LocalService` 및 제품 전용 ACL·방화벽 설정을 사용합니다.
- 관리자 승인을 받을 수 없고 같은 Windows 사용자가 계속 로그인된 PC라면 **현재 사용자 숨김
  방식**을 사용합니다. RDP 연결을 끊어도 실행되지만 Windows 로그오프 중에는 실행되지 않습니다.

### 3.1 관리자용 Windows 서비스 설치

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

Viewer에서 직접 한 줄 조회 명령을 실행하려면 설치 때만 다음 옵션을 명시합니다. 옵션을 빼면
기본값은 **사용 안 함**입니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -SwitchesJsonPath C:\Temp\switches.json `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42 `
  -EnableReadOnlyQueries
```

이 옵션은 설정 변경 명령을 허용하지 않습니다. Agent가 허용한 한 줄 `show` 계열만 실행하며
명령은 128자, 결과는 UTF-8 기준 64KiB로 제한됩니다. 분당 최대 12회이며, 결과는 Viewer의
현재 실행 메모리에만 남고 Agent DB·이벤트·원문 보관이나 파일 내보내기에 추가되지 않습니다.

설치기는 표준 4-octet 십진 IPv4만 받아 중복 제거·정렬하고 정확히 1~32개만 받습니다.
축약형, 16진수, 8진수로 해석될 수 있는 선행 0 표기는 거부합니다. `-SkipFirewall`은
없습니다. Agent는 `http://0.0.0.0:18443`에서 수신하지만 Domain/Private 프로필의 허용
Viewer 주소만 인바운드로 통과합니다.

[switches.example.json](examples/switches.example.json)에는 최대 256대의 등록 형식이 있습니다.
비밀번호는 JSON에 넣지 않습니다.

### 3.2 관리자 권한 없는 현재 사용자 숨김 설치

이미 Agent EXE를 직접 실행해 Viewer 연결과 장비 설정을 확인한 PC에서 사용할 수 있습니다.
먼저 보이는 Agent 창을 한 번만 정상 종료한 뒤, **관리자가 아닌 일반 PowerShell**에서 Agent ZIP
폴더의 다음 명령을 실행합니다.

```powershell
.\install-agent-background.ps1 -SourceDirectory . -Preflight
.\install-agent-background.ps1 -SourceDirectory .
```

숨김 설치에서도 Viewer 장비 명령을 사용할 때만 두 명령 모두에 `-EnableReadOnlyQueries`를
추가합니다. 기본값은 사용 안 함입니다.

설치기는 패키지 무결성을 확인하고 현재 설정과 같은 ZIP 폴더의 `data`를 다음 사용자 전용 위치로
이전한 뒤, 숨김 예약 작업을 시작합니다.

```text
프로그램 : %LOCALAPPDATA%\Programs\SamsungSwitchWatch\Agent
데이터   : %LOCALAPPDATA%\SamsungSwitchWatch\AgentData
예약 작업: SamsungSwitchWatchAgent-CurrentUser
```

예약 작업은 현재 사용자 로그온 15초 뒤 창 없이 시작되고, 비정상 종료 시 1분 간격으로 최대 3회
재시작합니다. RDP 창을 닫아도 같은 사용자가 Windows에 로그인되어 있으면 계속 실행됩니다.
실행 상태와 장애는 Agent PC의 별도 창이나 트레이 아이콘 대신 Viewer에서 확인합니다.

새 ZIP을 기존 설치 폴더가 아닌 별도 폴더에 풀고, 정상 소유권이 확인되는 설치를 업데이트하거나
삭제된 작업을 다시 등록할 때 다음처럼 실행합니다. 다른 프로그램이 같은 이름의 작업을 만들었거나
작업 정의가 변조된 경우에는 안전을 위해 자동 복구하지 않습니다.

```powershell
.\install-agent-background.ps1 -SourceDirectory . -Repair -Preflight
.\install-agent-background.ps1 -SourceDirectory . -Repair
```

`-Repair`에서 `-EnableReadOnlyQueries`를 생략하면 기존 사용 여부와 제한값을 그대로 보존합니다.
기존 비활성 설치를 명시적으로 활성화할 때만 Repair 명령에 `-EnableReadOnlyQueries`를 추가합니다.

이 방식은 Windows 서비스나 방화벽을 만들지 않습니다. 현재 원격 Viewer 연결에 사용하는
관리자 승인 방화벽 정책이 이미 있어야 하며, 설치기는 이를 그대로 둡니다. 회사 정책이 일반
사용자의 예약 작업 등록을 막으면 설치는 변경 사항을 되돌리고 중단합니다.

숨김은 오조작 가능성을 줄이는 기능이지 보안 잠금은 아닙니다. 같은 Windows 계정을 사용하는
사람은 작업 관리자에서 프로세스를 끝내거나 예약 작업을 비활성화할 수 있습니다. 로그오프하면
중지되고 다음 로그온 때 다시 시작합니다.

기본 제거로 데이터를 보존한 뒤 다시 설치할 때는 `-Repair` 없이 일반 설치 명령을 사용합니다.

## 4. 스위치 자격 증명

각 `CredentialId`를 관리자 PowerShell에서 대화형으로 저장합니다.

```powershell
.\set-switch-credential.ps1 `
  -CredentialId samsung-switch-readonly-01 `
  -Username monitor-readonly

.\diagnose-agent.ps1 -OutputPath C:\Temp\ssw-diagnostic.json
```

비밀번호와 Telnet 원문은 Agent PC의 DPAPI·제한 ACL 경계 안에 남습니다.

현재 사용자 숨김 설치는 기존 직접 실행 폴더의 `data`를 첫 설치 때 이전하므로 저장되어 있던
자격 증명을 그대로 사용합니다. 새 자격 증명이 필요하면 같은 계정의 일반 PowerShell에서
`-CurrentUser`를 추가합니다. 스크립트가 숨김 작업을 잠시 중지하고 입력·검증·재시작합니다.

```powershell
.\set-switch-credential.ps1 `
  -CurrentUser `
  -CredentialId samsung-switch-readonly-01 `
  -Username monitor-readonly
```

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
**연결 확인 및 저장**을 누릅니다. Viewer 설정의 기존 `https://` 주소는 v0.6 이상 첫 실행에서 같은
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

## 7. v0.5.x 또는 v0.6.0에서 v0.7.0으로 복구 설치

Agent와 Viewer는 **Agent 먼저, Viewer 다음** 순서로 같은 작업 시간에 올립니다. v0.5 Viewer는
현재 Agent와 호환되지 않으며, v0.6 Viewer에서는 새 장비 명령 기능을 사용할 수 없습니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -Repair -ReuseData `
  -ViewerRemoteAddress 10.20.30.41,10.20.30.42
```

`-ViewerRemoteAddress`를 생략하면 유효한 v0.6 receipt 또는 기존 제품 소유 방화벽 규칙의
주소를 가져옵니다. 명시 입력을 권장합니다.

Repair는 기존 `EnableReadOnlyQueries`와 제한값을 보존합니다. 비활성 설치를 이번 업데이트에서
활성화할 때만 위 명령에 `-EnableReadOnlyQueries`를 추가합니다.

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

현재 사용자 숨김 설치는 같은 계정의 일반 PowerShell에서 별도 제거 스크립트를 사용합니다.

```powershell
.\uninstall-agent-background.ps1
```

기본 제거는 DB, 보존 설정과 자격 증명을 `%LOCALAPPDATA%\SamsungSwitchWatch\AgentData`에
남깁니다. 완전 삭제할 때만 다음 명령을 사용하며 삭제한 데이터는 복구되지 않습니다.

```powershell
.\uninstall-agent-background.ps1 -RemoveData
```

## 9. 정상 확인

- 서비스 방식이면 `SamsungSwitchWatchAgent`, 숨김 방식이면
  `SamsungSwitchWatchAgent-CurrentUser` 예약 작업이 의도한 실행 상태
- `http://<Agent>:18443/health/live` 응답
- `/health/ready`가 ready 또는 원인을 나타내는 안정 오류 코드
- 서비스 방식은 Windows 방화벽 `Samsung Switch Watch Agent HTTP`의 RemoteAddress가 허용
  Viewer와 정확히 일치
- 숨김 방식은 기존 관리자 승인 방화벽 정책과 Viewer 연결이 유지됨
- Viewer의 API 상태와 SignalR 상태가 별도로 정상
- 장비 명령을 켠 설치는 Viewer `장비 명령` 탭이 활성화되고, 끈 설치는 이유와 함께 비활성 표시
- RDP 종료 후에도 Agent 수집 지속
- Viewer 재연결 뒤 누락 이벤트 catch-up

`agentLive=OK`, `agentReady=AGENT_NOT_READY_503`이면 프로세스는 동작하지만 DB·자격 증명·
스케줄러·필수 수집기 중 하나가 준비되지 않은 상태입니다. 모든 스위치를 Down으로 판정하지
말고 readiness 코드와 마지막 정상 수신 시각을 확인하십시오.

숨김 방식은 일반 PowerShell에서 다음 명령으로 창을 띄우지 않고 상태만 확인할 수 있습니다.

```powershell
Get-ScheduledTask -TaskName SamsungSwitchWatchAgent-CurrentUser |
  Select-Object TaskName, State
Get-ScheduledTaskInfo -TaskName SamsungSwitchWatchAgent-CurrentUser |
  Select-Object LastRunTime, LastTaskResult, NextRunTime
Invoke-RestMethod http://127.0.0.1:18443/health/live
Get-Content "$env:LOCALAPPDATA\SamsungSwitchWatch\AgentData\background-runner.log" -Tail 20
```

`background-runner.log`에는 시작·종료 코드만 기록되고 Telnet 원문, 계정, 주소는 기록되지 않습니다.
