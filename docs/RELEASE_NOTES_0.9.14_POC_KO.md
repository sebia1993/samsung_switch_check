# Samsung Switch Watch 0.9.14-poc 릴리스 노트

## 0.9.13 Agent 설치 오류 수정

- `0.9.13-poc`의 Windows PowerShell 5.1 설치기에서 `sc.exe`의 `binPath=`, `start=`,
  `obj=` 옵션과 값을 한 인자로 전달해 서비스 등록이 실패할 수 있던 오류를 수정했습니다.
- 공백이 포함된 `Program Files` 실행 경로의 따옴표를 보존하고, 서비스 등록 뒤 실제
  `PathName`, 서비스 계정과 시작 유형을 다시 확인합니다.
- 이 오류로 Agent가 TCP/18443에서 실행되지 않으면 Viewer에는
  `AGENT_CONNECTION_REFUSED`가 표시됩니다. `0.9.13-poc` 설치기를 반복 실행하거나 서비스를
  수동 등록하지 말고 `0.9.14-poc` Agent ZIP으로 설치하십시오.
- 관리자 설치 창은 실패 원인을 `Cause:` 한 줄로 표시하고 사용자가 확인할 때까지 유지합니다.
  서비스 제어 실패에는 `sc.exe` 종료 코드와 Windows가 반환한 진단도 포함됩니다.

## 안정성 보강

- 기존 Agent 설치·업데이트·제거 흐름을 유지하면서 서비스 계정, 폴더 신뢰 경계, 패키지
  교체와 rollback의 fail-closed 검사를 보강했습니다.
- Agent 서비스는 공유 `LocalService`가 아니라 암호가 필요 없는
  `NT SERVICE\SamsungSwitchWatchAgent` 전용 가상 계정으로 등록합니다.
- 기존 `LocalService` Agent는 서비스를 중지한 사실이 확인된 업데이트에서만
  `LocalService` 소유 DataDirectory 하위 항목을 한 번 이관 대상으로 인정합니다. 이 항목도
  Administrators owner로 정규화하고 서비스는 전용 가상 계정으로 전환합니다.
- DataDirectory는 정확히 `%ProgramData%\SamsungSwitchWatch`만 허용합니다. 신규 설치에서는
  이 경로가 이미 존재하면 비어 있더라도 채택하지 않습니다.
- 사전 검사 뒤 다른 프로세스가 DataDirectory를 먼저 만들면 `New-Item -Force`로 채택하거나
  rollback에서 삭제하지 않고 실패합니다.
- 파일 변경 직전에 서비스 상태와 `PathName`, 계정, 시작 유형을 다시 조회합니다. 준비 중
  다른 관리자가 서비스 구성을 바꾼 경우 해당 구성을 덮어쓰지 않고 변경 전에 중단합니다.
- 프로그램 교체 전 업데이트 실패는 기존 프로그램이 그대로임을 확인하고 이전 서비스를
  재시작합니다. 교체 여부나 신규 데이터 폴더의 ACL 적용 완료 여부가 불명확하면 자동
  삭제하지 않고 복구 증거와 작업 기록을 보존합니다.

## 디렉터리 신뢰와 ACL

- Agent 설치·업데이트·제거는 설치 폴더와 DataDirectory의 내용을 읽거나 삭제하기 전에
  루트 owner SID와 reparse point를 검사합니다.
- owner와 ACE는 계정명 변환 없이 SID로 직접 판독합니다.
- 루트 owner는 `SYSTEM`, 로컬 `Administrators` 또는 현재 elevated 관리자만 신뢰합니다.
- ACL 변경 전 전체 트리를 읽기 전용으로 검사합니다. 검사를 통과하면 루트를 먼저
  Administrators 소유와 폐쇄형 ACL로 잠근 뒤 부모 우선 순서로 자식을 다시 검사·이관합니다.
- DataDirectory 하위 항목의 정확한 Agent 서비스 SID owner는 정상 운영 결과로 허용합니다.
  `LocalService` owner는 위의 중지된 기존 서비스 1회 이관에서만 허용하며 프로그램 트리에는
  두 예외를 적용하지 않습니다.
- Builtin Users 등 비신뢰 owner나 junction·symlink는
  `AGENT_DIRECTORY_TRUST_INVALID`로 fail-closed 거부합니다.
- 마지막 전체 재열거에서 모든 owner, 허용 SID ACL과 reparse 부재를 다시 확인합니다.
- 설정 파일이 없는 제거 잔재도 설치 루트 신뢰 검사 없이 재귀 삭제하지 않습니다.

## 패키지와 설치 영수증

- 소스 패키지의 매니페스트를 검증한 뒤 SYSTEM·Administrators 전용 staging에 파일을
  복사합니다.
- staging의 모든 파일과 Agent EXE SHA-256을 메모리에 읽어 둔 매니페스트와 다시 비교한
  뒤에만 프로그램 폴더를 교체합니다.
