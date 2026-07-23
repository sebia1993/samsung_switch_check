# Samsung Switch Watch v0.9 현장 POC 체크리스트

실제 IP, ID, 비밀번호, 호스트명, MAC, 시리얼과 원문 출력은 이 문서에 기록하지 않습니다.
결과는 `통과`, `실패`, `미검증`과 sanitized 오류 코드만 남깁니다.

## 1. Agent 설치와 무창 실행

- [ ] Agent ZIP과 Viewer ZIP의 Release 검증 완료
- [ ] `Install-or-Update-Agent.cmd`에서 UAC 승인
- [ ] Viewer 관리 CIDR과 스위치 대상 CIDR을 최소 범위로 입력
- [ ] `SamsungSwitchWatchAgent` 서비스가 `LocalService`, 자동 시작으로 등록됨
- [ ] 서비스 실행 중 사용자 바탕 화면·작업 표시줄·트레이에 Agent 창이 없음
- [ ] Agent EXE 직접 더블클릭 시 별도 프로세스가 남지 않음
- [ ] RDP 연결 종료 후 서비스 계속 실행
- [ ] 다른 Windows 사용자 로그인 후에도 닫을 Agent 창이 없음
- [ ] PC 재부팅 후 사용자 로그인 전 서비스 자동 시작
- [ ] 강제 프로세스 종료 후 5초/15초/60초 복구 정책 확인

## 2. ProgramData와 HTTPS 신원

관리자 PC에서 민감한 파일 이름이나 내용을 수집하지 않고 ACL만 확인합니다.

- [ ] `%ProgramData%\SamsungSwitchWatch`의 상속이 차단됨
- [ ] SYSTEM, Administrators, Agent 서비스 SID만 허용됨
- [ ] 일반 Users 및 로그인 사용자가 데이터 폴더를 읽을 수 없음
- [ ] Viewer 최초 연결에서 지문·토큰 입력 없이 자동 연결됨
- [ ] Viewer 재실행 후 같은 Agent를 계속 신뢰함
- [ ] 다른 테스트 신원으로 바뀌면 Viewer가 연결을 차단함
- [ ] 관리자가 확인한 재설치에서만 `Agent 신뢰 다시 설정` 동작

## 3. 네트워크 경계

- [ ] Viewer 관리 CIDR에서 Agent HTTPS/18443 연결 성공
- [ ] 허용 범위 밖 테스트 PC에서 HTTPS/18443 차단
- [ ] Public 방화벽 프로필에서 제품 규칙이 적용되지 않음
- [ ] 대상 CIDR 안의 장비 TCP/23 요청 허용
- [ ] 대상 CIDR 밖 테스트 주소가 `TARGET_NOT_ALLOWED`로 거부됨
- [ ] 요청의 포트 23 이외 값이 거부됨
- [ ] DNS 이름, IPv6, loopback과 link-local 대상이 거부됨

## 4. Viewer 장비와 계정

각 모델에서 한 대씩 아래 항목을 반복합니다.

| 모델 | 등록 | 로그인 | enable | 결과 |
|---|---|---|---|---|
| IES4224GP | 미검증 | 미검증 | 미검증 | |
| IES4028XP | 미검증 | 미검증 | 미검증 | |
| IES4226XP | 미검증 | 미검증 | 미검증 | |

- [ ] Viewer에서 장비명, 모델, IPv4, ID, 로그인 PW 입력
- [ ] enable PW 없는 장비 저장·접속 시험
- [ ] enable PW가 필요한 장비의 `> → enable → #` 확인
- [ ] 편집 화면과 API 응답에 기존 PW가 노출되지 않음
- [ ] Viewer 종료 후 Agent PC에 장비·계정 자료가 남지 않음
- [ ] 다른 Windows 사용자로 Viewer 자료 복사 시 DPAPI 복호화 불가

## 5. 명령과 출력

각 모델에서 지원 여부를 기록합니다.

| 명령 | IES4224GP | IES4028XP | IES4226XP |
|---|---|---|---|
| `show port status` | 미검증 | 미검증 | 미검증 |
| `show sylog tail num 100` | 미검증 | 미검증 | 미검증 |
| `show syslog tail num 100` | 미검증 | 미검증 | 미검증 |

