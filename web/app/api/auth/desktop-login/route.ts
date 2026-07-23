import { NextResponse } from "next/server";
import { isEmail, isStrongEnoughPassword, normalizeAccountId } from "@/utils/auth-validation";
import { createAdminAuthClient, createPublicAuthClient } from "@/utils/supabase/server-auth";

type LoginBody = { account?: unknown; password?: unknown };

export async function POST(request: Request) {
  try {
    const body = await request.json() as LoginBody;
    const account = typeof body.account === "string" ? body.account : "";
    const password = typeof body.password === "string" ? body.password : "";
    if (isEmail(account) || !isStrongEnoughPassword(password)) return denied();

    const accountId = normalizeAccountId(account);
    if (!accountId) return denied();

    const profile = await createAdminAuthClient()
      .from("account_profiles")
      .select("recovery_email")
      .eq("account_id", accountId)
      .maybeSingle();
    if (profile.error || !profile.data?.recovery_email) return denied();

    const result = await createPublicAuthClient().auth.signInWithPassword({
      email: profile.data.recovery_email,
      password,
    });
    const session = result.data.session;
    const user = result.data.user;
    if (result.error || !session || !user) return denied();

    return NextResponse.json({
      access_token: session.access_token,
      refresh_token: session.refresh_token,
      user_id: user.id,
    });
  } catch (error) {
    console.error("Desktop ID login failed", error);
    return NextResponse.json({ error: "AUTH_UNAVAILABLE" }, { status: 503 });
  }
}

function denied() {
  return NextResponse.json({ error: "INVALID_CREDENTIALS" }, { status: 401 });
}
