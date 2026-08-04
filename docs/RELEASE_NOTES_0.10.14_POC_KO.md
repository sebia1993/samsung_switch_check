# Samsung Switch Watch 0.10.14-poc 릴리스 노트

이번 버전은 Agent Setup과 Viewer의 `익명 진단 저장` TXT를 사진 한 장으로 전달할 수 있도록
짧게 정리합니다. 설치, Agent 연결, 장비 조회, 감시, 방화벽과 읽기 전용 명령 정책은
변경하지 않았습니다.

## 한 장용 익명 진단

- 새 파일은 `SSW_FIELD_DIAGNOSTIC/2`로 시작합니다.
- Agent Setup은 최대 12줄, Viewer는 11줄이며 모든 줄은 88자 이하입니다.
- 작업·결과·실패 단계·오류·권장 조치와 핵심 서비스·TCP·HTTPS 상태는 보존합니다.
- 긴 단계 목록은 전체 개수와 88자 안에 들어오는 최신 단계 순서로 압축합니다.
- 저장 위치는 사용자가 직접 선택하며 자동 파일 생성은 하지 않습니다.

과거에 저장한 `SSW_FIELD_DIAGNOSTIC/1` 파일은 재현 도구에서 계속 검증하고 분석할 수
있습니다. 새 `/2` 파일과 기존 `/1` 파일은 같은 `Component`, `ErrorCode`, `FailedStage`
결과로 재현됩니다.

## 개인정보와 운영정보 제외

익명 진단에는 다음 정보가 포함되지 않습니다.

- 실제 IP, CIDR, DNS 이름, PC명과 사용자명
- 장비 계정, 비밀번호, enable 비밀번호와 인증서 정보
- 절대 경로, 방화벽 원문, 예외 원문과 프로세스 ID
- 스위치 명령과 장비 출력

기존 `진단정보 복사`와 SWD1 지원 코드는 변경하지 않습니다. 긴 화면용 요약은
`진단정보 복사`, 사진으로 전달할 파일은 `익명 진단 저장`을 사용합니다.

## 유지되는 동작

- Agent는 창 없는 Windows 서비스로 실행됩니다.
- Viewer와 Agent는 HTTPS/TCP 18443을 사용합니다.
- Agent는 Setup에서 승인한 관리망의 Telnet/TCP 23에만 접속합니다.
- Viewer와 Agent 양쪽에서 한 줄 읽기 전용 `show` 명령만 허용합니다.
- 장비 자격 증명과 운영 이력은 Viewer가 소유하며 Agent는 저장하지 않습니다.
- GitHub Release 사용자 정의 Assets는 다음 두 파일뿐입니다.

```text
SamsungSwitchWatch-Agent-0.10.14-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.10.14-poc-win-x64.zip
```

## 검증 범위

자동 테스트는 Agent와 Viewer의 성공·연결 거부·로컬 HTTPS 실패·복구 실패 사례에서 줄 수와
줄 길이, 원인 보존, 민감정보 제외 및 `/1`·`/2` 재현 호환성을 확인합니다. Mock과 패키지
smoke 검사는 실제 사내 EDR·방화벽·Windows 서비스 통합 설치 또는 삼성 스위치 펌웨어 동작을
증명하지 않으므로 승인된 시험 PC에서 별도로 확인해야 합니다.
