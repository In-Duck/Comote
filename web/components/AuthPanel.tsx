"use client";

import Link from "next/link";
import { FormEvent, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  isEmail,
  isStrongEnoughPassword,
  normalizeAccountId,
  normalizeEmail,
} from "@/utils/auth-validation";
import { createClient, isSupabaseConfigured } from "@/utils/supabase/client";

type Mode = "login" | "signup" | "find-id" | "forgot-password";
type Notice = { kind: "error" | "success" | "info"; text: string } | null;

const inputClass =
  "mt-2 w-full rounded-lg border border-[#d6d6d1] bg-white px-3.5 py-3 text-[#1c1c1a] outline-none transition focus:border-[#72726b] focus:ring-2 focus:ring-[#1f2937]/10";

export default function AuthPanel() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const supabase = useMemo(() => isSupabaseConfigured() ? createClient() : null, []);
  const [mode, setMode] = useState<Mode>("login");
  const [account, setAccount] = useState("");
  const [accountId, setAccountId] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [passwordConfirm, setPasswordConfirm] = useState("");
  const [loading, setLoading] = useState(false);
  const [notice, setNotice] = useState<Notice>(
    searchParams.get("error") === "invalid-link"
      ? { kind: "error", text: "인증 링크가 만료되었거나 올바르지 않습니다. 다시 요청해 주세요." }
      : null,
  );
  const [pendingEmail, setPendingEmail] = useState("");

  function changeMode(next: Mode) {
    setMode(next);
    setNotice(null);
    setPassword("");
    setPasswordConfirm("");
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!supabase) {
      setNotice({ kind: "error", text: "계정 서버 설정을 확인해 주세요." });
      return;
    }

    setLoading(true);
    setNotice(null);
    try {
      if (mode === "login") await login();
      if (mode === "signup") await signup();
      if (mode === "find-id") await findId();
      if (mode === "forgot-password") await requestPasswordReset();
    } catch {
      setNotice({ kind: "error", text: "계정 서버에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요." });
    } finally {
      setLoading(false);
    }
  }

  async function login() {
    if (!isStrongEnoughPassword(password)) {
      setNotice({ kind: "error", text: "아이디 또는 비밀번호를 확인해 주세요." });
      return;
    }

    if (isEmail(account)) {
      const normalizedEmail = normalizeEmail(account);
      if (!normalizedEmail) {
        setNotice({ kind: "error", text: "올바른 이메일을 입력해 주세요." });
        return;
      }
      const result = await supabase!.auth.signInWithPassword({ email: normalizedEmail, password });
      if (result.error) {
        setNotice({ kind: "error", text: authErrorMessage(result.error.message) });
        return;
      }
    } else {
      const normalizedId = normalizeAccountId(account);
      if (!normalizedId) {
        setNotice({ kind: "error", text: "아이디는 영문·숫자로 시작하는 4~32자여야 합니다." });
        return;
      }

      const response = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ account: normalizedId, password }),
      });
      const body = await response.json() as {
        error?: string;
        access_token?: string;
        refresh_token?: string;
      };

      if (!response.ok || !body.access_token || !body.refresh_token) {
        setNotice({
          kind: "error",
          text: body.error === "EMAIL_NOT_CONFIRMED"
            ? "이메일 인증을 완료한 뒤 로그인해 주세요."
            : "아이디 또는 비밀번호를 확인해 주세요.",
        });
        return;
      }

      const sessionResult = await supabase!.auth.setSession({
        access_token: body.access_token,
        refresh_token: body.refresh_token,
      });
      if (sessionResult.error) {
        setNotice({ kind: "error", text: "로그인 정보를 저장하지 못했습니다. 다시 시도해 주세요." });
        return;
      }
    }

    router.replace("/dashboard");
    router.refresh();
  }

  async function signup() {
    const normalizedId = normalizeAccountId(accountId);
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedId) {
      setNotice({ kind: "error", text: "아이디는 영문·숫자로 시작하는 4~32자여야 합니다." });
      return;
    }
    if (!normalizedEmail) {
      setNotice({ kind: "error", text: "비밀번호 복구에 사용할 실제 이메일을 입력해 주세요." });
      return;
    }
    if (!isStrongEnoughPassword(password)) {
      setNotice({ kind: "error", text: "비밀번호는 8~128자로 입력해 주세요." });
      return;
    }
    if (password !== passwordConfirm) {
      setNotice({ kind: "error", text: "비밀번호 확인이 일치하지 않습니다." });
      return;
    }

    const result = await supabase!.auth.signUp({
      email: normalizedEmail,
      password,
      options: {
        data: { account_id: normalizedId },
        emailRedirectTo: `${window.location.origin}/auth/callback?next=/dashboard`,
      },
    });

    if (result.error) {
      setNotice({ kind: "error", text: authErrorMessage(result.error.message) });
      return;
    }

    if (result.data.session) {
      router.replace("/dashboard");
      router.refresh();
      return;
    }

    setPendingEmail(normalizedEmail);
    setNotice({
      kind: "success",
      text: "가입 인증 메일을 보냈습니다. 메일의 인증 버튼을 누르면 가입이 완료됩니다.",
    });
  }

  async function resendConfirmation() {
    if (!supabase || !pendingEmail) return;
    setLoading(true);
    const result = await supabase.auth.resend({
      type: "signup",
      email: pendingEmail,
      options: { emailRedirectTo: `${window.location.origin}/auth/callback?next=/dashboard` },
    });
    setLoading(false);
    setNotice(result.error
      ? { kind: "error", text: "인증 메일을 다시 보내지 못했습니다. 잠시 후 시도해 주세요." }
      : { kind: "success", text: "인증 메일을 다시 보냈습니다. 스팸함도 확인해 주세요." });
  }

  async function findId() {
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) {
      setNotice({ kind: "error", text: "가입할 때 사용한 이메일을 입력해 주세요." });
      return;
    }

    await supabase!.auth.signInWithOtp({
      email: normalizedEmail,
      options: {
        shouldCreateUser: false,
        emailRedirectTo: `${window.location.origin}/auth/callback?next=/account`,
      },
    });
    setNotice({
      kind: "success",
      text: "가입된 이메일이라면 아이디 확인 링크를 보냈습니다. 메일함을 확인해 주세요.",
    });
  }

  async function requestPasswordReset() {
    const normalizedAccount = isEmail(account) ? normalizeEmail(account) : normalizeAccountId(account);
    if (!normalizedAccount) {
      setNotice({ kind: "error", text: "가입한 아이디 또는 이메일을 올바르게 입력해 주세요." });
      return;
    }

    await fetch("/api/auth/password-reset", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ account: normalizedAccount }),
    });
    setNotice({
      kind: "success",
      text: "가입된 계정이라면 비밀번호 재설정 메일을 보냈습니다. 메일함을 확인해 주세요.",
    });
  }

  return (
    <main className="grid min-h-screen place-items-center bg-[#f5f5f2] px-5 py-10 text-[#1c1c1a]">
      <section className="w-full max-w-[430px] rounded-2xl border border-[#d8d8d2] bg-white p-7 shadow-[0_18px_60px_rgba(31,41,55,0.08)] sm:p-9">
        <Link href="/" className="inline-flex items-center gap-2 text-lg font-semibold tracking-tight">
          <span className="grid h-8 w-8 place-items-center rounded-lg bg-[#1f2937] text-sm text-white">C</span>
          Comote
        </Link>

        <h1 className="mt-9 text-2xl font-semibold">{titleFor(mode)}</h1>
        <p className="mt-2 text-sm leading-6 text-[#6a6a64]">{descriptionFor(mode)}</p>

        <form onSubmit={submit} className="mt-7">
          {mode === "signup" ? (
            <>
              <Field label="아이디">
                <input className={inputClass} autoCapitalize="none" autoComplete="username"
                  value={accountId} onChange={(event) => setAccountId(event.target.value)}
                  placeholder="영문·숫자 4~32자" required />
              </Field>
              <Field label="복구 이메일" className="mt-5">
                <input className={inputClass} type="email" autoComplete="email"
                  value={email} onChange={(event) => setEmail(event.target.value)}
                  placeholder="email@example.com" required />
              </Field>
              <Field label="비밀번호" className="mt-5">
                <input className={inputClass} type="password" autoComplete="new-password"
                  minLength={8} maxLength={128} value={password}
                  onChange={(event) => setPassword(event.target.value)} required />
              </Field>
              <Field label="비밀번호 확인" className="mt-5">
                <input className={inputClass} type="password" autoComplete="new-password"
                  minLength={8} maxLength={128} value={passwordConfirm}
                  onChange={(event) => setPasswordConfirm(event.target.value)} required />
              </Field>
            </>
          ) : mode === "find-id" ? (
            <Field label="가입 이메일">
              <input className={inputClass} type="email" autoComplete="email"
                value={email} onChange={(event) => setEmail(event.target.value)}
                placeholder="email@example.com" required />
            </Field>
          ) : (
            <>
              <Field label={mode === "login" ? "아이디 또는 이메일" : "아이디 또는 이메일"}>
                <input className={inputClass} autoCapitalize="none" autoComplete="username"
                  value={account} onChange={(event) => setAccount(event.target.value)}
                  placeholder="ID 또는 email@example.com" required />
              </Field>
              {mode === "login" && (
                <Field label="비밀번호" className="mt-5">
                  <input className={inputClass} type="password" autoComplete="current-password"
                    minLength={8} maxLength={128} value={password}
                    onChange={(event) => setPassword(event.target.value)} required />
                </Field>
              )}
            </>
          )}

          {notice && (
            <p role="status" className={`mt-5 rounded-lg border p-3 text-sm leading-6 ${
              notice.kind === "error"
                ? "border-[#e5c1bd] bg-[#fff7f5] text-[#8a3d35]"
                : "border-[#c8d8c9] bg-[#f5faf5] text-[#416445]"
            }`}>{notice.text}</p>
          )}

          <button type="submit" disabled={!supabase || loading}
            className="mt-6 w-full rounded-lg bg-[#1f2937] px-5 py-3 font-medium text-white transition hover:bg-[#111827] disabled:cursor-not-allowed disabled:opacity-40">
            {loading ? "처리 중…" : submitLabelFor(mode)}
          </button>
        </form>

        {pendingEmail && mode === "signup" && (
          <button type="button" disabled={loading} onClick={resendConfirmation}
            className="mt-3 w-full rounded-lg border border-[#d8d8d2] px-5 py-2.5 text-sm hover:bg-[#f5f5f2]">
            인증 메일 다시 보내기
          </button>
        )}

        <AuthNavigation mode={mode} changeMode={changeMode} />
      </section>
    </main>
  );
}

