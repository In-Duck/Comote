import { createClient, createUserClient } from "@/utils/supabase/server";
import { NextResponse } from "next/server";

const sources = new Set(["client", "manager", "web", "turn", "updater"]);
const eventTypes = new Set([
  "connected",
  "disconnected",
  "reconnecting",
  "recovered",
  "direct",
  "relayed",
  "degraded",
  "turn_unavailable",
  "update_started",
  "update_succeeded",
  "update_failed",
  "update_rolled_back",
]);
const severities = new Set(["info", "warning", "critical"]);

type TelemetryBody = {
  hostId?: unknown;
  source?: unknown;
  eventType?: unknown;
  severity?: unknown;
  details?: unknown;
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

    const body = (await request.json()) as TelemetryBody;
    const source = typeof body.source === "string" ? body.source : "";
    const eventType =
      typeof body.eventType === "string" ? body.eventType : "";
    const severity =
      typeof body.severity === "string" ? body.severity : "info";
    const hostId =
      typeof body.hostId === "string" &&
      /^[A-Za-z0-9_-]{3,128}$/.test(body.hostId)
        ? body.hostId
        : null;
    const details =
      body.details &&
      typeof body.details === "object" &&
      !Array.isArray(body.details)
        ? body.details
        : {};

    if (
      !sources.has(source) ||
      !eventTypes.has(eventType) ||
      !severities.has(severity)
    ) {
      return NextResponse.json(
        { error: "Invalid telemetry event" },
        { status: 400 },
      );
    }

    const serializedDetails = JSON.stringify(details);
    if (serializedDetails.length > 4096) {
      return NextResponse.json(
        { error: "Telemetry details too large" },
        { status: 413 },
      );
    }

    const client = createUserClient(accessToken);
    const { error } = await client.from("connection_events").insert({
      user_id: user.id,
      host_id: hostId,
      source,
      event_type: eventType,
      severity,
      details,
    });
    if (error) {
      console.error("Telemetry insert failed", error.message);
      return NextResponse.json(
        { error: "Telemetry storage unavailable" },
        { status: 503 },
      );
    }

    return new NextResponse(null, { status: 204 });
  } catch (error: unknown) {
    console.error("Telemetry endpoint failed", error);
    return NextResponse.json(
      { error: "Telemetry service unavailable" },
      { status: 503 },
    );
  }
}
