# Samsung Switch Watch v0.2.0-poc 설치 안내

Agent는 스위치에 접근 가능한 원격 Windows PC에, Viewer는 운영자 PC에 설치합니다. 두 ZIP은 서로 다른 PC에서 사용합니다.

## 1. 설치 전 확인

- Windows 10/11 또는 Windows Server x64 환경이어야 합니다.
- Agent PC에서 스위치 IPv4 주소의 TCP/23으로 연결할 수 있어야 합니다.
- Viewer PC에서 Agent PC의 TCP/18443으로 연결할 수 있어야 합니다.
- Agent 설치에는 관리자 권한이 필요합니다.
- 가능한 한 스위치 조회 전용 Telnet 계정을 사용합니다.
- ZIP은 임시 로컬 폴더에 풀고 네트워크 공유에서 직접 실행하지 않습니다.

대상 PC에 .NET SDK나 Python은 필요하지 않습니다. POC 파일은 코드 서명되지 않았으므로 SmartScreen 경고가 표시될 수 있습니다.

공식 Release의 두 ZIP을 내려받은 폴더에서 무결성을 먼저 확인합니다.

```powershell
Get-FileHash .\SamsungSwitchWatch-Agent-0.2.0-poc-win-x64.zip -Algorithm SHA256
Get-FileHash .\SamsungSwitchWatch-Viewer-0.2.0-poc-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

## 2. Viewer 설치

Viewer ZIP을 별도 폴더에 풀고 설치 전 검사를 실행합니다. `-Preflight`는 시스템을 변경하지 않습니다.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-viewer.ps1 -Preflight
.\install-viewer.ps1 -StartWithWindows
```

Viewer는 현재 사용자 영역에 설치됩니다. 기존 버전이 있으면 새 파일을 임시 폴더에서 검증한 뒤 교체하고, 실패하면 이전 버전을 복구합니다. 화면 설정과 DPAPI 토큰은 프로그램 폴더 밖에 있어 업데이트 시 보존됩니다.

설치 없이 ZIP의 `SamsungSwitchWatch.Viewer.exe`를 직접 실행해 데모 화면을 먼저 확인할 수도 있습니다.

## 3. Agent Mock 설치

Agent ZIP을 별도 폴더에 풀고 관리자 PowerShell에서 실행합니다.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-agent.ps1 -MockMode -SkipFirewall -Preflight
.\install-agent.ps1 `
  -MockMode `
  -ViewerRemoteAddress '<Viewer-PC-IP>'
```

Mock 모드는 실제 Windows 서비스와 HTTPS 경로를 사용하지만 스위치 TCP/23에는 접속하지 않습니다. 설치 후 `/health/ready` 확인까지 성공해야 완료로 처리됩니다.

## 4. Agent 실환경 설치

먼저 전체 인수를 포함한 사전 검사를 실행한 뒤 같은 인수에서 `-Preflight`만 제거합니다.

```powershell
.\install-agent.ps1 `
  -SwitchHost '<IES4224GP-관리-IPv4>' `
  -ViewerRemoteAddress '<Viewer-PC-IP>' `
  -SwitchId 'ACCESS-SW-01' `
  -SwitchDisplayName '1층 액세스 스위치' `
  -UplinkPort '24' `
  -Preflight

.\install-agent.ps1 `
  -SwitchHost '<IES4224GP-관리-IPv4>' `
  -ViewerRemoteAddress '<Viewer-PC-IP>' `
  -SwitchId 'ACCESS-SW-01' `
  -SwitchDisplayName '1층 액세스 스위치' `
  -UplinkPort '24'
```

설치 결과:

- 프로그램: `C:\Program Files\SamsungSwitchWatch\Agent`
- DB·DPAPI 자격 증명: `C:\ProgramData\SamsungSwitchWatch`
- 고정 서비스 이름: `SamsungSwitchWatchAgent`
- 서비스 계정: `LocalService`, 프로그램·데이터 접근은 해당 서비스 SID에만 부여
- HTTPS: 자체 서명 인증서, 기본 TCP/18443, Viewer 주소 제한 방화벽 규칙

설치 중 오류가 발생하면 새 서비스·방화벽 규칙·프로그램 폴더를 제거하고 기존 설치를 복구합니다. 실환경의 최초 설치는 자격 증명 입력 전까지 서비스를 시작하지 않습니다.

```powershell
.\set-switch-credential.ps1 `
  -CredentialId 'samsung-switch-readonly' `
  -Username '<조회전용-계정명>'
```

비밀번호는 로컬 대화형 입력으로만 받고 DPAPI로 저장합니다. 스크립트는 설치 폴더를 작업 디렉터리로 고정해 다른 ZIP이나 현재 폴더의 설정을 읽지 않으며, 서비스 시작 후 readiness를 확인합니다. 최초 실환경 Telnet 배치와 복구 설치는 정상적인 장시간 출력을 고려해 최대 210초 동안 기다릴 수 있습니다.

## 5. 복구 설치

DB, 자격 증명, 설치 설정과 인증서를 보존하면서 프로그램 파일만 다시 배치할 때 사용합니다. 기존 서비스가 있어야 하며 포트·스위치 설정 변경 용도가 아닙니다.

```powershell
.\install-agent.ps1 -Repair -Preflight
.\install-agent.ps1 -Repair
```

기존 서비스가 실행 중이었다면 교체 후 다시 시작하고 readiness를 확인합니다. 실패하면 이전 프로그램 폴더와 서비스 상태를 복구합니다.

## 6. Viewer 페어링

Agent PC의 관리자 PowerShell에서 10분간 유효한 코드를 생성합니다.

```powershell
.\new-pairing-code.ps1
```

Viewer PC에서 인증서 지문을 고정해 코드를 교환합니다.

```powershell
.\pair-viewer.ps1 `
  -AgentUri 'https://<Agent-PC-IP>:18443' `
  -CertificateFingerprint '<64자리-SHA256-지문>'
```

출력된 주소·지문·토큰을 Viewer의 연결 설정에 입력합니다. 토큰과 페어링 코드를 파일로 저장하지 마십시오.

## 7. 정상 동작과 진단

```powershell
.\diagnose-agent.ps1
```

주요 정상 기대값은 다음과 같습니다.

```text
service       OK
configuration OK
certificate   OK
database      OK
switchTcp     OK
agentLive     OK
agentReady    OK
```

`agentLive=OK`, `agentReady=AGENT_NOT_READY_503`이면 프로세스는 동작하지만 DB·인증서·자격 증명·수집기 중 하나가 준비되지 않은 상태입니다. Viewer에서 마지막 성공 시각과 표준 오류 코드를 확인합니다. 실제 IP나 원문은 외부로 반출하지 않습니다.

## 8. 제거

```powershell
# Viewer 프로그램만 제거
.\uninstall-viewer.ps1

# Viewer 토큰과 화면 설정까지 영구 제거
.\uninstall-viewer.ps1 -RemoveSettings

# Agent 서비스와 프로그램만 제거하고 데이터 보존 (관리자 PowerShell)
.\uninstall-agent.ps1

# 이력·원문·DPAPI 자격 증명까지 영구 제거
.\uninstall-agent.ps1 -RemoveData
```

`-RemoveData`는 복구할 수 없습니다. 필요한 현장 이력을 먼저 사내 보안 절차에 맞춰 백업하십시오.
