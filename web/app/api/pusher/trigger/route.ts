
import { createClient } from '@/utils/supabase/server'
import { NextResponse } from 'next/server'
import Pusher from 'pusher'

const pusher = new Pusher({
    appId: process.env.PUSHER_APP_ID!,
    key: process.env.NEXT_PUBLIC_PUSHER_APP_KEY!,
    secret: process.env.PUSHER_SECRET!,
    cluster: process.env.NEXT_PUBLIC_PUSHER_CLUSTER!,
    useTLS: true,
})

export async function POST(request: Request) {
    const supabase = await createClient()

    // 1. Verify JWT
    const authHeader = request.headers.get('Authorization')
    if (!authHeader || !authHeader.startsWith('Bearer ')) {
        return NextResponse.json({ error: 'Missing Authorization Token' }, { status: 401 })
    }
    const token = authHeader.substring(7)
    const { data: { user }, error } = await supabase.auth.getUser(token)

    if (error || !user) {
        return NextResponse.json({ error: 'Unauthorized' }, { status: 401 })
    }

    // 2. Parse Trigger params
    const body = await request.json()
    const { channel, event, data } = body

    if (!channel || !event || !data) {
        return NextResponse.json({ error: 'Missing channel, event, or data' }, { status: 400 })
    }

    // 3. Validate channel & event (보안: 허용된 채널/이벤트만 전송 가능)
    const allowedPrefixes = ['private-control-', 'private-viewer-']
    if (!allowedPrefixes.some(prefix => channel.startsWith(prefix))) {
        return NextResponse.json({ error: 'Forbidden channel' }, { status: 403 })
    }

    const allowedEvents = ['signal']
    if (!allowedEvents.includes(event)) {
        return NextResponse.json({ error: 'Forbidden event' }, { status: 403 })
    }

    // 4. Trigger Event
    try {
        await pusher.trigger(channel, event, data)
        return NextResponse.json({ success: true })
    } catch (e: any) {
        return NextResponse.json({ error: e.message }, { status: 500 })
    }
}
