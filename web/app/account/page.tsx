import { redirect } from "next/navigation";
import AccountPanel from "@/components/AccountPanel";
import { createClient } from "@/utils/supabase/server";
import { displayAccount } from "@/utils/account-identity";

export const dynamic = "force-dynamic";

export default async function AccountPage() {
  const supabase = await createClient();
  const { data: { user } } = await supabase.auth.getUser();
  if (!user) redirect("/auth");

  const { data: profile } = await supabase
    .from("account_profiles")
    .select("account_id,recovery_email")
    .eq("user_id", user.id)
    .maybeSingle();

  const email = user.email ?? "";
  const accountId = profile?.account_id ?? displayAccount(email);
  const recoveryEmail = profile?.recovery_email ?? email;
  const legacy = recoveryEmail.endsWith("@accounts.kymote.app");

  return <AccountPanel accountId={accountId} email={recoveryEmail} legacy={legacy} />;
}
