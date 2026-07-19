import { createClient, createUserClient } from '@/utils/supabase/server'
import { NextResponse } from 'next/server'

export async function GET(request: Request) {
    try {
        const authHeader = request.headers.get('authorization')
        const accessToken = authHeader?.startsWith('Bearer ') ? authHeader.slice(7) : null
        const supabase = await createClient()
        const userResult = accessToken
            ? await supabase.auth.getUser(accessToken)
            : await supabase.auth.getUser()
        const user = userResult.data.user
        if (userResult.error || !user) return NextResponse.json({ error: 'Unauthorized' }, { status: 401 })
        if (!accessToken) return NextResponse.json({ error: 'Missing bearer token' }, { status: 401 })

        const client = createUserClient(accessToken)
        const { data: hosts, error } = await client
            .from('hosts')
            .select('host_id, thumbnail_path')
            .eq('user_id', user.id)
            .not('thumbnail_path', 'is', null)
        if (error) return NextResponse.json({ error: 'Thumbnail lookup failed' }, { status: 500 })

        const paths = (hosts ?? []).map(host => host.thumbnail_path).filter((path): path is string => typeof path === 'string' && path.length > 0)
        if (paths.length === 0) return NextResponse.json({ urls: {} })

        const { data: signedUrls, error: signingError } = await client.storage.from('thumbnails').createSignedUrls(paths, 90)
        if (signingError) return NextResponse.json({ error: 'Thumbnail signing failed' }, { status: 500 })
        const pathToUrl = new Map((signedUrls ?? []).map(item => [item.path, item.signedUrl]))
        const urls: Record<string, string> = {}
        for (const host of hosts ?? []) {
            if (host.thumbnail_path) {
                const signedUrl = pathToUrl.get(host.thumbnail_path)
                if (signedUrl) urls[host.host_id] = signedUrl
            }
        }
        return NextResponse.json({ urls })
    } catch (error: unknown) {
        console.error('Thumbnail endpoint failed', error)
        return NextResponse.json({ error: 'Thumbnail service unavailable' }, { status: 503 })
    }
}