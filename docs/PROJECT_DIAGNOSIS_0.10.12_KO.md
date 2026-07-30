# Samsung Switch Watch v0.10.12 프로젝트 진단 및 개선 계획

기준: `v0.10.12-poc` 릴리스 후보 소스

범위: Agent Setup, Agent 서비스, Viewer, Core, 로컬 저장소, 진단, 테스트, Windows 배포

평가 원칙: 실제 코드에서 확인된 사실과 현장 검증이 필요한 사항을 구분한다.

## 1. 프로젝트 목적과 현재 구조 요약

### 목적

Samsung Switch Watch는 스위치 관리망에 접근할 수 있는 원격 Windows PC의 Agent가 삼성 iES
스위치에 Telnet으로 접속하고, 운영자 PC의 Viewer가 장비·계정·명령·감시·이력을 소유하는
Windows 전용 도구다.

### 실제 동작 흐름

1. 운영자는 Agent PC에서 `SamsungSwitchWatch.Agent.Setup.exe`를 관리자 권한으로 실행한다.
2. Setup에서 허용할 Viewer PC 고정 IPv4와 1~2개의 사설 관리망 CIDR을 선택한다.
3. Setup은 패키지 무결성, 설치 경로, 서비스, HTTPS/18443, 방화벽 규칙과 readiness를 검사하고
   `SamsungSwitchWatchAgent` 무창 Windows 서비스를 설치한다.
4. 운영자는 Viewer에서 Agent 주소를 입력하고 DNS, TCP/18443, HTTPS identity, API 버전을
   순서대로 검사한다.
5. Viewer에서 장비명, 모델, 장비 IPv4, ID, 로그인 PW와 선택적 enable PW를 등록한다.
6. Viewer는 Agent API v4에 접속 시험 또는 읽기 전용 `show` 명령 실행을 요청한다.
7. Agent는 요청마다 새 Telnet 세션을 만들고, 허용 CIDR·TCP/23·명령 정책을 재검증한 뒤 결과를
   Viewer에 반환하고 세션을 닫는다.
8. Viewer가 실행 중일 때만 주기 감시, 기준선 비교, 신규 로그·상태 변경·복구 이벤트 생성,
   팝업·트레이·미니 창 표시가 동작한다.

근거:

- Agent/Viewer 책임 분리: `AGENTS.md`, `docs/ARCHITECTURE.md`
- API v4: `src/SamsungSwitchWatch.Agent/Api/ApiEndpoints.cs`
- Telnet 실행: `src/SamsungSwitchWatch.Agent/Execution/StatelessTelnetExecution.cs`,
  `src/SamsungSwitchWatch.Core/Telnet/TelnetClient.cs`
- Viewer 감시: `src/SamsungSwitchWatch.Viewer/ViewModels/DashboardViewModel.cs`,
  `src/SamsungSwitchWatch.Viewer/Services/ViewerMonitoringStore.cs`

### 주요 화면

- Agent Setup: Viewer 주소, 관리망, 사전 점검, 설치/업데이트, 이전 상태 복구, 지원 코드와
  익명 진단 저장
- Viewer 대시보드: 정상·경고·문제·미확인 이벤트 요약, 문제 우선 장비 목록, 상태·새 로그·
  변경 이력·장비 명령 탭, 최근 이벤트와 CSV/JSON 내보내기
- Agent 연결: 동일 PC 시험 또는 원격 Agent 주소 진단
- 장비 관리: 장비 IP·모델·ID·PW·enable PW, 접속 시험, 저장, 주기 감시
- 미니 창·트레이·알림 팝업

근거: `src/SamsungSwitchWatch.Agent.Setup/MainWindow.xaml`,
`src/SamsungSwitchWatch.Viewer/MainWindow.xaml`,
`src/SamsungSwitchWatch.Viewer/Views/ConnectionSettingsWindow.xaml`,
`src/SamsungSwitchWatch.Viewer/Views/DeviceManagementWindow.xaml`

### 실행·설치 환경

- Windows x64 전용, .NET 10 self-contained·single-file·untrimmed 배포
- 사내 PC에 Python이나 .NET 런타임이 없어도 공개 ZIP으로 실행 가능
- Agent Setup은 서비스와 방화벽 구성을 위해 UAC가 필요
- Viewer는 포터블이며 일반 사용자 권한으로 실행
- 공개 Release Asset은 Agent ZIP과 Viewer ZIP 두 개만 허용

근거: 각 `.csproj`, `scripts/build-release.ps1`,
`scripts/test-package-contract.ps1`, `.github/workflows/release.yml`

### 설정·인증정보·저장

