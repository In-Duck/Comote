# Comote 백엔드 재구축 가이드

기존 Supabase 프로젝트 주소는 2026-07-17 기준 DNS가 해석되지 않는다. 새 프로젝트를 만든 뒤 아래 순서로 연결한다.

## 1. 새 Supabase 프로젝트

1. Supabase에서 새 프로젝트를 생성한다.
2. SQL Editor에서 `supabase/migrations/202607170001_initial_fleet.sql`을 실행한다.
3. Authentication의 Email 로그인을 활성화한다.
4. Project Settings에서 다음 값을 확보한다.
   - Project URL
   - anon public key
   - service role key

service role key는 Vercel 서버 환경변수에만 저장한다. Host, Viewer, Git 저장소에 넣지 않는다.

## 2. Pusher

기존 Pusher Secret은 폐기하고 새 Secret을 발급한다. 데스크톱에는 App Key와 Cluster만 배포한다.

Vercel 환경변수:

```text
NEXT_PUBLIC_PUSHER_APP_KEY
NEXT_PUBLIC_PUSHER_CLUSTER
PUSHER_APP_ID
PUSHER_SECRET
NEXT_PUBLIC_SUPABASE_URL
NEXT_PUBLIC_SUPABASE_ANON_KEY
SUPABASE_SERVICE_ROLE_KEY
```

## 3. Vercel

1. `web/.env.example`의 이름을 기준으로 Production, Preview, Development 환경변수를 등록한다.
2. 새 배포를 실행한다.
3. `/api/pusher/auth`에 GET 요청 시 405가 반환되는지 확인한다.
4. 로그인 토큰 없이 POST 요청 시 401이 반환되는지 확인한다.
5. 다른 사용자의 host ID로 요청했을 때 403이 반환되는지 확인한다.

## 4. 데스크톱 설정

Host와 Viewer는 다음 환경변수를 지원한다.

```text
COMOTE_PUSHER_APP_KEY
COMOTE_PUSHER_CLUSTER
COMOTE_SUPABASE_URL
COMOTE_SUPABASE_ANON_KEY
COMOTE_WEB_AUTH_URL
```

Host 설정 파일:

```text
%ProgramData%\Comote\hostsettings.json
```

Viewer 설정 파일:

```text
%LocalAppData%\Comote\Viewer\settings.json
```

서버 연결값은 설치 패키지 생성 시 주입하거나 환경변수로 배포한다. 서버 Secret은 데스크톱 설정에 포함하지 않는다.

## 5. 키 교체 체크리스트

- [ ] 기존 Pusher Secret 폐기
- [ ] 새 Supabase 프로젝트 생성
- [ ] SQL 마이그레이션 실행
- [ ] Vercel 서버 환경변수 교체
- [ ] Vercel 재배포
- [ ] Host/Viewer 공개 연결값 교체
- [ ] 사용자 계정 재생성 또는 이전
- [ ] 호스트 재등록
- [ ] 채널 소유권 테스트
- [ ] 썸네일 비공개 접근 테스트
