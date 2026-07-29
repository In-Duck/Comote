"use client";

import Link from "next/link";
import { FormEvent, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { normalizeEmail } from "@/utils/auth-validation";
import { createClient, isSupabaseConfigured } from "@/utils/supabase/client";

export default function AccountPanel({ accountId, email, legacy }: {
  accountId: string;
  email: string;
  legacy: boolean;
}) {
  const router = useRouter();
  const supabase = useMemo(() => isSupabaseConfigured() ? createClient() : null, []);
  const [newEmail, setNewEmail] = useState("");
  const [message, setMessage] = useState("");
  const [loading, setLoading] = useState(false);

  async function updateEmail(event: FormEvent) {
    event.preventDefault();
    const normalized = normalizeEmail(newEmail);
    if (!supabase || !normalized) {
      setMessage("올바른 이메일을 입력해 주세요.");
      return;
    }
    setLoading(true);
    const result = await supabase.auth.updateUser(
      { email: normalized },
      { emailRedirectTo: `${window.location.origin}/auth/callback?next=/account` },
    );
    setLoading(false);
    setMessage(result.error
      ? "이메일 변경 요청을 처리하지 못했습니다."
      : "새 이메일로 인증 링크를 보냈습니다. 인증 후 복구 이메일이 변경됩니다.");
  }

  async function signOut() {
    if (!supabase) return;
    await supabase.auth.signOut();
    router.replace("/auth");
    router.refresh();
  }

  return (
    <main className="min-h-screen bg-[#f5f5f2] text-[#1c1c1a]">
      <nav className="border-b border-[#d8d8d2] bg-white">
        <div className="mx-auto flex max-w-3xl items-center justify-between px-6 py-4">
          <Link href="/dashboard" className="font-semibold">Comote</Link>
          <Link href="/dashboard" className="text-sm text-[#6a6a64]">내 컴퓨터</Link>
        </div>
      </nav>
      <section className="mx-auto max-w-3xl px-6 py-10">
        <h1 className="text-3xl font-semibold">계정</h1>
        <div className="mt-7 rounded-xl border border-[#d8d8d2] bg-white p-6">
          <dl className="grid gap-5 sm:grid-cols-2">
            <div><dt className="text-sm text-[#898983]">아이디</dt><dd className="mt-1 font-medium">{accountId}</dd></div>
            <div><dt className="text-sm text-[#898983]">복구 이메일</dt><dd className="mt-1 font-medium">{email}</dd></div>
          </dl>
          {legacy && (
            <form onSubmit={updateEmail} className="mt-7 border-t border-[#e5e5df] pt-6">
              <h2 className="font-medium">복구 이메일 등록</h2>
              <p className="mt-1 text-sm leading-6 text-[#6a6a64]">이전 버전에서 아이디만 만든 계정입니다. 비밀번호 재설정을 위해 실제 이메일을 연결하세요.</p>
              <div className="mt-4 flex flex-col gap-2 sm:flex-row">
                <input type="email" autoComplete="email" value={newEmail}
                  onChange={(event) => setNewEmail(event.target.value)} required
                  placeholder="email@example.com"
                  className="min-w-0 flex-1 rounded-lg border border-[#d6d6d1] px-3.5 py-3 outline-none focus:border-[#72726b]" />
                <button disabled={loading} className="rounded-lg bg-[#1f2937] px-5 py-3 text-white disabled:opacity-40">
                  {loading ? "요청 중…" : "이메일 연결"}
                </button>
              </div>
              {message && <p role="status" className="mt-3 text-sm text-[#5c5c57]">{message}</p>}
            </form>
          )}
        </div>
        <button type="button" onClick={signOut}
          className="mt-5 rounded-lg border border-[#d8d8d2] bg-white px-5 py-3 text-sm hover:bg-[#efefe9]">로그아웃</button>
      </section>
    </main>
  );
}
