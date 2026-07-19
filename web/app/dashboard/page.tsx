import Link from "next/link";
import { createClient } from "@/utils/supabase/server";

export const dynamic = "force-dynamic";

type Host = { host_id: string; host_name: string; resolution: string | null; cpu: number; ram: string | null; last_seen: string; agent_version: string | null; };

export default async function DashboardPage() {
  const configured = Boolean(process.env.NEXT_PUBLIC_SUPABASE_URL && process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY);
  if (!configured) return <SetupRequired />;
  const supabase = await createClient();
  const { data: { user } } = await supabase.auth.getUser();
  if (!user) return <LoginRequired />;

  const { data } = await supabase.from("hosts").select("host_id,host_name,resolution,cpu,ram,last_seen,agent_version").order("last_seen", { ascending: false });
  const hosts = (data ?? []) as Host[];
  const now = Date.now();
  const online = hosts.filter((host) => now - new Date(host.last_seen).getTime() < 45_000).length;

  return (
    <main className="min-h-screen bg-[#07111f] text-slate-100">
      <nav className="border-b border-white/8 bg-[#0b1828]"><div className="mx-auto flex max-w-7xl items-center justify-between px-6 py-4"><Link href="/" className="flex items-center gap-3 font-bold"><span className="grid h-9 w-9 place-items-center rounded-xl bg-cyan-400 text-[#07111f]">C</span>Comote</Link><div className="text-sm text-slate-400">{user.email} · <Link href="/login" className="text-cyan-300">계정</Link></div></div></nav>
      <section className="mx-auto max-w-7xl px-6 py-8">
        <div className="flex flex-wrap items-end justify-between gap-4"><div><p className="text-sm text-cyan-300">Fleet dashboard</p><h1 className="mt-1 text-3xl font-semibold">내 컴퓨터</h1></div><div className="flex gap-3 text-sm"><span className="rounded-xl bg-emerald-400/10 px-4 py-2 text-emerald-300">온라인 {online}</span><span className="rounded-xl bg-white/5 px-4 py-2 text-slate-300">전체 {hosts.length}</span></div></div>
        {hosts.length === 0 ? <div className="mt-8 rounded-3xl border border-dashed border-white/15 p-16 text-center"><h2 className="text-xl font-semibold">등록된 PC가 없습니다</h2><p className="mt-2 text-slate-400">클라이언트를 설치하고 이 계정으로 로그인하면 자동으로 표시됩니다.</p></div> : (
          <div className="mt-8 grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">{hosts.map((host) => { const isOnline = now - new Date(host.last_seen).getTime() < 45_000; return <article key={host.host_id} className="rounded-2xl border border-white/8 bg-[#0b1828] p-5"><div className="flex items-center justify-between"><h2 className="font-semibold">{host.host_name}</h2><span className={`h-2.5 w-2.5 rounded-full ${isOnline ? "bg-emerald-400" : "bg-slate-600"}`} /></div><p className="mt-1 truncate text-xs text-slate-600">{host.host_id}</p><dl className="mt-5 grid grid-cols-2 gap-3 text-sm"><div><dt className="text-slate-500">해상도</dt><dd className="mt-1">{host.resolution || "-"}</dd></div><div><dt className="text-slate-500">CPU</dt><dd className="mt-1">{host.cpu}%</dd></div><div><dt className="text-slate-500">메모리</dt><dd className="mt-1">{host.ram || "-"}</dd></div><div><dt className="text-slate-500">버전</dt><dd className="mt-1">{host.agent_version || "-"}</dd></div></dl></article>; })}</div>
        )}
      </section>
    </main>
  );
}

function SetupRequired() { return <main className="grid min-h-screen place-items-center bg-[#07111f] p-6 text-slate-100"><div className="max-w-lg rounded-3xl border border-amber-300/20 bg-amber-300/10 p-8"><h1 className="text-xl font-semibold">계정 서버 설정 필요</h1><p className="mt-3 leading-7 text-amber-100/70">Supabase 주소와 공개 키를 설정하면 계정별 PC 목록이 이 화면에 표시됩니다.</p></div></main>; }
function LoginRequired() { return <main className="grid min-h-screen place-items-center bg-[#07111f] p-6 text-slate-100"><div className="text-center"><h1 className="text-2xl font-semibold">로그인이 필요합니다</h1><Link href="/login" className="mt-5 inline-block rounded-xl bg-cyan-400 px-5 py-3 font-semibold text-[#07111f]">로그인하기</Link></div></main>; }