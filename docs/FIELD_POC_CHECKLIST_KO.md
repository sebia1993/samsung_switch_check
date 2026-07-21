# 현장 POC 체크리스트

이 체크리스트는 회사망에서만 수행합니다. 실제 IP, 호스트명, 사용자명, MAC, 시리얼,
Telnet 원문, 토큰과 인증서를 외부로 반출하지 않습니다.

## A. 설치와 복구

- [ ] Agent·Viewer ZIP의 build provenance·release attestation과 ZIP 내부 매니페스트·SBOM 일치
- [ ] Python/.NET 미설치 Windows x64에서 EXE 단독 실행
- [ ] 한글 사용자 경로와 임시 폴더에서 Viewer 설치
- [ ] Agent 신규 설치 후 서비스 자동 시작
- [ ] RDP 로그오프 후에도 수집 지속
- [ ] `-Preflight`가 시스템을 변경하지 않음
- [ ] `-Repair -ReuseData` 후 DB·WAL/SHM·자격 증명·설정·인증서 보존
- [ ] fault injection 실패 뒤 서비스·파일·DB·방화벽·인증서 rollback
- [ ] Viewer 실패 설치 뒤 기존 EXE와 바로가기 복구
- [ ] 재부팅 뒤 Agent 자동 복구와 Viewer 자동 시작 정책 확인
- [ ] 기본 제거가 데이터를 보존하고 `-RemoveData`만 명시 삭제

## B. 네트워크와 보안

- [ ] Agent → 각 스위치 TCP/23만 허용
- [ ] Viewer → Agent TCP/18443만 허용
- [ ] Agent 방화벽 rule remote address가 Viewer IPv4로 제한
- [ ] 스위치 계정이 조회 전용이며 설정 명령이 거부됨
- [ ] Telnet 경로가 격리 관리망 밖으로 나가지 않음
- [ ] 자격 증명·DB·원문 파일 ACL이 서비스 SID와 관리자만 허용
- [ ] SQLite raw blob에서 실제 명령 원문 평문 검색 불가
- [ ] Viewer 토큰 파일이 현재 사용자 DPAPI로 보호됨
- [ ] 토큰 최대 5개, 180일 절대·60일 idle 만료, 폐기 즉시 401
- [ ] 인증서 현재/예정 pin 2개 전환 후 오래된 pin 제거
- [ ] 만료 60/30/7일 상태와 만료 readiness 실패 확인
- [ ] `new-viewer-pairing.ps1`의 `SSW1:` 문자열 한 번으로 Viewer 최초 연결 성공
- [ ] 만료·재사용 문자열 거부, 인증서 불일치 `TLS_PIN_MISMATCH`, 최종 토큰 화면 미표시

## C. 모델별 Telnet 수집

아래 항목을 각 모델과 실제 펌웨어 조합별로 따로 기록합니다.

| 모델 | 펌웨어 | 로그인 | `show port status` | 포트 대체 명령 | `show syslog tail num 100` | 로그 대체 명령 | 페이징 | 결과 |
|---|---|---|---|---|---|---|---|---|
| IES4224GP |  |  |  |  |  |  |  |  |
| IES4028XP |  |  |  |  |  |  |  |  |
| IES4226XP |  |  |  |  |  |  |  |  |

- [ ] 실제 로그인/비밀번호 프롬프트 탐지
- [ ] 로그인 배너가 길어도 실제 장비 프롬프트 캡처
- [ ] 출력 본문의 `#`·`>` 행을 프롬프트로 오인하지 않음
- [ ] ANSI/백스페이스형 `--More--` 처리
- [ ] 명시적 empty 로그만 정상, 공백·부분 출력은 `INCOMPLETE_OUTPUT`
- [ ] 미지원 명령만 `PARSER_UNSUPPORTED`, 다른 수집은 계속
- [ ] 우선 명령 미지원 시 대체 명령 자동 선택, 재시작 후 선택 결과 재사용
- [ ] 인증 실패 1회 후 circuit block 및 자격 증명 교체 후 복구
- [ ] 장비별 동시 Telnet 세션 1개
- [ ] 세션 예산 초과 명령 분할, 중간 종료 시 완료 결과 보존과 남은 명령 1회만 재접속
- [ ] `COMMAND_TIMEOUT`·인증 실패에는 즉시 재접속하지 않음
- [ ] 5대 이상 동시 due에서 기본 최대 4대만 병렬 실행

## D. 로그와 상태 변경

- [ ] 최초 로그 조회가 기준선만 만들고 팝업을 만들지 않음
- [ ] 새 로그 1건과 동일 문구 반복 2건을 서로 구분
- [ ] 로그 buffer 순환 시 전체를 신규로 만들지 않음
- [ ] 최초 상태가 업링크 Down이면 즉시 활성 Critical 생성
- [ ] `UP → DOWN`, 지속 시간, `DOWN → UP` 복구 1회 표시
- [ ] 장애 지속 중 반복 팝업 없음
- [ ] 운영자 확인이 복구로 처리되지 않음
- [ ] uptime 감소와 로그 초기화를 재시작 사건으로 상관 처리
- [ ] Agent 재시작 후 활성 condition 중복 생성 없음
- [ ] DB change snapshot이 생성·확인·복구 당시 값으로 유지

## E. Viewer와 연결 장애

- [ ] API 정상/SignalR 재연결을 `실시간 저하`로 표시
- [ ] Agent Offline에서 cache 장비를 정상으로 오인하지 않고 `미확인` 표시
- [ ] 마지막 정상 수신 시각이 트레이·미니 창·대시보드에 일치
- [ ] Viewer 종료 중 발생한 이벤트가 재연결 catch-up 요약 1건으로 표시
- [ ] retention reset 기준선이 과거 이벤트 팝업을 만들지 않음
- [ ] 100개 Warning 중 Critical이 2초 안에 우선 표시
- [ ] 알림 클릭 시 해당 장비·이벤트로 이동
- [ ] 전체/미확인/새 로그/장애/복구 필터와 검색 결과 정확
- [ ] Agent authoritative 미확인 수와 현재 화면 표시 수를 구분
- [ ] CSV가 UTF-8 BOM이며 Excel에서 한글이 정상
- [ ] CSV/JSON export에 실제 device ID·IP·호스트·MAC·사용자·원문 없음
- [ ] 200% DPI, 키보드 전용, 고대비에서 핵심 상태 판독 가능
- [ ] 항상 위 미니 창, 트레이 최소화, 시작 시 트레이 정책 동작

## F. 장애와 장시간 운전

- [ ] Agent PC 절전·복귀
- [ ] Agent PC 재부팅
- [ ] Viewer PC 절전·복귀
- [ ] 스위치 TCP timeout·연결 거부·인증 실패 구분
- [ ] Agent 인증서 불일치가 `TLS_PIN_MISMATCH`로 차단
- [ ] DB read-only·디스크 부족·integrity 실패에서 수집 쓰기 중단
- [ ] DB 실패 중 `/health/live`는 유지, `/health/ready`는 실패
- [ ] DB 복구 후 수집 재개와 이벤트 cursor 연속성
- [ ] 24시간 이상 soak에서 메모리·파일·Telnet 세션 증가 없음
- [ ] raw 500MB 제한과 7일 retention 동작

## 완료 판정

세 모델의 실제 펌웨어 행이 모두 채워지고, Critical/복구·오프라인 catch-up·설치 rollback·
장시간 soak·사내 인증서·코드 서명까지 승인되어야 v1 운영 후보로 승격할 수 있습니다.
로컬 합성 테스트 통과만으로 이 표를 완료 처리하지 않습니다.
