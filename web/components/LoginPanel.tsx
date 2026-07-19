"use client";

import { FormEvent, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { createClient, isSupabaseConfigured } from "@/utils/supabase/client";

const accountEmail = (accountId: string) =>
  `${accountId.trim().toLowerCase()}@accounts.kymote.app`;

function friendlyAuthError(message: string) {
  if (message === "Invalid login credentials") return "\uc544\uc774\ub514 \ub610\ub294 \ube44\ubc00\ubc88\ud638\ub97c \ud655\uc778\ud574 \uc8fc\uc138\uc694.";
  if (message.includes("already registered")) return "\uc774\ubbf8 \uc0ac\uc6a9 \uc911\uc778 \uc544\uc774\ub514\uc785\ub2c8\ub2e4.";
  return message;
}

export default function LoginPanel() {
  const router = useRouter();
  const configured = isSupabaseConfigured();
  const supabase = configured ? createClient() : null;
  const [accountId, setAccountId] = useState("");
  const [password, setPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [mode, setMode] = useState<"login" | "signup">("login");
  const [message, setMessage] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!supabase) return;
    const normalizedId = accountId.trim();
    if (!/^[a-zA-Z0-9._-]{4,32}$/.test(normalizedId)) {
      setMessage("\uc544\uc774\ub514\ub294 \uc601\ubb38, \uc22b\uc790, \ub9c8\uce68\ud45c, \ubc11\uc904, \ud558\uc774\ud508\uc73c\ub85c 4~32\uc790\uae4c\uc9c0 \uc0ac\uc6a9\ud560 \uc218 \uc788\uc2b5\ub2c8\ub2e4.");
      return;
    }
    setLoading(true);
    setMessage("");
    try {
      const email = accountEmail(normalizedId);
      const result = mode === "login"
        ? await supabase.auth.signInWithPassword({ email, password })
        : await supabase.auth.signUp({ email, password });
      if (result.error) {
        setMessage(friendlyAuthError(result.error.message));
      } else if (mode === "signup" && !result.data.session) {
        setMessage("\uc989\uc2dc \uac00\uc785 \uc124\uc815\uc774 \uc544\uc9c1 \uc801\uc6a9\ub418\uc9c0 \uc54a\uc558\uc2b5\ub2c8\ub2e4.");
      } else {
        router.push("/dashboard");
        router.refresh();
      }
    } catch {
      setMessage("\uacc4\uc815 \uc11c\ubc84\uc5d0 \uc5f0\uacb0\ud560 \uc218 \uc5c6\uc2b5\ub2c8\ub2e4. \uc7a0\uc2dc \ud6c4 \ub2e4\uc2dc \uc2dc\ub3c4\ud574 \uc8fc\uc138\uc694.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main className="grid min-h-screen bg-[#07111f] text-slate-100 lg:grid-cols-2">
      <section className="hidden border-r border-white/8 bg-[#0b1828] p-12 lg:flex lg:flex-col lg:justify-between">
        <Link href="/" className="flex items-center gap-3 text-lg font-bold"><span className="grid h-9 w-9 place-items-center rounded-xl bg-cyan-400 text-[#07111f]">C</span>Comote</Link>
        <div><p className="text-sm font-medium text-cyan-300">&#54616;&#45208;&#51032; &#44228;&#51221;, &#47784;&#46304; PC</p><h1 className="mt-4 text-5xl font-bold leading-tight">&#50612;&#46356;&#49436; &#47196;&#44536;&#51064;&#54644;&#46020;<br />&#45236; PC&#44032; &#44536;&#45824;&#47196;</h1><p className="mt-5 max-w-md leading-7 text-slate-400">&#53364;&#46972;&#51060;&#50616;&#53944;&#50752; &#47588;&#45768;&#51200;&#50640;&#49436; &#44057;&#51008; &#50500;&#51060;&#46356;&#47484; &#49324;&#50857;&#54616;&#47732; &#46321;&#47197;&#46108; PC &#47785;&#47197;&#44284; &#49345;&#53468;&#44032; &#51088;&#46041;&#51004;&#47196; &#46041;&#44592;&#54868;&#46121;&#45768;&#45796;.</p></div>
        <p className="text-xs text-slate-600">Comote Remote Fleet</p>
      </section>
      <section className="flex items-center justify-center p-6">
        <form onSubmit={submit} className="w-full max-w-md rounded-3xl border border-white/10 bg-[#0b1828] p-8 shadow-2xl">
          <Link href="/" className="mb-8 flex items-center gap-3 font-bold lg:hidden"><span className="grid h-9 w-9 place-items-center rounded-xl bg-cyan-400 text-[#07111f]">C</span>Comote</Link>
          <h2 className="text-2xl font-semibold">{mode === "login" ? "\uacc4\uc815 \ub85c\uadf8\uc778" : "\uc0c8 \uacc4\uc815 \ub9cc\ub4e4\uae30"}</h2>
          <p className="mt-2 text-sm text-slate-400">&#51064;&#51613; &#51208;&#52264; &#50630;&#51060; &#51593;&#49884; &#44032;&#51077;&#46104;&#45716; &#53580;&#49828;&#53944; &#47784;&#46300;&#51077;&#45768;&#45796;.</p>
          {!configured && <div className="mt-6 rounded-xl border border-amber-300/20 bg-amber-300/10 p-4 text-sm text-amber-200">&#44228;&#51221; &#49436;&#48260; &#50672;&#44208; &#51221;&#48372;&#44032; &#50500;&#51649; &#49444;&#51221;&#46104;&#51648; &#50506;&#50520;&#49845;&#45768;&#45796;.</div>}
          <label className="mt-7 block text-sm text-slate-300">&#50500;&#51060;&#46356;<input autoCapitalize="none" autoComplete="username" type="text" required minLength={4} maxLength={32} value={accountId} onChange={(e) => setAccountId(e.target.value)} className="mt-2 w-full rounded-xl border border-white/10 bg-[#07111f] px-4 py-3 outline-none focus:border-cyan-300" placeholder="ID (4-32 characters)" /></label>
          <label className="mt-4 block text-sm text-slate-300">&#48708;&#48128;&#48264;&#54840;<input type="password" required minLength={8} value={password} onChange={(e) => setPassword(e.target.value)} className="mt-2 w-full rounded-xl border border-white/10 bg-[#07111f] px-4 py-3 outline-none focus:border-cyan-300" placeholder="8+ characters" /></label>
          {message && <p className="mt-4 rounded-xl bg-white/5 p-3 text-sm text-slate-300">{message}</p>}
          <button disabled={!configured || loading} className="mt-6 w-full rounded-xl bg-cyan-400 px-5 py-3 font-semibold text-[#07111f] disabled:cursor-not-allowed disabled:opacity-40">{loading ? "\ucc98\ub9ac \uc911..." : mode === "login" ? "\ub85c\uadf8\uc778" : "\ubc14\ub85c \uac00\uc785"}</button>
          <button type="button" onClick={() => { setMode(mode === "login" ? "signup" : "login"); setMessage(""); }} className="mt-3 w-full rounded-xl px-5 py-3 text-sm text-slate-400 hover:bg-white/5">{mode === "login" ? "\ud14c\uc2a4\ud2b8 \uacc4\uc815 \ub9cc\ub4e4\uae30" : "\uc774\ubbf8 \uacc4\uc815\uc774 \uc788\ub098\uc694? \ub85c\uadf8\uc778"}</button>
        </form>
      </section>
    </main>
  );
}
