import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.20",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.20/ComoteClient-1.6.0-preview.20-win-x64.zip",
      client_package_sha256: "4C3B598143CAA0208EB6E549E0114E726D3E16015A1837A3A958B8531D1400C5",
      minimum_version: "1.6.0.19",
      release_notes:
        "트레이 업데이트 알림, Manager의 PC별 업데이트 표시, 검증·복구가 포함된 자동 업데이트를 추가했습니다.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}