- [ ] 지원 명령 출력이 Viewer에 표시됨
- [ ] 미지원 명령이 장비 Down이 아닌 `명령 미지원`으로 표시됨
- [ ] 한 줄 `show running-config` 실행 가능
- [ ] 줄바꿈, `;`, `&`, `|`, configure, shutdown, reload 요청 차단
- [ ] 64KiB 초과 합성 출력에 잘림 표시
- [ ] 수동 명령과 원문 출력이 Agent 로그·DB·진단에 없음
- [ ] Viewer 재실행 후 이전 수동 원문이 복원되지 않음

`show running-config` 원문은 체크리스트, 캡처, 메일과 이슈에 첨부하지 않습니다.

## 6. 세션 유지 시간과 정리

- [ ] `exec-timeout 5 0` 장비에서 접속 시험 성공
- [ ] 명령 완료 후 Telnet 세션 즉시 종료
- [ ] 명령 단계 원격 종료 시 남은 명령만 새 세션으로 1회 재시도
- [ ] 재연결 뒤 이미 완료된 명령이 반복 실행되지 않음
- [ ] 수동 결과에 실제 세션 수와 재연결 횟수 표시
- [ ] 인증 또는 enable 실패는 자동 재시도하지 않음
- [ ] 명령 타임아웃은 자동 재시도하지 않음
- [ ] 인증 실패 뒤 세션 잔존 없음
- [ ] 명령 타임아웃 뒤 세션 잔존 없음
- [ ] Viewer 취소 뒤 세션 잔존 없음
- [ ] 같은 장비 동시 실행이 직렬화됨
- [ ] 전체 동시 장비 실행이 최대 2개로 제한됨
- [ ] 각 세션 최대 240초 경계 확인

## 7. Viewer 주기 감시와 공백

- [ ] Viewer 실행 중 설정한 주기로 감시 요청
- [ ] Viewer 종료 시 Agent가 독립적으로 장비를 조회하지 않음
- [ ] Viewer 종료 시간에 불필요한 Telnet 세션 없음
- [ ] Viewer 재실행 후 `감시 공백` 표시
- [ ] 공백 이후 기존 로그 100개를 모두 신규 이벤트로 오인하지 않음
- [ ] 모델별 후보 syslog 명령 대체 동작
- [ ] 동일 상태 지속 시 팝업 반복 없음
- [ ] `Down → Up` 복구 이벤트 표시

## 8. 업데이트와 rollback

- [ ] v0.7 또는 v0.8 설치에서 v0.9 설치기가 업데이트 모드 자동 감지
- [ ] 기존 Viewer 방화벽 주소와 장비 주소를 최소 `/32` CIDR로 이관
- [ ] v0.9 재업데이트 시 관리 CIDR 재입력 없이 보존
- [ ] 업데이트 전후 Agent HTTPS 신원 동일
- [ ] 업데이트 전후 ProgramData ACL 동일
- [ ] v0.7 자격 증명·SQLite 자료가 `legacy-v0.7-backup-*`으로 이동해 자동 삭제되지 않음
- [ ] legacy 백업 폴더와 하위 항목은 SYSTEM, Administrators만 접근 가능하고 Agent 서비스 SID는 제외됨
- [ ] 강제 readiness 실패 시 이전 프로그램 복구
- [ ] 강제 readiness 실패 시 ProgramData와 방화벽 복구
- [ ] rollback 후 기존 Agent가 이전 프로토콜로 다시 실행

## 9. 진단과 인수 기준

- [ ] `diagnose-agent.ps1`에 ID, PW, 장비 IP, 명령과 원문 없음
- [ ] 실패가 `TCP_TIMEOUT`, `AUTH_FAILED`, `ENABLE_FAILED`,
      `COMMAND_TIMEOUT`, `PROMPT_PARSE_FAILED` 등으로 구분됨
- [ ] 세 모델별 실제 펌웨어 버전과 검증 날짜를 사내 기록에만 보관
- [ ] POC 한계와 Viewer 비실행 감시 공백을 운영자가 이해함

모든 필수 항목이 통과하기 전에는 `현장 검증 완료` 또는 `운영 안정화 완료`로 표시하지 않습니다.
