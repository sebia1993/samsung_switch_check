# Samsung Switch Watch 0.9.2-poc 릴리스 노트

## 핵심 변경

이번 버전은 GitHub Release 게시 직후의 일시적인 attestation 조회 지연을 안전하게
재시도하도록 보강한 배포 안정성 패치입니다.

- Windows PowerShell 5.1에서 `gh release verify`가 일시적인 오류를 표준 오류로
  출력해도 첫 시도에서 Release 작업 전체가 종료되지 않습니다.
- Release 조회, Release attestation 검증과 공개 ZIP 검증의 종료 코드를 직접
  판정하고, 호출 뒤에는 원래 PowerShell 오류 처리 정책을 반드시 복원합니다.
- 기존 최대 8회 bounded retry와 점진적 대기 간격을 그대로 유지합니다.
- 불변 Release, annotated tag, 내부 6개 검증 산출물과 공개 ZIP 2개 제한은
  변경하지 않습니다.

## 호환성

- Agent와 Viewer의 화면, API, 장비 접속, 감시와 저장 동작은
  `0.9.1-poc`과 같습니다.
- 장비 명령 목록, 설정 파일과 저장 데이터 형식은 변경하지 않았습니다.
- 기존 Viewer 장비 설정과 DPAPI 보호 계정을 그대로 사용할 수 있습니다.
- 기존 `v0.9.1-poc` 불변 Release는 수정하거나 다시 게시하지 않습니다.

## 검증

- Windows PowerShell 5.1에서 첫 native 호출이 표준 오류와 종료 코드 1을 반환하고
  두 번째 호출이 성공하는 재시도 회귀 테스트
- GitHub Release workflow의 bounded retry, 오류 정책 복원, 종료 코드 판정 계약 검사
- Core, Agent와 Viewer 전체 자동화 테스트
- Release 빌드, 패키지 계약, PowerShell 5.1 구문과 Git 공백 검사
- GitHub에서 내려받은 Agent와 Viewer ZIP의 attestation 및 SHA-256 검증

## 알려진 제한

- 실제 IES4224GP, IES4028XP, IES4226XP 펌웨어 검증 전에는 POC 상태입니다.
- Telnet 구간은 평문이므로 격리된 사내 관리망에서만 사용하십시오.
- 공개 `poc` 빌드에 조직 코드 서명이 없으면 사내 EDR 또는 WDAC 승인이 필요할 수
  있습니다.

## 배포 파일

GitHub Release의 사용자 정의 Assets에는 다음 두 파일만 게시합니다.

- `SamsungSwitchWatch-Agent-0.9.2-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.2-poc-win-x64.zip`
