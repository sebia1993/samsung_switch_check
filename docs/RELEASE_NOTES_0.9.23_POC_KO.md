# Samsung Switch Watch 0.9.23-poc 릴리스 노트

## Viewer 업데이트 rollback 정확성

- 정상 업데이트 뒤 남은 rollback 슬롯을 다음 업데이트 시작 시 무조건 복원하던
  동작을 수정했습니다.
- 예를 들어 V1에서 V2로 정상 업데이트한 뒤 V3 설치가 실패하면, 두 세대 전 V1이
  아니라 정확한 직전 버전 V2를 복원합니다.
- 기존 rollback 슬롯은 새 패키지 staging 검증과 실행 중 Viewer 종료가 끝날 때까지
  보존합니다. 실제 폴더 교체 직전에만 정상 현재 Viewer를 새 rollback 기준으로
  회전합니다.
- 실패 복구는 이번 설치가 실제로 현재 Viewer를 rollback 슬롯으로 옮긴 경우에만
  수행합니다. 오래된 슬롯을 이번 작업의 백업으로 잘못 판단하지 않습니다.
- 새 Viewer로 교체하기 전 실패하면 현재 설치를 그대로 유지하고
  `CURRENT_VIEWER_PRESERVED`로 안내합니다.

## 일시적 실행 차단과 동시 설치 보호

- 기존 Viewer의 무화면 자체점검이 EDR, AppLocker, WDAC 또는 일시적인 지연으로
  실패해도 자동으로 구버전으로 내리지 않습니다.
- `VIEWER_CURRENT_SELF_CHECK_FAILED`를 표시하고 현재 설치와 rollback 슬롯을 모두
  보존합니다.
- 각 관리자 설치는 고유 작업 ID와 활성·rollback manifest SHA-256을
  Administrators 전용 marker에 원자적으로 기록합니다.
- 같은 Viewer ZIP을 A/B 두 작업이 연속 설치해 manifest SHA-256이 같아도, A의
  늦은 복구 요청은 작업 ID 불일치로 `VIEWER_ROLLBACK_ACTIVE_CHANGED`에서
  중단하고 B의 활성 설치·rollback 슬롯·marker를 보존합니다.
- 활성 설치가 EDR 격리나 파일 손상으로 검증되지 않더라도 작업 ID와 rollback 슬롯
  해시가 marker에 일치할 때만 검증된 이전 버전을 복원합니다.
- rollback 파일 복원이 성공하기 전에는 marker를 제거하지 않으며, 완료 후에만
  한 번 소비해 같은 요청의 재실행을 차단합니다.

## 결정적 회귀 검증

- 서로 다른 실행 파일 내용, 버전과 manifest 해시를 가진 V1·V2·V3 합성 패키지로
  세대별 업데이트와 실패 복구를 검증했습니다.
- 다음 상태를 실제 파일 시스템 Fixture로 확인했습니다.
  - 정상 V2와 stale V1 슬롯을 staging 전까지 함께 보존
  - V3 교체 직전에 V2를 새 rollback 슬롯으로 회전
  - V3 실패 시 V2 복원
  - 같은 V3 ZIP의 A/B marker 원자 교체와 stale A 복구 요청 차단
  - 다른 유효 활성 패키지, 누락 slot, slot 해시 불일치 시 모든 증거 보존
  - marker 재사용, 손상 JSON, 잘못된 version과 신뢰할 수 없는 ACL 차단
  - 기존 Viewer 자체점검 실패 시 V2와 V1 슬롯 보존
  - 현재 설치 누락 시 검증된 슬롯 복원
  - 현재 설치 손상 시 검증된 슬롯 복원
  - 손상된 stale 슬롯은 삭제하지 않고 현재 설치와 증거 보존
- 테스트에서는 실제 실행 중인 Viewer 프로세스를 조회하거나 종료하지 않도록
  프로세스 경계를 가상화했습니다.
- Core 85건, Agent 53건, Viewer 351건으로 총 489건의 자동 테스트를 통과했습니다.
- Release 빌드 경고·오류 0, C# 서식, PowerShell 5.1 구문·설치·복구 계약,
  NuGet 취약 패키지와 Git whitespace 검사를 통과했습니다.

## 호환성과 운영 영향

- Agent API v4, Viewer 설정 JSON, 장비 저장 JSON 스키마 v1과 감시 이력 JSON
  스키마는 변경하지 않았습니다.
- Agent·Viewer 설치 위치, UAC 흐름, 바로 가기, 기본 감시 주기, 장비별 세션 제한과
  읽기 전용 `show` 명령 정책은 변경하지 않았습니다.
- Agent와 Viewer는 같은 `0.9.23-poc` 릴리스 ZIP으로 함께 업데이트하는 것을
  권장합니다.

## 보안과 현장 검증 범위

- 이 `-poc` 패키지는 Authenticode 서명이 없는 현장 검증용 프리릴리스입니다.
- 합성 파일 시스템과 로컬 Fixture로 검증했으며 실제 사내 UAC 계정 전환, 다중 사용자
  동시 설치, EDR·AppLocker·WDAC 정책과 IES4224GP, IES4028XP, IES4226XP
  펌웨어에서는 검증하지 않았습니다.
- 사내에서는 먼저 Viewer 한 대에서 정상 업데이트와 의도적으로 차단된 자체점검의
  보존 동작을 확인한 뒤 배포 범위를 넓히십시오.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.23-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.23-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
