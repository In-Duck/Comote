# LinkIO Remote 입력 드라이버 분석

작성일: 2026-07-17  
분석 경로: `C:\Program Files (x86)\LinkIO_Remote`  
분석 방식: 파일·서명·INF·서비스·실행 파일 내장 문자열 정적 확인  
주의: 프로그램이나 드라이버 설치/삭제 명령은 실행하지 않았다.

## 1. 결론

LinkIO의 입력 모드 2·3은 Windows **가상 HID(Virtual HID Framework) 커널 드라이버**를 이용해 원격 키보드와 마우스 입력을 주입하는 구조다.

Remote 설치 폴더에는 현재 드라이버 패키지 세 세트가 있다.

| 패키지 | Hardware ID | 커널 서비스 | 사용자 모드 연결 |
|---|---|---|---|
| `IOD` | `HID\IOD` | `IOD_Service` | `IOC.dll` |
| `IOHID` | `HID\IOHID` | `IOHID_Service` | `IOCON.dll` 계열 |
| `HIDi` | `HID\HIDi` | `HIDi_Service` | Remote 내 가상 HID backend |

세 드라이버 모두 다음 특성을 갖는다.

- Windows 10 build 18362 이상, AMD64 대상
- KMDF 1.15
- Windows Virtual HID Framework의 `hidvhf.inf` 사용
- 커널 드라이버 시작 방식은 Demand Start
- Lower filter로 등록
- Microsoft Windows Hardware Compatibility Publisher 서명 유효

모드 전환 메뉴와 설치 메뉴를 합치면 다음은 확정할 수 있다.

| 입력 모드 | 별도 설치 메뉴 | 분석 |
|---|---:|---|
| 모드1 | 없음 | 기본/호환 입력 backend |
| 모드2 | 있음 | 가상 HID 드라이버 backend |
| 모드3 | 있음 | 다른 가상 HID 드라이버 backend |

현재 정적 분석만으로는 모드2와 모드3이 `IOD`, `IOHID`, `HIDi` 중 어느 조합을 정확히 선택하는지 1:1로 확정할 수 없다. Remote 코드에는 `newhid`, `hidi`, `iohid`, `hido`, `legacy` 등 여러 내부 backend 이름이 함께 남아 있다.

## 2. 설치 파일 구성

### 실행 프로그램

- `remote.exe`: 화면 캡처, WebRTC/네트워크, 입력 backend 선택
- `launcher.exe`: 사용자 세션 Launcher
- `launcher_svc.exe`: LocalSystem 서비스
- `restart.exe`: 클라이언트 재시작 보조 프로그램
- `update.exe`, `autoupdate-*.exe`: 업데이트 적용

### 입력 관련 DLL

#### `IOC.dll`

내장 문자열과 export에서 다음을 확인했다.

- 부모 장치 `HID\IOD` 탐색
- `IOD_Service` 사용
- 마우스 절대/상대 이동
- 좌/우/중간/X1/X2 버튼
- 스크롤
- 키보드 Down/Up/Type
- 가상 HID 자식 collection 탐색

따라서 `IOC.dll`은 `IOD` 드라이버의 사용자 모드 입력 라이브러리다.

#### `IOCON.dll`

다음 기능을 확인했다.

- 가상 키보드/마우스/절대 마우스 instance 생성
- 프로필을 지정한 instance 생성
- `DeviceIoControl` 사용
- 별도 인증 장치와 PID 등록
- 키보드와 마우스 전체 입력 API

`IOCON.dll`은 인증과 프로필을 강화한 다른 가상 HID backend로 보인다.

## 3. 드라이버별 세부 정보

### `IOD`

- INF: `driver\IOD.inf`
- SYS: `driver\IOD.sys`
- Hardware ID: `HID\IOD`
- 서비스: `IOD_Service`
- 제조자 표기: `IOSoft`
- DriverVer: `2026-04-27 17.30.46.453`
- 사용자 모드 DLL: `IOC.dll`

### `IOHID`

- INF: `driver\IOHID.inf`
- SYS: `driver\IOHID.sys`
- Hardware ID: `HID\IOHID`
- 서비스: `IOHID_Service`
- 제조자 표기: `IOSoft`
- DriverVer: `2026-04-28 14.59.13.280`
- Remote 코드가 별도의 인증 장치 경로를 사용
- 사용자 모드 DLL은 `IOCON.dll` 계열로 판단

### `HIDi`

- INF: `driver\HIDi.inf`
- SYS: `driver\HIDi.sys`
- Hardware ID: `HID\HIDi`
- 서비스: `HIDi_Service`
- DriverVer: `2026-05-29 18.33.33.176`
- 현재 세 패키지 중 가장 최근 버전
- Remote 코드가 별도의 인증 장치 경로를 사용

## 4. `구버전 삭제`의 대상

Remote 실행 파일은 현재 폴더에 없는 다음 과거 장치와 INF도 제거 대상으로 인식한다.

- `HID\DevIOHID`
- `ROOT\DevIOHID`
- `DevIOHID.inf`
- `HID\DeviceIO`
- `ROOT\DeviceIO`
- `DeviceIO.inf`
- `HID\HIDo`
- `HIDo.inf`

따라서 LinkIO 메뉴의 `드라이버 삭제 → 구버전`은 이 레거시 장치, 서비스, Driver Store 패키지를 정리하는 기능으로 판단할 수 있다.

