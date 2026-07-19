import { createClient } from '@/utils/supabase/server'
import {
    isRecord,
    parsePusherChannel,
    userOwnsHost,
} from '@/utils/pusher-authorization'
import { NextResponse } from 'next/server'
import Pusher from 'pusher'

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

async function readAuthParameters(request: Request): Promise<{
    socketId: string
    channelName: string
} | null> {
    const contentType = request.headers.get('content-type') ?? ''

    if (contentType.includes('application/x-www-form-urlencoded')) {
        const formData = await request.formData()
        const socketId = formData.get('socket_id')
        const channelName = formData.get('channel_name')

        return typeof socketId === 'string' && typeof channelName === 'string'
            ? { socketId, channelName }
            : null
    }

    const body: unknown = await request.json()
    if (!isRecord(body)) return null

    const socketId = body.socketId ?? body.socket_id
    const channelName = body.channelName ?? body.channel_name

    return typeof socketId === 'string' && typeof channelName === 'string'
        ? { socketId, channelName }
        : null
}

export async function POST(request: Request) {
    try {
        const supabase = await createClient()
        const authHeader = request.headers.get('authorization')

        const userResult = authHeader?.startsWith('Bearer ')
            ? await supabase.auth.getUser(authHeader.slice(7))
            : await supabase.auth.getUser()

        const user = userResult.data.user
        if (userResult.error || !user) {
            return NextResponse.json({ error: 'Unauthorized' }, { status: 401 })
        }

        const parameters = await readAuthParameters(request)
        if (!parameters) {
            return NextResponse.json(
                { error: 'Missing socket_id or channel_name' },
                { status: 400 }
            )
        }

        const target = parsePusherChannel(parameters.channelName)
        if (!target) {
            return NextResponse.json({ error: 'Forbidden channel' }, { status: 403 })
        }

        if (
            target.kind === 'control' &&
            !(await userOwnsHost(authHeader!.slice(7), user.id, target.hostId))
        ) {
            return NextResponse.json({ error: 'Host access denied' }, { status: 403 })
        }

        const authResponse = createPusher().authorizeChannel(
            parameters.socketId,
            parameters.channelName
        )
        return NextResponse.json(authResponse)
    } catch (error: unknown) {
        console.error('Pusher authorization failed', error)
        return NextResponse.json(
            { error: 'Signaling service unavailable' },
            { status: 503 }
        )
    }
}
