import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.25",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.25/ComoteClient-1.6.0-preview.25-win-x64.zip",
      client_package_sha256:
        "AFB6E8EE806A2A76389E271C4D4DF7AC77C45442C5CB2B28C3A9D2A9204F2182",
      minimum_version: "1.6.0.19",
      release_notes:
        "Managed TURN failover, distributed reconnect recovery, operations telemetry, and automatic update rollback.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}
