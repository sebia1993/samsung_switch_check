# Samsung Switch Watch 0.8.0-poc 릴리스 노트

## 핵심 변경

이번 버전은 역할을 명확히 분리했습니다.

- Viewer가 장비 IP·모델, ID·로그인 PW·enable PW, 감시 설정과 이력을 소유합니다.
- Agent는 Viewer 요청을 받아 실제 Telnet 접속과 명령 실행만 수행합니다.
- Agent는 장비, 계정, 명령, 원문 출력과 감시 데이터를 저장하지 않습니다.

## Viewer

- 장비명, 세 모델, IPv4, ID, 로그인 PW와 선택적 enable PW 입력
- Viewer 현재 Windows 사용자 DPAPI로 계정 보호
- 접속 시험과 로그인 후 `>`/`#` 권한 확인
- 한 줄 `show` 명령 직접 실행과 최대 64KiB 원문 표시
- `show running-config` 허용, 원문 저장·내보내기 금지
- `show port status`, `show sylog tail num 100`, `show syslog tail num 100` 후보 지원
- Viewer 실행 중에만 주기 감시하고 재실행 시 감시 공백 표시
- 인증서 지문과 페어링 토큰 수동 입력 제거

## Agent

- 창 없는 `LocalService` Windows 서비스
- 공개 패키지에서는 `--service` Windows 서비스로만 실행
- 직접 더블클릭한 무인자 EXE는 즉시 종료
- HTTPS/18443 고정
- 영구 ECDSA P-256 신원과 DPAPI LocalMachine 보호
- 설치 대상 CIDR 안의 canonical IPv4와 Telnet/23만 허용
- 전체 동시 실행 2건, 요청 기준 분당 60회, 요청당 명령 8개
- 요청 본문 32KiB 상한과 바인딩 전 413 거부
- 요청마다 새 Telnet 세션을 만들고 각 세션을 최대 240초 안에 종료
- 명령 실행 중 원격 종료 시 완료된 명령을 제외한 나머지만 새 세션으로 1회 재시도
- 수동 결과에 세션 수와 재연결 횟수 표시
- 인증·enable 실패와 명령 타임아웃은 자동 재시도하지 않음
- 로그인·무암호/암호 enable·페이징·프롬프트 복귀 처리
- 설정 변경 및 여러 명령 연결 차단

## 설치와 업데이트

- Agent ZIP 루트에 `Install-or-Update-Agent.cmd` 추가
- 더블클릭 시 UAC 요청
- 신규 설치와 기존 서비스 업데이트 자동 판별
- Viewer 관리 CIDR 기반 HTTPS 인바운드 방화벽
- Agent가 소비하는 스위치 대상 CIDR allowlist
- 서비스 정지, ProgramData 전체 백업, 프로그램 원자 교체, HTTPS readiness 순서
- readiness 실패 시 프로그램, HTTPS 신원, CIDR, 방화벽과 실행 상태 rollback
- ProgramData ACL을 SYSTEM, Administrators와 Agent 서비스 SID로 제한
- v0.7 장비 목록 설정 사본·자격 증명·SQLite 레거시 백업과 하위 항목은
  SYSTEM·Administrators 전용 ACL로 별도 격리
- Agent 자격 증명 등록 및 Viewer 접근 변경 스크립트를 패키지에서 제거
- 현재 사용자 예약 작업 설치·실행·제거 스크립트와 loose `appsettings`를 공개 Agent ZIP에서 제거
- Agent 서비스에 필요 없는 IIS·정적 자산·NuGet 잠금 publish 부산물을 공개 ZIP에서 제거
- 이전 제품 소유 예약 작업은 사용자·설명·경로·인수·영수증·패키지 매니페스트와
  실행 파일·숨김 실행기·보존 설정 해시를 모두 검증한 뒤 서비스로 자동 이관하고,
  실패 시 예약 작업까지 복구
- 이전 현재 사용자 HTTPS 신원을 보존하고 프로그램·데이터 전체를
  SYSTEM·Administrators 전용 `legacy-background-backup-*`으로 이동
- Viewer 업데이트에서 자동 시작 옵션을 생략하면 기존 상태를 보존하고
  `-DisableStartWithWindows`에서만 명시적으로 해제
- 최종 PDF 사용설명서를 Agent·Viewer ZIP 모두에 포함하고 편집용 DOCX는 배포에서 제외

## API

API v4를 추가했습니다.

- `GET /api/v4/identity`
- `GET /health/live`
- `GET /health/ready`
- `POST /api/v4/telnet/test`
- `POST /api/v4/telnet/execute`

## 호환성과 주의사항

- 업데이트 순서는 Agent 먼저, Viewer 다음입니다.
- v0.7 Agent의 저장 장비 목록과 방화벽 주소는 v0.8 설치 시 대상/관리 `/32` CIDR로
  안전하게 이관할 수 있습니다.
- v0.7 Agent의 로컬 자격 증명과 SQLite 원문·이력 DB는 제한 ACL의
  `legacy-v0.7-backup-*` 폴더로 이동하고 자동 삭제하지 않습니다.
- v0.8 Viewer가 다시 장비와 계정을 등록해야 할 수 있습니다.
- Viewer가 꺼져 있는 동안에는 감시하지 않으며 그 사이 사라진 장비 로그를 복원할 수 없습니다.
- Telnet 구간은 계속 평문이므로 격리된 사내 관리망에서만 사용하십시오.
- 애플리케이션 인증이 없으므로 Windows 방화벽의 관리 CIDR을 최소 범위로 유지하십시오.
- 실제 IES4224GP, IES4028XP, IES4226XP 펌웨어 검증 전에는 POC 상태입니다.

## 배포 파일

GitHub Release의 사용자 정의 Assets에는 다음 두 파일만 게시합니다.

- `SamsungSwitchWatch-Agent-0.8.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.8.0-poc-win-x64.zip`