- Agent: `%ProgramData%\SamsungSwitchWatch` 아래 identity와 최소 설정만 보관
- Viewer: `%LOCALAPPDATA%\SamsungSwitchWatch` 아래 설정, 장비, 감시 상태와 진단 로그 보관
- 장비 자격 증명: Windows DPAPI CurrentUser 보호
- Agent HTTPS 개인키: DPAPI LocalMachine 보호
- Agent는 장비 목록, 계정, 명령, 결과와 이벤트 이력을 영구 보관하지 않음
- 수동 명령 원문과 수동 출력은 Viewer 메모리에만 유지

근거: `CurrentUserSecretProtector`, `ManagedDeviceStore`, `ViewerSettingsStore`,
`ViewerMonitoringStore`, `AgentIdentity`

### 로그·오류·종료

- 사용자 화면에는 안정적인 오류 코드와 한국어 조치 문구를 표시
- Viewer 개발자 진단 로그는 허용 목록의 단계·오류 코드·버전·UTC 시각만 JSONL로 기록
- Viewer 진단 로그는 1 MiB에서 1회 순환하며 장비 주소·계정·명령·출력을 기록하지 않음
- Setup과 Viewer는 민감정보가 없는 `SSW_FIELD_DIAGNOSTIC/1` 파일과 짧은 SWD1 지원 코드를 제공
- Viewer 종료 시 lifetime 취소, 감시 루프·클라이언트·단일 인스턴스 자원을 제한 시간 안에서 정리
- Telnet 전송은 성공·실패·취소 경로의 `finally`에서 닫힘

근거: `ViewerDiagnosticLog`, `ViewerFieldDiagnostic`, `SetupUiPresentation`,
`DashboardViewModel.DisposeAsync`, `App.xaml.cs`, `TelnetClient`

### 테스트

현재 자동 검증은 총 950개 테스트와 PowerShell 계약 검사로 구성된다. 합성 Telnet 서버, 고정
Fixture, 저장 실패, 인증 실패, 타임아웃, 재접속, 감시 중복, 취소, 종료, Setup rollback,
방화벽 소유권, 진단 스키마와 패키지 계약을 포함한다.

실제 삼성 스위치 펌웨어, 사내 EDR, 원격 방화벽·라우팅, UAC를 포함한 전체 네이티브 설치는
집의 개발 환경에서 증명할 수 없다.

### 설명과 코드의 차이

초기 기획의 “Agent가 SQLite·스케줄러·이력·SignalR을 소유”하는 구조는 현재 코드가 아니다.
현재 v0.10 구조는 Agent가 무상태 중계기이고 Viewer가 감시와 이력을 소유한다. 현재
`README.md`와 `docs/ARCHITECTURE.md`는 실제 코드와 일치하지만, 오래된 v0.7 설명을 운영 기준으로
사용하면 안 된다.

## 2. 전체 평가 점수

| 평가 항목 | 점수 | 판단 |
|---|---:|---|
| 초급 엔지니어 사용성 | 7.5/10 | 화면 안내와 오류 분류는 좋지만 최초 Agent 네트워크 입력과 현장 보안정책 대응은 여전히 어렵다. |
| 관리자 사용성 | 6.5/10 | 문제 우선 요약·지속 시간·확인 상태·익명 내보내기는 좋지만 영향도·담당자·권장 조치 요약은 제한적이다. |
| 프로그램 안정성 | 8.0/10 | 타임아웃·출력 제한·동시성 제한·원자 저장·취소·rollback·950개 테스트가 강점이다. 실장비와 네이티브 설치 현장 검증은 남아 있다. |
| 유지보수성 | 6.5/10 | Core/Agent/Viewer 경계와 테스트는 좋지만 `DashboardViewModel` 4,276줄, `AgentDeploymentOrchestrator` 2,297줄의 변경 결합도가 높다. |

## 3. 가장 잘 구현된 부분

1. **권한과 데이터 소유권이 명확하다.** Viewer만 장비·자격 증명·감시 이력을 소유하고 Agent는
   요청 단위 실행만 담당한다.
2. **읽기 전용 방어가 중복 적용된다.** Viewer와 Agent 양쪽에서 한 줄 `show` 명령을 검사하고,
   Agent는 관리 CIDR·TCP/23·요청 크기·명령 수·출력 크기를 다시 제한한다.
3. **장비 부하 억제가 구체적이다.** 전체 동시 실행 2개, 장비당 1개 세션, 분당 60개 요청,
   세션 최대 240초, 즉시 세션 종료 시 미완료 명령만 최대 1회 재접속한다.
4. **실패를 정상으로 위장하지 않는다.** Loading, Deferred, 확인 불가와 마지막 정상 값을
   구분하고, 저장소 실패와 수집 실패를 경고 상태로 표시한다.
5. **설치 실패 증거가 보존된다.** Setup은 staging·backup·failed·journal을 이용해 transaction과
   rollback을 수행하고, 복구 실패 시 원본 실패와 복구 실패를 분리해 기록한다.
6. **진단정보가 사내 반출 제약을 고려한다.** 원문 대신 안정적인 코드·단계·시간만 제공하고,
   SWD1 지원 코드는 짧게 전달할 수 있다.
