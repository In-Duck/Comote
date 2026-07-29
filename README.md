# Comote

Comote는 같은 계정으로 로그인한 Windows PC를 인터넷에서 확인하고 원격 제어하는 팀용 프로토타입입니다. 기본 연결은 Supabase 계정 소유권, Pusher 시그널링, WebRTC 미디어 채널을 사용하므로 Manager의 IP 주소·포트·VPN 주소를 입력하지 않습니다.

## 빠른 시작

1. Client PC에서 `ComoteClient.exe`를 실행합니다.
2. Comote 아이디 또는 이메일과 비밀번호로 로그인합니다.
3. Manager PC에서 `ComoteManager.exe`를 실행하고 같은 계정으로 로그인합니다.
4. Client가 보이면 더블클릭해 연결합니다.

Client는 PC 이름과 기본 모니터를 자동으로 등록합니다. 처음 30초 동안 Manager가 2초 간격으로 새 장치를 확인한 뒤 10초 간격으로 전환합니다. Client의 트레이 아이콘에서 고급 설정·로그아웃·종료를 선택할 수 있습니다.

`--manager-hub`, `--listen-direct`, TCP 45820 방식은 문제 진단과 폐쇄망 호환을 위한 고급 모드로만 남아 있습니다. 기본 사용자는 `Open Manager Hub Firewall as Administrator.cmd`를 실행할 필요가 없습니다.

## 입력 모드

- 기본: 공식 서명된 FakerInput v0.1.1 가상 HID
- 자동 대체: FakerInput이 없거나 세션 중 실패하면 Windows SendInput
- 고급 설정: `ComoteClient.exe --setup`

FakerInput이 모든 게임에서 작동하거나 안티치트 제재가 없다는 보장은 없습니다. Comote는 장치 숨김, 위장, 안티치트 우회를 구현하지 않습니다.

## 인터넷 연결 품질

직접 P2P 연결이 어려운 NAT 환경에서는 TURN 중계가 필요합니다. 테스트용 공개 릴레이가 호환 경로로 포함되지만 실제 운영 환경은 아래 값을 팀 소유 TURN 서비스로 설정해야 합니다.

- `COMOTE_STUN_URL`
- `COMOTE_TURN_URLS` (`;`로 여러 URL 구분)
- `COMOTE_TURN_USERNAME`
- `COMOTE_TURN_CREDENTIAL`

## 검증

```powershell
dotnet build Host/Host.csproj -c Release
dotnet build Viewer/Viewer.csproj -c Release
cd web
npm.cmd run build
```

배포 패키지는 `Distribution/Build_Hub_Packages.ps1`로 생성합니다. 상세 사용법은 `docs/ACCOUNT_CONNECTION_GUIDE.md`, 입력 구조는 `docs/INPUT_ARCHITECTURE.md`, 남은 출시 위험은 `docs/SECURITY_REVIEW.md`를 확인하세요.