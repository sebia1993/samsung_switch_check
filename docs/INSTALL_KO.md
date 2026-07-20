# Samsung Switch Watch 설치 안내

## 1. 배포 파일 검증

공식 GitHub `v0.4.0-poc` Release에서 Agent·Viewer ZIP과 다음 파일을 같은 폴더에
내려받습니다.

- `BUILD-MANIFEST.json`
- `SBOM.spdx.json`, `SBOM.cdx.json`
- `SHA256SUMS.txt`

```powershell
Get-FileHash .\SamsungSwitchWatch-*-0.4.0-poc-win-x64.zip -Algorithm SHA256
.\scripts\test-package-contract.ps1 -ReleaseDirectory . -Version 0.4.0-poc
```

GitHub의 SHA-256 및 provenance와 일치하지 않으면 설치하지 마십시오. ZIP은 최종 설치
폴더 바깥의 임시 폴더에 각각 압축 해제합니다.

## 2. Agent PC 준비

Agent PC는 다음 조건을 충족해야 합니다.

- Windows x64, 관리자 PowerShell 사용 가능
- 스위치 관리 IPv4로 TCP/23 접근 가능
- Viewer PC에서 Agent TCP/18443 접근 가능
- 스위치와 같은 격리 관리망 또는 승인된 관리 경로
- 읽기 전용 Telnet 계정과 중요 업링크 포트 식별값 준비

실제 비밀번호를 JSON·명령줄·문서에 기록하지 마십시오.

## 3. Agent 설치

먼저 변경 없는 사전 검사를 실행합니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -MockMode `
  -SwitchModel IES4224GP `
  -SwitchHost 192.0.2.10 `
  -ViewerRemoteAddress 192.0.2.50 `
  -Preflight
```

위 명령은 문서용 주소를 쓰는 로컬 사전 점검 예시입니다. 실제 설치를 사전 점검할 때는
`-MockMode`를 빼고 `-SwitchHost`와 `-ViewerRemoteAddress`를 회사망의 실제 값으로 바꾸십시오.

검사가 통과하면 `-Preflight`를 빼고 설치합니다. 예시 주소는 실제 관리망 값으로 바꾸되
외부 자료에 복사하지 마십시오.

### 여러 스위치 등록

[switches.example.json](examples/switches.example.json)을 회사 PC에서 복사한 다음, 모든
`Host` 값을 실제 스위치 관리 IPv4 주소로 바꾸고 실행합니다. 예제의 `192.0.2.0/24`는
문서 전용 주소이므로 실환경 설치기가 의도적으로 거부합니다. 파일에는 비밀번호를 넣지
않습니다. 배포용 Agent ZIP에서는 같은 예제가 ZIP 루트의 `switches.example.json`으로
포함됩니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -SwitchesJsonPath C:\Temp\switches.json `
  -ViewerRemoteAddress 192.0.2.50
```

Agent 한 대에 최대 256개 장비를 등록하며 `Id`는 대소문자 구분 없이 고유해야 합니다.
지원 모델은 `IES4224GP`, `IES4028XP`, `IES4226XP`입니다.

설치기는 다음 작업을 트랜잭션 형태로 수행합니다.

- 패키지 매니페스트와 EXE SHA-256 확인
- 프로그램 staging·교체와 실패 시 rollback
- SQLite DB와 `-wal`, `-shm` 일관 백업
- LocalService 서비스와 데이터 ACL 설정
- 가능한 경우 `LocalMachine\My` 비내보내기 인증서 생성
- Viewer 단일 IPv4 범위의 소유된 방화벽 규칙 생성
- 설치 receipt와 단계별 operation journal 저장

## 4. 스위치 자격 증명 저장

각 `CredentialId`마다 관리자 PowerShell에서 실행합니다. 비밀번호는 대화형으로 입력되어
명령 기록에 남지 않습니다.

```powershell
.\set-switch-credential.ps1 `
  -CredentialId samsung-switch-readonly-01 `
  -Username monitor-readonly