7. **테스트가 실제 위험에 가깝다.** 합성 Telnet, 저장 손상, 취소 경쟁, Viewer 교체,
   방화벽 readback, rollback과 ZIP 추출 후 실행 파일 smoke를 검사한다.

## 4. 가장 위험한 문제

### 코드에서 확인된 사실

- `DashboardViewModel`이 연결, 감시, 상태 투영, 이벤트 순서, 명령, 저장 오류와 종료를 함께
  담당한다. 테스트가 많아도 작은 수정의 회귀 범위가 넓다.
- `AgentDeploymentOrchestrator`가 설치, 업데이트, 서비스, 파일 교체, 방화벽, readiness,
  rollback과 journal 복구를 한 클래스에서 조정한다.
- Viewer가 종료되면 주기 감시도 끝난다. Agent는 독립적인 수집기가 아니다.

### 현장 검증이 필요한 사항

- IES4224GP, IES4028XP, IES4226XP의 실제 펌웨어별 프롬프트, 페이징, 한글 인코딩과
  `show port status`, `show sylog tail num 100` 지원 여부
- Telnet 수신·송신의 고정 Latin-1 처리에서 장비의 비ASCII 출력이 손실되거나 파싱 불가가 되는지
- `exec-timeout 5 0` 환경에서 명령 중 장비가 세션을 닫을 때 1회 재접속 정책이 충분한지
- 사내 EDR·AppLocker·WDAC가 미서명 POC 실행 파일, 서비스 설치와 자체 서명 HTTPS를 허용하는지
- 원격 Viewer/Agent 사이 TCP/18443, 정확한 Viewer `/32`와 관리망 라우팅
- UAC를 포함한 실제 Windows 서비스 설치·업데이트·rollback 전체 경로

이 항목은 Mock 통과를 실제 장비 검증으로 표현하면 안 된다.

## 5. 초급 사용자 관점 문제점

| 문제 현상 | 어려운 이유와 업무 영향 | 관련 코드/화면 | 개선 방법 | 난이도 | 우선순위 |
|---|---|---|---|---:|---:|
| Agent 최초 설정에서 Viewer IP와 관리망 CIDR을 구분해야 함 | 스위치 IP, Agent IP, Viewer IP를 혼동하면 연결 거부 또는 대상 차단이 발생 | `Agent.Setup/MainWindow.xaml`, `ViewerAddressSuggestion`, `NetworkDiscovery` | 현재 단계 번호·자동 검색을 유지하고, 입력란 옆에 “입력하는 PC”와 “입력하지 않는 주소”를 계속 명시. 현장 체크리스트로 원격 배치 전 `/32` 재적용 확인 | 중 | P1 |
| Agent와 Viewer 버전이 다르면 연결되지 않음 | ZIP을 따로 보관하면 원인을 네트워크 장애로 오해할 수 있음 | `HttpAgentClient`, `AgentConnectionProbe`, 연결 화면 | 첫 실패 문구에서 “같은 Release의 두 ZIP 사용”을 최상단 조치로 유지하고 버전을 항상 나란히 표시 | 하 | P1 |
| Setup은 UAC와 방화벽 권한이 필수 | 일반 Viewer와 달리 관리자 승인이 필요해 설치 실패로 오해 | Agent Setup manifest, `WindowsAdministratorChecker` | 시작 화면과 매뉴얼에서 “Agent만 1회 UAC, Viewer는 UAC 없음”을 동일 문구로 유지 | 하 | P1 |
| 저장된 자격 증명은 다른 Windows 사용자에게 이동되지 않음 | DPAPI 특성을 모르면 파일 복사 후 비밀번호 손상으로 보임 | `CurrentUserSecretProtector`, `ManagedDeviceStore` | `VIEWER_CREDENTIAL_CORRUPT`에서 계정 재입력과 감시 재활성화 순서를 바로 표시 | 하 | P1 |
| 원시 `show` 출력은 초급자가 해석하기 어려움 | 현상·원인·다음 조치를 구분하기 어려움 | 장비 명령 탭, `ViewerMonitoringStore` | 자동 감시 이벤트에는 현상과 확인 순서를 유지하고 원시는 별도 탭에만 둔다. 자유 명령 결과를 자동 장애 판정에 사용하지 않는다. | 중 | P1 |
| Viewer를 닫아도 트레이 감시는 계속되지만 완전 종료 시 감시가 중단됨 | 창 닫기와 프로그램 종료의 차이를 놓칠 수 있음 | `TrayIconService`, `App.xaml.cs` | 트레이 안내에 감시 대수와 “완전 종료 시 감시 중단”을 계속 표시 | 하 | P1 |
| 오류 코드가 많음 | 코드 자체보다 어떤 PC에서 무엇을 확인할지가 중요 | `AgentClientException`, `DeviceManagementFailureMapper`, Setup 결과 | 사용자 문구는 “원인 → 확인 위치 → 다음 행동” 순서를 유지하고 원시 예외는 노출하지 않음 | 중 | P1 |

