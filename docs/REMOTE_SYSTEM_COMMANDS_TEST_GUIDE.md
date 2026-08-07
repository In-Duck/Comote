# 원격 시스템 명령·등록 해제 수동 테스트 가이드

이 문서는 새로 추가된 **재부팅·종료·Host 재실행·취소·등록 해제**(허브 모드)를
사람이 직접 확인하는 절차다. 기본 연결·썸네일·원격창은
[HUB_CONNECTION_TEST_GUIDE.md](HUB_CONNECTION_TEST_GUIDE.md)를 먼저 따른다.

## 왜 VM인가

이 테스트는 **실제로 입력을 주입하고 실제로 기계를 재부팅·종료한다.** 따라서
제어당하는 쪽(Client)은 **되돌릴 수 있는 일회용 VM**으로 한다. 재부팅·종료
단계 직전에 반드시 **VM 스냅샷**을 찍는다. Manager는 개발 PC에서 실행해도 된다.

- **Manager(제어)**: 개발 PC. 프레임워크 종속 빌드로 바로 실행 가능.
- **Client(피제어)**: 일회용 Windows 10 x64 VM.

Client VM에는 .NET 10 데스크톱 런타임이 없을 수 있으므로 둘 중 하나가 필요하다.
1. VM에 .NET Desktop Runtime 10.0.10 설치 후 프레임워크 종속 `Host.exe` 복사, 또는
2. self-contained 단일 파일로 퍼블리시해 복사(런타임 불필요).

## 실행 커맨드

**Manager (개발 PC):**
```
Viewer\bin\Release\net10.0-windows\Viewer.exe --manager-hub
```
로그인/허브 시작 화면에서 수신 포트와 CMT1 접속 키를 발급한다.

**Client (VM), 시스템 명령까지 허용:**
```
Host.exe --manager-hub --send-input --allow-remote-tasks --allow-system-commands
```
- `--allow-system-commands` 없이 실행하면 재부팅·종료·재실행·취소는 **거부**된다(설계).
- `--allow-remote-tasks`만 있으면 run/terminate만 되고 시스템 명령은 거부된다.

같은 CMT1 키를 Client 설정에 입력해 연결한다.

---

## 단계별 절차 (안전 → 위험 순)

### 0. 준비
- [ ] Manager 실행, 허브 시작, CMT1 키 발급
- [ ] Client(VM) 실행 후 같은 키로 연결
- [ ] Manager 로비에 Client가 **online**으로 뜨고 썸네일이 보이는지
- [ ] **VM 스냅샷 촬영** (재부팅/종료 테스트 대비)

### 1. 옵트인 경계 — 거부부터 확인 (부작용 없음)
- [ ] Client를 `--allow-system-commands` **빼고** 실행
- [ ] Manager "작업 패널 → 시스템" 탭에서 재부팅 시도
- [ ] → "이 Client는 시스템 명령을 허용하지 않았습니다" 차단 메시지가 뜨는지
- [ ] Client를 `--allow-system-commands` 포함해 다시 실행하고 재연결

### 2. 취소(cancel-shutdown) — 안전
- [ ] 아무 예약이 없는 상태에서 "취소" 버튼
- [ ] → 오류 없이 "예약된 재부팅·종료 취소됨" 처리되는지(대기 없어도 정상)

### 3. Host 재실행(restart-host) — 회복 가능
- [ ] "Host 프로그램 재실행" 버튼
- [ ] → Client가 잠깐 offline 됐다가 자동으로 다시 online 되는지
- [ ] 재실행 후에도 시스템 명령이 여전히 동작하는지(옵트인 플래그 보존 확인)

### 4. 재부팅 유예 + 취소 — 실제 재부팅 없이 abort
- [ ] 유예를 60초로 입력하고 "재부팅"
- [ ] Client(VM)에 Windows 종료 경고가 뜨는지
- [ ] 유예 안에 Manager "취소" 버튼 → VM이 **재부팅되지 않고** 경고가 사라지는지

### 5. 실제 재부팅 — VM이 실제로 재부팅됨
- [ ] (스냅샷 확인) 유예 5~10초로 "재부팅"
- [ ] VM이 실제로 재부팅되는지
- [ ] 부팅 후 Client가 자동 재연결되어 Manager 로비에 다시 online 되는지

### 6. 실제 종료 — VM이 꺼짐
- [ ] 유예 짧게 "종료"
- [ ] VM이 실제로 종료되는지
- [ ] (VM 다시 켜서 이후 단계 진행)

### 7. 다중 선택 안전성
- [ ] 여러 Client를 띄운 경우, 여러 대 선택 후 재부팅 유예 → 전체에 걸리는지
- [ ] 유예 중 취소가 선택 대상 모두에 적용되는지

### 8. 등록 해제(deregister)
- [ ] 한 대 선택 후 "선택 PC 등록 해제"
- [ ] 확인창에 **PC 이름을 정확히 재입력**해야만 진행되는지(오타 시 취소)
- [ ] 대상 Client의 연결이 끊기는지
- [ ] 해당 Client를 **다시 실행해도** Manager에 재등록되지 않는지(재등록 차단)
- [ ] Manager를 껐다 켜도 그 기기가 다시 나타나지 않는지(영속 차단)

### 9. 감사 로그 확인
두 로그에 시각·commandId·action·결과가 남고 **키는 없어야** 한다.
- Manager: `%LOCALAPPDATA%\Comote\audit\remote-commands-manager.log`
- Client(VM): `%LOCALAPPDATA%\Comote\audit\remote-commands-host.log`
- [ ] 발신(Manager)과 실행(Client) 기록이 각각 남는지
- [ ] 실패한 명령은 `failed`, 만료는 `rejected/expired`, 중복은 `ignored/duplicate`로 남는지
- [ ] access key·CMT1 키·비밀번호가 **절대** 기록되지 않는지

---

## 실패 시 증상별 진단

| 증상 | 가능한 원인 |
|---|---|
| 재부팅 눌러도 "허용하지 않았습니다" | Client에 `--allow-system-commands` 없음 |
| 명령이 "만료되었습니다"로 실패 | Manager와 Client 시계 차이가 60초 초과 (NTP 동기화 필요) |
| "shutdown.exe 실패: exit …" | Client 세션에 종료 권한 없음(비대화형/권한 부족) |
| 재부팅이 성공으로 뜨는데 안 꺼짐 | (수정됨) 종료코드 검사 후 실패면 `ok=false`로 보고되어야 함 — 재현 시 로그 첨부 |
| 등록 해제 후에도 재등록됨 | 마지막 1대 해제 시 설정이 비워짐 — 정상. 그 외면 버그 |

## 안전 메모
- 재부팅·종료는 **실제로** 그 기계를 끈다. 반드시 VM에서, 스냅샷을 찍고.
- 개발 PC를 Client로 삼아 자기 자신을 재부팅·종료하지 말 것.
- 이 경로는 SendInput(모드 1) 기준이며 커널 드라이버(모드 2)와 무관하다.