function Field({ label, className = "", children }: {
  label: string;
  className?: string;
  children: React.ReactNode;
}) {
  return <label className={`block text-sm text-[#494945] ${className}`}>{label}{children}</label>;
}

function AuthNavigation({ mode, changeMode }: { mode: Mode; changeMode: (mode: Mode) => void }) {
  if (mode !== "login") {
    return <button type="button" onClick={() => changeMode("login")}
      className="mt-5 w-full px-4 py-2 text-sm text-[#5c5c57] hover:text-[#1f2937]">로그인으로 돌아가기</button>;
  }
  return (
    <div className="mt-6 grid grid-cols-3 border-t border-[#e5e5df] pt-5 text-center text-sm text-[#5c5c57]">
      <button type="button" onClick={() => changeMode("signup")} className="hover:text-[#1f2937]">계정 만들기</button>
      <button type="button" onClick={() => changeMode("find-id")} className="border-x border-[#e5e5df] hover:text-[#1f2937]">아이디 찾기</button>
      <button type="button" onClick={() => changeMode("forgot-password")} className="hover:text-[#1f2937]">비밀번호 재설정</button>
    </div>
  );
}

function titleFor(mode: Mode) {
  if (mode === "signup") return "계정 만들기";
  if (mode === "find-id") return "아이디 찾기";
  if (mode === "forgot-password") return "비밀번호 재설정";
  return "로그인";
}

