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

    if (!configured || !supabase) {
        return (
            <div className="flex min-h-screen items-center justify-center p-6 bg-[radial-gradient(ellipse_at_top,_var(--tw-gradient-stops))] from-gray-900 via-black to-black">
                <div className="glass-card w-full max-w-md p-8 text-center space-y-6">
                    <div className="text-5xl mb-2">⚠️</div>
                    <h2 className="text-2xl font-bold text-white">Setup Required</h2>
                    <p className="text-gray-400">
                        Supabase configuration is missing.
                    </p>
                    <div className="bg-black/50 rounded-lg p-4 text-left border border-white/5">
                        <p className="text-xs text-gray-500 font-mono mb-2">web/.env.local</p>
                        <code className="block text-xs text-blue-400 font-mono break-all">
                            NEXT_PUBLIC_SUPABASE_URL=...<br />
                            NEXT_PUBLIC_SUPABASE_ANON_KEY=...
                        </code>
                    </div>
                    <Link href="/" className="btn-secondary w-full block">
                        Back to Home
                    </Link>
                </div>
            </div>
        )
    }

    const handleLogin = async () => {
        setLoading(true)
        setMessage('')
        const { error } = await supabase.auth.signInWithPassword({ email, password })
        if (error) setMessage(error.message)
        else {
            router.push('/')
            router.refresh()
        }
        setLoading(false)
    }

    const handleSignUp = async () => {
        setLoading(true)
        setMessage('')
        const { error } = await supabase.auth.signUp({ email, password })
        if (error) setMessage(error.message)
        else setMessage('Check your email for the confirmation link!')
        setLoading(false)
    }

    return (
        <div className="flex min-h-screen items-center justify-center p-6 bg-[radial-gradient(ellipse_at_top,_var(--tw-gradient-stops))] from-blue-900/20 via-black to-black relative overflow-hidden">
            {/* Background Effects */}
            <div className="absolute top-0 left-0 w-full h-full overflow-hidden pointer-events-none">
                <div className="absolute top-[-10%] left-[20%] w-[500px] h-[500px] bg-blue-600/10 rounded-full blur-[100px]"></div>
                <div className="absolute bottom-[-10%] right-[20%] w-[500px] h-[500px] bg-purple-600/10 rounded-full blur-[100px]"></div>
            </div>

            <div className="glass-card w-full max-w-md p-8 relative z-10">
                <div className="text-center mb-10">
                    <Link href="/" className="text-3xl font-bold bg-clip-text text-transparent bg-gradient-to-r from-blue-400 to-purple-400 inline-block mb-2">
                        Comote
                    </Link>
                    <p className="text-gray-400 text-sm">Sign in to control your world</p>
                </div>

                <div className="space-y-6">
                    <div className="space-y-4">
                        <div>
                            <label className="block text-xs font-medium text-gray-400 mb-1 ml-1">Email</label>
                            <input
                                type="email"
                                required
                                className="input-field w-full"
                                placeholder="name@example.com"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                            />
                        </div>
                        <div>
                            <label className="block text-xs font-medium text-gray-400 mb-1 ml-1">Password</label>
                            <input
                                type="password"
                                required
                                className="input-field w-full"
                                placeholder="••••••••"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                            />
                        </div>
                    </div>

                    {message && (
                        <div className={`p-3 rounded-lg text-sm text-center ${message.includes('Check') ? 'bg-green-500/10 text-green-400 border border-green-500/20' : 'bg-red-500/10 text-red-400 border border-red-500/20'}`}>
                            {message}
                        </div>
                    )}

                    <div className="flex flex-col gap-3 pt-2">
                        <button
                            onClick={handleLogin}
                            disabled={loading}
                            className="btn-primary w-full flex justify-center items-center"
                        >
                            {loading ? (
                                <span className="w-5 h-5 border-2 border-white/20 border-t-white rounded-full animate-spin"></span>
                            ) : 'Sign In'}
                        </button>
                        <button
                            onClick={handleSignUp}
                            disabled={loading}
                            className="btn-secondary w-full"
                        >
                            Create Account
                        </button>
                    </div>

                    <p className="text-center text-xs text-gray-500 mt-6">
                        By continuing, you agree to our Terms of Service and Privacy Policy.
                    </p>
                </div>
            </div>
        </div>
    )
}
