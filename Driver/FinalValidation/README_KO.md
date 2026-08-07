# Comote Virtual HID 최종 Windows 10 VM E2E

이 검증기는 `Comote.InputBroker` → Phase 2 커널 드라이버 → VHF → Windows Raw Input 전체 경로와 `HostInputProtocol` → `VirtualHidInputBackend` 경로를 확인한다. 물리 PC에서는 빌드·실행·설치·입력을 절대 수행하지 않는다.

## 고정 안전 경계

- 일회용 VMware 게스트만 허용
- Windows 10 Home 22H2 x64, 커널 빌드 `19045`만 허용(UBR은 제한하지 않음)
- 설치 완료된 정확한 Phase 2 장치 1개와 VHF 자식 3개를 요구
- `ComoteInputBroker`가 LocalSystem/자동 시작/실행 중인지 검사
- 실행 중인 Broker 이미지 경로와 최종 릴리스 SHA-256을 검사
- 현재 로그인 사용자가 `Comote Input Controllers` 로컬 그룹 토큰을 보유한 경우에만 진행
- 입력 생성 확인 문구와 복구 스냅샷 이름을 모두 요구
- Raw Input은 `RIDEV_NOLEGACY`로 등록한 전경 관찰 창에서만 수집
- 관찰된 장치 인스턴스가 설치 영수증의 정확한 VHF 자식과 일치해야 통과

## 실제 입력 검증 12개

직접 Broker 경로:

1. F24 누름/뗌
2. F13~F18 6키 동시 입력(6KRO) 전체 누름/뗌
3. 상대 마우스 X/Y 이동
4. 절대 마우스 이동
5. 왼쪽·오른쪽·가운데·X1·X2 다섯 버튼 누름/뗌
6. 세로 +120 및 가로 -120 휠

Host 백엔드 경로:

7. F12 누름/뗌
8. 왼쪽 Ctrl 수식키 누름/뗌
9. 절대 마우스 이동
10. X2 버튼 누름/뗌
11. 세로 +240 휠(드라이버 허용 범위로 분할 후 총합 확인)
12. 가로 -240 휠(분할 후 총합 확인)

모든 테스트는 정확한 키 make/break, 마우스 버튼 플래그, 휠 부호·총합을 JSON 증거로 남긴다. 종료 경로에서는 `RELEASE_ALL`, 원래 커서 위치 복원, Raw Input 등록 해제를 모두 확인한다.

## 본체에서 허용되는 검사

아래 명령은 소스 경계와 증거 파서만 검사하며 프로젝트 실행·입력·시스템 변경을 하지 않는다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Driver\FinalValidation\Test-Phase2FinalE2EBoundary.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Driver\FinalValidation\Test-Phase2FinalEvidenceParser.ps1
```

## VM 실제 E2E

관리자 PowerShell에서 실행한다. `<BROKER_SHA256>`은 최종 패키지 매니페스트의 `Comote.InputBroker.exe` SHA-256이어야 한다.

```powershell
.\Driver\FinalValidation\Invoke-Phase2FinalE2E.ps1 `
  -AcknowledgeDisposableVm `
  -RecoverySnapshotName "comote-phase2-test-signed-ready-19045" `
  -AcknowledgeInputGeneration `
  -ConfirmationPhrase "RUN COMOTE VIRTUAL HID E2E IN VM" `
  -ExpectedBrokerSha256 "<BROKER_SHA256>" `
  -ValidateOnly
```

검증 전용 실행이 통과한 뒤 `-ValidateOnly`만 제거한다.

```powershell
.\Driver\FinalValidation\Invoke-Phase2FinalE2E.ps1 `
  -AcknowledgeDisposableVm `
  -RecoverySnapshotName "comote-phase2-test-signed-ready-19045" `
  -AcknowledgeInputGeneration `
  -ConfirmationPhrase "RUN COMOTE VIRTUAL HID E2E IN VM" `
  -ExpectedBrokerSha256 "<BROKER_SHA256>"
```

결과는 다음 위치에 저장된다.

```text
Driver\FinalValidation\artifacts\phase2-final-e2e\<run-id>\
  validation-summary.json
  raw-input-report.json
  input-recovery-state.json
```

블루스크린·멈춤·예상 밖 재부팅이 발생하면 VM을 강제로 계속 사용하지 않는다. 전원을 끄고 기록한 복구 스냅샷으로 되돌린 뒤, 덤프가 필요하면 스냅샷 복제본에서만 분석한다.