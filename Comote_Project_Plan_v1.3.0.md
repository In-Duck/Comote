# v1.3.0 Feature Update & Fixes Plan

## 1. 개요 (Overview)
*   **목표:** 사용자 피드백을 반영하여 호스트 상태 동기화 문제 해결, 정보 표시 정확도 개선, 그리고 썸네일 미리보기 기능을 추가합니다.
*   **버전:** v1.3.0

## 2. 사용자 요청 사항 및 분석 (Issues)
1.  **Offline Status Check:** 호스트 종료 시 뷰어에서 'OFF'로 즉시 반영되지 않음.
    - *Cause:* Viewer가 `LastSeen`을 주기적으로 확인하지 않거나 Threshold가 너무 김.
2.  **Re-registration:** 호스트 목록 삭제 후 호스트 재실행 시 목록에 추가되지 않음.
    - *Cause:* Viewer가 목록이 비어 있으면 Refresh를 중단하거나, Host가 동일 ID로 재등록 시도 시 DB 정책(RLS) 문제 발생 가능성.
3.  **Host Info Display:** IP, CPU, Memory, Disk, Resolution, Uptime 정보 미표시.
    - *Cause:* v1.2.2 Hotfix에서 해당 컬럼이 DB 스키마에 없어 Host 전송 로직을 주석 처리했음.
4.  **Thumbnail Preview:** 썸네일 뷰에 실제 호스트 화면 미리보기 추가.
    - *New Feature:* `thumbnail_url` 컬럼 및 호스트 캡처/업로드 로직 필요.

## 3. 상세 구현 계획 (Implementation Details)

### 3.1 [Database] Supabase Schema Update
> [!IMPORTANT]
> **사용자 조치 필요:** 아래 SQL 스크립트를 Supabase SQL Editor에서 실행하여 스키마를 업데이트해야 합니다.

```sql
-- 1. 호스트 정보 컬럼 추가 (if not exists)
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS resolution TEXT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS ip TEXT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS cpu INT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS ram TEXT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS hdd TEXT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS uptime TEXT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS mac_address TEXT;
ALTER TABLE hosts ADD COLUMN IF NOT EXISTS thumbnail_url TEXT;

-- 2. Storage Bucket 생성 (썸네일용)
-- (이미 'thumbnails' 버킷이 없다면 생성 필요 - 대시보드에서 수행 권장)
-- INSERT INTO storage.buckets (id, name, public) VALUES ('thumbnails', 'thumbnails', true);
```

### 3.2 [Host] 기능 개선
- **Heartbeat & Info 복구:** `SignalingClient.cs`에서 `resolution`, `cpu`, `ram`, `hdd`, `uptime`, `ip`, `mac_address` 필드 전송을 재활성화합니다.
- **Thumbnail Capture & Upload:**
    - **도구 선택:** 별도의 SDK 대신 `HttpClient`를 사용하여 **Multipart/form-data** 방식으로 직접 Supabase Storage에 업로드합니다.
    - **캡처 로직:** `ScreenCapture.cs`에 `CaptureThumbnail(width, height, quality)` 추가. `System.Drawing.Common` 의존성을 체크합니다.
    - **주기 관리:** Heartbeat(30초)와 별개로 **60초 주기**로 캡처 및 업로드를 수행합니다. (네트워크 부하 최소화)
    - **업로드 경로:** `thumbnails/{user_id}/{host_id}.jpg` (Public Bucket 설정 필수)

### 3.3 [Viewer] 로직 및 UI 개선
- **상태 동기화 (OFF) 강화:**
    - `MainWindow.xaml.cs`의 `pollTimer`(`System.Threading.Timer`)를 `DispatcherTimer`로 교체하거나, `async void` 발생 예외 처리를 강화하여 폴링 안정성을 확보합니다.
    - `TotalSeconds < 60` 기준은 유지하되, UI 반영 시 썸네일 캐시 방지 처리(`?t=timestamp`)를 추가합니다.
- **Re-registration (재등록) 문제 해결:**
    - `HostRepository.cs`의 `UpsertHostAsync` 시 RLS(Row Level Security) 정책이 `INSERT`와 `UPDATE` 권한을 모두 포함하고 있는지 확인 가이드를 추가합니다.
    - Viewer의 `_persistentHosts` 리스트가 비어있을 때도 `PollHostsFromSupabaseAsync`가 정상적으로 동작하는지 코드를 재검토합니다. (현재 분석 결과 로직상 문제는 없으나, 타이머 중단 여부 확인)
- **UI 바인딩:**
    - 리스트 뷰 및 썸네일 카드에 `Resolution`, `Uptime` 등 누락된 필드 데이터 바인딩.
    - 썸네일 이미지 보기에 호스트 화면 미리보기 적용.

## 4. 작업 단계 (Execution Steps)

### Step 1: Database Setup
- [ ] User executes SQL scripts on Supabase.
- [ ] User confirms `thumbnails` storage bucket exists.

### Step 2: Host Update (Dependencies & Capture)
- [ ] **[Dependency]** Host 프로젝트에 `System.Drawing.Common` 패키지 설치 확인.
- [ ] **[Host]** `ScreenCapture.cs`: 썸네일 캡처 및 이미지 리사이징/압축 로직 구현.
- [ ] **[Host]** `SignalingClient.cs`: 
    - `resolution`, `cpu` 등 전체 필드 복구.
    - `HttpClient` 기반 Multipart 파일 업로드 메서드 구현.
    - 60초 주기 타이머 추가.

### Step 3: Viewer Update (Logic & UI)
- [ ] **[Viewer]** `HostDto`: `ThumbnailUrl` 필드 추가.
- [ ] **[Viewer]** `MainWindow.xaml.cs`:
    - `DispatcherTimer` 추가 (10초 간격 Refresh).
    - `UpdateLobbyUI`: Offline 판단 로직(60초 타임아웃) 및 중복 제거 로직 재검토.
    - Grid View 썸네일 바인딩 수정.

### Step 4: Verification & Release
- [ ] Local Test: Host 실행 -> Viewer 목록 갱신 및 썸네일 확인 -> Host 종료 -> Viewer OFFLINE 전환 확인.
- [ ] Build Release v1.3.0.

## 5. 승인 요청 (Approval Request)
위 계획대로 진행하시겠습니까? 특히 **Supabase 대시보드에서 SQL 실행**이 선행되어야 함을 인지해 주시기 바랍니다.