- `install-receipt.json`은 Administrators owner이며 SYSTEM·Administrators만 FullControl을
  갖는 일반 파일로 확정합니다. 서비스 SID의 상속 쓰기 권한은 제거합니다.
- install receipt는 설치 경로와 증거 확인용이며 CIDR 권한원이 아닙니다. 업데이트의 스위치
  대상 CIDR은 검증된 `appsettings.Production.json`, Viewer 관리 CIDR은 정확히 제품이
  소유한 방화벽 규칙에서 가져옵니다.
- 데이터 영구 제거에서 영수증 ACL을 신뢰할 수 없으면
  `AGENT_RECEIPT_TRUST_INVALID`로 변경 전에 중단합니다.

## Rollback과 증거 보존

- rollback은 새 서비스를 중지한 사실과 필요한 서비스 삭제를 순서대로 확인합니다. 이 전제가
  실패하면 실행 중일 수 있는 프로그램·데이터 파일의 후속 삭제·복구를 진행하지 않습니다.
- 프로그램, 서비스 설정, legacy 자료와 DataDirectory의 선행 복구가 확인된 경우에만 다음
  복구 단계와 이전 서비스 재시작을 수행합니다.
- legacy current-user Agent의 program 또는 data 이동이 일부만 완료된 경우 원래 위치와
  archive를 그대로 보존하고 후속 DataDirectory 복구를 차단합니다.
- rollback 오류가 하나라도 남으면 transaction snapshot, program backup, legacy archive와
  journal 등 남아 있는 증거를 자동 정리하지 않습니다.
- 제거에서도 서비스 중지·삭제 확인이 실패하면 후속 방화벽·프로그램·데이터 삭제를 차단하고
  실패 journal을 남깁니다.

## 검증 계약

- Windows PowerShell 5.1 정적 계약에서 전용 가상 서비스 계정, 정확한 DataDirectory,
  빈 선점 폴더 거부, 보호 staging 재해시와 설치 영수증 ACL 순서를 확인합니다.
- GitHub Windows CI의 관리자 ACL fixture는 암호 없는 `NT SERVICE\...` 가상 계정 등록과
  결정론적 서비스 SID를 확인합니다.
- 테스트 전용 `SeRestorePrivilege` 활성화 뒤 Builtin Users owner를 실제로 만들고 read-back하여
  비신뢰 root와 child가 내용·ACL 변경 없이 거부되는지 확인합니다.
- `Modify`와 `ReadAndExecute` 권한, 상속 차단, 정확한 SID별 권한, 하위 상속과 멱등성을
  검사합니다.
- ACL 적용 전후 파일 SHA-256을 비교하고 junction 외부 대상의 내용과 ACL이 변경되지 않는지
  확인합니다.
- 서비스 중지·삭제 실패, legacy 부분 이동과 rollback 오류가 후속 파일 변경을 차단하고
  snapshot·archive·journal을 보존하는 순서를 계약으로 검사합니다.

## 호환성과 관리자 조치

- API, Viewer UI, Agent 설정 형식과 install receipt JSON 형식은 변경하지 않습니다.
- 기존 서비스 이름을 유지하므로 같은 `SamsungSwitchWatchAgent` 서비스 SID를 계속 사용합니다.
- 이전 릴리스의 서비스 쓰기 가능 receipt는 CIDR 권한원으로 사용하지 않고 정상 업데이트에서
  Administrators 전용 receipt로 교체합니다.
- 다른 관리자 계정을 install/data owner로 남긴 환경은 자동으로 그룹 포함 여부를 조회하지
  않습니다. 폐쇄망의 디렉터리 조회 지연과 권한 오판을 피하기 위한 fail-closed 동작입니다.
- `AGENT_DIRECTORY_TRUST_INVALID`, `AGENT_RECEIPT_TRUST_INVALID` 또는
  `AGENT_DEPLOYMENT_RECOVERY_REQUIRED`가 표시되면 폴더·영수증·snapshot·archive를 삭제하거나
  `takeown`, `icacls`로 강제 우회하지 말고 사내 Windows 관리자가 설치 이력을 확인해야
  합니다.

## 알려진 제한

- 관리자 ACL·서비스·rollback 통합 검증은 GitHub Windows CI와 사내 관리자 시험 PC에서
  확인해야 합니다.
- 실제 사내 PC의 EDR·백신·그룹 정책, 기존 릴리스의 다양한 owner·ACL 조합은 아직 현장
  검증하지 않았습니다.
- 검사 중 다른 프로세스가 트리를 동시에 바꾸면 이미 안전하게 잠긴 상위 ACL이 남은 채
  중단될 수 있습니다. 자동 복구로 추측하지 않고 관리자 확인이 필요합니다.
- 실제 삼성 스위치 세 모델의 펌웨어와 Telnet 동작은 이번 안정성 보강 범위가 아니며 여전히
  현장 POC가 필요합니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.14-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.14-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
