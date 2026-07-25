import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.28",
      manager_setup_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.28/ComoteManager_Setup.exe",
      manager_setup_sha256:
        "60760C6812ADEE28F7BBC02A8981B1F572D88FE3D2411B3C1C0CE66BCF41D5A3",
      release_notes:
        "Fleet-wide Client update controls with online and offline target reporting.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}
