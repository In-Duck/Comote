import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.26",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.26/ComoteClient-1.6.0-preview.26-win-x64.zip",
      client_package_sha256:
        "4481A7FED470582A2509CC6BDD529D4F0A44A6A17C46F70D06ED4647BCE0D04A",
      minimum_version: "1.6.0.19",
      release_notes:
        "Tray input controls, stable host ordering, and signaling readiness fixes.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}