function descriptionFor(mode: Mode) {
  if (mode === "signup") return "아이디와 복구 이메일을 등록하면 Client와 Manager에서 같은 계정을 사용할 수 있습니다.";
  if (mode === "find-id") return "이메일 인증 후 계정 페이지에서 가입한 아이디를 확인합니다.";
  if (mode === "forgot-password") return "가입한 아이디 또는 이메일로 재설정 링크를 받습니다.";
  return "Client와 Manager에서 사용한 계정으로 로그인하세요.";
}

function submitLabelFor(mode: Mode) {
  if (mode === "signup") return "가입하고 인증 메일 받기";
  if (mode === "find-id") return "아이디 확인 메일 받기";
  if (mode === "forgot-password") return "재설정 메일 받기";
  return "로그인";
}

function authErrorMessage(message: string) {
  const normalized = message.toLowerCase();
  if (normalized.includes("invalid login credentials")) return "아이디 또는 비밀번호를 확인해 주세요.";
  if (normalized.includes("email not confirmed")) return "이메일 인증을 완료한 뒤 로그인해 주세요.";
  if (normalized.includes("already registered") || normalized.includes("user already registered")) {
    return "이미 가입된 이메일입니다. 로그인하거나 비밀번호를 재설정해 주세요.";
  }
  if (normalized.includes("database error") || normalized.includes("duplicate")) {
    return "이미 사용 중인 아이디이거나 이메일입니다.";
  }
  if (normalized.includes("rate limit")) return "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.";
  return "계정 요청을 처리하지 못했습니다. 입력 내용을 확인해 주세요.";
}
