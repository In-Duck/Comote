import { createUserClient } from '@/utils/supabase/server'

export type PusherChannelTarget =
    | { kind: 'control'; hostId: string }
    | { kind: 'viewer'; viewerId: string }

const hostIdPattern = /^[A-Za-z0-9_-]{3,128}$/
const viewerIdPattern = /^[a-f0-9]{32}$/

export function parsePusherChannel(channelName: string): PusherChannelTarget | null {
    const controlPrefix = 'private-control-'
    if (channelName.startsWith(controlPrefix)) {
        const hostId = channelName.slice(controlPrefix.length)
        return hostIdPattern.test(hostId) ? { kind: 'control', hostId } : null
    }
    const viewerPrefix = 'private-viewer-'
    if (channelName.startsWith(viewerPrefix)) {
        const viewerId = channelName.slice(viewerPrefix.length)
        return viewerIdPattern.test(viewerId) ? { kind: 'viewer', viewerId } : null
    }
    return null
}

export async function userOwnsHost(accessToken: string, userId: string, hostId: string): Promise<boolean> {
    const client = createUserClient(accessToken)
    const { data, error } = await client
        .from('hosts')
        .select('host_id')
        .eq('user_id', userId)
        .eq('host_id', hostId)
        .maybeSingle()
    if (error) {
        console.error('Host ownership lookup failed', { code: error.code, hostId, userId })
        return false
    }
    return data !== null
}

export function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value)
}