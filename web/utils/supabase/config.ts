export const defaultSupabaseUrl =
  'https://xhdpmxarnkntbkwqobzm.supabase.co'

// Supabase publishable keys are intentionally safe to embed in public clients.
// Database access is restricted by Row Level Security policies.
export const defaultSupabasePublishableKey =
  'sb_publishable_0DND4OEVX8lTX5qGIgs1Xg_Bnm7ORhO'

const retiredProjectRef = 'nlodelehewbbniayzjuv'

export function publicSupabaseConfig() {
  const environmentUrl = process.env.NEXT_PUBLIC_SUPABASE_URL
  const environmentKey = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY
  const environmentIsCurrent =
    Boolean(environmentUrl) && !environmentUrl!.includes(retiredProjectRef)

  return environmentIsCurrent
    ? {
        url: environmentUrl!,
        key: environmentKey ?? defaultSupabasePublishableKey,
      }
    : {
        url: defaultSupabaseUrl,
        key: defaultSupabasePublishableKey,
      }
}