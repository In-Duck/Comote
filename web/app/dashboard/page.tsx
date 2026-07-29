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

type ConnectionEvent = {
  id: number;
  host_id: string | null;
  source: string;
  event_type: string;
  severity: "info" | "warning" | "critical";
  created_at: string;
};

export default async function DashboardPage() {
  const configured = Boolean(
    process.env.NEXT_PUBLIC_SUPABASE_URL &&
      process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY,
  );
  if (!configured) return <LoginRequired />;

  const supabase = await createClient();
  const {
    data: { user },
  } = await supabase.auth.getUser();
  if (!user) return <LoginRequired />;

  const [hostsResult, eventsResult] = await Promise.all([
    supabase
      .from("hosts")
      .select(
        "host_id,host_name,resolution,cpu,ram,last_seen,agent_version",
      )
      .order("last_seen", { ascending: false }),
    supabase
      .from("connection_events")
      .select("id,host_id,source,event_type,severity,created_at")
      .gte(
        "created_at",
        // eslint-disable-next-line react-hooks/purity
        new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString(),
      )
      .order("created_at", { ascending: false })
      .limit(100),
  ]);

  return (
    <FleetDashboard
      hosts={(hostsResult.data ?? []) as Host[]}
      events={(eventsResult.data ?? []) as ConnectionEvent[]}
      account={displayAccount(user.email ?? "Comote 계정")}
      turnConfigured={Boolean(
        process.env.CLOUDFLARE_TURN_KEY_ID &&
          process.env.CLOUDFLARE_TURN_API_TOKEN,
      )}
      telemetryReady={!eventsResult.error}
    />
  );
}

function FleetDashboard({
  hosts,
  events,
  account,
  turnConfigured,
  telemetryReady,
}: {
  hosts: Host[];
  events: ConnectionEvent[];
  account: string;
  turnConfigured: boolean;
  telemetryReady: boolean;
}) {
  // One request-time value keeps the server-rendered cards consistent.
  // eslint-disable-next-line react-hooks/purity
  const now = Date.now();
  const isOnline = (host: Host) =>
    now - new Date(host.last_seen).getTime() < 75_000;
  const online = hosts.filter(isOnline).length;
  const offline = hosts.length - online;
  const incidents = events.filter(
    (event) => event.severity !== "info",
  ).length;
  const recovered = events.filter(
    (event) => event.event_type === "recovered",
  ).length;
  const onlineRate =
    hosts.length > 0 ? Math.round((online / hosts.length) * 100) : 100;

  return (
    <main className="min-h-screen bg-[#f4f4f1] text-[#20201d]">
      <nav className="border-b border-[#d8d8d2] bg-white">
        <div className="mx-auto flex max-w-7xl items-center justify-between px-6 py-4">
          <Link href="/" className="font-semibold tracking-tight">
            Comote
          </Link>
          <div className="text-sm text-[#6a6a64]">
            {account} ·{" "}
            <Link href="/account" className="underline underline-offset-4">
              계정
            </Link>
          </div>
        </div>
      </nav>

      <section className="mx-auto max-w-7xl px-6 py-9">
        <div className="flex flex-wrap items-end justify-between gap-5">
          <div>
            <p className="text-sm text-[#6a6a64]">운영 현황 · 최근 24시간</p>
            <h1 className="mt-1 text-3xl font-semibold">연결 상태</h1>
          </div>
          <div
            className={`border px-4 py-2 text-sm ${
              turnConfigured
                ? "border-[#b7d8c0] bg-[#eff8f1] text-[#326543]"
                : "border-[#e6c58a] bg-[#fff8e9] text-[#7b5b1e]"
            }`}
          >
            {turnConfigured
              ? "TURN 자동 우회 준비됨"
              : "직접 연결 전용 · TURN 계정 등록 필요"}
          </div>
        </div>

        <div className="mt-7 grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
          <Metric label="전체 PC" value={hosts.length} />
          <Metric label="온라인" value={online} tone="good" />
          <Metric label="오프라인" value={offline} tone={offline ? "warn" : "normal"} />
          <Metric label="온라인율" value={`${onlineRate}%`} />
          <Metric label="복구 / 장애" value={`${recovered} / ${incidents}`} />
        </div>

        {!telemetryReady && (
          <div className="mt-5 border border-[#e6c58a] bg-[#fff8e9] p-4 text-sm text-[#7b5b1e]">
            장애 기록 테이블을 준비 중입니다. 최신 데이터베이스 변경을
            적용하면 복구 이력이 표시됩니다.
          </div>
        )}

        <div className="mt-8 grid gap-6 lg:grid-cols-[1.5fr_1fr]">
          <section>
            <div className="mb-3 flex items-center justify-between">
              <h2 className="font-semibold">컴퓨터</h2>
              <span className="text-xs text-[#777770]">
                마지막 보고 75초 이내를 온라인으로 표시
              </span>
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              {hosts.map((host) => (
                <article
                  key={host.host_id}
                  className="border border-[#d8d8d2] bg-white p-5"
                >
                  <div className="flex items-center justify-between">
                    <h3 className="font-medium">{host.host_name}</h3>
                    <span
                      className={`h-2.5 w-2.5 rounded-full ${
                        isOnline(host) ? "bg-[#42845a]" : "bg-[#aaa9a2]"
                      }`}
                    />
                  </div>
                  <p className="mt-1 truncate text-xs text-[#898983]">
                    {host.host_id}
                  </p>
                  <dl className="mt-5 grid grid-cols-2 gap-4 text-sm">
                    <Stat label="해상도" value={host.resolution || "-"} />
                    <Stat label="CPU" value={`${host.cpu}%`} />
                    <Stat label="메모리" value={host.ram || "-"} />
                    <Stat label="버전" value={host.agent_version || "-"} />
                  </dl>
                  <p className="mt-4 text-xs text-[#898983]">
                    {formatAge(now, host.last_seen)}
                  </p>
                </article>
              ))}
              {hosts.length === 0 && (
                <div className="border border-dashed border-[#c9c9c2] bg-white p-12 text-center text-sm text-[#6a6a64] sm:col-span-2">
                  등록된 PC가 없습니다. Client에서 같은 계정으로
                  로그인하면 자동으로 표시됩니다.
                </div>
              )}
            </div>
          </section>

          <section>
            <div className="mb-3 flex items-center justify-between">
              <h2 className="font-semibold">최근 장애와 복구</h2>
              <span className="text-xs text-[#777770]">{events.length}건</span>
            </div>
            <div className="border border-[#d8d8d2] bg-white">
              {events.slice(0, 20).map((event) => (
                <div
                  key={event.id}
                  className="border-b border-[#ecece8] px-4 py-3 last:border-0"
                >
                  <div className="flex items-center justify-between gap-4">
                    <span className="text-sm font-medium">
                      {eventLabel(event.event_type)}
                    </span>
                    <span
                      className={`text-xs ${
                        event.severity === "critical"
                          ? "text-[#a63e32]"
                          : event.severity === "warning"
                            ? "text-[#9a6a1d]"
                            : "text-[#54705c]"
                      }`}
                    >
                      {formatAge(now, event.created_at)}
                    </span>
                  </div>
                  <p className="mt-1 text-xs text-[#898983]">
                    {event.host_id || event.source}
                  </p>
                </div>
              ))}
              {events.length === 0 && (
                <p className="p-8 text-center text-sm text-[#777770]">
                  최근 24시간에 기록된 장애가 없습니다.
                </p>
              )}
            </div>
          </section>
        </div>
      </section>
    </main>
  );
}

