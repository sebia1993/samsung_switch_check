# Samsung Switch Watch v0.7.0-poc

## 핵심 변경

- Viewer의 선택 장비 상세 화면에 **장비 명령** 탭을 추가했습니다.
- 관리자가 설치 때 명시적으로 켠 경우에만 한 줄 읽기 전용 `show` 조회를 실행할 수 있습니다.
- Agent가 명령을 자격 증명 조회와 TCP 연결 전에 다시 검사하며 설정·계정·인증·암호 관련
  조회와 제어문자 및 명령 구분자를 차단합니다.
- `show port status`, `show syslog tail num 100`, 현장 표기인
  `show sylog tail num 100`을 포함한 안전한 조회 계열을 지원합니다. 모델·펌웨어가 명령을
  지원하지 않으면 설정 변경 없이 오류로 표시합니다.
- 조회 결과는 UTF-8 기준 최대 64KiB만 Viewer의 현재 실행 메모리에 표시합니다. Agent DB,
  이벤트, 원문 보관, Viewer 설정, 명령 이력과 CSV/JSON 내보내기에 저장하지 않습니다.
- Agent snapshot의 `features.readOnlyQueries`로 기능 사용 여부와 명령·출력 한도를 협상합니다.
  구형 또는 비활성 Agent에서는 Viewer 탭을 이유와 함께 비활성화합니다.
- 관리자 권한이 없는 PC에서도 Agent 창을 노출하지 않는 현재 사용자 예약 작업 모드를
  추가했습니다. 숨김 설치·실행·제거 스크립트는 Agent ZIP 안에 포함됩니다.
- 숨김 모드의 자격 증명은 `set-switch-credential.ps1 -CurrentUser`로 안전하게 갱신하며,
  Agent 시작·종료 코드는 크기가 제한된 로컬 수명주기 로그에만 남깁니다.

## 설치와 업데이트

기능 기본값은 **사용 안 함**입니다. 새 Windows 서비스 설치에서 사용할 때만 다음 옵션을
추가합니다.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -SwitchesJsonPath C:\Temp\switches.json `
  -ViewerRemoteAddress 10.20.30.41 `
  -EnableReadOnlyQueries
```

현재 사용자 숨김 설치에서도 필요할 때만 `install-agent-background.ps1`에 같은 옵션을
추가합니다. Repair에서 옵션을 생략하면 기존 사용 여부와 제한값을 보존합니다. 비활성 설치를
업데이트하면서 활성화할 때만 Repair 명령에도 옵션을 명시합니다.

안전 기본값은 명령 128자, 결과 65,536바이트, Viewer IPv4별 분당 12회, 장비 잠금 대기 5초,
전체 요청 60초입니다. Agent와 Viewer 사이에는 여전히 암호화와 사용자 인증이 없으므로 고정
Viewer IPv4 방화벽과 격리된 사내 관리망이 유일한 접근 경계입니다.

## 업그레이드 순서

Agent를 먼저 Repair하고 Viewer를 이어서 업데이트하십시오.

```powershell
.\install-agent.ps1 `
  -SourceDirectory . `
  -Repair -ReuseData `
  -ViewerRemoteAddress 10.20.30.41
```

위 명령은 기존 기능 활성화 값을 보존합니다. DB·WAL/SHM, snapshot/event/raw/audit,
스위치 자격 증명과 장비 설정도 기존 Repair 계약에 따라 보존됩니다.

## 현장 검증 필요

합성 Telnet 서버와 mock Agent 검증은 실제 삼성 펌웨어의 CLI 차이를 대신하지 않습니다.
`IES4224GP`, `IES4028XP`, `IES4226XP` 각 모델에서 다음을 확인해야 합니다.

- 허용 명령 성공, 미지원 명령의 안전한 실패와 프롬프트 복귀
- 페이징, 긴 출력 64KiB 잘림, 세션 조기 종료와 한 번 재접속
- 장비별 기존 주기 수집과 수동 조회가 서로 굶기지 않음
- 취소·30초 명령 제한·60초 전체 제한 뒤 Telnet 세션 정리
- 명령과 출력 본문이 DB, 감사, 진단, 내보내기에 남지 않음

## GitHub Release Assets

사용자 정의 Assets에는 실행 파일과 설치 자료를 포함한 다음 ZIP 두 개만 게시합니다.

- `SamsungSwitchWatch-Agent-0.7.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.7.0-poc-win-x64.zip`

`BUILD-MANIFEST.json`, SPDX/CycloneDX SBOM과 `SHA256SUMS.txt`는 Actions 내부 검증용이며
별도 Release Asset으로 게시하지 않습니다. 각 ZIP 안에는 해당 패키지의 매니페스트와 SBOM이
포함됩니다.

숨김 실행 스크립트는 Agent ZIP 내부에만 포함되며 별도 Release Asset으로 게시하지 않습니다.
