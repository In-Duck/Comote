import { NextResponse } from "next/server";
import { isEmail, isStrongEnoughPassword, normalizeAccountId } from "@/utils/auth-validation";
import { createAdminAuthClient, createPublicAuthClient } from "@/utils/supabase/server-auth";

type LoginBody = { account?: unknown; password?: unknown };

export async function POST(request: Request) {
  try {
    const body = await request.json() as LoginBody;
    const account = typeof body.account === "string" ? body.account : "";
    const password = typeof body.password === "string" ? body.password : "";

    if (!isStrongEnoughPassword(password) || isEmail(account)) {
      return genericFailure();
    }

    const accountId = normalizeAccountId(account);
    if (!accountId) return genericFailure();

    const admin = createAdminAuthClient();
    const profileResult = await admin
      .from("account_profiles")
      .select("recovery_email")
      .eq("account_id", accountId)
      .maybeSingle();

    if (profileResult.error || !profileResult.data?.recovery_email) {
      return genericFailure();
    }

    const auth = createPublicAuthClient();
    const result = await auth.auth.signInWithPassword({
      email: profileResult.data.recovery_email,
      password,
    });

    if (result.error || !result.data.session) {
      const unconfirmed = result.error?.message.toLowerCase().includes("email not confirmed");
      return NextResponse.json(
        { error: unconfirmed ? "EMAIL_NOT_CONFIRMED" : "INVALID_CREDENTIALS" },
        { status: 401 },
      );
    }

    return NextResponse.json({
      access_token: result.data.session.access_token,
      refresh_token: result.data.session.refresh_token,
    });
  } catch (error) {
    console.error("ID login failed", error);
    return NextResponse.json({ error: "AUTH_UNAVAILABLE" }, { status: 503 });
  }
}

function genericFailure() {
  return NextResponse.json({ error: "INVALID_CREDENTIALS" }, { status: 401 });
}