function Metric({
  label,
  value,
  tone = "normal",
}: {
  label: string;
  value: string | number;
  tone?: "normal" | "good" | "warn";
}) {
  return (
    <div className="border border-[#d8d8d2] bg-white p-5">
      <p className="text-xs text-[#777770]">{label}</p>
      <p
        className={`mt-2 text-2xl font-semibold ${
          tone === "good"
            ? "text-[#326543]"
            : tone === "warn"
              ? "text-[#9a6a1d]"
              : ""
        }`}
      >
        {value}
      </p>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-[#898983]">{label}</dt>
      <dd className="mt-1">{value}</dd>
    </div>
  );
}

function formatAge(now: number, value: string) {
  const seconds = Math.max(
    0,
    Math.floor((now - new Date(value).getTime()) / 1000),
  );
  if (seconds < 60) return `${seconds}초 전`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}분 전`;
  return `${Math.floor(seconds / 3600)}시간 전`;
}

function eventLabel(type: string) {
  const labels: Record<string, string> = {
    connected: "연결됨",
    disconnected: "연결 끊김",
    reconnecting: "재접속 중",
    recovered: "자동 복구됨",
    direct: "직접 연결",
    relayed: "TURN 우회 연결",
    degraded: "연결 품질 저하",
    turn_unavailable: "TURN 사용 불가",
    update_started: "업데이트 시작",
    update_succeeded: "업데이트 완료",
    update_failed: "업데이트 실패",
    update_rolled_back: "이전 버전 복원",
  };
  return labels[type] || type;
}

function LoginRequired() {
  return (
    <main className="grid min-h-screen place-items-center bg-[#f5f5f2] p-6">
      <div className="text-center">
        <h1 className="text-2xl font-semibold">로그인이 필요합니다</h1>
        <Link
          href="/login"
          className="mt-5 inline-block bg-[#1f2937] px-5 py-3 text-white"
        >
          로그인
        </Link>
      </div>
    </main>
  );
}