## 6. 관리자 관점 문제점

| 문제 현상 | 업무 영향 | 관련 코드/화면 | 개선 방법 | 난이도 | 우선순위 |
|---|---|---|---|---:|---:|
| 장비 상태와 이벤트는 문제 우선 정렬되지만 업무 영향도는 별도 모델이 아님 | 업링크·일반 포트·미사용 포트의 우선도를 관리자 화면만으로 완전히 판단하기 어려움 | `SortDevicesByPriority`, `EventDisplayPriority`, `ViewerMonitoringStore` | 기존 심각도 체계를 유지하고 중요 포트 역할이 검증된 경우에만 영향 문구를 강화. 추측으로 영향도를 만들지 않음 | 중 | P1 |
| 확인 처리는 있으나 담당자·조치 내용 이력은 없음 | 인수인계와 사후 보고에서 누가 무엇을 했는지 별도 기록 필요 | `EventViewModel`, `AcknowledgeAsync` | 단기에는 익명 이벤트 CSV와 운영일지를 함께 사용. 중장기는 저장 형식 호환을 지키는 별도 조치 메타데이터 검토 | 중 | P2 |
| CSV/JSON은 안전한 이벤트 내보내기이지 완성된 관리자 보고서가 아님 | 바로 보고하기에는 요약·기간·영향·조치 칸이 부족 | `ViewerExportService`, `MainWindow.xaml.cs` | 기존 익명 내보내기를 유지하고, 우선 문서 템플릿으로 보완. UI 전면 교체나 원문 자동 포함은 금지 | 중 | P2 |
| Viewer가 실행 중인 동안만 감시 | 운영자 PC 종료 시 중앙 관제가 중단 | 현재 v0.10 책임 구조 | 현재 핵심 구조를 임의로 바꾸지 않는다. 항상 켜진 운영자 PC 사용 여부를 운영 규정으로 명시하고, 중앙 수집 구조는 별도 버전의 아키텍처 결정으로 취급 | 상 | P2 |
| 장애 원인보다 증상 중심인 이벤트가 존재 | 장비 출력만으로 원인을 확정할 수 없어 조치 판단에 추가 점검 필요 | `DeviceObservationEngine`, `ViewerMonitoringStore` | “확인된 사실”과 “권장 확인”을 분리하고 원인으로 단정하지 않음 | 중 | P1 |

관리자 첫 화면의 정상·경고·문제·미확인 이벤트 수, 문제 우선 정렬, 지속 시간, 복구·확인 상태는
적절하다. 다음 개선은 화면 재설계보다 영향·조치 문구의 정확성 향상이 우선이다.

## 7. 안정성 문제점

### 예외 처리

- 네트워크, 인증, 프롬프트, 명령 시간 초과와 출력 초과는 안정적인 코드로 분류된다.
- 저장소 손상은 정상 상태로 진행하지 않고 격리 또는 확인 불가로 전환한다.
- `ViewerDiagnosticLog.WriteLine`의 광범위한 `catch`는 진단 기록 실패가 본 기능을 중단하지
  않도록 한 의도적 예외다. 이 로그 실패를 업무 성공으로 판단하는 코드에는 사용하지 않는다.
- 예상하지 못한 Setup 예외는 예외 원문 대신 허용된 단계·범주·소요 시간으로 투영한다.

### 타임아웃·재시도

- Core는 연결·로그인·인증·쓰기·명령·로그아웃·전체 세션 시간 제한을 가진다.
- Agent 기본 세션 상한은 240초, Viewer 읽기 전용 HTTP 상한은 510초다.
- 장비가 즉시 세션을 닫은 경우 인증·enable 실패나 명령 타임아웃은 재시도하지 않고,
  완료되지 않은 명령만 최대 1회 재접속한다.
- 출력은 Agent 64 KiB, Viewer 응답 파서는 경로별 상한을 적용한다.
- 무한 재시도 또는 무제한 출력은 코드에서 확인되지 않았다.

### 비동기·동시성

- Viewer는 lifetime token, 초기화·동기화 gate, 장비별 gate, 감시 gate와 전체 동시 2개 제한을
  사용한다.
- Agent도 전체 동시 실행 2개와 장비별 1개를 적용한다.
- v0.10.12는 Agent 클라이언트 교체 중 진행 요청을 취소·drain하고, 3초 안에 정리되지 않아도
  새 연결 성공을 이전 클라이언트 정리 실패로 덮어쓰지 않도록 보강했다.
- 남은 위험은 `DashboardViewModel` 내부 상태가 많아 새로운 경로 추가 시 gate·revision·UI
  dispatcher 규칙을 누락하기 쉽다는 점이다.

