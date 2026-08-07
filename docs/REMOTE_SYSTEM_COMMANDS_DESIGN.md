# 원격 시스템 명령 설계 (재부팅·종료·Host 재실행·등록 해제)

작성 목적: 로드맵의 남은 앱 기능 4종을 기존 인증된 원격 작업 경로 위에 안전하게
얹기 위한 설계. **이 문서는 설계만 담고 구현 코드는 포함하지 않는다.** 물리 PC에서
개발·검증 가능한 범위이며 커널·드라이버와 무관하다.

관련 원칙(로드맵 3장): 3번(시스템 명령은 WebRTC 입력 메시지로 직접 실행하지 않는다),
4번(재부팅·종료·설치는 인증된 원격 작업 시스템 사용), 6번(위험 작업은 허용 목록·
만료 시간·중복 방지 키·감사 로그 사용).

---

## 1. 현재 fleet-command 경로

지금 `run`/`terminate` 두 액션이 5홉을 지난다. **액션 허용 목록이 네 지점에서
독립적으로 강제**된다 — 새 명령을 넣으려면 네 곳을 모두 일관되게 바꿔야 하고,
한 곳이라도 빠지면 조용히 어긋난다(경계 테스트 드리프트와 같은 교훈).

| # | 위치 | 역할 | 강제 항목 |
|---|---|---|---|
| 1 | [MainWindow.Operations.cs:194](../Viewer/MainWindow.Operations.cs) `SendFleetCommandAsync` | Viewer UI 발신 | 대상 온라인 + `AllowsRemoteTasks` 확인 |
| 2 | [ManagerHubServer.cs:247](../Viewer/ManagerHubServer.cs) `SendCommandAsync` | 허브 서버 중계 | `AllowsRemoteTasks` 재확인, action∈{run,terminate}, name≤128, value 1..1024, folder∈{"",desktop,custom} |
| 3 | [ManagerHubClient.cs:338](../Host/ManagerHubClient.cs) `IsValidCommand` | Host 수신 검증 | `HasOnlyProperties(type,action,name,folder,value)` + 위와 동일 규칙 + `_allowRemoteTasks` |
| 4 | [RemoteTaskExecutor.cs](../Host/RemoteTaskExecutor.cs) `ExecuteAsync` | Host 실행 | `action switch { run, terminate, _ }` |

메시지 스키마: `{ type:"command", action, name, folder, value }`, SecurePskChannel
(AES-GCM, 상호 인증)으로 전송. 옵트인은 Host의 `--allow-remote-tasks`
([Program.HubClient.cs:89](../Host/Program.HubClient.cs)).

**현재 갖춘 가드:** 옵트인 + 액션 허용 목록 + 필드 엄격 검증 + 암호 채널.
**아직 없는 가드(원칙 6):** 만료 시간, 중복 방지 키, 감사 로그. `run`/`terminate`는
비교적 저위험(알려진 파일 실행, 프로세스 종료)이라 지금까지 문제 없었다.

---

## 2. 새 명령의 두 부류

네 기능은 실행 주체가 다르다. 같은 메커니즘에 억지로 넣지 않는다.

### 부류 A — Host 실행형 시스템 명령
Host가 명령 채널로 받아 자기 OS에 수행. 기존 경로를 **확장**한다.
- `reboot` — Windows 재부팅
- `shutdown` — Windows 종료
- `restart-host` — Comote Host 프로그램만 재실행(OS 아님)

### 부류 B — Manager/서버 registry 작업
Host가 실행하는 게 아니다. **Manager가 등록 정보를 제거**하면 대상이 인증에
실패해 자연히 끊긴다. 명령 채널이 아니라 등록 계층에서 처리한다.
- `deregister` — 대상 Host의 등록 해제 + 재등록 차단

