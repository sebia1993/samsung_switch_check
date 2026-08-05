# Samsung Switch Watch 0.11.4-poc 릴리스 노트

릴리스 날짜: 2026-08-05

이번 버전은 Viewer를 매번 새 압축 해제 폴더에서 실행하면서 이전 실행 파일과 바로 가기가
남을 수 있던 문제를 줄이기 위해 사용자 전용 네이티브 Setup을 도입합니다. Agent API v4,
장비 데이터 형식, DPAPI 자격 증명, 감시 저장 형식과 읽기 전용 장비 명령 정책은 유지합니다.

## Viewer 설치·업데이트 단순화

- 공개 Viewer 진입점은 `SamsungSwitchWatch.Viewer.Setup.exe`입니다.
- UAC와 관리자 권한 없이 다음 고정 경로에 설치합니다.

```text
%LOCALAPPDATA%\Programs\SamsungSwitchWatch\Viewer
```

- 바탕 화면과 시작 메뉴의 제품 바로 가기를 현재 설치 경로로 갱신합니다.
- 제품이 이전에 만든 시작프로그램 바로 가기는 제거하고 새 자동 시작은 등록하지 않습니다.
- 설치된 Viewer를 자동 실행하고 정상 실행 유지를 확인한 뒤 완료합니다.
- 설치가 끝나면 ZIP을 압축 해제한 임시 폴더는 삭제해도 됩니다.
- 프로그램 파일만 현재 버전으로 교체하고 `%LOCALAPPDATA%\SamsungSwitchWatch`의 Agent 주소,
  장비 목록, DPAPI 자격 증명, 화면 설정과 감시 이력은 보존합니다.

## 안전한 교체와 복구

- 패키지 manifest와 모든 파일 해시를 파일 교체 전에 확인합니다.
- 관리되는 staging에 복사한 뒤 해시를 다시 확인하고 기존 설치를 backup 폴더에 보관합니다.
- backup 이동 직후와 복구 직전·직후에 이전 manifest와 파일 해시를 다시 확인합니다.
- 설치된 Viewer의 `--install-smoke-check`와 정상 실행 유지를 확인한 뒤 완료합니다.
- 완료 전에 실패하면 기존 관리 버전을 복구하며, 최초 설치면 새 파일을 정리해 미설치 상태로 돌아갑니다.
- 고정 설치·staging·backup·failed 경로와 Setup evidence·journal만 정리하며 사용자가 ZIP을 푼
  임의 폴더는 삭제하지 않습니다.
- 현재 설치나 복구 근거를 안전하게 확인할 수 없으면 덮어쓰기 또는 삭제 없이 실패 폐쇄합니다.
- 정확한 이전 manifest와 파일 해시가 남아 있는 0.11.3 이하 포터블 Viewer 설치도 안전한
  업그레이드 대상으로 인식합니다.

## 실행 중 Viewer 처리

- 0.11.4 이후 Viewer는 Setup의 업데이트 종료 요청을 받고 저장·진행 작업 정리를 마친 뒤
  정상 종료할 수 있습니다.
- 0.11.3 포터블 Viewer는 새 요청을 이해하지 못하므로 첫 전환 때 Setup이 수동 종료를
  안내합니다. Setup은 Viewer 프로세스를 강제로 종료하지 않습니다.

## 배포 파일

공식 GitHub Release Assets에는 다음 두 ZIP만 게시합니다.

```text
SamsungSwitchWatch-Agent-0.11.4-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.4-poc-win-x64.zip
```

Viewer ZIP에는 Viewer Setup, Viewer 실행 파일, 필요한 WPF 네이티브 런타임, manifest, SBOM,
설치 안내, PDF 사용설명서와 이번 릴리스 노트를 포함합니다. PowerShell/CMD 설치 스크립트,
개발 파일, 데이터와 인증정보는 포함하지 않습니다.

## 검증 범위

Mock·fixture 기반 테스트에서 패키지 손상, Viewer 종료 실패, smoke 실패, 실행 조기 종료,
파일·바로 가기 복구, 관리 경로 제한과 데이터 보존을 검증합니다. 한글 경로, 일반 사용자 계정,
실제 사내 EDR·AppLocker·Windows 프로필 정책과 삼성 장비 동작은 별도의 Windows/사내 현장
확인이 필요합니다.