### 자원 정리

- Telnet 연결은 `finally`에서 닫힌다.
- Viewer 종료는 감시·snapshot 루프를 취소하고 제한 시간으로 기다린다.
- Setup은 프로세스 전역 gate와 journal을 사용하며 rollback 증거를 함부로 삭제하지 않는다.
- 현장에서는 장비의 VTY 세션이 실제로 사라지는지 별도 확인이 필요하다.

### 데이터 무결성

- Viewer 설정·장비·감시 상태는 임시 파일 작성 후 교체하는 원자 저장을 사용한다.
- 스키마 버전, 중복 이벤트, 활성 장애 참조, 손상 자격 증명과 부분 상태를 검증한다.
- 수집 실패 시 마지막 정상 값은 참고값으로 남지만 현재 정상으로 합산하지 않는다.
- 날짜 저장은 UTC, 화면은 로컬 시각 변환을 사용한다.
- 저장 스키마 변경 시 기존 v1 호환 fixture를 반드시 유지해야 한다.
- 임시 파일 후 교체는 부분 JSON을 방지하지만 명시적인 `Flush(true)`/WriteThrough가 없어
  갑작스러운 전원 손실에서 가장 최근 변경의 디스크 내구성은 추가 검증이 필요하다.

### 로그·진단

- Viewer 진단 로그는 허용 목록과 순환 정책이 있다.
- 연결 실패·복구는 동일 상태 중복 기록을 억제한다.
- v0.10.12 진단 replay는 기존 v1, health 확장 v1, 현재 failure 확장 v1을 모두 읽는다.
- 원문 장비 출력은 외부 반출용 진단에 포함되지 않는다.

## 8. P0, P1, P2 우선순위 개선 목록