허브 모드에서 등록은 Manager가 보유한 `_deviceSecrets`(device→PSK)로 성립한다
([ManagerHubServer.cs:133](../Viewer/ManagerHubServer.cs), 재등록은
[:336](../Viewer/ManagerHubServer.cs)에서 알려진 secret일 때만 수락). 따라서
**해당 secret을 제거하면 그 Host는 재등록도 불가**하다. Host에 아무 명령도
보낼 필요가 없다.

중앙/Supabase 모드(`feat: add central account mode and web fleet portal`)에서는
등록이 웹·Supabase 계층에 있으므로, 그 경우의 deregister는 서버 레코드 삭제 +
재등록 차단(예: 취소 목록)으로 별도 설계가 필요하다. **부류 B의 중앙 모드
구현은 web/supabase 코드를 따로 읽고 설계해야 한다** — 이 문서 범위는 허브 모드
까지.

---

## 3. 위험 등급과 별도 옵트인

`run`/`terminate`(앱 실행)와 `reboot`/`shutdown`(OS 중단)·`deregister`(되돌리기
어려움)는 위험 등급이 다르다. **원격 앱 실행을 허용한 사용자가 원격 재부팅까지
암묵적으로 허용한 것은 아니다.**

제안: 옵트인을 이원화한다.
- `--allow-remote-tasks` — 기존. `run`/`terminate`만.
- `--allow-system-commands`(신규) — `reboot`/`shutdown`/`restart-host`를 별도 허용.
  이 플래그 없이는 부류 A 시스템 명령을 4번 실행 지점에서 거부.
- `deregister`(부류 B)는 Host 옵트인과 무관 — Manager 소유 작업이므로 Manager UI
  측 확인(대상 이름 재입력 등)으로 가드.

Host의 옵트인 상태는 등록 시 Manager에 전달되어야 하므로(`AllowsRemoteTasks`가
이미 그렇듯) `AllowsSystemCommands` 필드를 등록 페이로드와 `HubConnectionInfo`,
Viewer의 대상 표시에 추가한다.

---

## 4. 스키마 변경

부류 A 시스템 명령은 원칙 6의 세 가드를 반드시 갖춘다. 기존 `value`(run=경로,
terminate=프로세스명)의 느슨한 의미에 얹지 않고, 시스템 명령 전용 필드를 둔다.

신규 공통 필드(부류 A):
- `commandId` — GUID 문자열. **중복 방지 키.** Host는 최근 처리한 commandId
  집합(예: 최근 200개 또는 5분)을 유지해 재생/중복 수신 시 재실행하지 않는다.
  재부팅 명령이 두 번 도착해도 한 번만 수행.
- `issuedAtUnix` — 발신 시각(Unix ms). **만료.** Host는 `now - issuedAt`이 창
  (예: 60초)을 넘으면 거부. 지연·재생된 오래된 재부팅 명령을 막는다.
- `delaySeconds` — reboot/shutdown 유예(0~600). `shutdown.exe`에 전달, 취소 가능.

검증은 **액션별로 분기**한다(현재의 일괄 규칙 대신):
- `run`/`terminate` — 기존 규칙 유지, 신규 필드 없음(하위 호환).
- `reboot`/`shutdown` — `commandId`(GUID 형식), `issuedAtUnix`(정수, 만료 내),
  `delaySeconds`(0..600) 필수. `value`/`folder`는 비어 있어야 함.
- `restart-host` — 위와 같되 `delaySeconds` 무시.
- `deregister` — 부류 B, 명령 채널을 타지 않음.

`HasOnlyProperties`가 엄격하므로 필드 추가는 양쪽이 동시에 합의해야 한다. 액션별
허용 프로퍼티 집합을 별도로 정의한다.

---

## 5. 명령별 구현 방식(부류 A)

- **reboot / shutdown** — `shutdown.exe /r /t <delay>` 또는 `/s /t <delay>`.
  `shutdown /a`로 유예 중 취소 가능. 대화형 사용자 세션에는 보통
  SeShutdownPrivilege가 있으므로 asInvoker로 동작. 실패 시 결과 JObject로 사유
  반환. Win32 `InitiateSystemShutdownEx`는 대안이나 `shutdown.exe`가 단순·취소 용이.
