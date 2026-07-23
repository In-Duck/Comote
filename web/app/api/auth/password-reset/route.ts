import { NextResponse } from "next/server";
import { isEmail, normalizeAccountId, normalizeEmail } from "@/utils/auth-validation";
import { createAdminAuthClient, createPublicAuthClient } from "@/utils/supabase/server-auth";

type ResetBody = { account?: unknown };

export async function POST(request: Request) {
  try {
    const body = await request.json() as ResetBody;
    const account = typeof body.account === "string" ? body.account : "";
    let email: string | null = null;

    if (isEmail(account)) {
      email = normalizeEmail(account);
    } else {
      const accountId = normalizeAccountId(account);
      if (accountId) {
        const result = await createAdminAuthClient()
          .from("account_profiles")
          .select("recovery_email")
          .eq("account_id", accountId)
          .maybeSingle();
        email = result.data?.recovery_email ?? null;
      }
    }

    if (email) {
      const configuredSiteUrl = process.env.NEXT_PUBLIC_SITE_URL;
      const requestOrigin = new URL(request.url).origin;
      const siteUrl = configuredSiteUrl?.startsWith("https://") ? configuredSiteUrl : requestOrigin;
      await createPublicAuthClient().auth.resetPasswordForEmail(email, {
        redirectTo: `${siteUrl}/auth/callback?next=/account/reset-password`,
      });
    }

    // Do not reveal whether an account exists.
    return NextResponse.json({ ok: true });
  } catch (error) {
    console.error("Password reset request failed", error);
    // Keep the same public response to prevent account enumeration.
    return NextResponse.json({ ok: true });
  }
}
