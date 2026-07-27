# Samsung Switch Watch 0.9.16-poc 릴리스 노트

## Viewer 설치 복구 경고 수정

- Windows 사용자 프로필에 시작 메뉴 또는 시작프로그램 폴더가 아직 없을 때 Viewer
  바로 가기 저장이 실패하던 문제를 수정했습니다.
- 설치기가 필요한 바로 가기 상위 폴더를 안전하게 만들고, 설치 실패로 되돌릴 때 이번
  설치에서 만든 빈 폴더만 정리합니다.
- 다른 Viewer 인스턴스가 단일 실행 잠금을 보유해 새 Viewer가 종료 코드 0으로 정상
  종료한 경우를 설치 실패로 잘못 판단하지 않도록 수정했습니다.

## 실패 원인과 복구 결과 표시

Viewer 설치가 실패하면 다음 정보를 설치 창에 구분해 표시합니다.

```text
Cause: <최초 실패 코드>
Recovery: <복구 결과>
Diagnostic: %LOCALAPPDATA%\SamsungSwitchWatch-Operations\viewer-install.json
```

기존 Viewer가 복구되면 자동으로 다시 실행되지는 않습니다. 시작 메뉴의
`Samsung Switch Watch`를 실행하면 됩니다. 복구가 완전하지 않으면 백업과 진단 파일을
보존하고 Windows 관리자에게 표시된 코드를 전달해야 합니다.

## 검증 범위

- 시작 메뉴와 시작프로그램 폴더가 없는 격리된 Windows 사용자 경로에서 실제 Viewer
  릴리스 패키지 설치를 검증했습니다.
- 단일 실행 잠금 때문에 새 Viewer가 종료 코드 0으로 끝나는 조건에서도 설치 성공을
  검증했습니다.
- PowerShell 배포 도우미, .NET 회귀 테스트, 오프라인 Windows x64 패키지 계약과
  사용자 매뉴얼 렌더링을 검증했습니다.
- 실제 사내 EDR 정책과 운영 PC 권한 조합은 현장에서 확인해야 합니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.16-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.16-poc-win-x64.zip`

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다.
