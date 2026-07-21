import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.22",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.22/ComoteClient-1.6.0-preview.22-win-x64.zip",
      client_package_sha256: "D522DDE7BD851E84EDD785118EEAB08660CAC0D153E2941DD1706825B3786C3C",
      minimum_version: "1.6.0.19",
      release_notes:
        "업데이트 다운로드·검증·설치 준비 과정을 퍼센트 게이지로 표시합니다.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}