# Samsung Switch Watch 0.11.0-poc 릴리스 노트

릴리스 날짜: 2026-08-04

이번 버전은 보안 설정의 복잡도보다 현장 동작과 복구 가능성을 우선해 Agent 설치·연결 흐름을
단순화합니다. Agent는 여전히 창 없는 Windows 서비스이며, Viewer가 장비와 자격 증명, 명령,
결과와 감시 상태를 소유합니다.

## 가장 큰 변경

- Agent Setup에서 Viewer IPv4와 스위치 관리 CIDR 입력을 제거했습니다.
- Agent는 loopback과 RFC1918 사설 IPv4의 Viewer 요청을 자동 허용합니다.
- 스위치 대상은 RFC1918 사설 IPv4와 Telnet/TCP 23으로 자동 제한합니다.
- Agent는 서비스 시작마다 새 임시 RSA 자체 서명 인증서를 생성합니다. Windows Schannel용
  임시 키 컨테이너는 프로세스 수명에만 사용하고 종료 시 제거합니다.
- Viewer는 Agent TLS 인증서를 자동 수락합니다. 지문, 페어링 토큰, TOFU 재신뢰 절차는
  없습니다.
- API v4가 호환되면 Agent·Viewer 제품 버전이 달라도 경고 후 연결합니다.

## 설치와 준비 상태 분리

- 패키지 검증, 파일 교체, 서비스 구성처럼 실제 설치 변경이 실패하면 기존 트랜잭션 복구를
  수행합니다.
- 서비스 설치 후 로컬 TCP/HTTPS/API/버전 확인이 실패해도 정상 설치를 되돌리지 않습니다.
- 이 경우 `AGENT_LOCAL_CONNECTION_UNCONFIRMED` 경고와 Viewer에서 수행할 연결 확인 절차를
  표시하고 Agent 서비스는 유지합니다.
- 제품 방화벽 규칙은 Domain/Private 프로필, TCP/18443, RFC1918 원격 대역으로 자동 구성하려고
  시도합니다.
- 방화벽 적용·재조회 또는 회사 GPO 확인이 실패해도 설치를 되돌리지 않고
  `FIREWALL_REMOTE_ACCESS_UNCONFIRMED` 경고를 표시합니다.

## 단순해진 사용 흐름

1. Agent ZIP을 풀고 `SamsungSwitchWatch.Agent.Setup.exe`를 실행합니다.
2. UAC를 승인한 뒤 별도 네트워크 입력 없이 `설치/업데이트`를 누릅니다. Setup이 내부 사전
   점검을 수행한 뒤 설치를 계속합니다.
3. Viewer ZIP을 풀고 `SamsungSwitchWatch.Viewer.exe`를 실행합니다.
4. Agent PC 주소 하나만 입력해 연결합니다.
5. Viewer에서 장비 IP, ID, 로그인 PW, 선택적 enable PW를 입력하고 한 줄 `show` 명령을
   실행합니다.

## 보안 및 운영상 주의

- HTTPS는 Viewer와 Agent 사이의 전송 내용을 암호화하지만 Agent 신원을 인증하지 않습니다.
- 별도 API 인증도 없으므로 Agent를 사용자 VLAN, 공용 Wi-Fi 또는 인터넷에 노출하면 안 됩니다.
- Agent와 Viewer는 신뢰할 수 있는 사내 사설망에서만 사용합니다.
- Telnet은 암호화되지 않습니다. Agent와 스위치 사이도 제한된 관리망이어야 합니다.
- 수동 명령은 줄바꿈·구분자가 없는 한 줄 `show`만 허용하며 설정 변경 명령은 계속 차단합니다.
- Agent는 장비, 자격 증명, 명령, 출력과 감시 이력을 저장하지 않습니다.

## 호환성

- API v4 경로와 요청·응답 형식은 유지합니다.
- 이전 Viewer 설정 파일의 인증서 신뢰 값은 삭제하지 않지만 연결 판단에는 사용하지 않습니다.
- 운영 시에는 기능 차이를 줄이기 위해 같은 Release의 Agent와 Viewer 사용을 권장합니다.

## 배포 파일

GitHub Release의 사용자 정의 Assets에는 다음 두 ZIP만 게시합니다.

```text
SamsungSwitchWatch-Agent-0.11.0-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.0-poc-win-x64.zip
```

두 ZIP은 Windows x64 self-contained 패키지이며 Python 또는 별도 .NET 런타임 설치를 요구하지
않습니다. 코드 서명되지 않은 POC이므로 사내 EDR·AppLocker·WDAC 승인 여부는 별도로 확인해야
합니다.

자동 검증은 합성 Telnet 서버와 비식별 Fixture만 사용합니다. 실제 삼성 스위치의 펌웨어별
명령, 회사 GPO·EDR·방화벽·라우팅과 장시간 현장 동작은 승인된 사내 시험 PC에서 단계적으로
확인해야 합니다.
