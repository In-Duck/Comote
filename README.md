# Comote prototype

Comote는 한 대의 Manager에서 여러 Windows Client를 확인하고 원격 화면·입력·파일 전송·작업 실행을 시험하는 팀용 프로토타입입니다.

## 빠른 시작

1. Manager PC에서 `ComoteManager.exe`를 실행하고 Manager Hub를 시작합니다.
2. Client PC에서 `ComoteClient.exe`를 실행하고 Manager의 LAN/VPN IP, 포트 45820, 같은 팀 암호를 입력합니다.
3. 입력 모드를 선택합니다.
   - 모드 1: Windows SendInput
   - 모드 2: 공식 FakerInput v0.1.1 가상 HID
4. Manager 목록에 나타난 Client를 더블클릭해 연결합니다.

인터넷 공유기에 TCP 45820을 직접 공개하지 마세요. Manager Hub 제어 채널은 현재 TLS가 아니므로 같은 LAN 또는 WireGuard/Tailscale 같은 팀 VPN에서만 사용해야 합니다.

## FakerInput

모드 2는 공식 배포 MSI를 수정하지 않고 포함합니다. 패키징 시 Authenticode 서명과 고정 SHA-256을 검사하고 설치 직전 Client가 해시를 다시 확인합니다. 드라이버 통신이 세션 중 실패하면 Comote는 SendInput으로 자동 전환합니다.

FakerInput 또는 가상 HID가 모든 게임에서 작동하거나 안티치트 제재가 없다는 보장은 없습니다. Comote는 장치 숨김, 위장 또는 안티치트 우회를 구현하지 않습니다.

## 안전 제한

- 수신 파일: 최대 256MB, 선언 크기 초과·불완전 수신·SHA-256 불일치 시 저장 안 함
- 파일 저장: `다운로드\Comote Downloads`
- 원격 실행: 바탕 화면 또는 Program Files 아래의 EXE/바로가기만 허용
- 업데이트: HTTPS URL과 SHA-256이 일치할 때만 적용
- 연결/입력 채널 종료: 눌린 키와 마우스 버튼 해제 시도
- GitHub Release 게시: 수동 실행에서만 수행

## 개발 검증

```powershell
dotnet build Host/Host.csproj -c Release
dotnet build Viewer/Viewer.csproj -c Release
cd web
npm.cmd run build
```

실제 패키지 생성:

```powershell
powershell.exe -ExecutionPolicy Bypass -File Distribution/Build_Hub_Packages.ps1
```

상세 안내는 `docs/HUB_CONNECTION_TEST_GUIDE.md`, 입력 구조는 `docs/INPUT_ARCHITECTURE.md`, 남은 위험은 `docs/SECURITY_REVIEW.md`를 확인하세요.
