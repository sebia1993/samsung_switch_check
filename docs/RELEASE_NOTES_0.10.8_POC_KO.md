# Samsung Switch Watch 0.10.8-poc 릴리스 노트

이번 버전은 사내에서 발생한 Agent 설치·연결 문제를 실제 주소나 계정 없이 외부 개발
환경에서 재현할 수 있도록, Agent Setup과 Viewer 연결 화면에 수동 저장형 익명 진단을
추가합니다. 장비 기능과 Agent API·설정·데이터베이스 형식은 바꾸지 않습니다.

## 수동 익명 진단 저장

Agent Setup의 검사·설치·복구와 Viewer의 Agent 연결 검사가 성공 또는 실패로 끝나면
`익명 진단 저장`을 사용할 수 있습니다.

- 파일 첫 줄은 `SSW_FIELD_DIAGNOSTIC/1`입니다.
- 한국어 Windows 메모장에서 바로 읽을 수 있는 UTF-8 BOM TXT입니다.
- 사용자가 저장 위치를 선택할 때만 파일을 만듭니다. 자동 저장하지 않습니다.
- 제품·Windows 버전, 작업과 결과, 실패 단계, 안전한 오류·권장 조치 코드, 제한된 단계별
  소요 시간을 기록합니다.
- Viewer는 일반 연결과 같은 PC 시험을 구분하고 주소·DNS·TCP/18443·HTTPS·Identity 단계,
  후보 수와 확인된 Agent/API 버전을 기록합니다.
- 연결 검사가 먼저 성공했더라도 후속 Agent 신원 재확인이나 Viewer 설정 저장이 실패하면
  최종 결과를 실패로 교체해 성공으로 오판하지 않습니다.
- Agent Setup은 패키지, 이전 작업 기록, 서비스, 방화벽, 로컬 TCP/18443과 readiness 단계를
  기록합니다.
- Agent와 Viewer 모두 같은 폴더의 임시 파일을 디스크에 기록한 뒤 최종 TXT로 교체하므로,
  저장 중 실패해도 기존 진단 파일을 가능한 한 보존합니다.
- 저장 실패는 `DIAGNOSTIC_WRITE_FAILED`로 명확하게 표시합니다.

다음 정보는 익명 진단에 포함하지 않습니다.

- IP/CIDR, DNS 이름, PC 이름과 Windows 사용자명
- 장비 계정, 로그인 PW, enable PW와 인증서 정보
- 절대 경로, 방화벽 규칙 원문과 예외 원문
- 실행한 장비 명령과 Telnet 출력

Agent Setup의 기존 실패 전용 `진단정보 복사`는 그대로 유지됩니다.

## 같은 PC 시험 문구 단순화

서로 다른 PC의 주소를 혼동하지 않도록 다음 문구를 명확하게 바꿨습니다.

- Agent Setup: `허용할 Viewer PC 고정 IPv4 · Agent PC 주소 아님`
- Agent Setup 도우미: `같은 PC 시험용 주소`
- Viewer: `Agent와 Viewer가 같은 PC일 때 테스트`

같은 PC 시험 성공은 Agent 서비스, TCP/18443, HTTPS, API와 버전까지만 증명합니다. 실제 원격
Viewer 경로와 스위치 접속은 기존과 같이 별도로 확인해야 합니다.

## 개발 환경 Replay 검증

저장된 익명 진단의 스키마와 민감정보 제외 계약을 확인하고 기존 Fake/Mock 실패 시나리오를
선택하는 개발 전용 Replay 스크립트를 추가했습니다. 이 스크립트와 테스트 자료는 공개 Agent·
Viewer ZIP에 포함하지 않으며 실제 네트워크나 장비에 접속하지 않습니다. Replay는 UTF-8 BOM과
필수 필드, 허용된 값, 중복·미허용 필드 및 민감정보 오염 여부를 먼저 엄격하게 검사합니다.

## 호환성과 유지되는 동작

- Agent와 Viewer는 같은 `0.10.8-poc` Release 조합을 사용합니다.
- Agent API, Viewer 설정, 장비·자격 증명 소유권과 저장 형식은 유지됩니다.
- Agent는 창이나 트레이 아이콘 없이 Windows 서비스로 실행됩니다.
- Viewer는 관리자 권한이나 설치 스크립트가 필요 없는 포터블 실행 파일입니다.
- Agent는 승인된 관리망의 읽기 전용 `show` 명령만 처리합니다.
- 수동 장비 명령과 출력은 Viewer 메모리에만 유지되며 익명 진단에 포함되지 않습니다.

## 검증 범위와 현장 확인

자동 테스트는 보고서 필드, UTF-8 BOM, 단계·시간 보존, 금지 문자열 차단, 저장 실패와 Replay
계약을 합성 데이터로 확인합니다. 이는 실제 Windows SCM, 방화벽 COM, EDR, 원격 라우팅과 삼성
스위치 펌웨어를 증명하지 않습니다.

사내 적용 전에는 다음을 확인하십시오.

1. Agent Setup 검사와 Viewer 연결 성공·실패 뒤 TXT를 수동 저장합니다.
2. 메모장에서 한글과 첫 줄 `SSW_FIELD_DIAGNOSTIC/1`을 확인합니다.
3. 파일에 실제 주소, PC·사용자명, 계정, 경로, 방화벽·예외 원문이 없는지 확인합니다.
4. 같은 PC 시험 후 실제 원격 Viewer에서 Agent 연결을 다시 확인합니다.
5. 스위치 접속과 `show` 명령은 영향이 적은 장비 한 대에서 별도로 검증합니다.

## 공개 Assets

- `SamsungSwitchWatch-Agent-0.10.8-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.10.8-poc-win-x64.zip`

GitHub가 자동 표시하는 Source code ZIP과 tar.gz는 실행 패키지가 아닙니다. 사용자 정의
Release Assets는 위 두 ZIP만 게시합니다.
