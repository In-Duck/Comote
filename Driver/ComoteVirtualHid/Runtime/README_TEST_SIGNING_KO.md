# Phase 1 VM 테스트 서명 게이트

이 단계는 Windows 10 x64 빌드 19045 VMware 시험 VM 전용입니다.
물리 PC에서는 실행하지 마세요.

현재 단계가 수행하는 작업:

1. 읽기 전용 runtime preflight 재실행
2. 14일 유효한 Comote 전용 비내보내기 테스트 인증서 생성
3. 인증서를 VM의 LocalMachine Root와 TrustedPublisher에 등록
4. 복사한 SYS에 테스트 서명
5. 서명된 SYS 기준으로 CAT 재생성 후 CAT 서명
6. 커널 정책, Authenticode, CAT 포함 관계 검증
7. 별도 명령으로 `TESTSIGNING`을 다음 부팅에 사용하도록 기록

현재 단계가 하지 않는 작업:

- 장치 생성
- INF 설치
- 드라이버 로드
- Driver Verifier 실행
- 자동 재부팅
- 본체 설정 변경

## 1. 정적 경계 검사

```powershell
.\Runtime\Test-Phase1RuntimePreflightBoundary.ps1
.\Runtime\Test-Phase1TestSigningBoundary.ps1
```

## 2. 테스트 서명 패키지 준비

관리자 PowerShell에서 실행합니다.

```powershell
.\Runtime\Prepare-Phase1TestSigning.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-unsigned-build-pass-19045.3803"
```

성공해도 `TESTSIGNING`은 아직 꺼져 있고 드라이버는 설치되지 않습니다.

## 3. TESTSIGNING 요청

```powershell
.\Runtime\Enable-Phase1TestMode.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-unsigned-build-pass-19045.3803"
```

성공 후 VM을 수동으로 재부팅합니다. 자동 재부팅은 하지 않습니다.

## 4. 재부팅 후 스냅샷과 준비 확인

VMware에서 다음 이름으로 새 스냅샷을 만든 뒤 관리자 PowerShell에서
실행합니다.

```text
comote-phase1-testmode-ready-19045.3803
```

```powershell
.\Runtime\Test-Phase1TestModeReady.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName "comote-phase1-testmode-ready-19045.3803"
```

이 확인까지도 장치 생성과 드라이버 설치는 수행하지 않습니다.

## 설치 전에 서명 상태만 원복

장치가 설치되지 않은 상태에서만 실행할 수 있습니다.

```powershell
.\Runtime\Restore-Phase1TestSigningState.ps1 `
  -AcknowledgeDisposableVm
```

`TESTSIGNING`을 켰던 경우 VM을 수동으로 재부팅해야 원복이 완료됩니다.
스냅샷 복구가 가능한 경우에는 스냅샷 복구가 가장 확실합니다.