| 우선순위 | 구분 | 문제/발생 조건 | 사용자 영향 | 원인 | 관련 코드 | 개선 방법 | 검증 방법 | 예상 범위 |
|---|---|---|---|---|---|---|---|---|
| P0 완료 | 설치 | 구형 API v4 readiness의 선택 필드 부재를 신규 필수값처럼 처리 | 정상 구형 Agent도 설치 전 점검 실패 | 프로토콜 세대 차이 | `HttpsAgentHealthProbe` | 사전 점검은 최소 payload 허용, 설치 완료는 HTTPS와 정확한 버전 강제 | legacy/current payload 단위 테스트 | 소 |
| P0 완료 | 진단 | rollback 실패인데 최초 실패 단계가 최종 실패 단계로 표시 | 잘못된 복구 판단 | 최종 결과보다 원 예외 metadata 우선 | `SetupFailureDiagnosticProjection` | 최종 `SETUP_ROLLBACK_FAILED`는 RECOVERY로 표시하고 `PrimaryFailureCode` 보존 | UI·clipboard·field diagnostic 테스트 | 소 |
| P0 완료 | 호환성 | 새 진단 필드가 기존 replay v1 스키마를 깨뜨림 | 사내 진단을 집에서 재생 불가 | exact schema 단일형 | `replay-field-diagnostic.ps1` | legacy·health·current 확장을 all-or-none으로 허용 | 세 형식과 부분·미지 필드 거부 테스트 | 소 |
| P0 완료 | 동시성 | Agent 연결 교체 중 이전 클라이언트 정리가 요청과 경쟁 | 연결 성공이 실패로 바뀌거나 작업 잔존 | 비동기 dispose 경계 | `HttpAgentClient`, `DashboardViewModel` | 요청 lease·취소·bounded drain·best-effort dispose | 교체·취소·종료 경쟁 테스트 | 중 |
| P0 검증 게이트 | 배포 | 추출 ZIP의 실행 파일이 CI에서 실제 시작되지 않으면 누락 DLL·경로 문제를 놓침 | 사내에서만 시작 실패 | 패키지 계약이 정적 검사 중심 | `test-release-executable-smoke.ps1`, `AgentSetupPackageSmokeCheck` | 다운로드 후 Viewer·Mock Agent·Agent Setup 실행 smoke 필수 | Windows CI 관리자 환경 | 중 |
| P1 | 현장 검증 | 실제 펌웨어에서 명령·프롬프트·페이징이 다름 | 수집 실패 또는 오판 | 공개 자료와 fixture 한계 | `TelnetClient`, 모델 프로파일·파서 | 설정 변경 없이 단일 장비·읽기 명령부터 단계 확대 | 세 모델별 현장 체크리스트 | 중 |
| P1 | 보안 검증 | 최초 Agent 연결은 저장된 pin이 없는 TOFU | 최초 연결 경로가 변조되면 중앙 CA 수준의 검증은 제공하지 못함 | 자체 서명 identity 자동 고정 | `HttpAgentClient` | Viewer `/32`·관리망 격리를 유지하고 사내 정책에서 최초 연결 확인 절차 승인 | 격리망 최초 연결·identity 변경 차단 테스트 | 중 |
| P1 | 인코딩 | Telnet이 Latin-1을 고정 사용 | 비ASCII 로그·배너·호스트명이 손실되거나 파싱 실패할 수 있음 | 장비별 문자셋 미확정 | `TelnetClient` | 현장 샘플을 민감정보 제거 fixture로 만들고, 확인된 장비 문자셋만 좁게 지원 | 비ASCII fixture·세 모델 현장 검사 | 중 |
| P1 | 사용성 | IP·CIDR·버전·UAC 흐름 혼동 | 설치·연결 반복 실패 | 두 PC 구조의 필수 복잡성 | Setup/연결/장비 관리 화면 | 현재 단계형 안내와 동일 PC 시험을 유지하고 조치 문구 일관성 회귀 검사 | WPF 화면·문구 smoke, 초급 사용자 시나리오 | 소 |
| P1 | 유지보수 | `DashboardViewModel` 4,276줄 | 작은 수정의 회귀 범위 확대 | 화면·연결·감시·저장 조정 집중 | `DashboardViewModel.cs` | 외부 동작을 유지하며 다음 수정 때 한 책임씩 내부 서비스로 추출 | 기존 427개 Viewer 테스트 | 중 |
| P1 | 유지보수 | 설치 오케스트레이터 2,297줄 | rollback 변경 위험 | transaction 단계 집중 | `AgentDeploymentOrchestrator.cs` | stage 실행과 rollback projection부터 작은 단위로 추출 | 355개 Setup 테스트·journal fixture | 중 |
| P2 | 관리자 | 영향도·담당자·조치 이력 부족 | 즉시 보고·인수인계 한계 | 이벤트가 장비 사실 중심 | 이벤트 모델·내보내기 | 기존 스키마를 깨지 않는 별도 운영 기록 방안 검토 | 구버전 데이터 호환·익명 export | 중 |
| P2 | 사용성 | `CSV`, `JSON` 버튼만으로 익명화 내보내기임을 알기 어려움 | 민감 원문 포함 여부를 오해할 수 있음 | 짧은 버튼명 | `MainWindow.xaml`, `ViewerExportService` | 레이아웃을 바꾸지 않고 접근성 이름과 도움말에 익명화 범위를 명시 | 내보내기 fixture와 WPF 문구 검사 | 소 |
| P2 | 릴리스 | 패키지 검사는 PDF 서명·크기만 확인 | 미래의 오래된 매뉴얼이 포함될 수 있음 | 생성물 버전 검사 없음 | `build-release.ps1`, `test-package-contract.ps1` | PDF metadata 또는 생성 manifest의 버전 일치 검사 | 의도적 stale PDF fixture 거부 | 소 |
| P2 | 진단 | replay의 단계 시간 정규식이 포매터 상한과 의미상 완전히 일치하지 않음 | 경계값 유지보수 혼동 | 숫자 자리수 검사 | `replay-field-diagnostic.ps1` | 0~86,400,000 범위로 명시 검증 | 경계값 테스트 | 소 |
| P2 | 데이터 내구성 | 원자 교체 전에 명시적 디스크 flush가 없음 | 전원 손실 직전 최신 상태가 유실될 수 있음 | 파일시스템 cache 의존 | Viewer 저장소 3종 | 호환 형식을 유지하며 flush 내구성 테스트 후 필요한 저장소만 보강 | 강제 종료·전원 손실 유사 fixture | 중 |
| P2 | 메모리 | 장비 IP별 operation gate를 제거하지 않음 | 주소를 매우 많이 바꾸는 장기 실행에서 작은 누적 가능 | 세마포어 사전 수명 제한 없음 | `DashboardViewModel.GetDeviceOperationGate` | 실제 증가량 측정 후 안전한 참조 수명 정리 검토 | 수천 주소 반복 soak | 중 |
| P2 | 의존성 | v4 무상태 경로에 legacy event route·SignalR package가 남음 | 코드 탐색과 패키지 이해 혼동 | 이전 버전 호환 흔적 | `IAgentClient`, `AgentContractMapper`, Viewer `.csproj` | 사용 참조를 먼저 증명한 뒤 별도 변경으로 제거 검토 | 전체 Viewer 회귀·패키지 smoke | 중 |

현재 릴리스 후보에는 확인된 미수정 P0 코드 결함이 없다. 다만 “P0 검증 게이트”가 Windows CI와
현장 최소 검증에서 통과하기 전에는 사내 전체 배포로 확대하지 않는다.

## 9. 단계별 개선 계획

### 1단계 — 치명적 안정성 문제 해결

- 목적: 설치 readiness, rollback 진단, Viewer 교체 경쟁, 패키지 실행 실패 차단
- 변경 대상: `HttpsAgentHealthProbe`, `SetupUiPresentation`, `HttpAgentClient`,
  `DashboardViewModel`, 실행 파일 smoke
