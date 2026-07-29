"use client";

import { FormEvent, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { normalizeAccountEmail } from "@/utils/account-identity";
import { createClient, isSupabaseConfigured } from "@/utils/supabase/client";

function friendlyAuthError(message: string) {
  if (message === "Invalid login credentials") return "아이디 또는 비밀번호를 확인해 주세요.";
  if (message.includes("already registered")) return "이미 사용 중인 계정입니다.";
  return "계정 요청을 처리하지 못했습니다. 잠시 후 다시 시도해 주세요.";
}

export default function LoginPanel() {
  const router = useRouter();
  const configured = isSupabaseConfigured();
  const supabase = configured ? createClient() : null;
  const [account, setAccount] = useState("");
  const [password, setPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [mode, setMode] = useState<"login" | "signup">("login");
  const [message, setMessage] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!supabase) return;
    const email = normalizeAccountEmail(account);
    if (!email) {
      setMessage("4~32자의 아이디 또는 올바른 이메일을 입력해 주세요.");
      return;
    }

    setLoading(true);
    setMessage("");
    try {
      const result = mode === "login"
        ? await supabase.auth.signInWithPassword({ email, password })
        : await supabase.auth.signUp({ email, password });
      if (result.error) setMessage(friendlyAuthError(result.error.message));
      else if (mode === "signup" && !result.data.session) setMessage("이메일 확인 후 로그인해 주세요.");
      else {
        router.push("/dashboard");
        router.refresh();
      }
    } catch {
      setMessage("계정 서버에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="grid min-h-screen place-items-center bg-[#f5f5f2] px-5 text-[#1c1c1a]">
      <form onSubmit={submit} className="w-full max-w-sm border border-[#d8d8d2] bg-white p-8 shadow-sm">
        <Link href="/" className="text-lg font-semibold tracking-tight">Comote</Link>
        <h1 className="mt-10 text-2xl font-semibold">{mode === "login" ? "로그인" : "계정 만들기"}</h1>
        <p className="mt-2 text-sm leading-6 text-[#6a6a64]">클라이언트와 매니저에서 같은 계정을 사용하세요.</p>

        {!configured && <p className="mt-5 border border-[#dccb9e] bg-[#fffaf0] p-3 text-sm text-[#725d29]">계정 서버 설정이 필요합니다.</p>}

        <label className="mt-7 block text-sm text-[#494945]">아이디 또는 이메일
          <input autoCapitalize="none" autoComplete="username" required value={account}
                 onChange={(event) => setAccount(event.target.value)}
                 className="mt-2 w-full border border-[#d6d6d1] bg-white px-3 py-3 outline-none focus:border-[#667085]"
                 placeholder="ID 또는 email@example.com" />
        </label>
        <label className="mt-5 block text-sm text-[#494945]">비밀번호
          <input type="password" autoComplete={mode === "login" ? "current-password" : "new-password"}
                 required minLength={8} value={password} onChange={(event) => setPassword(event.target.value)}
                 className="mt-2 w-full border border-[#d6d6d1] bg-white px-3 py-3 outline-none focus:border-[#667085]" />
        </label>

        {message && <p className="mt-4 bg-[#f0f0ec] p-3 text-sm text-[#5c5c57]">{message}</p>}
        <button disabled={!configured || loading}
                className="mt-6 w-full bg-[#1f2937] px-5 py-3 font-medium text-white disabled:cursor-not-allowed disabled:opacity-40">
          {loading ? "처리 중" : mode === "login" ? "로그인" : "계정 만들기"}
        </button>
        <button type="button" onClick={() => { setMode(mode === "login" ? "signup" : "login"); setMessage(""); }}
                className="mt-3 w-full px-5 py-2 text-sm text-[#5c5c57] hover:bg-[#f5f5f2]">
          {mode === "login" ? "계정이 없나요?" : "이미 계정이 있나요?"}
        </button>
      </form>
    </main>
  );
}