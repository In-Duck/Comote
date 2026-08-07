# Phase 1 VM 설치 및 열거 게이트

이 단계는 다음 VMware 스냅샷에서만 실행합니다.

```text
comote-phase1-testmode-ready-19045.3803
```

드라이버는 입력을 제출할 수 없는 Phase 1 빌드입니다. 설치 시험은
Driver Store 등록, 루트 장치 생성, VHF 키보드와 마우스 자식 열거,
즉시 제거 가능성만 확인합니다.

## 설치 전 읽기 전용 확인

```powershell
.\Runtime\Test-Phase1EnumerationBoundary.ps1

.\Runtime\Install-Phase1Enumeration.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-testmode-ready-19045.3803" `
  -ValidateOnly
```

## 최초 설치 및 열거

```powershell
.\Runtime\Install-Phase1Enumeration.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-testmode-ready-19045.3803"
```

설치 스크립트는 자동 재부팅, Driver Verifier, 입력 제출을 수행하지
않습니다. 실패하면 DevGen으로 생성된 장치를 제거한 뒤 기록된
`oem#.inf`만 Driver Store에서 제거합니다.

## 제거 전 읽기 전용 확인

```powershell
.\Runtime\Remove-Phase1Enumeration.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-testmode-ready-19045.3803" `
  -ValidateOnly
```

## 장치와 Driver Store 패키지 제거

```powershell
.\Runtime\Remove-Phase1Enumeration.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-testmode-ready-19045.3803"
```

인증서와 `TESTSIGNING` 원복은 장치와 Driver Store 패키지가 제거된
뒤 별도 게이트에서 수행합니다.
