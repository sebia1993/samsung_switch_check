# Samsung Switch Watch 0.10.1-poc 릴리스 노트

## 요약

이번 패치 버전은 새 기능보다 Agent 설치 복구, Viewer 연결 판정, 장시간 Telnet 조회와
자동 릴리스의 안정성을 보강합니다.

```text
Agent PC: 최초 1회 Agent Setup → 창 없는 Windows 서비스
Viewer PC: 포터블 Viewer 실행 → 장비 등록·show 명령·결과 확인
```

Agent가 장비와 계정 정보를 저장하지 않고 Viewer가 장비·자격 증명·감시를 소유하는
API v4 구조와 화면 흐름은 바뀌지 않습니다.

## 안정성 개선

### Viewer와 Agent 연결 판정

- Identity API가 403, 404, 503 또는 잘못된 본문을 반환하면 Viewer가 더 이상
  `연결됨` 상태로 남지 않습니다.
- API 버전, Agent 신원과 HTTPS 인증서 공개키 확인이 끝난 뒤에만 `연결됨`을 표시합니다.
- 장비 명령 자체가 차단되거나 실패한 경우에는 Agent 통신까지 끊긴 것으로 오판하지 않는
  기존 동작을 유지합니다.

### Telnet 세션 유지시간 대응

- 한 요청에서 7~8개의 읽기 전용 `show` 명령을 실행할 때 총 세션 제한을 넘기기 전에
  안전한 복수 세션으로 나눕니다.
- 결과 순서, 부분 성공 정보, 전체 세션 수와 실제 재접속 수를 보존합니다.
- 장비가 명령 중 세션을 닫은 경우에만 남은 명령을 대상으로 최대 한 번 재접속하는 제한은
  그대로 유지합니다.
- 주 사용 명령인 `show port status`, `show syslog tail num 100` 또는
  `show sylog tail num 100`은 모델이 지원하는 경우 동일하게 사용할 수 있습니다.

### Agent Setup 롤백

- 업데이트 실패 뒤 이전 Agent 폴더 복원은 끝났지만 ACL 적용이 실패한 경우, 다음 Setup
  실행이 보존된 트랜잭션을 자동으로 이어서 복구할 수 있습니다.
- 복원 완료된 폴더를 다시 이동하지 않고 필요한 ACL과 나머지 상태만 재적용합니다.
- 사전 점검의 경로 문구를 실제 검사 범위에 맞춰 수정했습니다. 경로 형식과 상위 폴더는
  사전 점검에서, 실제 쓰기 권한과 EDR 허용 여부는 설치 단계에서 확인합니다.

## 릴리스와 문서 재현성

- GitHub Release는 빈 초안의 숫자 ID를 먼저 확인한 뒤 Agent와 Viewer ZIP을 개별
  업로드합니다. 공개 전 업로드 실패 시 검증한 해당 초안만 정리할 수 있습니다.
- 블록형과 한 줄형 GitHub Actions PowerShell 모두 ASCII와 구문 계약을 검사합니다.
- Agent Setup 화면을 포함한 비식별 WPF 스크린샷 7개와 정확한 문서 재생성 명령을
  저장소에서 관리합니다.
- ManualCapture의 Agent Setup 프로젝트 참조를 NuGet 잠금 파일에 반영했습니다.

## 유지되는 동작과 보안 경계

- Viewer가 장비 IPv4, 모델, ID, 로그인 PW와 선택적 enable PW를 입력합니다.
- 자격 증명은 Viewer PC의 현재 Windows 사용자 DPAPI로 보호합니다.
- Agent는 장비·자격 증명·명령·원문 결과·감시 이력을 저장하지 않습니다.
- Viewer가 종료되면 주기 감시도 중단됩니다.
- 한 줄 `show` 명령만 허용하고 설정 변경 명령과 구분자는 Viewer와 Agent 양쪽에서
  차단합니다.
- Viewer→Agent는 HTTPS/TCP 18443, Agent→스위치는 선택 관리망의 Telnet/TCP 23만
  사용합니다.
- 인증서 지문과 페어링 토큰을 사용자가 입력하지 않습니다. 고정 Viewer IPv4 방화벽
  제한이 Agent API의 접근 경계입니다.

## 설치 및 업데이트

1. 이 Release의 Agent ZIP과 Viewer ZIP을 각각 완전히 압축 해제합니다.
2. Agent PC에서 `SamsungSwitchWatch.Agent.Setup.exe`를 실행하고 설치/업데이트합니다.
3. Viewer PC에서는 새 ZIP의 `SamsungSwitchWatch.Viewer.exe`를 직접 실행합니다.
4. Agent와 Viewer가 모두 `0.10.1-poc`인지 확인합니다.
5. Agent 연결, 장비 접속 시험, 단일 읽기 전용 명령, 주기 감시 순서로 확인합니다.

이 POC는 코드 서명되지 않았습니다. Release 본문의 SHA-256과 ZIP 파일을 비교하고
SmartScreen, EDR, AppLocker 또는 WDAC를 우회하지 말고 사내 승인 절차를 따르십시오.

## 현장 확인 필요

다음 항목은 Mock·Fixture로만 확인했으며 실제 사내 장비 검증이 필요합니다.

- IES4224GP, IES4028XP, IES4226XP의 로그인·enable·프롬프트 차이
- `show port status` 출력 형식
- `show syslog tail num 100`과 `show sylog tail num 100` 지원 여부
- 5분 VTY timeout 환경에서 복수 세션과 제한적 재접속 동작
- 실제 UAC, Windows 서비스, 방화벽과 롤백 재개
- 사내 방화벽, EDR, AppLocker 또는 WDAC에서 서명되지 않은 POC 실행 파일 허용 여부

첫 적용에서는 Agent 한 대, 고정 Viewer 한 대와 영향이 적은 스위치 한 대만 연결한 뒤
읽기 전용 명령으로 단계적으로 확대하십시오.