- **restart-host** — 기존 [RestartForSetup](../Host/Program.HubClient.cs) 패턴 재사용.
  현재 인자에서 `--setup` 없이 Host를 재기동 후 자신은 종료. 입력 백엔드 모드
  (`--virtual-hid`/`--send-input`)를 보존.

세 명령 모두 실행 전 감사 로그 1건, 실행 결과 1건을 남긴다(원칙 6, 아래 6장).

---

## 6. 감사 로그

부류 A·B 모두 대상 측/Manager 측에 감사 기록을 남긴다. 최소 항목:
`시각, commandId, action, 발신 Manager 식별(routingId), 결과, 사유`.
키·PSK·access key는 절대 기록하지 않는다(기존 promotion 로그 정책과 동일).
Host는 보호된 로그 경로(기존 Broker 로그 정책 참고)에, Manager는 자체 작업 로그에.

---

## 7. 변경 지점 체크리스트

새 액션 하나를 넣을 때 반드시 함께 바뀌어야 하는 지점(누락 시 조용히 어긋남):

부류 A:
1. Viewer UI 발신 — 시스템 명령 탭/버튼, 대상의 `AllowsSystemCommands` 확인,
   commandId/issuedAt 생성 ([MainWindow.Operations.cs](../Viewer/MainWindow.Operations.cs))
2. 허브 서버 중계 검증 — 액션 목록·필드 규칙·옵트인 재확인
   ([ManagerHubServer.cs:247](../Viewer/ManagerHubServer.cs))
3. Host 수신 검증 — `IsValidCommand` 액션별 프로퍼티/규칙, `_allowSystemCommands`
   ([ManagerHubClient.cs:338](../Host/ManagerHubClient.cs))
4. Host 실행 — `ExecuteAsync` switch + 중복 방지/만료 검사
   ([RemoteTaskExecutor.cs](../Host/RemoteTaskExecutor.cs))
5. 등록 페이로드·`HubConnectionInfo`·대상 표시에 `AllowsSystemCommands` 추가
6. Host 옵트인 파싱 `--allow-system-commands` ([Program.HubClient.cs](../Host/Program.HubClient.cs))
7. 감사 로그 지점(발신·수신·결과)

부류 B(deregister, 허브 모드):
1. Manager가 `_deviceSecrets`에서 대상 제거 + 영속 저장 갱신
   ([ManagerHubServer.cs](../Viewer/ManagerHubServer.cs), 설정 저장소)
2. 현재 연결돼 있으면 해당 연결 종료
3. Viewer UI 확인 절차(대상 이름 재입력 등) + 감사 로그
4. (중앙 모드 별도) web/supabase 레코드 삭제 + 재등록 차단 — 이 문서 범위 밖

---

## 8. 셀프테스트

기존 `HubTransportSelfTest`(12/12)가 이 경로의 순수 검증 자리다. 추가할 케이스:
- 시스템 명령은 `--allow-system-commands` 없으면 거부(옵트인 분리)
- 만료된 `issuedAtUnix` 거부
- 동일 `commandId` 재수신 시 재실행 안 함(중복 방지)
- 액션별 프로퍼티 집합 위반 거부(reboot에 value 포함 등)
- deregister 후 동일 device 재등록 거부

모두 파이프·프로세스·OS를 건드리지 않는 순수 프로토콜 테스트로 작성 가능
(실제 `shutdown.exe` 호출은 주입 가능한 실행자 인터페이스 뒤로 분리해 목으로 검증).

---

## 9. 테스트 경계

- **물리 PC 가능** — 전 구간 프로토콜/검증/UI 로직, 셀프테스트, `restart-host`
  실동작(Host 프로세스만 재기동). 커널 무관.
