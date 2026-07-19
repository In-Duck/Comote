"use client";

import { FormEvent, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { createClient, isSupabaseConfigured } from "@/utils/supabase/client";

export default function LoginPanel() {
  const router = useRouter();
  const configured = isSupabaseConfigured();
  const supabase = configured ? createClient() : null;
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [mode, setMode] = useState<"login" | "signup">("login");
  const [message, setMessage] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!supabase) return;
    setLoading(true);
    setMessage("");
    try {
      const result = mode === "login"
        ? await supabase.auth.signInWithPassword({ email, password })
        : await supabase.auth.signUp({ email, password });

      if (result.error) {
        const friendlyMessage = result.error.message === "Invalid login credentials"
          ? "이메일 또는 비밀번호를 확인해 주세요."
          : result.error.message;
        setMessage(friendlyMessage);
      } else if (mode === "signup") {
        setMessage("가입 확인 메일을 확인해 주세요.");
      } else {
        router.push("/dashboard");
        router.refresh();
      }
    } catch {
      setMessage("계정 서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="grid min-h-screen bg-[#07111f] text-slate-100 lg:grid-cols-2">
      <section className="hidden border-r border-white/8 bg-[#0b1828] p-12 lg:flex lg:flex-col lg:justify-between">
        <Link href="/" className="flex items-center gap-3 text-lg font-bold"><span className="grid h-9 w-9 place-items-center rounded-xl bg-cyan-400 text-[#07111f]">C</span>Comote</Link>
        <div><p className="text-sm font-medium text-cyan-300">하나의 계정, 모든 PC</p><h1 className="mt-4 text-5xl font-bold leading-tight">어디서 로그인해도<br />내 PC가 그대로</h1><p className="mt-5 max-w-md leading-7 text-slate-400">클라이언트와 매니저에서 같은 계정을 사용하면 등록된 PC 목록과 상태가 자동으로 동기화됩니다.</p></div>
        <p className="text-xs text-slate-600">Comote Remote Fleet</p>
      </section>
      <section className="flex items-center justify-center p-6">
        <form onSubmit={submit} className="w-full max-w-md rounded-3xl border border-white/10 bg-[#0b1828] p-8 shadow-2xl">
          <Link href="/" className="mb-8 flex items-center gap-3 font-bold lg:hidden"><span className="grid h-9 w-9 place-items-center rounded-xl bg-cyan-400 text-[#07111f]">C</span>Comote</Link>
          <h2 className="text-2xl font-semibold">{mode === "login" ? "계정 로그인" : "새 계정 만들기"}</h2>
          <p className="mt-2 text-sm text-slate-400">매니저와 클라이언트에서 동일한 계정을 사용하세요.</p>
          {!configured && <div className="mt-6 rounded-xl border border-amber-300/20 bg-amber-300/10 p-4 text-sm text-amber-200">계정 서버 연결 정보가 아직 설정되지 않았습니다.</div>}
          <label className="mt-7 block text-sm text-slate-300">이메일<input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} className="mt-2 w-full rounded-xl border border-white/10 bg-[#07111f] px-4 py-3 outline-none focus:border-cyan-300" placeholder="name@example.com" /></label>
          <label className="mt-4 block text-sm text-slate-300">비밀번호<input type="password" required minLength={8} value={password} onChange={(e) => setPassword(e.target.value)} className="mt-2 w-full rounded-xl border border-white/10 bg-[#07111f] px-4 py-3 outline-none focus:border-cyan-300" placeholder="8자 이상" /></label>
          {message && <p className="mt-4 rounded-xl bg-white/5 p-3 text-sm text-slate-300">{message}</p>}
          <button disabled={!configured || loading} className="mt-6 w-full rounded-xl bg-cyan-400 px-5 py-3 font-semibold text-[#07111f] disabled:cursor-not-allowed disabled:opacity-40">{loading ? "처리 중..." : mode === "login" ? "로그인" : "회원가입"}</button>
          <button type="button" onClick={() => { setMode(mode === "login" ? "signup" : "login"); setMessage(""); }} className="mt-3 w-full rounded-xl px-5 py-3 text-sm text-slate-400 hover:bg-white/5">{mode === "login" ? "처음이신가요? 계정 만들기" : "이미 계정이 있나요? 로그인"}</button>
        </form>
      </section>
    </main>
  );
}