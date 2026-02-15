
import { createBrowserClient } from '@supabase/ssr'

// Supabase 환경변수가 설정되지 않았을 때 크래시 방지
const supabaseUrl = process.env.NEXT_PUBLIC_SUPABASE_URL ?? ''
const supabaseAnonKey = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY ?? ''

export function isSupabaseConfigured(): boolean {
    return supabaseUrl.startsWith('http://') || supabaseUrl.startsWith('https://')
}

export function createClient() {
    if (!isSupabaseConfigured()) {
        // 설정되지 않은 경우 null 반환 (호출부에서 처리)
        return null
    }
    return createBrowserClient(supabaseUrl, supabaseAnonKey)
}
