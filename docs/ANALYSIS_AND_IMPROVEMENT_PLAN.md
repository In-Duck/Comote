# Comote 프로젝트 폴더 분석 보고서 및 개선 계획서

**분석 일자:** 2026-02-19  
**대상:** Comote (KYMOTE) 원격 제어 솔루션

---

## 1. 프로젝트 개요

| 구분 | 내용 |
|------|------|
| **목적** | Windows 원격 제어 (Host: 화면 공유/입력 수신, Viewer: 조종 클라이언트) |
| **기술 스택** | .NET 9 (Host: WinForms, Viewer: WPF), WebRTC(SIPSorcery), FFmpeg, Pusher, Supabase |
| **구성** | Host, Viewer, web(Next.js 랜딩), Distribution(인스톨러), ApiCheck(유틸) |

---

## 2. 발견된 문제점

### 2.1 긴급 (즉시 수정 권장)

#### 2.1.1 서비스 이름 불일치 (설치 후 서비스 미동작)

- **위치:** `Host/Program.cs` vs `Distribution/Host/Host_Installer.iss`
- **내용:**  
  - `Program.cs`: 서비스 이름 **`KymoteHost`** 로 등록/해제 (`sc create KymoteHost`, `sc start KymoteHost`)  
  - `Host_Installer.iss`: **[Run]** 섹션에서 **`ComoteHost`** 로 시작/중지 (`sc start ComoteHost`, `sc stop ComoteHost`)
- **결과:** 설치 후 인스톨러가 `ComoteHost` 서비스를 시작하려 하나, 실제 등록된 서비스는 `KymoteHost`이므로 **서비스가 시작되지 않음**.
- **조치:** 서비스 이름을 하나로 통일 (권장: 코드와 동일하게 `KymoteHost`로 ISS 수정).

#### 2.1.2 민감 정보 저장소 포함 (보안)

- **위치:** `Host/appsettings.json`
- **내용:** Pusher **Secret**, Supabase **Anon Key**, WebAuth URL 등이 평문으로 저장되어 Git에 커밋 가능.
- **위험:** 저장소 유출 시 Pusher/Supabase 악용, 인증 우회 등.
- **조치:**  
  - `appsettings.json`은 Git에서 제외하고 `appsettings.example.json`만 커밋.  
  - 실제 값은 User Secrets(`dotnet user-secrets`) 또는 환경 변수로 주입.  
  - CI/배포 시에도 환경 변수 또는 시크릿 매니저 사용.

#### 2.1.3 업데이트 무결성 검증 미사용

- **위치:** `Distribution/version.json`
- **내용:** `host_hash`, `viewer_hash`가 **빈 문자열**로 두어져 있음.
- **결과:** `Host/AutoUpdater.cs`, `Viewer/AutoUpdater.cs`의 SHA256 검증 로직이 동작하지 않아, **다운로드된 설치 파일 변조 시에도 검증 없이 설치 가능**.
- **조치:**  
  - 릴리스 빌드 후 Host/Viewer Setup exe에 대해 SHA256 해시 계산.  
  - `version.json`의 `host_hash`, `viewer_hash`에 반영.  
  - 빌드 스크립트에 해시 자동 갱신 단계 추가.

---

### 2.2 중요 (단기 개선)

#### 2.2.1 버전 표기 불일치

- **위치:** `Host/Program.cs`, `Viewer/Program.cs`
- **내용:** 콘솔 배너에 **"v1.1.0"** 하드코딩. 실제 버전은 csproj/version.json 기준 **1.2.0**.
- **조치:** `Assembly.GetExecutingAssembly().GetName().Version` 또는 공통 상수/리소스에서 버전을 읽어 표시하도록 수정.

#### 2.2.2 웹 다운로드 링크 하드코딩

