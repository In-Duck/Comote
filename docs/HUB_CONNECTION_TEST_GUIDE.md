# Comote 1.6.0 Preview 5 Manager Hub 테스트 안내

## 연결 구조

```text
Client 1 ─┐
Client 2 ─┼──▶ Manager 공인 IP 또는 DDNS : TCP 45820
Client 3 ─┘
```

포트포워딩은 Manager PC가 있는 공유기에서 한 번만 설정합니다. Client PC에는
포트포워딩이나 인바운드 방화벽 규칙이 필요하지 않습니다.

## 1. Manager PC

1. `Manager\ComoteManager.exe`를 실행합니다.
2. 수신 포트 `45820`과 Client 등록 암호를 입력합니다.
3. `Manager\Open Manager Hub Firewall as Administrator.cmd`를 관리자 권한으로
   실행합니다.
4. Manager PC의 내부 IPv4 주소를 확인합니다.
5. Manager 공유기에서 아래 포트포워딩 규칙을 추가합니다.

| 항목 | 값 |
|---|---|
| 외부 포트 | TCP 45820 |
| 내부 IP | Manager PC 내부 IPv4 |
| 내부 포트 | TCP 45820 |
| 프로토콜 | TCP |

Manager PC의 공인 IP가 공유기 WAN IP와 다르면 CGNAT 또는 이중 공유기일 수
있습니다. 이 경우 상위 공유기에도 같은 포트포워딩이 필요하거나 공인 IPv4가
필요합니다.

## 2. Client PC

1. `Client\ComoteClient.exe`를 실행합니다.
2. Manager 공유기의 공인 IP 또는 DDNS를 입력합니다.
3. 포트 `45820`과 Manager에서 지정한 동일한 등록 암호를 입력합니다.
4. Manager 목록에 표시할 Client 이름과 캡처 모니터를 선택합니다.
5. `Manager에 연결`을 누릅니다.

설정 창이 닫힌 뒤 Client는 별도 송출 창 없이 시스템 트레이에서 실행됩니다.
트레이 아이콘의 우클릭 메뉴에서 Client를 종료할 수 있습니다.

Client는 연결이 끊어지면 2초부터 최대 30초 간격으로 Manager에 자동
재접속합니다.

## 썸네일 메인 뷰

Manager는 LinkIO 메인 뷰처럼 접속된 Client 화면을 밀집 그리드로 표시합니다.
각 Client는 약 3초 간격으로 저용량 JPEG 미리보기를 전송하며, 타일을 한 번
누르면 선택되고 두 번 누르면 해당 PC의 실시간 원격제어 화면으로 전환됩니다.
썸네일은 상태 확인용이며 원격제어 화면의 실시간 영상과는 별도입니다.

## 3. 연결 시험

같은 공유기 안에서 먼저 시험할 때는 Client에 Manager PC의 내부 IP를
입력합니다. 외부망 시험에서는 Manager 공유기의 공인 IP 또는 DDNS를
입력합니다.

Manager 목록에 Client가 온라인으로 나타나면 해당 Client를 더블클릭하거나
`CONNECT`를 누릅니다.

- 목록 등록: TCP Hub 인증과 Client 등록 완료
- 원격 화면 표시: WebRTC 영상 연결 완료
- 키보드/마우스 동작: SendInput 데이터 채널 완료
- Client가 오프라인 표시: Hub 연결 종료

## 포트 하나의 의미

사용자가 포트포워딩해야 하는 포트는 Manager의 `TCP 45820` 하나뿐입니다.
여러 Client가 같은 포트로 동시에 접속하며 Client ID로 구분됩니다.

화면·음성·입력은 WebRTC로 전달되므로 운영체제 내부에서는 동적 UDP 연결과
Google 공개 STUN `stun.l.google.com:19302`을 사용합니다. 이 UDP 통신은
Client와 Manager가 바깥으로 생성하며 공유기에 별도 포트포워딩할 필요는
없습니다.

## 보안

- Supabase/Pusher/Comote 중앙 서버: 사용하지 않음
- Hub 등록 인증: 일회성 nonce + HMAC-SHA256
- 화면·음성·키보드·마우스: WebRTC DTLS/SRTP 암호화
- 등록 암호: 최소 8자, 16자 이상의 무작위 암호 권장

Manager 포트는 인터넷에 공개되므로 재사용 암호나 짧은 암호를 사용하지
마세요. 사용하지 않을 때는 Manager를 종료하거나 포트포워딩을 비활성화하는
것이 안전합니다.

## 현재 테스트 버전의 범위

- 여러 Client 동시 온라인 등록 지원
- 여러 Client 화면의 저부하 썸네일 그리드 지원
- 썸네일 선택 및 더블클릭 원격 연결 지원
- Manager 목록에서 Client 선택 제어 지원
- 한 Manager 창에서 한 번에 한 Client 원격 화면 활성화
- Client 연결 유지 및 자동 재접속 지원
- 설치별 고유 장치 ID 생성 및 재실행 시 유지
- H.264 우선 연결, 20fps/6Mbps 기본값으로 WAN 안정성 개선
- 원격제어 중 썸네일 캡처 일시중지
- Manager/Client FFmpeg 캐시 분리
- 영상의 실제 표시 영역을 기준으로 마우스 좌표 보정
- 다중 모니터의 음수 좌표와 가상 데스크톱 입력 지원
- 창 포커스 상실 시 눌린 키·마우스 강제 해제
- PC 전환 중 이전 연결의 재연결 이벤트 차단
- SendInput 키보드/마우스 제어 지원
- 가상 HID 모드2/3은 후속 버전

