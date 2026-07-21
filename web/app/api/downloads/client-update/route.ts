import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.21",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.21/ComoteClient-1.6.0-preview.21-win-x64.zip",
      client_package_sha256: "D43D7E2F93FECADFF088BF70E5406F458F2EAE94B5891DC87F77E889CE786C17",
      minimum_version: "1.6.0.19",
      release_notes:
        "UAC 보안 데스크톱 전환 시 화면 캡처를 즉시 다시 연결하도록 수정했습니다.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}