- **위치:** `web/app/page.tsx`
- **내용:** 다운로드 URL과 버전 문구가 **v1.2.0** 등으로 하드코딩.
- **조치:**  
  - `Distribution/version.json`을 빌드 시 복사하거나 API로 노출해 버전/URL을 동적으로 사용.  
  - 또는 환경 변수(`NEXT_PUBLIC_*`)로 버전·URL 주입.

#### 2.2.3 솔루션 파일 부재

- **위치:** 리포지토리 루트
- **내용:** `.sln` 파일이 없어 Host, Viewer, ApiCheck 등을 한 번에 열거나 빌드하기 어려움.
- **조치:** `Comote.sln` 생성 후 Host, Viewer, ApiCheck(필요 시 web은 제외) 참조 추가.

#### 2.2.4 .gitignore 규칙 충돌

- **위치:** `.gitignore`
- **내용:**  
  - `!Distribution/**/*.exe`, `!Distribution/*.exe` 로 exe 포함 시도  
  - 이어서 `Distribution/**/*.exe`, `*.zip` 로 다시 제외  
- **결과:** Distribution exe는 사실상 무시됨. 의도가 “exe는 커밋하지 않는다”라면 현재 동작과 일치하지만, 주석과 `!` 규칙과 혼동 가능.
- **조치:** “Distribution exe는 커밋하지 않음”으로 의도를 명확히 하고, 불필요한 `!` 규칙 정리.

---

### 2.3 권장 (중기 개선)

#### 2.3.1 코드 중복

- **대상:**  
  - **AutoUpdater + UpdateInfo:** `Host/AutoUpdater.cs` / `Viewer/AutoUpdater.cs` (URL·JsonProperty 이름만 다름).  
  - **FFmpegExtractor:** `Host/FFmpegExtractor.cs` / `Viewer/FFmpegExtractor.cs` 거의 동일.
- **조치:**  
  - 공통 라이브러리 프로젝트(예: `Comote.Shared`) 생성 후 AutoUpdater, FFmpegExtractor, 공통 상수 이동.  
  - Host/Viewer는 해당 라이브러리 참조.

#### 2.3.2 단위/통합 테스트 부재

- **내용:** 테스트 프로젝트 및 테스트 코드가 없음.
- **조치:**  
  - `Host.Tests`, `Viewer.Tests` 또는 `Comote.Tests` xUnit/NUnit 프로젝트 추가.  
  - 시그널링, 설정 로드, 해시 검증 등 핵심 로직부터 테스트 추가.  
  - CI에서 `dotnet test` 실행.

#### 2.3.3 Viewer 시작 시 디버그 로그

- **위치:** `Viewer/Program.cs`
- **내용:** `[DEBUG] Main() started`, `[DEBUG] Creating Application...` 등 다수 `Console.WriteLine` 디버그 출력.
- **조치:**  
  - `#if DEBUG` 로 감싸거나 로거로 교체.  
  - 배포 빌드에서는 로그 최소화.

#### 2.3.4 웹 미사용 import

- **위치:** `web/app/page.tsx`
- **내용:** `import Image from "next/image";` 선언만 있고 사용처 없음.
- **조치:** 사용하지 않으면 import 제거 (lint 경고 제거).

#### 2.3.5 Host 설치 경로와 표시 이름

- **위치:** `Distribution/Host/Host_Installer.iss`
- **내용:** `DefaultDirName={autopf}\{#MyAppName}` → "Comote Host" 폴더. 앱 내부/콘솔 표기는 "KYMOTE".
- **판단:** 브랜딩이 "Comote" (제품명) + "KYMOTE" (UI명)으로 나뉜 것으로 보임. 팀 정책에 따라 통일 여부 결정 후, 사용자 안내(문서/웹)와 일치시키기.

---

## 3. 개선 계획서 (실행 순서)

### Phase 1: 긴급 수정 (1~2일)

