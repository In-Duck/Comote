# Comote Virtual HID Phase 1 시험 계획

## 목적

Phase 1은 입력을 보내는 단계가 아닙니다. KMDF 부모 장치가 VHF 키보드와
마우스를 안전하게 열거하고, 실패·제거·재부팅 과정에서 커널 오류 없이
정리되는지만 검증합니다.

## 시험 환경

- 일상용 PC가 아닌 Windows 10 Home 22H2 x64 가상 머신
- Windows 10 22H2 빌드 `19045` (`19045.x` 누적 업데이트 허용)
- 시험 전 VM 스냅샷
- Secure Boot 비활성화
- BitLocker가 사용 중이면 복구 키 확보 후 보호 일시 중지
- 테스트 서명된 SYS와 CAT
- 커널 덤프가 저장되도록 구성
- WinDbg를 사용할 수 있는 별도 분석 환경

## 설치 전 게이트

- [ ] 깨끗한 VM 스냅샷 이름 기록
- [ ] 비관리자 Developer PowerShell에서 아래 VM 전용 빌드 성공

```powershell
.\Invoke-Phase1VmBuild.ps1 `
    -AcknowledgeDisposableVm `
    -SnapshotName "comote-phase1-clean"
```

- [ ] 컴파일러 경고 0개
- [ ] 정적 코드 분석 성공
- [ ] `Test-Phase1Boundary.ps1` 성공
- [ ] `InfVerif /k` 성공
- [ ] `Inf2Cat` 성공
- [ ] SYS와 CAT 테스트 서명 검증 성공
- [ ] 패키지 SHA-256 기록
- [ ] `artifacts\phase1-reports`의 결과가 `passed`

위 항목 중 하나라도 실패하면 설치하지 않습니다.

## 기능 시험

- [ ] Comote VHF 부모 장치에 경고 아이콘 없음
- [ ] VHF 키보드와 마우스 자식 장치에 경고 아이콘 없음
- [ ] 사용자 모드에서 Comote 부모 장치를 열 수 없음
- [ ] 키보드와 마우스 입력이 발생하지 않음
- [ ] 장치 비활성화 후 재활성화 성공
- [ ] 절전 후 복귀 성공
- [ ] Windows 재부팅 성공
- [ ] 장치 제거 성공
- [ ] 드라이버 패키지 제거 성공

## 반복 안정성 시험

각 사이클 전 VM 스냅샷을 준비하고 아래 과정을 10회 반복합니다.

1. 드라이버 설치
2. 재부팅
3. 장치 상태 확인
4. 장치 비활성화·활성화
5. 장치 제거
6. Driver Store 패키지 제거
7. 재부팅
8. 장치·서비스·드라이버 패키지 잔여 확인

한 번이라도 bug check, 장치 관리자 오류, 제거 실패 또는 잔여 장치가
발생하면 입력 기능 개발을 중단하고 원인을 먼저 수정합니다.

## Driver Verifier

Driver Verifier는 VM 스냅샷에서 Comote 드라이버만 대상으로 실행합니다.
모든 드라이버를 선택하지 않습니다.

```powershell
verifier /standard /driver ComoteVirtualHid.sys
```

재부팅 후 설치·비활성화·활성화·절전·제거 시나리오를 반복합니다.
시험이 끝나거나 부팅 문제가 발생하면 복구 환경에서 다음 명령으로
Verifier 설정을 해제합니다.

```powershell
verifier /reset
```

## Phase 2 진입 조건

- [ ] 반복 설치/제거 10회 성공
- [ ] Driver Verifier에서 위반 없음
- [ ] 수집된 커널 덤프 없음
- [ ] 장치 정리 후 Comote 서비스와 Driver Store 항목 없음
- [ ] 보고서 설명자 검토 완료

모든 조건을 만족한 뒤에만 SYSTEM 전용 제어 인터페이스와 고정 크기
IOCTL 프로토콜을 설계합니다.
