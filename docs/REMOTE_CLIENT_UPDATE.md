# Manager 원격 Client 업데이트

Manager Hub는 선택한 Client에 `update` 작업과 HTTPS manifest URL을 전송할 수 있습니다.
Client는 Manager에서 설치 파일을 받지 않고 manifest에 지정된 패키지를 직접 다운로드합니다.

## 명령 형식

```json
{
  "action": "update",
  "value": "https://example.com/comote-client-update.json"
}
```

## Client 처리 순서

1. HTTPS manifest 다운로드
2. 현재 어셈블리 버전과 비교
3. HTTPS ZIP 패키지 스트리밍 다운로드
4. ZIP 전체 SHA-256 검증
5. 임시 폴더에 압축 해제
6. 실행 중인 Client 종료 후 패키지 전체 파일 교체
7. 기존 실행 인수로 Client 재시작

## manifest 예시

`Distribution/client-update.example.json`을 참고합니다.

## 게시 주의사항

- `client_package_sha256`에는 ZIP 파일 전체의 64자리 SHA-256 값을 넣어야 합니다.
- ZIP 내부에는 `ComoteClient.exe`와 함께 배포에 필요한 DLL 및 리소스가 포함되어야 합니다.
- 설치 폴더에 쓰기 권한이 없으면 파일 교체가 실패할 수 있습니다.
- Manager UI에서 업데이트 버튼을 연결할 때 기존 `SendFleetCommandAsync`에 `action=update`, `value=manifest URL`을 전달하면 됩니다.
