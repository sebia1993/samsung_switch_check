# Samsung Switch Watch 0.9.11-poc 릴리스 노트

## 핵심 변경

- Viewer 설치 성공을 durable journal에 먼저 기록한 뒤 이전 버전 백업을
  정리하도록 설치 commit 경계를 보강했습니다.
- commit 이후 백업 정리가 실패하더라도 검증을 통과한 새 Viewer 설치를 유지하고,
  이전 버전으로 잘못 되돌리지 않습니다.
- journal 교체 후 EDR 또는 파일 잠금으로 임시 `.bak` 정리가 실패해도 완료된 설치를
  실패로 바꾸지 않습니다.
- 기존 바로가기 백업이 모두 완료되기 전에 설치가 실패하면 기존 시작 메뉴와
  자동 시작 바로가기를 건드리지 않습니다.
- rollback 시 필요한 바로가기 백업 파일을 모두 확인한 뒤에만 현재 링크를 변경합니다.
- 위 설치·복구 순서를 고정하는 배포 helper 계약 테스트를 추가했습니다.

## 호환성

- Agent, API, Viewer UI와 저장 형식은 변경하지 않았습니다.
- 기존 무관리자 Viewer 설치 방식과 정상 설치·rollback 동작을 유지합니다.

## 검증

- 설치 commit 이전 실패와 commit 이후 cleanup 실패를 구분하는 회귀 계약을
  검증했습니다.
- 바로가기 백업 전 실패 시 기존 링크가 보존되는 계약을 검증했습니다.
- 잠긴 journal 임시 파일을 정리하지 못해도 성공 경로가 예외로 바뀌지 않는지
  Windows PowerShell 5.1에서 실행 검증했습니다.

## 알려진 제한

- 실제 사내 PC와 삼성 스위치에서는 검증하지 않았습니다.
- 실제 장비 검증 전까지는 POC 상태입니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.11-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.11-poc-win-x64.zip`
