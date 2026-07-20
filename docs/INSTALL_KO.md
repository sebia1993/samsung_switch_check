# Samsung Switch Watch 설치 안내

이 문서는 `IES4224GP` 한 대를 대상으로 하는 1차 POC 배포 절차입니다. Agent는 스위치에 접근 가능한 원격 Windows PC에, Viewer는 운영자 PC에 설치합니다.

## 1. 설치 전 확인

- 두 PC 모두 Windows 10/11 또는 Windows Server x64 환경이어야 합니다.
- Agent PC에서 스위치의 TCP/23으로 연결할 수 있어야 합니다.
- Viewer PC에서 Agent PC의 TCP/18443으로 연결할 수 있어야 합니다.
- Agent 설치와 서비스 구성에는 관리자 권한이 필요합니다.
- 스위치에는 가능한 한 조회 전용 Telnet 계정을 사용합니다.
- 실제 비밀번호, IP, 호스트명, MAC, 명령 원문을 외부로 전달하지 않습니다.

배포 ZIP은 self-contained이므로 대상 PC에 .NET SDK나 Python을 설치할 필요가 없습니다.

## 2. Viewer 데모 먼저 확인

Viewer ZIP을 풀고 다음 중 하나를 선택합니다.

```powershell
# 설치하지 않고 즉시 실행
.\SamsungSwitchWatch.Viewer.exe

# 현재 사용자 영역에 설치하고 시작 메뉴 바로가기 생성
Set-ExecutionPolicy -Scope Process Bypass
.\install-viewer.ps1
```

처음에는 네트워크를 사용하지 않는 데모 모드로 열립니다. 대시보드, 경고/장애/복구, 팝업, 미니 창, 항상 위, 시스템 트레이 동작을 먼저 확인합니다.

## 3. Agent 모의 설치

실제 스위치에 접속하기 전에 원격 PC에서 모의(Mock) 모드로 설치할 수 있습니다.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-agent.ps1 `
  -MockMode `
  -ViewerRemoteAddress '<Viewer-PC-IP>'
```

설치 결과에 표시되는 인증서 SHA-256 지문을 안전하게 보관합니다. 모의 모드도 HTTPS와 실제 서비스 경로를 사용하지만 스위치 TCP/23에는 접속하지 않습니다.

## 4. Agent 실환경 설치

Agent ZIP을 원격 PC의 임시 폴더에 풀고 관리자 PowerShell에서 실행합니다.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-agent.ps1 `
  -SwitchHost '<IES4224GP-관리-IP>' `
  -ViewerRemoteAddress '<Viewer-PC-IP>' `
  -SwitchId 'ACCESS-SW-01' `
  -SwitchDisplayName '1층 액세스 스위치' `
  -UplinkPort '24'
```

스크립트는 다음을 수행합니다.

- `C:\Program Files\SamsungSwitchWatch\Agent`에 프로그램 설치
- `C:\ProgramData\SamsungSwitchWatch`에 DB와 DPAPI 자격 증명 영역 준비
- 유효 기간이 3년인 자체 서명 HTTPS 인증서 생성
- 임의 TokenPepper 생성
- `LocalService` 계정으로 자동 시작 Windows 서비스 등록
- Viewer PC 주소만 허용하는 TCP/18443 방화벽 규칙 생성

실환경에서는 자격 증명 입력 전까지 서비스를 시작하지 않습니다. 다음 명령에서 비밀번호는 화면과 로그에 표시되지 않습니다.

```powershell
.\set-switch-credential.ps1 `
  -CredentialId 'samsung-switch-readonly' `
  -Username '<조회전용-계정명>'
```

DPAPI 저장이 끝나면 Agent 서비스가 시작됩니다. `services.msc`에서 서비스 이름 `SamsungSwitchWatchAgent`를 확인할 수 있습니다.

## 5. Viewer 페어링

Agent PC의 관리자 PowerShell에서 10분간 유효한 일회용 코드를 만듭니다.

```powershell
.\new-pairing-code.ps1
```

Viewer PC에서 Agent 주소와 설치 시 표시된 인증서 지문을 사용해 코드를 교환합니다.

```powershell
.\pair-viewer.ps1 `
  -AgentUri 'https://<Agent-PC-IP>:18443' `
  -CertificateFingerprint '<64자리-SHA256-지문>'
```

스크립트가 출력한 Agent 주소, 지문, 페어링 토큰을 Viewer의 `연결 설정`에 입력하고 `오프라인 데모 모드 사용`을 해제합니다. 토큰은 Viewer PC의 현재 Windows 사용자 DPAPI로 보호됩니다. 토큰이나 페어링 코드를 파일로 저장하지 마세요.

## 6. 정상 동작 확인

Agent PC에서 민감한 원문을 포함하지 않는 진단을 실행합니다.

```powershell
.\diagnose-agent.ps1
```

정상 기대값은 다음과 같습니다.

```text
service       OK
configuration OK
certificate   OK
database      OK
switchTcp     OK
agentHttps    OK
```

Viewer에서는 다음을 확인합니다.

- Agent 연결 상태가 `연결됨`
- 최초 로그 수집이 기존 로그 알림을 만들지 않음
- 등록 명령 `port_status`, `system`, `log_ram`, `version`만 선택 가능
- Viewer 종료 후 다시 열어도 마지막 시퀀스 다음부터 누락 이벤트가 동기화됨
- Agent 통신이 끊기면 장비를 DOWN으로 오판하지 않고 연결 미확인으로 표시

공개 매뉴얼 기반 명령이 현장 펌웨어에서 지원되지 않으면 해당 수집기만 `PARSER_UNSUPPORTED`로 표시됩니다. 이때 설정 명령을 시도하지 않습니다.

## 7. 데이터와 보존

Agent 로컬 기본 경로:

```text
C:\ProgramData\SamsungSwitchWatch\switchwatch.db
C:\ProgramData\SamsungSwitchWatch\credentials\
```

원문 Telnet 출력은 Agent의 SQLite에만 저장되고 Viewer API로 전송되지 않습니다. 기본 보존 기간은 원문 7일이며 최대 보관 용량은 500MB입니다. 이벤트는 90일, 감사 기록은 180일 동안 보존합니다. SQLite 파일을 네트워크 공유에 두지 마세요.

## 8. 제거

Viewer 제거:

```powershell
.\uninstall-viewer.ps1

# DPAPI 토큰과 창 위치 설정도 함께 제거할 때만
.\uninstall-viewer.ps1 -RemoveSettings
```

Agent 제거는 관리자 PowerShell에서 실행합니다.

```powershell
# 프로그램과 서비스만 제거하고 수집 이력은 보존
.\uninstall-agent.ps1

# 이력, 원문, DPAPI 자격 증명까지 영구 제거할 때만
.\uninstall-agent.ps1 -RemoveData
```

`-RemoveData`는 복구할 수 없으므로 필요한 현장 이력을 먼저 내부 보안 절차에 맞춰 백업합니다.
