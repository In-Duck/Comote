"use client";

import Link from "next/link";
import { FormEvent, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { isStrongEnoughPassword } from "@/utils/auth-validation";
import { createClient, isSupabaseConfigured } from "@/utils/supabase/client";

export default function ResetPasswordPanel() {
  const router = useRouter();
  const supabase = useMemo(() => isSupabaseConfigured() ? createClient() : null, []);
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [message, setMessage] = useState("");
  const [loading, setLoading] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!supabase) return;
    if (!isStrongEnoughPassword(password)) {
      setMessage("새 비밀번호는 8~128자로 입력해 주세요.");
      return;
    }
    if (password !== confirm) {
      setMessage("비밀번호 확인이 일치하지 않습니다.");
      return;
    }

    setLoading(true);
    const result = await supabase.auth.updateUser({ password });
    setLoading(false);
    if (result.error) {
      setMessage("재설정 링크가 만료되었거나 비밀번호를 변경하지 못했습니다.");
      return;
    }
    router.replace("/dashboard");
    router.refresh();
  }

  return (
    <main className="grid min-h-screen place-items-center bg-[#f5f5f2] px-5 text-[#1c1c1a]">
      <form onSubmit={submit} className="w-full max-w-sm rounded-2xl border border-[#d8d8d2] bg-white p-8 shadow-sm">
        <Link href="/" className="text-lg font-semibold">Comote</Link>
        <h1 className="mt-9 text-2xl font-semibold">새 비밀번호 설정</h1>
        <p className="mt-2 text-sm leading-6 text-[#6a6a64]">다른 서비스에서 사용하지 않는 비밀번호를 입력하세요.</p>
        <label className="mt-7 block text-sm">새 비밀번호
          <input type="password" autoComplete="new-password" minLength={8} maxLength={128}
            value={password} onChange={(event) => setPassword(event.target.value)} required
            className="mt-2 w-full rounded-lg border border-[#d6d6d1] px-3.5 py-3 outline-none focus:border-[#72726b]" />
        </label>
        <label className="mt-5 block text-sm">새 비밀번호 확인
          <input type="password" autoComplete="new-password" minLength={8} maxLength={128}
            value={confirm} onChange={(event) => setConfirm(event.target.value)} required
            className="mt-2 w-full rounded-lg border border-[#d6d6d1] px-3.5 py-3 outline-none focus:border-[#72726b]" />
        </label>
        {message && <p role="alert" className="mt-5 rounded-lg border border-[#e5c1bd] bg-[#fff7f5] p-3 text-sm text-[#8a3d35]">{message}</p>}
        <button disabled={!supabase || loading}
          className="mt-6 w-full rounded-lg bg-[#1f2937] px-5 py-3 font-medium text-white disabled:opacity-40">
          {loading ? "변경 중…" : "비밀번호 변경"}
        </button>
      </form>
    </main>
  );
}
