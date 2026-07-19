# Comote 직접 연결 테스트 안내

이 빌드는 Supabase, Pusher, 로그인 서버 없이 Client PC와 Manager PC가 직접
연결됩니다. Client가 TCP 포트를 열고 Manager가 Client의 공인 IP 또는 DDNS로
접속합니다.

## 1. Client PC 설정

1. `Client\ComoteClient.exe`를 실행합니다.
2. PC 이름과 접속 암호를 입력합니다. 암호는 최소 8자이며 12자 이상의 무작위
   암호를 권장합니다.
3. 캡처할 모니터를 선택하고 시작합니다.
4. `Client\Windows 방화벽 열기 (관리자).cmd`를 관리자 권한으로 실행합니다.
5. Client PC의 내부 IPv4 주소를 확인합니다.

기본 수신 포트는 `TCP 45820`입니다.

## 2. 공유기 포트포워딩

Client가 연결된 공유기에서 다음 규칙을 추가합니다.

| 항목 | 값 |
|---|---|
| 외부 포트 | TCP 45820 |
| 내부 IP | Client PC의 내부 IPv4 |
| 내부 포트 | TCP 45820 |
| 프로토콜 | TCP |

공인 IP가 공유기 WAN IP와 다르면 통신사 CGNAT 또는 이중 공유기일 수 있습니다.
이 경우 상위 공유기에도 같은 규칙이 필요하거나 통신사에 공인 IPv4를 요청해야
합니다.

## 3. Manager PC 접속

1. `Manager\ComoteManager.exe`를 실행합니다.
2. Client 공유기의 공인 IP 또는 DDNS를 입력합니다.
3. 포트 `45820`과 Client에서 지정한 동일한 암호를 입력합니다.
4. `직접 연결`을 누릅니다.

같은 공유기 안에서는 Client의 내부 IP로 먼저 시험할 수 있습니다. 외부망 시험은
Manager PC를 휴대폰 핫스팟이나 다른 인터넷 회선에 연결해 진행하는 것이 가장
확실합니다. 일부 공유기는 내부에서 자기 공인 IP로 접속하는 NAT Loopback을
지원하지 않습니다.

## 연결 상태 판정

- `Client ...에 직접 연결 중`: TCP 접속 및 암호 확인 중
- 원격 화면 표시: WebRTC 영상 연결 완료
- 키보드/마우스 동작: SendInput 데이터 채널 완료
- 10초 후 실패: IP, 포트포워딩, 방화벽 또는 암호 확인

## 여러 Client PC

공유기 하나 뒤에 여러 Client가 있으면 외부 포트를 다르게 배정합니다.

- PC 1: 외부 `45820` → PC 1 `45820`
- PC 2: 외부 `45821` → PC 2 `45821`
- PC 3: 외부 `45822` → PC 3 `45822`

두 번째 PC부터는 명령행으로 포트를 지정할 수 있습니다.

```text
ComoteClient.exe --port 45821 --password "충분히-긴-암호"
```

## 서버 및 보안 범위

- Supabase/Pusher/Comote 중앙 서버: 사용하지 않음
- TCP 신호 채널: 일회성 nonce와 HMAC-SHA256으로 암호 확인
- 화면·음성·키보드·마우스: WebRTC DTLS/SRTP 암호화
- NAT 주소 탐색: Google 공개 STUN `stun.l.google.com:19302` 사용

STUN도 사용하지 않는 완전 폐쇄형 구성은 현재 빌드 범위가 아닙니다. 이 경우
고정 UDP 포트와 공인 주소 후보를 직접 지정하는 별도 전송 모드가 필요합니다.

인터넷에 포트를 공개하므로 짧거나 재사용된 암호는 사용하지 말고, 사용하지 않을
때는 Client를 종료하거나 포트포워딩 규칙을 비활성화하세요.