- **실기에서만 최종 확인** — 실제 `reboot`/`shutdown`은 기계를 실제로 끄므로
  일회용 VM 또는 별도 실기에서. 물리 개발 PC에서 실행 금지(부팅 중단은
  드라이버 BSOD와 같은 취급).
- 가상 HID(모드 2)와 무관 — 이 기능은 SendInput 여부와 독립적으로 동작.

---

## 10. 구현된 결정 (허브 모드)

권장 묶음대로 허브 모드를 구현했다.

1. **옵트인 분리** — `--allow-system-commands`를 `--allow-remote-tasks`와 별개로.
   등록 페이로드·`HubClientInfo`·`HostInfo`에 `AllowsSystemCommands`가 흐르고,
   서버 `SendSystemCommandAsync`와 Host 수신 게이트가 각각 강제한다.
2. **유예 + 취소** — Viewer 시스템 탭에서 유예 0~600초(기본 30) 입력, 재부팅·종료·
   `cancel-shutdown`(`shutdown /a`) 버튼 제공. 전송 전 OK/Cancel 확인.
3. **허브 모드 deregister** — `ManagerHubServer.RemoveDevice`가 `_deviceSecrets`
   에서 제거(락 보호)하고 연결을 끊는다. 자격증명 설정을 갱신 저장해 재등록을
   차단. 중앙/Supabase 모드는 별도 항목으로 남김.
4. **감사 로그** — `RemoteCommandAuditLog`가 Manager(발신)와 Host(수신·실행) 양쪽
   에 기록. `%LOCALAPPDATA%\Comote\audit`에 5MB×5 회전, 키/PSK 미기록.

원칙 6 가드는 `RemoteCommandProtocol`에 중앙화했다: `commandId`(GUID, 중복 방지),
`issuedAtUnixMs`(60초 만료), 액션별 허용 프로퍼티 집합, `delaySeconds` 범위.
`RemoteCommandDeduplicator`가 재생·중복 명령을 한 번만 통과시킨다.

검증: `HubTransportSelfTest` 15/15(시스템명령 기본 거부·옵트인·재등록 차단 포함),
`InputCore.SelfTest` 15/15(카테고리·중복방지·만료 규칙 포함), Host·Viewer 풀빌드
클린, 경계 8/8. 실제 `reboot`/`shutdown` 실동작은 실기/VM에서만 확인한다.

## 11. 리뷰 후 하드닝

적대적 리뷰에서 나온 결함을 수정했다.

- **등록해제 경합** — 핸드셰이크 도중 해제되면 새 연결이 통과하던 창을,
  `_deviceSecretsGate` 락 안에서 secret 재확인 후 `_clients` 등록하도록 닫음.
- **시계 오차** — `IsWithinLifetime`를 대칭 창(±수명)으로 바꿔 Manager/Host 시계
  차이로 정상 명령이 만료 처리되던 문제 해결. 재생은 중복 방지가 계속 담당.
- **거짓 성공** — `shutdown.exe` 종료 코드를 대기·검사하고, `restart-host` 실패를
  전파하며, 감사에 executed/failed를 기록(취소는 관대).
- **강건성** — 잘못된 명령 봉투는 연결을 끊지 않고 무시. Manager 감사 로그는
  `Lazy`로 경합 없는 단일 인스턴스. 마지막 기기 해제 시 설정을 비워 재시작 재로드
  방지.

실행자 레벨 테스트(HostInputSelfTest)로 유효 재부팅 1회 실행·중복 무시·만료 무효
과를 mock 실행자로 검증한다.

## 12. 남은 항목

- 중앙/Supabase 모드 deregister(레코드 삭제 + 서버측 재등록 차단) — web/supabase
  계층 작업, Supabase 백엔드 안정화 후.
- 재시작을 넘어서는 중복 방지(현재 인메모리) — 만료 창이 보완하므로 낮은 순위.
- `restart-host` 외 시스템 명령의 실기/VM E2E.
