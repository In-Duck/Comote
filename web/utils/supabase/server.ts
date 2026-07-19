import { createServerClient } from '@supabase/ssr'
import { createClient as createSupabaseClient } from '@supabase/supabase-js'
import { cookies } from 'next/headers'
import { publicSupabaseConfig } from './config'

function requireEnvironmentVariable(name: string): string {
    const publicConfig = publicSupabaseConfig()
    const value = name === 'NEXT_PUBLIC_SUPABASE_URL'
        ? publicConfig.url
        : name === 'NEXT_PUBLIC_SUPABASE_ANON_KEY'
            ? publicConfig.key
            : process.env[name]
    if (!value) {
        throw new Error(`Missing required environment variable: ${name}`)
    }
    return value
}

export async function createClient() {
    const cookieStore = await cookies()

    return createServerClient(
        requireEnvironmentVariable('NEXT_PUBLIC_SUPABASE_URL'),
        requireEnvironmentVariable('NEXT_PUBLIC_SUPABASE_ANON_KEY'),
        {
            cookies: {
                getAll() {
                    return cookieStore.getAll()
                },
                setAll(cookiesToSet) {
                    try {
                        cookiesToSet.forEach(({ name, value, options }) =>
                            cookieStore.set(name, value, options)
                        )
                    } catch {
                        // Server Components cannot write cookies. A middleware can
                        // refresh the browser session when that becomes necessary.
                    }
                },
            },
        }
    )
}

export function createAdminClient() {
    return createSupabaseClient(
        requireEnvironmentVariable('NEXT_PUBLIC_SUPABASE_URL'),
        requireEnvironmentVariable('SUPABASE_SERVICE_ROLE_KEY'),
        {
            auth: {
                autoRefreshToken: false,
                persistSession: false,
            },
        }
    )
}

export function createUserClient(accessToken: string) {
    return createSupabaseClient(
        requireEnvironmentVariable('NEXT_PUBLIC_SUPABASE_URL'),
        requireEnvironmentVariable('NEXT_PUBLIC_SUPABASE_ANON_KEY'),
        {
            global: { headers: { Authorization: `Bearer ${accessToken}` } },
            auth: { autoRefreshToken: false, persistSession: false },
        }
    )
}