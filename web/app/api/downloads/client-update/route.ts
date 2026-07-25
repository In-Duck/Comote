import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.28",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.28/ComoteClient-1.6.0-preview.28-win-x64.zip",
      client_package_sha256:
        "6A5E5FEBCE3DA971ED65F88A1D5160426243D99E8351F6B1442BE3F11C4391C2",
      minimum_version: "1.6.0.19",
      release_notes:
        "Fleet-wide updates from Manager with automatic offline-client recovery.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}
