import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.27",
      manager_setup_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.27/ComoteManager_Setup.exe",
      manager_setup_sha256:
        "A0E5079BF55A3FE43F0D678BDA99BC3BFC477A495F5B91B4CA36174B62EE1D61",
      release_notes:
        "Manager update controls, Client automatic login, and online-state recovery.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}
