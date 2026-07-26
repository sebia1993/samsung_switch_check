# Samsung Switch Watch 0.9.12-poc 릴리스 노트

## 핵심 변경

- Agent 설치·업데이트와 제거가 하나의 시스템 전역 잠금을 공유하도록 보강했습니다.
- Viewer 설치·업데이트와 제거가 같은 Windows 사용자 범위의 잠금을 공유하도록
  보강했습니다.
- 같은 제품의 다른 배포 작업이 이미 실행 중이면 기다리거나 파일을 함께 변경하지 않고
  `DEPLOYMENT_ALREADY_RUNNING`으로 즉시 중단합니다.
- 잠금 커널 객체가 유지된 상태에서 이전 배포 프로세스의 비정상 종료를 감지하면
  `DEPLOYMENT_PREVIOUS_RUN_INTERRUPTED`로 이번 자동 변경을 중단합니다.
- Agent 잠금 ACL은 SYSTEM과 로컬 Administrators, Viewer 잠금 ACL은 SYSTEM과 현재 사용자
  SID로 제한했습니다.
- 제품과 잠금 이름이 일치하지 않으면 잘못된 ACL로 잠금을 만들지 않도록 차단했습니다.

## 호환성

- Agent, API, Viewer UI, 설정 및 저장 형식은 변경하지 않았습니다.
- 기존 Agent 관리자 설치와 Viewer 무관리자 현재 사용자 설치 방식을 유지합니다.
- 잠금 이름의 `.v1`은 앱 릴리스 번호가 아닌 배포 잠금 프로토콜 식별자이며 이후 릴리스에서도
  유지합니다.

## 검증

- Windows PowerShell 5.1 자식 프로세스가 잠금을 보유한 동안 두 번째 작업이 즉시
  거부되는지 검증했습니다.
- 잠금 보유 프로세스를 강제 종료해 abandoned 상태를 만든 뒤 첫 재시도가 안전하게
  중단되고 다음 재시도부터 잠금을 정상 재사용하는지 검증했습니다.
- Agent와 Viewer 잠금 ACL의 허용 SID 집합을 검증했습니다.
- Agent·Viewer 설치 및 제거 네 경로가 journal 기록과 실제 변경 전에 잠금을 획득하고
  바깥쪽 `finally`에서 해제하는 계약을 검증했습니다.

## 알려진 제한

- named mutex는 동시에 실행 중인 설치·제거의 충돌을 방지합니다.
- 모든 잠금 핸들이 사라진 뒤나 Windows 재부팅 뒤의 부분 설치를 영구 감지하거나 자동
  복구하는 기능은 이번 버전에 포함하지 않았습니다.
- 실제 사내 PC와 삼성 스위치에서는 검증하지 않았습니다.
- 실제 장비 검증 전까지는 POC 상태입니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.12-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.12-poc-win-x64.zip`
