import { NextResponse } from "next/server";

export const dynamic = "force-static";

export async function GET() {
  return NextResponse.json(
    {
      version: "1.6.0.27",
      client_package_url:
        "https://github.com/In-Duck/Comote/releases/download/v1.6.0-preview.27/ComoteClient-1.6.0-preview.27-win-x64.zip",
      client_package_sha256:
        "96806C71E87932E99F8B772E1F93F0A905F711AB44120F2C6268948C6B8F86B3",
      minimum_version: "1.6.0.19",
      release_notes:
        "Automatic login after updates, saved login by default, and online-state recovery.",
    },
    {
      headers: {
        "Cache-Control": "public, max-age=300, s-maxage=300",
        "X-Content-Type-Options": "nosniff",
      },
    },
  );
}