삭제 로직은 다음 수단을 사용한다.

- `devcon.exe remove`
- `pnputil.exe /delete-driver`
- `/uninstall /force`
- 장치 rescan
- Driver Store 패키지 열거 및 일치 패키지 제거
- 과거 장치용 레지스트리 값 정리

## 5. 설치 및 전환 과정

Remote 내장 로그 문자열을 기준으로 설치 흐름은 다음과 같다.

1. 선택한 vHID backend 결정
2. `driver` 폴더와 대상 패키지 존재 확인
3. 관리자 권한 확인
4. Driver Store/장치 설치
5. 장치 rescan
6. 최대 약 5초 동안 설치 확인
7. 가상 HID backend 초기화
8. 실패하면 일반 Windows 입력 방식으로 fallback

Remote는 backend 초기화 실패 시 일반 입력 방식으로 되돌아가는 로직을 갖고 있다. Comote에도 같은 fallback 구조가 필요하다.

## 6. Launcher 서비스

설치된 서비스:

- 서비스 이름: `LinkIORemoteLauncherService`
- 표시 이름: `LinkIO Remote Control Launcher`
- 시작 방식: Automatic
- 실행 계정: LocalSystem
- 실행 파일: `launcher_svc.exe`
- 현재 확인 시 상태: Stopped

Remote는 `LinkIORemoteLauncherPipe`라는 Named Pipe를 통해 Launcher 서비스와 통신한다.

이 서비스는 다음 목적을 가진 것으로 판단된다.

- 관리자 권한이 필요한 드라이버 설치/삭제
- 원격 프로그램 재실행
- 사용자 세션과 서비스 세션 사이 명령 전달
- 일반 사용자 프로세스가 직접 수행할 수 없는 시스템 작업

Comote도 Host Windows Service와 사용자 세션 Helper를 분리하고, 제한된 Named Pipe IPC를 사용하는 구조가 적합하다.

## 7. 서명 상태

### LinkIO 프로그램

다음 파일은 IOSoft 코드 서명이 유효하다.

- `remote.exe`
- `IOC.dll`
- `IOCON.dll`
- `launcher_svc.exe`

### 커널 드라이버

다음 SYS와 CAT는 Microsoft Windows Hardware Compatibility Publisher 서명이 유효하다.

- `IOD.sys`, `IOD.cat`
- `IOHID.sys`, `iohid.cat`
- `HIDi.sys`, `hidi.cat`

INF의 PowerShell 개별 서명 표시는 일부 `UnknownError`가 있었지만, 대응 CAT/SYS 서명과 설치된 IOHID Driver Store 패키지의 서명은 유효했다.

## 8. 현재 PC에서 확인된 설치 상태

- Driver Store에는 `IOHID.inf`가 `oem29.inf`로 등록되어 있다.
- Provider: IOSoft
- Class: IOHID
- 해당 패키지의 Microsoft Hardware Compatibility Publisher 서명이 확인된다.
- 현재 조회 시 `HID\IOHID`, `HID\IOD`, `HID\HIDi` 활성 PnP 장치는 발견되지 않았다.
- Launcher 서비스는 설치되어 있으나 중지 상태였다.

즉 설치 프로그램이 IOHID 패키지는 Driver Store에 준비했지만, 현재 가상 장치를 활성화하지 않은 상태일 수 있다.

## 9. Comote 적용 설계

LinkIO 드라이버나 DLL을 복사해 사용하지 않고 동일한 기능 범주를 독립 구현하려면 다음 계층이 필요하다.

```text
Viewer 입력
    ↓
인증된 WebRTC DataChannel
    ↓
Host 사용자 세션 Helper
    ↓
IInputBackend
    ├─ SendInputBackend
    ├─ VirtualHidBackend2
    └─ VirtualHidBackend3
          ↓
서명된 자체 가상 HID 드라이버
```

필수 안전 조건:

- Viewer가 임의 INF 경로나 시스템 명령을 전달하지 못하게 한다.
- Host에 내장된 허용 패키지만 설치/삭제한다.
- 패키지 SHA-256과 서명을 모두 검증한다.
- 관리자 작업은 LocalSystem 서비스만 수행한다.
- Named Pipe는 사용자 SID와 ACL을 검증한다.
- 현재 모드, 설치 버전, 재부팅 필요 여부를 서버에 보고한다.
- 드라이버 backend 실패 시 `SendInput`으로 자동 fallback한다.
- 설치, 삭제, 전환 이력을 감사 로그로 남긴다.

## 10. 모드2·3 정확한 매핑을 확정하는 방법

중요한 운영 PC가 아닌 테스트 PC에서 다음을 비교하면 정확한 매핑을 확정할 수 있다.

1. 설치 전 Driver Store/PnP/서비스 목록 저장
2. LinkIO에서 `드라이버 설치 → 모드2`
3. 새 INF, Hardware ID, 서비스 확인
4. `입력제어 전환 → 모드2` 후 활성 장치 확인
5. 모드2 제거
6. 같은 절차로 모드3 확인

관찰 대상:

- `HID\IOD`
- `HID\IOHID`
- `HID\HIDi`
- `IOD_Service`
- `IOHID_Service`
- `HIDi_Service`
- 새로 등록되는 `oem*.inf`

이 검증을 하면 모드2와 모드3의 정확한 드라이버 조합, 동시 설치 가능 여부, 전환 시 재부팅 필요 여부까지 확정할 수 있다.
