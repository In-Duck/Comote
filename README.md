# Comote prototype

Comote는 여러 Windows PC를 한 화면에서 보고 제어하는 Manager/Client 프로토타입입니다. Manager는 명령과 상태만 중계하고, Client는 화면 전송·입력·작업 실행·업데이트를 담당합니다.

## 빠른 테스트

### 1. 데모 UI

```powershell
dotnet run --project Viewer/Viewer.csproj -- --demo
```

실제 Client 없이 샘플 PC 목록, 썸네일 그리드와 작업 패널을 확인할 수 있습니다.

### 2. 같은 PC에서 Hub 연결

Manager:

```powershell
dotnet run --project Viewer/Viewer.csproj -- --manager-hub --port 45820 --password comote-test-2026
```

Client:

```powershell
dotnet run --project Host/Host.csproj -- --manager-client --manager 127.0.0.1 --port 45820 --password comote-test-2026 --name Local-Test-PC
```

다른 PC에서 테스트할 때는 `127.0.0.1` 대신 Manager PC의 LAN IP를 사용하고 Windows 방화벽에서 TCP 45820을 허용합니다. Client 측 포트포워딩은 필요하지 않습니다.

### 3. 웹사이트

```powershell
cd web
npm install
npm run dev
```

브라우저에서 표시되는 로컬 주소를 엽니다. 다운로드 링크는 다음 환경 변수로 실제 설치 파일 주소에 연결할 수 있습니다.

```env
NEXT_PUBLIC_MANAGER_DOWNLOAD_URL=https://example.com/ComoteManagerSetup.exe
NEXT_PUBLIC_CLIENT_DOWNLOAD_URL=https://example.com/ComoteClientSetup.exe
```

## 원격 업데이트 게시

1. Client 패키지를 ZIP으로 만들고 GitHub Release 또는 HTTPS CDN에 업로드합니다.
2. ZIP의 SHA-256을 계산합니다.
3. `Distribution/client-update.latest.json`의 `version`, `downloadUrl`, `sha256`을 갱신합니다.
4. Manager Hub의 작업 패널에서 원격 업데이트를 선택합니다.
5. 선택 PC 또는 온라인 전체 PC에 명령을 전송합니다.

Client는 패키지를 직접 다운로드하고 SHA-256이 일치할 때만 교체합니다. 설정, 기기 ID와 로그는 패키지 교체 대상에서 제외해야 합니다. 상세 내용은 `docs/REMOTE_CLIENT_UPDATE.md`와 `docs/REMOTE_UPDATE_TEST_CHECKLIST.md`를 참고하세요.

## 구성

- `Viewer/`: Manager 데스크톱 앱
- `Host/`: Client 데스크톱 앱
- `web/`: 랜딩, 로그인, 대시보드와 다운로드 API
- `Distribution/`: 패키징 스크립트와 업데이트 manifest
- `docs/`: 연결, 업데이트와 테스트 가이드
- `supabase/`: 계정·Fleet 백엔드 스키마

## 검증 명령

```powershell
dotnet build Viewer/Viewer.csproj
dotnet build Host/Host.csproj
cd web
npm run build
```

실제 원격 입력과 파일 교체는 반드시 테스트 전용 Windows PC 두 대에서 먼저 검증하세요.
