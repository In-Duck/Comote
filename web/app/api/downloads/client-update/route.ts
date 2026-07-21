import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.23",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.23/ComoteClient-1.6.0-preview.23-win-x64.zip",
      client_package_sha256: "AA310686FD0ACEA7C03CDB12B33134F2AEEA46B12CF47BE35BB28D03B6C26C1F",
      minimum_version: "1.6.0.19",
      release_notes:
        "The thumbnail tab now shows up to four live low-bandwidth previews at 2 FPS.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}