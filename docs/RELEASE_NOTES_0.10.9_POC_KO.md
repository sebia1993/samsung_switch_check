# Samsung Switch Watch 0.10.9-poc 릴리스 노트

이번 버전은 Agent 설치·업데이트 실패 뒤 `이전 상태 복구`를 실행했는데도
`SETUP_RECOVERY_REQUIRED`가 반복되던 현상을 안전하게 진단하고 처리하도록 복구 완료 판정을
강화합니다. Agent API, Viewer 설정, 장비 정보와 감시 데이터 형식은 변경하지 않습니다.

## 제한된 복구 자료 정리 재시도

`ROLLBACK_EVIDENCE_CLEANUP_FAILED`가 발생할 수 있는 정리 단계는 다음 원칙을 따릅니다.

- Setup이 현재 작업 기록으로 검증한 staging·backup·failed·journal 대상만 처리합니다.
- 일시적인 파일 잠금이나 보안 프로그램 검사 지연을 고려해 최대 3회 시도하고, 실패한 시도
  사이에만 250ms 대기합니다.
- 넓은 상위 폴더, 다른 트랜잭션 또는 사용자가 지정한 임의 경로는 정리하지 않습니다.
- 삭제 호출이 성공해도 대상이 실제로 남아 있으면 복구 성공으로 처리하지 않습니다.
- 계속 실패하면 작업 기록과 복구 근거를 보존하고 설치를 차단합니다.

## 복구 성공 판정과 화면 상태 일치

복구 작업의 반환값만으로 `복구 완료`를 표시하지 않습니다.

- 정리 대상이 모두 사라진 뒤 미완료 작업 기록을 새로 검사합니다.
- 새 검사에서도 journal이 남아 있으면 설치 버튼을 계속 비활성화합니다.
- 정리와 새 검사가 모두 통과한 경우에만 복구 성공과 설치 버튼 활성화를 표시합니다.
- 복구 성공 뒤 설치·업데이트는 자동으로 시작되지 않습니다.
- 복구 실패 화면은 `이전 상태를 완전히 복구하지 못했습니다`라는 제목 아래
  `설치 자료 정리 미완료 · 작업 기록 보존`으로 안전 상태를 설명하고, 먼저
  `복구 다시 시도`, 반복되면 `익명 진단 저장`을 안내합니다.

## 안전한 진단

정리 실패 진단은 실제 경로, 파일명과 예외 원문을 포함하지 않습니다.

- 최초 설치·업데이트 실패와 복구 실패를 분리해서 유지합니다.
- 화면 상단은 `SETUP_ROLLBACK_FAILED`로 유지하고, `ROLLBACK_EVIDENCE_CLEANUP_FAILED`
  범주 아래 staging·backup·failed·journal 중 실패한 안전 단계를 세부 행으로 구분합니다.
- 대상별 코드는 `ROLLBACK_STAGING_CLEANUP_FAILED`,
  `ROLLBACK_BACKUP_CLEANUP_FAILED`, `ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED`,
  `ROLLBACK_JOURNAL_CLEANUP_FAILED`입니다.
- IP/CIDR, PC·사용자명, 계정, 인증서, 절대 경로, 방화벽 원문, 장비 명령과 출력은 계속
  제외합니다.

운영자는 `Agent.__staging_*`, `Agent.__backup_*`, `Agent.__failed_*` 폴더나
`%ProgramData%\SamsungSwitchWatch-Operations`의 작업 기록을 직접 삭제·이동·이름 변경하지
마십시오. 복구 우회 또는 자동 설치 기능은 추가하지 않았습니다.

## 호환성과 유지되는 동작

- Agent와 Viewer는 같은 `0.10.9-poc` Release 조합을 사용합니다.
- Agent는 창이나 트레이 아이콘 없이 Windows 서비스로 실행됩니다.
- Viewer는 관리자 권한이나 설치 스크립트가 필요 없는 포터블 실행 파일입니다.
- 승인된 관리망의 읽기 전용 `show` 명령과 Viewer 소유 장비·자격 증명 흐름은 유지됩니다.
- 수동 장비 명령과 출력은 Viewer 메모리에만 있고 익명 진단이나 내보내기에 포함되지 않습니다.

## 검증 범위와 현장 확인

자동 검증은 일시적 정리 실패 뒤 성공, 지속 실패, 삭제 호출 성공 뒤 대상 잔존, 복구 직후
journal 재검출과 화면 상태 일치를 합성 파일 시스템과 Mock으로 확인합니다. 이는 실제 EDR,
Windows 서비스, 방화벽 COM과 사내 파일 잠금 정책을 증명하지 않습니다.

사내에서는 영향이 적은 시험 PC에서 다음 순서로 확인하십시오.

1. 같은 Release의 Agent ZIP을 새 로컬 폴더에 완전히 압축 해제합니다.
2. Setup에서 `이전 상태 복구`를 한 번 실행하고 최종 상태를 확인합니다.
3. 복구 성공과 설치 버튼 활성화가 함께 표시된 경우에만 검사를 다시 확인합니다.
4. 설치·업데이트를 별도로 실행합니다.
5. 복구가 다시 실패하면 수동 삭제로 우회하지 말고 `익명 진단 저장`의 안전 코드만 전달합니다.

## 공개 Assets

- `SamsungSwitchWatch-Agent-0.10.9-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.9-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 사용자 정의
Release Assets는 위 두 ZIP만 게시합니다.
