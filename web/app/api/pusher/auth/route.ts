
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

    // 1. JWT Token Verification via Authorization Header (for Desktop App)
    // Or Cookie Session (for Web)

    let userId = ''
    let email = ''

    // Check Authorization header first (Desktop App scenario)
    const authHeader = request.headers.get('Authorization')
    if (authHeader && authHeader.startsWith('Bearer ')) {
        const token = authHeader.substring(7)
        const { data: { user }, error } = await supabase.auth.getUser(token)

        if (error || !user) {
            return NextResponse.json({ error: 'Unauthorized via Token' }, { status: 401 })
        }
        userId = user.id
        email = user.email || ''
    } else {
        // Check Cookie Session (Web scenario)
        const { data: { user }, error } = await supabase.auth.getUser()
        if (error || !user) {
            return NextResponse.json({ error: 'Unauthorized via Cookie' }, { status: 401 })
        }
        userId = user.id
        email = user.email || ''
    }

    // 2. Parse Pusher Auth params
    // Pusher client sends: socket_id, channel_name
    const contentType = request.headers.get('content-type') || ''
    let socketId = ''
    let channelName = ''

    if (contentType.includes('application/x-www-form-urlencoded')) {
        const formData = await request.formData()
        socketId = formData.get('socket_id') as string
        channelName = formData.get('channel_name') as string
    } else {
        // application/json
        const body = await request.json()
        socketId = body.socketId || body.socket_id
        channelName = body.channelName || body.channel_name
    }

    if (!socketId || !channelName) {
        return NextResponse.json({ error: 'Missing socket_id or channel_name' }, { status: 400 })
    }

    // 3. Authorize
    // Presence channel requires user_id and user_info
    if (channelName.startsWith('presence-')) {
        // [중요] Host와 Viewer가 같은 계정을 써도 서로 다른 멤버로 인식되도록 식별자 추가
        // socketId를 붙이면 연결마다 고유해짐
        const uniqueUserId = `${userId}-${socketId}`

        // Host가 보낸 시스템 정보가 있으면 사용 (X-Host-Info 헤더)
        const hostInfoHdr = request.headers.get('X-Host-Info')
        let userInfo: any = {
            email: email,
            ip: request.headers.get('x-forwarded-for') || 'unknown'
        }

        if (hostInfoHdr) {
            try {
                // 1. Try to Base64 decode first (New format)
                const decoded = Buffer.from(hostInfoHdr, 'base64').toString('utf-8')
                const parsed = JSON.parse(decoded)
                userInfo = { ...userInfo, ...parsed }
            } catch (e) {
                // 2. Fallback to raw JSON parse (Old format or if not base64)
                try {
                    const parsed = JSON.parse(hostInfoHdr)
                    userInfo = { ...userInfo, ...parsed }
                } catch (e2) {
                    console.log('Host info parse error', e2)
                }
            }
        }

        const presenceData = {
            user_id: uniqueUserId,
            user_info: userInfo,
        }
        const authResponse = pusher.authorizeChannel(socketId, channelName, presenceData)
        return NextResponse.json(authResponse)
    } else {
        // Private channel
        const authResponse = pusher.authorizeChannel(socketId, channelName)
        return NextResponse.json(authResponse)
    }
}
