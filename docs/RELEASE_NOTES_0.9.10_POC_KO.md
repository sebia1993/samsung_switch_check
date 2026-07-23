# Samsung Switch Watch 0.9.10-poc 릴리스 노트

## 핵심 변경

- 장비를 삭제한 뒤 같은 IP로 다시 등록하는 동안 진행 중 작업 잠금이 교체되어
  Telnet 세션이 겹칠 수 있던 문제를 수정했습니다.
- IP 입력 앞뒤의 공백 때문에 주기 감시와 수동 연결 테스트가 서로 다른 장비
  잠금을 사용하던 문제를 수정했습니다.
- 동일 IP의 작업 잠금을 Viewer 수명 동안 유지해 모든 Telnet 작업을 직렬화합니다.

## 호환성

- Agent API, Viewer 화면, 장비 설정과 저장 형식은 변경하지 않았습니다.

## 검증

- 장비 삭제·재등록 및 IP 앞뒤 공백 입력 뒤에도 같은 IP 작업이 Viewer 수명 동안
  직렬화되는 회귀 테스트를 추가했습니다.

## 알려진 제한

- 실제 장비 검증 전까지는 POC 상태입니다.

## 배포 파일

- `SamsungSwitchWatch-Agent-0.9.10-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.9.10-poc-win-x64.zip`
