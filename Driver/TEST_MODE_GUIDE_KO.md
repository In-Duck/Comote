# Comote Virtual HID 팀 테스트 설치 안내

이 패키지는 Comote 팀 내부 시험용입니다. Windows 테스트 모드를 사용하며, 공개 배포나 일반 사용자 설치용이 아닙니다.

> 현재 저장소의 `ComoteVirtualHid`는 입력 제출과 Client 연결이 비활성화된
> Phase 1 안전 골격입니다. Windows WDK 빌드·INF 검증·Driver Verifier 및
> 반복 설치/제거 시험이 끝나기 전에는 이 설치 절차를 사용하지 마세요.

## 설치

1. 압축을 완전히 풉니다.
2. `Install Team Test Driver as Administrator.cmd`를 실행합니다.
3. Secure Boot가 켜져 있다는 안내가 나오면 UEFI/BIOS에서 Secure Boot를 직접 끈 뒤 다시 실행합니다. 스크립트가 Secure Boot를 자동으로 끄지는 않습니다.
4. 첫 실행에서 테스트 모드가 켜지면 Windows를 재부팅합니다.
5. 재부팅 후 같은 CMD 파일을 다시 실행해 드라이버를 설치합니다.
6. Comote Client를 실행하고 입력 모드 2(Comote Virtual HID)를 선택합니다.

설치 후 Windows 바탕 화면에 테스트 모드 워터마크가 표시되는 것은 정상입니다. 설치기가 재부팅을 요구하면 한 번 더 재부팅하세요.

## 제거 및 원복

1. `Remove Team Test Driver and Disable Test Mode.cmd`를 실행합니다.
2. Windows를 재부팅합니다.
3. 필요하다면 UEFI/BIOS에서 Secure Boot를 다시 켭니다.

원복 도구는 Comote 가상 장치를 제거하고, 팀 인증서를 신뢰 저장소에서 삭제하며, Windows 테스트 모드를 끕니다.

## 주의

- 테스트 모드는 Windows의 드라이버 서명 보안 수준을 낮춥니다. 중요한 업무용 PC나 평소 게임을 하는 주 PC보다는 별도 시험 PC를 권장합니다.
- 일부 안티치트 또는 보안 프로그램은 테스트 모드 자체를 감지해 실행을 거부할 수 있습니다. 이를 우회하는 기능은 포함하지 않습니다.
- 인증서는 각 GitHub Actions 빌드마다 새로 만들어지며 90일 동안 유효합니다. 새 빌드를 설치할 때는 해당 빌드에 포함된 인증서와 설치 파일을 함께 사용하세요.
- Secure Boot, 조직 정책, 메모리 무결성 설정에 따라 테스트 드라이버 설치가 차단될 수 있습니다.