- 예상 위험: 구형 Agent 호환을 넓히면서 신규 설치 검증을 약화할 수 있음
- 기존 기능 영향: API·저장 형식·UI 흐름 유지
- 테스트: Setup 355개, Viewer 427개, 전체 950개, 추출 ZIP smoke
- 완료 기준: 로컬 전체 검증과 Windows CI 다운로드 후 실행 smoke 통과
- 상태: v0.10.12 코드 완료, Windows CI 증거 필요

### 2단계 — 사용자 오류 방지

- 목적: IP·CIDR·버전·UAC·DPAPI 실패를 운영자가 스스로 구분
- 변경 대상: 기존 Setup·연결·장비 관리 문구와 매뉴얼
- 예상 위험: 설명이 길어져 핵심 행동이 묻힘
- 기존 기능 영향: 없음
- 테스트: 1280×720 WPF 시각 검사, 키보드 접근, 대표 오류 코드별 문구 테스트
- 완료 기준: 각 실패가 “어느 PC에서 무엇을 확인할지” 한 화면에 표시

### 3단계 — 초급 사용자 사용성 개선

- 목적: 작업 순서를 `Agent 설치 → Agent 연결 → 장비 접속 시험 → 저장 → 감시`로 고정
- 변경 대상: 빈 상태, 상태 문구, 설명서
- 예상 위험: 고급 정보가 숨겨질 수 있음
- 기존 기능 영향: 원문 탭과 수동 `show` 명령 유지
- 테스트: 신규 사용자 시나리오, 잘못된 IP·계정·명령·저장 권한 시나리오
- 완료 기준: 개발 도구 없이 설치·연결·첫 장비 시험과 실패 조치 가능

### 4단계 — 관리자용 정보 개선

- 목적: 현재 장애, 지속 시간, 수집 실패, 미확인 항목과 확인 순서를 빠르게 판단
- 변경 대상: 기존 요약 카드·이벤트 정렬·익명 내보내기의 문구
- 예상 위험: 장비 사실만으로 영향이나 원인을 과장할 수 있음
- 기존 기능 영향: 레이아웃 전면 교체 없음
- 테스트: 장애·복구·수집 실패·미확인 혼합 fixture
- 완료 기준: 문제 장비가 우선 노출되고 “확인된 사실”과 “권장 확인”이 분리됨

### 5단계 — 회귀 테스트와 장시간 안정성 검증

- 목적: 장기간 감시의 메모리·세션·중복 이벤트·종료 안정성 확인
- 변경 대상: 테스트와 fixture, 코드 변경은 실패가 재현된 부분만
- 예상 위험: Mock 결과를 현장 증거로 오인
- 기존 기능 영향: 없음
- 테스트: 24시간 이상 soak, 느린·끊김·대량·빈 출력, 저장 권한 실패, 강제 종료,
  Agent 재시작, Viewer 재연결
- 완료 기준: 메모리·핸들·세션 수가 안정되고 중복·오판이 없으며 현장 결과가 별도 기록됨

## 10. 테스트 및 검증 계획

### 실제 장비 없이 자동화

- 합성 Telnet 서버: 정상 로그인, enable, 잘못된 비밀번호, 로그인 프롬프트 없음, 연결 끊김,
  paging, 지연, 대량·무한 출력
- 출력 fixture: 세 모델의 정상·빈·변형·부분 포트 표와 syslog
- 네트워크: 연결 거부, DNS 실패, TCP/HTTPS/API 버전·identity 변경
- 저장: 읽기 전용 폴더, 부분 파일, 잘못된 UTF-8, 미래 스키마, 원자 교체 실패
- 동시성: 여러 장비 중 일부 실패, 장비별 중복 실행, 연결 교체, 취소, Viewer 종료
- 데이터: 최초 baseline, 로그 순환, 재부팅, 같은 로그 반복, 카운터·상태 변경, 중복 event ID
- 배포: Python/.NET 미설치 clean Windows, 인터넷 차단, 한글 경로, 일반 사용자 Viewer,
  관리자 Windows CI의 추출 ZIP 실행 smoke
- 회귀: 전체 950개 테스트, PowerShell 5.1 구문, NuGet 취약점, 패키지·workflow·진단 계약

### 사내에서만 수동 검증

1. Agent Setup 사전 점검만 실행하고 서비스·방화벽·경로 상태 확인
2. 영향이 적은 단일 장비에서 접속 시험
3. `show port status` 한 번 실행
4. 지원 장비에서만 `show sylog tail num 100` 실행
5. 명령 완료 후 VTY 세션 종료 확인
6. 5분 `exec-timeout`을 넘기지 않는 정상 경로와 장비 종료 경로 확인
7. Viewer 종료·재실행 후 감시 gap와 마지막 정상 값 표시 확인
8. EDR 이벤트와 Windows 이벤트 로그 확인
9. 소수 장비로 하루 감시 후 단계 확대

