# Samsung Switch Watch 0.9.13-poc 릴리스 노트

## 핵심 변경

- Agent 설치기와 제거기가 시스템 전역 잠금을 획득한 직후
  `%ProgramData%\SamsungSwitchWatch-Operations`의 설치·제거 journal 두 개를 모두
  교차 검사하도록 보강했습니다.
- 이전 설치 또는 제거가 `running`으로 남았거나 rollback·제거 오류가 기록된 경우
  `AGENT_DEPLOYMENT_RECOVERY_REQUIRED`로 중단합니다.
- journal JSON 손상, 필수 필드 누락, 지원하지 않는 형식, 잘못된 transaction ID 또는
  단계·상태 조합은 `AGENT_DEPLOYMENT_JOURNAL_INVALID`로 중단합니다.
- 기존 작업 기록 루트의 관리자 소유권과 reparse 여부를 확인한 뒤 루트 ACL을 먼저 잠그고,
  부모부터 각 하위 항목을 검증·이관합니다. 전체 재열거까지 통과하면 로컬 Administrators
  소유, SYSTEM·Administrators 전용 ACL로 제한합니다. 신뢰할 수 없으면
  `AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID`로 중단합니다.
- journal은 64KiB 상한을 적용하고 쓰기를 허용하지 않는 파일 공유 모드로 읽습니다.
- 이 오류들은 서비스, 설치 폴더, Agent 데이터와 방화벽 상태를 읽거나 변경하기 전에
  fail-closed로 발생합니다.
- 정상 완료된 설치·제거 기록과 오류 없이 완료된 설치 rollback 기록은 기존처럼 다음
  작업을 허용합니다.
- 검사 과정에서는 journal, staging, program backup, transaction backup과 legacy backup을
  자동 삭제·이동·복원하지 않습니다.

## 호환성

- Agent, API, Viewer UI, 설정 파일과 저장 형식은 변경하지 않았습니다.
- 기존 Agent 관리자 설치와 Viewer 무관리자 현재 사용자 설치 방식을 유지합니다.
- v0.9.12-poc에서 작성한 formatVersion 1 Agent journal을 그대로 판독합니다.
- 기존 개별 owner는 현재 실행 중인 관리자일 때만 자동 이관합니다. 제한 없이 지연될 수 있는
  로컬·도메인 그룹 조회는 하지 않습니다.
- Viewer 설치·제거 journal에는 이번 영구 교차 검사를 적용하지 않았습니다.

## 검증

- journal 없음, 정상 설치·제거 완료, 오류 없는 rollback 완료가 작업을 허용하는지
  Windows PowerShell 5.1에서 검증했습니다.
- 설치·제거 `running`, rollback 오류와 제거 오류가 모두 안정 코드로 중단되는지
  검증했습니다.
- 손상 JSON, 미지원 format, 잘못된 operation·transaction ID·시간·stage와 오류 코드가
  fail-closed로 차단되는지 검증했습니다.
- 64KiB journal은 허용하고 64KiB+1은 파싱 전에 거부하는지 검증했습니다.
- production journal writer의 생성·교체 결과가 reader와 호환되며, canonical 파일이 잠긴
  상태의 교체 실패가 이전 bytes를 보존하는지 검증했습니다.
- 미래 시각의 `running` journal도 age를 근거로 무시하지 않는지 확인했습니다.
- 차단된 journal이 바이트 단위로 보존되고 관련 없는 파일도 변경되지 않는지 확인했습니다.
- 공백과 한글이 포함된 임시 경로에서 실제 Windows PowerShell 5.1로 fixture를 실행했습니다.
- 관리자 권한 Windows CI에서는 개별 관리자 소유의 기존 OperationsRoot를 ACL 이관한 뒤
  `prepared/running` journal이 transaction·외부 staging·backup 내용을 보존한 채 차단되는
  통합 fixture를 필수로 실행합니다.
- 전체 .NET 397개 테스트, PowerShell 배포 계약, GitHub 릴리스 워크플로 계약과 NuGet
  취약 패키지 검사를 통과했습니다.

## 알려진 제한

- 이번 버전은 Agent 대상 미완료 기록 감지와 추가 변경 차단만 포함합니다.
- 프로그램, 서비스, ProgramData, 방화벽을 자동 rollback·roll-forward하거나 임시 자료를
  정리하는 자동 복구 기능은 아닙니다.
- 복구 판단에 필요한 세부 단계와 완전성 표식이 없는 기존 중단 상태는 관리자가 확인해야
  합니다. journal이나 백업을 삭제해 검사를 우회하면 안 됩니다.
- 실제 사내 PC, Windows 서비스·방화벽 정책, EDR과 삼성 스위치에서는 검증하지 않았습니다.
- 기존 OperationsRoot의 실제 관리자 소유권·ACL 마이그레이션은 사내 관리자 시험 PC에서
  별도 확인해야 합니다.
- 이전 작업 기록이 현재 실행한 관리자와 다른 계정 소유라면 자동 이관하지 않고
  `AGENT_DEPLOYMENT_JOURNAL_TRUST_INVALID`로 중단합니다.
- 실제 장비 검증 전까지는 POC 상태입니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.13-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.13-poc-win-x64.zip`
