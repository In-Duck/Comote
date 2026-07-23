import { createClient } from "@supabase/supabase-js";
import { publicSupabaseConfig } from "./config";

export function createPublicAuthClient() {
  const { url, key } = publicSupabaseConfig();
  return createClient(url, key, {
    auth: { autoRefreshToken: false, persistSession: false },
  });
}

export function createAdminAuthClient() {
  const { url } = publicSupabaseConfig();
  const serviceRoleKey = process.env.SUPABASE_SERVICE_ROLE_KEY;
  if (!serviceRoleKey) throw new Error("SUPABASE_SERVICE_ROLE_KEY is not configured");

  return createClient(url, serviceRoleKey, {
    auth: { autoRefreshToken: false, persistSession: false },
  });
}