실제 장비의 설정 변경 명령은 테스트에 사용하지 않는다.

## 11. 수정하면 안 되는 기존 동작

- Agent는 무창 Windows 서비스이고 Viewer는 포터블 일반 사용자 앱인 구조
- Viewer가 장비·자격 증명·감시·이력을 소유하고 Agent가 무상태로 실행하는 책임 경계
- Viewer `/32` 방화벽과 Agent 내부 IP 재검증의 이중 경계
- 관리망 1~2개, TCP/23, RFC1918 IPv4 대상 제한
- 한 줄 `show` 명령만 허용하고 설정·구분자·줄바꿈을 차단하는 정책
- 인증·enable 실패, 명령 timeout을 자동 재시도하지 않는 정책
- 수동 명령과 원문 출력 비저장
- DPAPI 자격 증명 보호와 Agent identity 보존
- 수집 실패를 정상으로 표시하지 않고 마지막 정상 값과 현재 확인 불가를 구분하는 동작
- 기존 설정·감시·진단 v1 형식의 하위 호환
- 공개 Release Asset을 Agent ZIP과 Viewer ZIP 두 개로 제한하는 계약

## 12. 최종 권고사항

1. v0.10.12는 현재 확인된 설치 진단·Viewer 동시성 P0를 수정한 뒤 전체 950개 테스트를 통과한
   릴리스 후보로 평가한다.
2. Windows CI에서 다운로드 후 실행 파일 smoke가 통과한 경우에만 tag와 immutable release를
   생성한다.
3. 사내에서는 처음부터 전체 장비를 등록하지 말고 단일 장비·단일 조회 명령·세션 종료 확인
   순서로 확대한다.
4. 다음 코드 작업은 기능 추가보다 `DashboardViewModel`의 한 책임 단위 추출 또는 패키지의
   매뉴얼 버전 fail-closed 검사 중 하나만 작은 단위로 수행한다.
5. 실장비 명령 차이는 안전한 읽기 전용 fixture로 환류하되 실제 IP·계정·출력은 저장소나
   외부 진단에 포함하지 않는다.

### 즉시 수정해야 할 항목 5개

현재 후보에서 아래 5개는 수정 완료되었고 회귀 검증 대상이다.

1. 구형 API v4 readiness 호환과 신규 설치 exact-version 검증 분리
2. rollback 최종 실패 단계와 최초 실패 코드 분리
3. field diagnostic v1 확장 하위 호환
4. Viewer Agent 클라이언트 교체·취소·dispose 경쟁 제거
5. 추출된 Viewer·Mock Agent·Agent Setup 실행 smoke

### 사용성 개선 효과가 가장 큰 항목 5개

1. Agent IP·Viewer IP·스위치 IP의 입력 위치를 계속 명확히 표시
2. 같은 Release의 Agent/Viewer 버전을 나란히 표시
3. 오류를 원인·확인 위치·다음 행동 순서로 표시
4. 현재 확인 불가와 마지막 정상 값을 명확히 분리
5. 관리자 이벤트에 확인된 사실과 권장 확인을 분리

### 안정성 개선 효과가 가장 큰 항목 5개

1. 현장 세 모델의 프롬프트·페이징·세션 종료 검증
2. Windows CI 추출 ZIP 실행 smoke 유지
3. 사내 UAC·서비스·방화벽·EDR 최소 검증
4. Viewer 장시간 감시의 메모리·핸들·세션 soak
5. 대형 ViewModel·오케스트레이터를 변경 시점에 작은 책임 단위로만 축소

### 가장 먼저 수정할 파일 또는 모듈

다음 작업의 첫 대상은 기능 추가가 아니라
`src/SamsungSwitchWatch.Agent.Setup/MainWindow.xaml`과 `ViewerAddressSuggestion.cs`의 주소 안내다.
실제 사용자가 반복해서 혼동한 Viewer 고정 IPv4, Agent 주소와 스위치 관리망 CIDR을 기능이나
보안 범위 변경 없이 더 단순하게 설명하는 것이 가장 큰 사용성 효과를 낸다.

### 첫 번째 작업 단위

1. 각 입력란에 “어느 PC의 주소인가”와 “입력하면 안 되는 주소”를 한 문장으로 통일한다.
2. Viewer 고정 IPv4, Agent 주소와 관리망 CIDR의 예시는 모두 가상 사설 주소로 유지한다.
3. 자동 검색 실패, 잘못된 CIDR, 동일 PC 시험과 원격 배치 문구의 충돌 여부를 검사한다.
4. 기존 입력 검증, `/32` 방화벽과 RFC1918 관리망 제한은 변경하지 않는다.
5. WPF 캡처, 키보드 접근성, Setup 관련 자동 테스트와 전체 회귀 테스트를 실행한다.