```

등록되지 않은 CredentialId는 거부됩니다. 설정 뒤 Agent readiness와 진단을 확인합니다.

```powershell
.\diagnose-agent.ps1 -OutputPath C:\Temp\ssw-diagnostic.json
```

진단 JSON은 원문과 실제 주소를 포함하지 않지만 외부 반출 전 회사 정책을 확인하십시오.

## 5. Viewer 설치와 페어링

운영자 PC의 일반 사용자 PowerShell에서 Viewer를 설치합니다.

```powershell
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows -Preflight
.\install-viewer.ps1 -SourceDirectory . -StartWithWindows
```

Agent PC의 관리자 PowerShell에서 10분 유효 일회용 코드를 만듭니다.

```powershell
.\new-pairing-code.ps1
```

Viewer PC에서 Agent 설치 결과의 SHA-256 지문과 코드를 사용해 토큰을 교환합니다.

```powershell
.\pair-viewer.ps1 `
  -AgentUri https://192.0.2.20:18443 `
  -CertificateFingerprint <64자리-SHA256>
```

출력된 주소·지문·토큰을 Viewer 연결 설정에 한 번 입력합니다. Viewer는 토큰을 현재 사용자
DPAPI로 보호합니다. PowerShell 창과 클립보드에 남은 토큰은 즉시 지웁니다.

## 6. 인증서 회전과 dual pin

만료 경고가 60일 상태에 들어오면 회전을 계획합니다. Agent PC에서 새 인증서를 미리 만들고
아직 활성화하지 않습니다.

```powershell
.\new-agent-certificate.ps1
```

출력된 새 store thumbprint와 SHA-256을 별도 경로로 확인합니다. 모든 Viewer 설정에 현재와
새 SHA-256 pin을 최대 2개까지 등록해 연결을 확인한 다음 Agent를 전환합니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -Repair -ReuseData -RotateCertificate `
  -RotationCertificateThumbprint <새-40자리-store-thumbprint> `
  -CertificateOverlapDays 7
```

새 인증서는 `LocalMachine\My`에 비내보내기 개인키로 생성되며 서비스 SID에만 개인키 읽기
권한을 줍니다. 중첩 기간은 최대 14일입니다. 새 연결을 확인한 뒤 이전 pin을 Viewer에서
제거하십시오.
Agent 응답에 나온 pin을 자동 신뢰하지 말고 설치 콘솔의 지문을 별도 경로로 대조합니다.

## 7. 복구 설치

프로그램 파일만 다시 배치하면서 DB·자격 증명·설정·인증서를 유지하려면 다음을 사용합니다.

```powershell
.\install-agent.ps1 -SourceDirectory . -Repair -ReuseData
```

`-ReuseData`는 이전 설치 receipt와 제품 경계를 확인하지 못하면 중단합니다. 실패하면 설치
journal을 바탕으로 서비스, 프로그램, DB/WAL/SHM, 방화벽과 인증서를 이전 상태로 되돌립니다.

Viewer 재설치도 staging, EXE smoke와 바로가기 rollback을 적용합니다.

## 8. 제거

기본 제거는 Agent 데이터를 보존합니다.

```powershell
.\uninstall-agent.ps1
.\uninstall-viewer.ps1
```

Agent DB·자격 증명·receipt와 설치기가 소유한 인증서까지 제거하려면 명시적으로 실행합니다.

```powershell
.\uninstall-agent.ps1 -RemoveData
```

`-RemoveData`는 복구하기 어려운 작업입니다. 제품 receipt와 절대 경로가 정확한지 먼저
확인하십시오. 설치기가 소유하지 않은 방화벽 규칙과 인증서는 제거하지 않습니다.

## 9. 정상 확인

- 서비스 `SamsungSwitchWatchAgent`가 실행 중
- `https://<Agent>:18443/health/live` 응답
- `/health/ready`가 `ready`, 또는 표시된 안정 오류 코드로 원인 구분
- Viewer의 API 상태와 SignalR 상태가 별도로 정상
- 세 모델별 capability가 실제 펌웨어에서 확인됨
- RDP를 종료해도 Agent 점검 지속
- Viewer 재연결 후 누락 이벤트가 catch-up 요약 1건으로 표시

`agentLive=OK`, `agentReady=AGENT_NOT_READY_503`이면 프로세스는 동작하지만 DB·인증서·
자격 증명·스케줄러·필수 수집기 중 하나가 준비되지 않은 상태입니다. 모든 스위치를 Down으로
판정하지 말고 readiness 코드와 마지막 정상 수신 시각을 확인하십시오.
