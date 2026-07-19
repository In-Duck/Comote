import Link from "next/link";

const features = [
  ["계정 기반 기기 관리", "클라이언트를 한 계정에 등록하면 어느 매니저에서 로그인해도 같은 PC 목록을 확인합니다."],
  ["실시간 썸네일 관제", "온라인 상태와 최신 화면을 한 화면에서 확인하고 필요한 PC만 즉시 제어합니다."],
  ["일괄 원격 작업", "선택한 여러 PC에 파일을 전송하고 프로그램 실행·종료 작업을 한 번에 보냅니다."],
];
const sampleHosts = ["사무실 01", "사무실 02", "작업실 01", "렌더팜 03", "창고 01", "테스트 02"];

export default function HomePage() {
  const managerUrl = process.env.NEXT_PUBLIC_MANAGER_DOWNLOAD_URL || "/api/downloads/manager";
  const clientUrl = process.env.NEXT_PUBLIC_CLIENT_DOWNLOAD_URL || "/api/downloads/client";
  return (
    <main className="min-h-screen bg-[#07111f] text-slate-100">
      <nav className="mx-auto flex max-w-7xl items-center justify-between px-6 py-5">
        <Link href="/" className="flex items-center gap-3 text-lg font-bold"><span className="grid h-9 w-9 place-items-center rounded-xl bg-cyan-400 text-[#07111f]">C</span>Comote</Link>
        <div className="flex items-center gap-3"><Link href="/login" className="rounded-xl px-4 py-2 text-sm text-slate-300 hover:bg-white/5">로그인</Link><Link href="/login" className="rounded-xl bg-cyan-400 px-4 py-2 text-sm font-semibold text-[#07111f] hover:bg-cyan-300">시작하기</Link></div>
      </nav>
      <section className="mx-auto grid max-w-7xl gap-14 px-6 pb-24 pt-16 lg:grid-cols-[1.05fr_.95fr] lg:items-center">
        <div>
          <div className="mb-5 inline-flex rounded-full border border-cyan-300/20 bg-cyan-300/10 px-3 py-1 text-xs font-medium text-cyan-200">여러 대의 Windows PC를 한 계정에서</div>
          <h1 className="max-w-3xl text-5xl font-bold leading-[1.08] tracking-tight md:text-7xl">멀리 있는 모든 PC를<span className="block text-cyan-300">한 화면에서 관리하세요.</span></h1>
          <p className="mt-6 max-w-2xl text-lg leading-8 text-slate-400">Comote는 실시간 썸네일 관제, 원격 제어, 파일 전송과 프로그램 일괄 실행을 하나의 관리 화면으로 묶습니다.</p>
          <div className="mt-9 flex flex-wrap gap-3"><Link href="/login" className="rounded-xl bg-cyan-400 px-6 py-3 font-semibold text-[#07111f] hover:bg-cyan-300">무료 계정 만들기</Link><a href="#downloads" className="rounded-xl border border-white/10 bg-white/5 px-6 py-3 font-semibold hover:bg-white/10">설치 파일 보기</a></div>
          <div className="mt-8 flex flex-wrap gap-x-6 gap-y-2 text-sm text-slate-500"><span>클라이언트 포트포워딩 불필요</span><span>계정별 기기 분리</span><span>암호화된 원격 연결</span></div>
        </div>
        <div className="rounded-3xl border border-white/10 bg-[#0d1b2c] p-4 shadow-2xl shadow-cyan-950/40">
          <div className="mb-4 flex items-center justify-between px-2"><div><p className="text-sm font-semibold">내 PC</p><p className="text-xs text-slate-500">온라인 6대 · 전체 8대</p></div><span className="rounded-lg bg-emerald-400/10 px-3 py-1 text-xs text-emerald-300">연결 정상</span></div>
          <div className="grid grid-cols-2 gap-3">{sampleHosts.map((name, index) => <div key={name} className="overflow-hidden rounded-2xl border border-white/8 bg-[#081321]"><div className={`h-24 bg-gradient-to-br ${index % 3 === 0 ? "from-blue-800 to-cyan-700" : index % 3 === 1 ? "from-slate-700 to-blue-900" : "from-indigo-900 to-cyan-900"}`} /><div className="flex items-center justify-between p-3"><span className="text-xs font-medium">{name}</span><span className="h-2 w-2 rounded-full bg-emerald-400" /></div></div>)}</div>
        </div>
      </section>
      <section className="border-y border-white/8 bg-white/[0.02]"><div className="mx-auto grid max-w-7xl gap-4 px-6 py-16 md:grid-cols-3">{features.map(([title, description], index) => <article key={title} className="rounded-2xl border border-white/8 bg-[#0b1828] p-6"><span className="text-xs font-bold text-cyan-300">0{index + 1}</span><h2 className="mt-4 text-xl font-semibold">{title}</h2><p className="mt-3 leading-7 text-slate-400">{description}</p></article>)}</div></section>
      <section id="downloads" className="mx-auto max-w-7xl px-6 py-20"><div className="rounded-3xl border border-cyan-300/15 bg-gradient-to-r from-cyan-400/10 to-blue-500/10 p-8 md:flex md:items-center md:justify-between"><div><h2 className="text-2xl font-semibold">Comote 설치하기</h2><p className="mt-2 text-slate-400">매니저와 클라이언트를 설치한 뒤 같은 계정으로 로그인하세요.</p></div><div className="mt-6 flex flex-wrap gap-3 md:mt-0"><a href={managerUrl} className="rounded-xl bg-white px-5 py-3 font-semibold text-[#07111f]">매니저 다운로드</a><a href={clientUrl} className="rounded-xl border border-white/15 bg-white/5 px-5 py-3 font-semibold">클라이언트 다운로드</a></div></div></section>
    </main>
  );
}