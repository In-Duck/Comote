# Comote 설치 프로그램 제작 가이드

이 가이드는 Inno Setup을 사용하여 배포용 `setup.exe` 파일을 만드는 방법을 설명합니다.

## 1. 사전 준비 (Prerequisites)

설치 프로그램을 빌드하려면 **Inno Setup**이 설치되어 있어야 합니다.
- **다운로드**: [https://jrsoftware.org/isdl.php](https://jrsoftware.org/isdl.php) (Stable Release 다운로드)
- **설치**: 다운로드한 파일을 실행하여 기본 설정으로 설치하세요.

## 2. 파일 확인

`Distribution` 폴더에 다음과 같은 스크립트 파일이 준비되어 있습니다.

- **Host 설치 스크립트**: `Distribution\Host\Host_Installer.iss`
- **Viewer 설치 스크립트**: `Distribution\Viewer\Viewer_Installer.iss`

이 스크립트들은 `..\..\publish\Host-single\Host.exe` 및 `..\..\publish\Viewer-single\Viewer.exe` 경로의 파일을 참조합니다.
반드시 먼저 프로젝트를 `PublishSingleFile` 모드로 게시해야 합니다. (이미 되어 있습니다)

## 3. 설치 프로그램 만들기 (Build) - **[추천] 자동 빌드 스크립트 사용**

Inno Setup을 설치했다면, `Distribution` 폴더에 있는 **`Build_Installers_With_InnoSetup.bat`** 파일을 더블 클릭하세요.
이 스크립트는 다음 작업을 자동으로 수행합니다:

1.  Host/Viewer 프로젝트 최신 버전으로 빌드 (`dotnet publish`)
2.  Inno Setup 컴파일러(`ISCC.exe`) 실행
3.  최종 설치 프로그램 생성

### 수동 빌드 방법 (스크립트 미사용 시)
1.  탐색기에서 `Distribution\Host\Host_Installer.iss` 파일을 더블 클릭하여 엽니다. (Inno Setup Compiler가 실행됩니다)
2.  상단 메뉴의 **Build > Compile** (또는 `Ctrl+F9`)을 누릅니다.
3.  컴파일이 완료되면 `Output` 폴더에 `ComoteHost_Setup.exe` 파일이 생성됩니다.
4.  **Viewer**도 동일한 방법으로 `Viewer_Installer.iss`를 열고 컴파일하세요.

## 4. 결과물

생성된 `Setup.exe` 파일들을 배포하면 됩니다.

- **ComoteHost_Setup.exe**: 설치 시 자동으로 윈도우 서비스로 등록됩니다.
- **ComoteViewer_Setup.exe**: 바탕화면에 바로가기를 만들고 실행합니다.

---
**Tip**: 스크립트 내의 `AppVersion` 등을 수정하여 버전을 올릴 수 있습니다.
