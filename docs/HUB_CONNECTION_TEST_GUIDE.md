# Comote Manager Hub 사용 안내

## 권장 연결 방식

Manager Hub의 TCP 45820 채널은 nonce+HMAC-SHA256으로 등록 암호를 확인하지만 현재 TLS 채널은 아닙니다. 따라서 인터넷 공유기에서 45820을 공개하지 말고 다음 중 하나로 연결하세요.

1. 같은 사내/팀 LAN
2. WireGuard 또는 Tailscale 같은 팀 VPN

`Open Manager Hub Firewall as Administrator.cmd`는 Manager PC의 Windows 방화벽에서 TCP 45820 수신을 여는 도구입니다. 공유기 포트포워딩을 만들지는 않습니다. VPN 어댑터 또는 신뢰하는 사설 네트워크에서만 사용하세요.

## Manager PC

1. `Manager\ComoteManager.exe`를 실행합니다.
2. Manager Hub 모드에서 포트 `45820`과 12자 이상의 팀 전용 암호를 입력합니다.
3. Windows 방화벽 차단이 뜨면 사설 네트워크만 허용합니다.
4. Client에는 Manager의 LAN IP 또는 VPN IP를 입력합니다.

## Client PC

1. `Client\ComoteClient.exe`를 실행합니다.
2. Manager 주소, 포트 `45820`, 같은 등록 암호를 입력합니다.
3. 입력 모드를 고릅니다.
   - 모드 1: Windows SendInput
   - 모드 2: FakerInput 가상 HID
4. 모드 2를 처음 선택하면 동봉된 공식 MSI의 해시를 검사한 뒤 관리자 설치가 시작됩니다.
5. 설치 후에도 장치를 찾지 못하면 Client를 완전히 종료하고 Client PC를 한 번 재부팅합니다.

## 연결 확인

- Manager 목록에 Client가 온라인으로 표시되는지 확인합니다.
- Client를 더블클릭해 영상이 나타나는지 확인합니다.
- 먼저 메모장에서 키보드와 마우스를 시험합니다.
- 게임 테스트 전 해당 게임의 가상 입력·원격 제어 정책을 확인합니다.
- 연결을 닫은 뒤 눌린 키가 남지 않는지 확인합니다.

## 파일 전송

- 파일 크기는 1바이트 이상 256MB 이하로 제한됩니다.
- SHA-256이 포함된 전송은 수신 후 일치할 때만 저장합니다.
- 저장 위치는 `다운로드\Comote Downloads`입니다.
- 선언 크기 초과, 불완전 수신, 해시 불일치 파일은 저장하지 않습니다.

## 문제 해결

- “FakerInput 필요”: 패키지 안의 `FakerInput_Setup_0.1.1_x64.msi`가 있는지 확인하고, 없으면 Client 전체 ZIP을 다시 받습니다.
- 설치했지만 미감지: Client 종료 → PC 재부팅 → Client 재실행 순서로 확인합니다.
- Manager에 Client가 안 보임: Manager/VPN IP, 포트, 등록 암호, Windows 사설 네트워크 방화벽을 확인합니다.
- 연결 중에서 멈춤: 양쪽 시간을 맞추고 VPN 연결 및 UDP 통신을 확인한 뒤 재접속합니다.
