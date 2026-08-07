# Comote Virtual HID Phase 1 런타임 사전검사

이 폴더의 현재 단계는 읽기 전용 사전검사만 수행합니다.

검사 항목:

- 알려진 가상 머신인지 확인
- Windows 10 Home 22H2 x64, 빌드 19045 확인
- 관리자 PowerShell 확인
- 사용자가 기록한 VMware 스냅샷 이름 기록
- Phase 1 무서명 패키지 SHA-256 확인
- SYS와 CAT가 실제로 무서명인지 확인
- v5가 만든 임시 테스트 인증서가 제거됐는지 확인
- Secure Boot가 VM에서 비활성화됐는지 확인
- BitLocker 보호 상태 확인
- TESTSIGNING이 아직 꺼져 있는지 확인
- SignTool과 DevGen 위치 확인

이 스크립트는 다음 작업을 하지 않습니다.

- 인증서 생성 또는 가져오기
- SYS 또는 CAT 서명
- 테스트 모드 변경
- 장치 생성
- 드라이버 설치 또는 로드
- Driver Verifier 실행

VM 안의 관리자 PowerShell에서 실행:

```powershell
.\Runtime\Test-Phase1RuntimePreflightBoundary.ps1

.\Runtime\Invoke-Phase1RuntimePreflight.ps1 `
    -AcknowledgeDisposableVm `
    -SnapshotName "comote-phase1-unsigned-build-pass-19045.3803"
```
