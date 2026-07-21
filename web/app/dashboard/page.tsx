import Link from "next/link";
import { displayAccount } from "@/utils/account-identity";
import { createClient } from "@/utils/supabase/server";

export const dynamic = "force-dynamic";

type Host = {
  host_id: string;
  host_name: string;
  resolution: string | null;
  cpu: number;
  ram: string | null;
  last_seen: string;
  agent_version: string | null;
};

export default async function DashboardPage() {
  const configured = Boolean(process.env.NEXT_PUBLIC_SUPABASE_URL && process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY);
  if (!configured) return <FleetDashboard hosts={[]} account="로컬 데모" demo />;

  const supabase = await createClient();
  const { data: { user } } = await supabase.auth.getUser();
  if (!user) return <LoginRequired />;

  const { data } = await supabase
    .from("hosts")
    .select("host_id,host_name,resolution,cpu,ram,last_seen,agent_version")
    .order("last_seen", { ascending: false });

  return <FleetDashboard hosts={(data ?? []) as Host[]} account={displayAccount(user.email ?? "Comote 계정")} />;
}

function FleetDashboard({ hosts, account, demo = false }: { hosts: Host[]; account: string; demo?: boolean }) {
  // One request-time value keeps every card consistent in this server render.
  // eslint-disable-next-line react-hooks/purity
  const now = Date.now();
  const isOnline = (host: Host) => now - new Date(host.last_seen).getTime() < 45_000;
  const online = hosts.filter(isOnline).length;

  return (
    <main className="min-h-screen bg-[#f5f5f2] text-[#1c1c1a]">
      <nav className="border-b border-[#d8d8d2] bg-white">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
          <Link href="/" className="font-semibold tracking-tight">Comote</Link>
          <div className="text-sm text-[#6a6a64]">{account} · <Link href="/login" className="underline underline-offset-4">계정</Link></div>
        </div>
      </nav>

      <section className="mx-auto max-w-6xl px-6 py-10">
        <div className="flex flex-wrap items-end justify-between gap-4 border-b border-[#d8d8d2] pb-6">
          <div><p className="text-sm text-[#6a6a64]">Manager</p><h1 className="mt-1 text-3xl font-semibold">내 컴퓨터</h1></div>
          <div className="flex gap-4 text-sm text-[#6a6a64]">{demo && <span>데모</span>}<span>온라인 {online}</span><span>전체 {hosts.length}</span></div>
        </div>

        {hosts.length === 0 ? (
          <div className="mt-8 border border-dashed border-[#c9c9c2] bg-white p-14 text-center">
            <h2 className="text-lg font-semibold">등록된 PC가 없습니다</h2>
            <p className="mt-2 text-sm text-[#6a6a64]">클라이언트를 실행하고 이 계정으로 로그인하면 자동으로 표시됩니다.</p>
          </div>
        ) : (
          <div className="mt-8 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {hosts.map((host) => (
              <article key={host.host_id} className="border border-[#d8d8d2] bg-white p-5">
                <div className="flex items-center justify-between"><h2 className="font-medium">{host.host_name}</h2><span className={`h-2 w-2 rounded-full ${isOnline(host) ? "bg-[#42845a]" : "bg-[#aaa9a2]"}`} /></div>
                <p className="mt-1 truncate text-xs text-[#898983]">{host.host_id}</p>
                <dl className="mt-5 grid grid-cols-2 gap-4 text-sm">
                  <Stat label="해상도" value={host.resolution || "-"} /><Stat label="CPU" value={`${host.cpu}%`} />
                  <Stat label="메모리" value={host.ram || "-"} /><Stat label="버전" value={host.agent_version || "-"} />
                </dl>
              </article>
            ))}
          </div>
        )}
      </section>
    </main>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return <div><dt className="text-[#898983]">{label}</dt><dd className="mt-1">{value}</dd></div>;
}

function LoginRequired() {
  return <main className="grid min-h-screen place-items-center bg-[#f5f5f2] p-6"><div className="text-center"><h1 className="text-2xl font-semibold">로그인이 필요합니다</h1><Link href="/login" className="mt-5 inline-block bg-[#1f2937] px-5 py-3 text-white">로그인</Link></div></main>;
}