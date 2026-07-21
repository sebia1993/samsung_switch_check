# Samsung Switch Watch 0.5.1-poc

원격 Windows PC의 Agent가 삼성 스위치를 읽기 전용 Telnet으로 점검하고,
운영자 PC의 Viewer가 변경·장애·복구를 보여 주는 현장 검증용 프리릴리스입니다.

## 주요 변경

- Viewer 안에서 일회용 페어링 문자열을 교환하고 최종 Bearer 토큰을 DPAPI로 저장하는
  최초 연결 흐름
- Agent의 `new-viewer-pairing.ps1`이 주소·인증서 pin·10분 일회용 코드를 하나의
  `SSW1:` 연결 문자열로 만들며 최종 토큰은 화면에 표시하지 않는 흐름
- 인증서 SHA-256·일회용 코드·최종 토큰의 용어와 오류 안내를 분리한 한국어 연결 화면
- 포트 상태의 `show port status`, 시스템 로그의 `show syslog tail num 100`을 우선
  탐지하고 미지원 모델에서는 기존 후보 명령으로 전환하는 장비별 capability 처리
- 명령이 모두 미지원인 수집기만 `미지원`으로 격리하고 장비 전체 장애로 확대하지 않는 판정
- Telnet 세션 예산 계산, 장비의 중간 세션 종료 식별, 완료 결과 보존과 남은 읽기 전용
  명령의 제한된 재접속
- 인증이 끝난 명령 단계에서 세션이 닫힌 경우에만 한 번 재접속하며 로그인·인증 중 종료는
  계정 잠금 방지를 위해 즉시 재시도하지 않는 처리와 최대 240초 세션 상한
- 발급된 Viewer 토큰을 네트워크 사전 점검보다 먼저 DPAPI로 보호하고 같은 연결 창에서
  재시도하여 orphan token을 만들지 않는 페어링 복구 흐름
- 페어링 HTTP redirect·proxy 차단, 64 KiB 응답 상한, 중복/추가 JSON 필드 거부와
  Kestrel 인증서 비밀번호를 읽지 않는 Agent 로컬 페어링 CLI
- GitHub Release 사용자 정의 Assets를 설치용 Agent ZIP과 Viewer ZIP 두 개로 단순화
- 빌드 매니페스트, SPDX/CycloneDX SBOM과 `SHA256SUMS.txt`는 6개 파일의 내부 Actions
  검증 계약으로 유지하고 각 ZIP에 필요한 매니페스트와 SBOM 포함
- 공개 ZIP 두 개만 provenance attestation, draft digest·크기 대조와 게시 후
  `verify-asset` 대상으로 제한
- 한글이 포함된 배포 스크립트를 Windows PowerShell 5.1 호환 UTF-8 BOM으로 고정하고,
  누락 시 로컬 검증에서 차단하는 소스 인코딩 계약 추가

## 호환성

- 기존 `/api/v1`, `/api/v2` 호환은 유지되며 Viewer는 지원되는 최신 API를 우선 사용합니다.
- 기존 주소·인증서 pin·토큰 설정은 복구용 고급 연결 설정으로 계속 사용할 수 있습니다.
- 장비 설정과 `line vty`의 `exec-timeout 5 0`은 변경하지 않습니다.
- 기존 `v0.4.1-poc` 릴리스와 자산은 수정하거나 교체하지 않습니다.
- `v0.5.0-poc`는 PowerShell 5.1 인코딩 사전 검증에서 중단되어 GitHub Release가
  게시되지 않았으며, 해당 불변 태그는 이동하지 않습니다.

## 설치와 검증

공식 GitHub Release에서는 다음 파일만 내려받습니다.

- `SamsungSwitchWatch-Agent-0.5.1-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.5.1-poc-win-x64.zip`

각 ZIP의 build provenance와 release attestation을 확인한 뒤 [설치 안내](INSTALL_KO.md)를
따르십시오. 패키지 내부의 `BUILD-MANIFEST.json`과 SPDX/CycloneDX SBOM도 함께 확인할 수
있습니다. 로컬 자동 검증은 합성 Telnet 출력과 mock Agent를 사용하므로 실장비 검증을
대체하지 않습니다.

## 알려진 제한

- 세 모델의 실제 펌웨어에 따라 우선 명령과 대체 명령을 모두 지원하지 않을 수 있습니다.
  이 경우 해당 수집기만 `미지원`으로 표시되며 회사망에서 모델별 확인이 필요합니다.
- `show syslog tail num 100`은 최근 100건만 제공하므로 점검 사이에 더 많은 로그가 생기면
  `LOG_WINDOW_GAP` 경고가 발생할 수 있습니다.
- Telnet은 암호화되지 않습니다. Agent PC와 스위치는 격리된 관리망에 두고 스위치 ACL로
  Agent IPv4 주소만 TCP/23 접근을 허용해야 합니다.
- 이 `-poc` 산출물은 코드 서명 인증서가 없으면 서명되지 않습니다. 실제 운영 전에는
  세 모델 실장비, 장시간 soak, 절전·재부팅·네트워크 단절과 사내 인증서 검증이 필요합니다.
