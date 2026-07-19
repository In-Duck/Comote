import { createClient } from '@/utils/supabase/server'
import {
    isRecord,
    parsePusherChannel,
    userOwnsHost,
} from '@/utils/pusher-authorization'
import { NextResponse } from 'next/server'
import Pusher from 'pusher'

const maxRequestBytes = 64 * 1024

function createPusher(): Pusher {
    const appId = process.env.PUSHER_APP_ID
    const key = process.env.NEXT_PUBLIC_PUSHER_APP_KEY
    const secret = process.env.PUSHER_SECRET
    const cluster = process.env.NEXT_PUBLIC_PUSHER_CLUSTER

    if (!appId || !key || !secret || !cluster) {
        throw new Error('Pusher server environment is not configured')
    }

    return new Pusher({ appId, key, secret, cluster, useTLS: true })
}

export async function POST(request: Request) {
    try {
        const contentLength = Number(request.headers.get('content-length') ?? '0')
        if (contentLength > maxRequestBytes) {
            return NextResponse.json({ error: 'Request too large' }, { status: 413 })
        }

        const authHeader = request.headers.get('authorization')
        if (!authHeader?.startsWith('Bearer ')) {
            return NextResponse.json({ error: 'Missing authorization token' }, { status: 401 })
        }

        const supabase = await createClient()
        const { data: { user }, error } = await supabase.auth.getUser(authHeader.slice(7))
        if (error || !user) {
            return NextResponse.json({ error: 'Unauthorized' }, { status: 401 })
        }

        const body: unknown = await request.json()
        if (!isRecord(body)) {
            return NextResponse.json({ error: 'Invalid request body' }, { status: 400 })
        }

        const { channel, event, data } = body
        if (typeof channel !== 'string' || event !== 'signal' || !isRecord(data)) {
            return NextResponse.json({ error: 'Invalid signal request' }, { status: 400 })
        }

        const target = parsePusherChannel(channel)
        if (!target) {
            return NextResponse.json({ error: 'Forbidden channel' }, { status: 403 })
        }

        if (target.kind === 'control') {
            if (!(await userOwnsHost(authHeader.slice(7), user.id, target.hostId))) {
                return NextResponse.json({ error: 'Host access denied' }, { status: 403 })
            }
        } else {
            const sourceHostId = data.from
            if (
                typeof sourceHostId !== 'string' ||
                !(await userOwnsHost(authHeader.slice(7), user.id, sourceHostId))
            ) {
                return NextResponse.json({ error: 'Signal source denied' }, { status: 403 })
            }
        }

        await createPusher().trigger(channel, event, data)
        return NextResponse.json({ success: true })
    } catch (error: unknown) {
        console.error('Pusher trigger failed', error)
        return NextResponse.json(
            { error: 'Signaling service unavailable' },
            { status: 503 }
        )
    }
}
