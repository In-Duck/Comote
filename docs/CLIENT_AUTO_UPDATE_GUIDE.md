# Client 자동 업데이트 게시 안내

## 동작 방식

Client는 시작 전에 HTTPS 업데이트 안내 파일을 확인할 수 있습니다. 새 버전이면
ZIP을 내려받아 SHA-256을 검증한 뒤, Client 실행 파일만 교체하고 같은 실행
인수로 다시 시작합니다. 원격제어 세션 도중에는 업데이트하지 않습니다.

## 게시 절차

1. `Comote-<버전>-win-x64.zip`을 GitHub Release 또는 HTTPS 파일 서버에 올립니다.
2. 아래 형식의 JSON 파일을 HTTPS로 공개합니다.

```json
{
  "version": "1.6.0.5",
  "client_package_url": "https://example.com/Comote-1.6.0-preview.5-win-x64.zip",
  "client_package_sha256": "ZIP_SHA256"
}
```

3. Client 실행 인수에 다음을 추가합니다.

```text
--update-manifest "https://example.com/comote-client-update.json"
```

환경 변수 `COMOTE_UPDATE_MANIFEST_URL`로 지정해도 됩니다.

## 주의 사항

- 안내 파일과 ZIP은 반드시 HTTPS로 제공해야 합니다.
- SHA-256은 ZIP 전체 파일의 해시입니다.
- 버전은 `1.6.0.5`처럼 숫자 네 자리 형식을 사용합니다.
- Program Files에 설치한 경우 자동 교체에는 관리자 권한이 필요할 수 있습니다.
