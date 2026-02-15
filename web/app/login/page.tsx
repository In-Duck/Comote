
'use client'

import { useState } from 'react'
import { createClient, isSupabaseConfigured } from '@/utils/supabase/client'
import { useRouter } from 'next/navigation'
import Link from 'next/link'

export default function LoginPage() {
    const router = useRouter()
    const configured = isSupabaseConfigured()
    const supabase = configured ? createClient() : null

    const [email, setEmail] = useState('')
    const [password, setPassword] = useState('')
    const [loading, setLoading] = useState(false)
    const [message, setMessage] = useState('')

    // Supabase가 설정되지 않은 경우 안내 메시지 표시
    if (!configured || !supabase) {
        return (
            <div className="flex min-h-screen flex-col items-center justify-center p-24 bg-gray-950 text-white">
                <div className="w-full max-w-md space-y-6 text-center">
                    <h2 className="text-3xl font-bold tracking-tight">⚠️ 설정 필요</h2>
                    <p className="text-gray-400">
                        로그인 기능을 사용하려면 Supabase를 먼저 설정해야 합니다.
                    </p>
                    <div className="bg-gray-900 rounded-lg p-4 text-left text-sm text-gray-300 space-y-2">
                        <p className="font-semibold text-white">📄 web/.env.local 파일 수정:</p>
                        <code className="block text-xs text-indigo-400 whitespace-pre-wrap">
                            {`NEXT_PUBLIC_SUPABASE_URL=https://your-project.supabase.co
NEXT_PUBLIC_SUPABASE_ANON_KEY=your-anon-key`}
                        </code>
                    </div>
                    <Link
                        href="/"
                        className="inline-block rounded-full bg-gray-700 px-6 py-2 text-sm font-semibold hover:bg-gray-600 transition-colors"
                    >
                        ← 홈으로 돌아가기
                    </Link>
                </div>
            </div>
        )
    }

    const handleLogin = async () => {
        setLoading(true)
        setMessage('')

        const { error } = await supabase.auth.signInWithPassword({
            email,
            password,
        })

        if (error) {
            setMessage(error.message)
        } else {
            router.push('/')
            router.refresh()
        }
        setLoading(false)
    }

    const handleSignUp = async () => {
        setLoading(true)
        setMessage('')

        const { error } = await supabase.auth.signUp({
            email,
            password,
        })

        if (error) {
            setMessage(error.message)
        } else {
            setMessage('Check your email for the confirmation link!')
        }
        setLoading(false)
    }

    return (
        <div className="flex min-h-screen flex-col items-center justify-center p-24 bg-gray-950 text-white">
            <div className="w-full max-w-md space-y-8">
                <div>
                    <h2 className="mt-6 text-center text-3xl font-bold tracking-tight">
                        Sign in to your account
                    </h2>
                </div>
                <div className="mt-8 space-y-6">
                    <div className="-space-y-px rounded-md shadow-sm">
                        <div>
                            <input
                                type="email"
                                required
                                className="relative block w-full rounded-t-md border-0 bg-gray-900 py-1.5 text-white ring-1 ring-inset ring-gray-700 placeholder:text-gray-400 focus:z-10 focus:ring-2 focus:ring-indigo-500 sm:text-sm sm:leading-6 px-3"
                                placeholder="Email address"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                            />
                        </div>
                        <div>
                            <input
                                type="password"
                                required
                                className="relative block w-full rounded-b-md border-0 bg-gray-900 py-1.5 text-white ring-1 ring-inset ring-gray-700 placeholder:text-gray-400 focus:z-10 focus:ring-2 focus:ring-indigo-500 sm:text-sm sm:leading-6 px-3"
                                placeholder="Password"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                            />
                        </div>
                    </div>

                    <div>
                        {message && <p className="text-red-500 text-sm">{message}</p>}
                    </div>

                    <div className="flex gap-4">
                        <button
                            onClick={handleLogin}
                            disabled={loading}
                            className="group relative flex w-full justify-center rounded-md bg-indigo-500 px-3 py-2 text-sm font-semibold text-white hover:bg-indigo-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:opacity-50"
                        >
                            Sign in
                        </button>
                        <button
                            onClick={handleSignUp}
                            disabled={loading}
                            className="group relative flex w-full justify-center rounded-md bg-gray-700 px-3 py-2 text-sm font-semibold text-white hover:bg-gray-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-700 disabled:opacity-50"
                        >
                            Sign up
                        </button>
                    </div>
                </div>
            </div>
        </div>
    )
}
