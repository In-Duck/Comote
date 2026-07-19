
import { createBrowserClient } from '@supabase/ssr'
import { publicSupabaseConfig } from './config'

const { url: supabaseUrl, key: supabaseAnonKey } = publicSupabaseConfig()

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
