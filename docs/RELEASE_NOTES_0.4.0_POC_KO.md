# Samsung Switch Watch 0.4.0-poc

원격 Windows PC의 Agent가 삼성 스위치를 읽기 전용 Telnet으로 점검하고,
운영자 PC의 Viewer가 변경·장애·복구를 보여 주는 현장 검증용 프리릴리스입니다.

## 주요 변경

- `IES4224GP`, `IES4028XP`, `IES4226XP` 모델 프로파일과 런타임 capability 확인
- Agent 한 대당 최대 256개 장비 등록, 기본 최대 4개 장비 병렬 점검
- 장비별 Telnet 세션 1개 보장과 등록된 `show` 명령 ID만 실행
- 최초 업링크 Down, 장비 재시작, 로그 기준선 초기화, 장애 지속과 복구 감지 강화
- 불변 event change 이력과 API v3 snapshot/events/check-runs 추가
- Agent/API/실시간/DB/인증서/수집기 상태를 서로 구분
- 오프라인 catch-up 요약, Critical 우선 알림, 중복 알림 억제, 복구 알림
- 검색과 이벤트 필터, 개인정보를 익명화한 CSV·JSON 내보내기
- Viewer 항상 위 미니 창, 시스템 트레이, Windows 알림과 인앱 fallback
- 자격 증명과 Telnet 원문 DPAPI 보호, 원문 7일·500MB 기본 보존 제한
- Viewer 토큰 최대 5개, 절대 180일·미사용 60일, 로컬 조회·폐기·교체
- 인증서 60/30/7일 만료 경고, 만료 시 readiness 실패, 최대 14일 dual-pin 전환
- 설치 receipt, 단계별 작업 journal, DB/WAL/SHM 포함 rollback, 소유한 방화벽·인증서만 제거
- 잠금 복원, 고정된 GitHub Actions, SBOM 2종, 빌드 매니페스트, SHA-256, provenance attestation

## 호환성

- 기존 `/api/v1`, `/api/v2`는 유지됩니다.
- Viewer는 `/api/v3`를 우선 사용하고 404일 때 `/api/v2`로 안전하게 되돌아갑니다.
- 기존 단일 인증서 pin 설정은 dual-pin 설정으로 자동 정규화됩니다.
- 기존 DB의 평문 가능성이 있는 Telnet 원문은 schema v4 전환 시 보안을 위해 폐기됩니다.

## 설치와 검증

설치·복구·인증서 회전은 [설치 안내](INSTALL_KO.md), 실제 장비 검증은
[현장 POC 체크리스트](FIELD_POC_CHECKLIST_KO.md)를 따르십시오. ZIP, 매니페스트,
SBOM과 `SHA256SUMS.txt`는 같은 GitHub Release에서 내려받아 검증해야 합니다.

로컬 Release 검증에서는 Core 45개, Agent 104개, Viewer 68개로 총 217개 자동
테스트와 WPF 창 표시 스모크 테스트가 통과했으며 빌드 경고와 오류는 없었습니다. 이
결과는 합성 Telnet 출력과 mock Agent를 사용한 것이므로 아래의 실장비 검증을 대체하지
않습니다.

## 알려진 제한

- 세 모델의 공개 자료를 기반으로 한 후보 명령 프로파일입니다. 실제 펌웨어별 출력은
  capability 상태로 격리되지만, 회사망에서 모델별 실장비 검증이 별도로 필요합니다.
- Telnet은 암호화되지 않습니다. Agent PC와 스위치는 격리된 관리망에 두고 스위치 ACL로
  Agent IPv4 주소만 TCP/23 접근을 허용해야 합니다.
- 이 `-poc` 산출물은 코드 서명 인증서가 없으면 서명되지 않습니다. 공식 Release의
  SHA-256과 provenance를 확인하고 사내 승인된 PC에서만 실행하십시오.
- 실제 펌웨어 3종, 장시간 soak, 절전·재부팅·네트워크 단절, 사내 인증서와 코드 서명은
  이 릴리스의 로컬 자동 검증만으로 완료됐다고 간주하지 않습니다.