| 순서 | 작업 | 담당 참고 |
|------|------|-----------|
| 1.1 | **서비스 이름 통일** – `Host_Installer.iss`의 [Run]/[UninstallRun]에서 `ComoteHost` → `KymoteHost`로 변경 | Host/Program.cs의 서비스명과 동일하게 |
| 1.2 | **appsettings 보안** – `appsettings.json`을 `.gitignore`에 추가, `appsettings.example.json` 생성(값은 플레이스홀더), 문서에 User Secrets/환경변수 설정 방법 명시 | Host/AppSettings.cs 로드 경로 확인 |
| 1.3 | **version.json 해시** – 릴리스 빌드 후 Host/Viewer Setup exe의 SHA256 계산해 `host_hash`, `viewer_hash` 채우기; 필요 시 배치/스크립트로 자동화 | Distribution/Build_Installers_With_InnoSetup.bat 확장 |

### Phase 2: 중요 개선 (약 1주)

| 순서 | 작업 | 담당 참고 |
|------|------|-----------|
| 2.1 | **버전 표기 통일** – Host/Viewer Program.cs에서 Assembly 버전 또는 공통 상수로 콘솔 배너 버전 출력 | csproj의 Version/AssemblyVersion |
| 2.2 | **웹 버전/URL 동적화** – version.json 기반 또는 env 기반으로 다운로드 링크·버전 문구 표시 | web/app/page.tsx |
| 2.3 | **솔루션 파일** – `Comote.sln` 생성, Host·Viewer·ApiCheck 포함 | dotnet new sln; dotnet sln add ... |
| 2.4 | **.gitignore 정리** – Distribution exe/zip 제외 의도 명확화, 불필요한 `!` 제거 | 현재 동작 유지하면서 가독성 개선 |

### Phase 3: 권장 개선 (2~4주)

| 순서 | 작업 | 담당 참고 |
|------|------|-----------|
| 3.1 | **공통 라이브러리** – Comote.Shared 프로젝트 생성, AutoUpdater·FFmpegExtractor·공통 모델 이동 | Host/Viewer에서 참조 |
| 3.2 | **테스트 도입** – 테스트 프로젝트 추가, 설정/업데이트 해시/시그널링 등 핵심 로직 테스트 | CI 파이프라인에 dotnet test 추가 |
| 3.3 | **Viewer 디버그 로그** – DEBUG 전처리기 또는 로거로 정리 | Viewer/Program.cs |
| 3.4 | **웹 lint** – page.tsx 미사용 import 제거 | next/image |
| 3.5 | **브랜딩/경로 정책** – Comote vs KYMOTE, 설치 폴더명·서비스명 최종 확정 후 문서 반영 | 선택 사항 |

---

## 4. 폴더 구조 요약 (참고)

```
Comote/
├── ApiCheck/           # SIPSorcery 타입 확인 유틸 (빌드 참조용)
├── Distribution/       # 인스톨러(ISS), version.json, 빌드 배치
├── Host/               # WinForms 호스트 (캡처, WebRTC, Pusher, 로그인)
├── Viewer/             # WPF 뷰어 (영상 수신, 입력 전송, Supabase)
├── web/                # Next.js 랜딩 (다운로드, 소개)
├── ffmpeg/             # 네이티브 DLL (임베드용)
├── publish/            # dotnet publish 출력 (.gitignore 권장 유지)
└── docs/               # 본 문서 등
```

---

## 5. 정리

- **즉시:** 서비스 이름 불일치 수정으로 설치 후 Host 서비스가 실제로 시작되도록 하고, appsettings 비공개화·version.json 해시 채우기로 보안과 업데이트 무결성을 확보하는 것을 권장합니다.
- **단기:** 버전 표기·다운로드 URL·솔루션·.gitignore 정리로 유지보수성과 일관성을 높일 수 있습니다.
- **중기:** 공통 코드 추출, 테스트 도입, 로그/import 정리로 품질과 확장성을 개선할 수 있습니다.

이 문서를 기준으로 Phase 1부터 순차 적용하시면 됩니다. 필요 시 각 항목별로 이슈/태스크로 쪼개어 진행하시면 됩니다.
