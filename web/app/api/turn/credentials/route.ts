import { createClient } from "@/utils/supabase/server";
import { NextResponse } from "next/server";

export const dynamic = "force-dynamic";

type IceServer = {
  urls: string | string[];
  username?: string;
  credential?: string;
};

type CredentialsResponse = {
  iceServers?: IceServer[];
};

export async function POST(request: Request) {
  try {
    const authorization = request.headers.get("authorization");
    if (!authorization?.startsWith("Bearer ")) {
      return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
    }

    const accessToken = authorization.slice(7);
    const supabase = await createClient();
    const userResult = await supabase.auth.getUser(accessToken);
    const user = userResult.data.user;
    if (userResult.error || !user) {
      return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
    }

    const keyId = process.env.CLOUDFLARE_TURN_KEY_ID;
    const apiToken = process.env.CLOUDFLARE_TURN_API_TOKEN;
    if (!keyId || !apiToken) {
      return NextResponse.json(
        { error: "Managed TURN is not configured", mode: "direct-only" },
        { status: 503 },
      );
    }

    const ttlSeconds = 86_400;
    const response = await fetch(
      `https://rtc.live.cloudflare.com/v1/turn/keys/${encodeURIComponent(keyId)}/credentials/generate-ice-servers`,
      {
        method: "POST",
        headers: {
          Authorization: `Bearer ${apiToken}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          ttl: ttlSeconds,
          customIdentifier: user.id,
        }),
        cache: "no-store",
      },
    );

    if (!response.ok) {
      console.error(
        "Cloudflare TURN credential request failed",
        response.status,
      );
      return NextResponse.json(
        { error: "TURN credential service unavailable" },
        { status: 503 },
      );
    }

    const payload = (await response.json()) as CredentialsResponse;
    const hasTurn = payload.iceServers?.some((server) =>
      (Array.isArray(server.urls) ? server.urls : [server.urls]).some(
        (url) => url.startsWith("turn:") || url.startsWith("turns:"),
      ),
    );
    if (!hasTurn) {
      return NextResponse.json(
        { error: "TURN provider returned no relay server" },
        { status: 502 },
      );
    }

    return NextResponse.json(
      {
        iceServers: payload.iceServers,
        expiresAt: new Date(
          Date.now() + ttlSeconds * 1000,
        ).toISOString(),
      },
      {
        headers: {
          "Cache-Control": "private, no-store",
          "X-Content-Type-Options": "nosniff",
        },
      },
    );
  } catch (error: unknown) {
    console.error("TURN credential endpoint failed", error);
    return NextResponse.json(
      { error: "TURN credential service unavailable" },
      { status: 503 },
    );
  }
}
