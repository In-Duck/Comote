
import { createClient } from "@/utils/supabase/server";
import Link from "next/link";
import { redirect } from "next/navigation";

export default async function Index() {
  const supabase = await createClient();

  const {
    data: { user },
  } = await supabase.auth.getUser();

  return (
    <div className="flex flex-col min-h-screen bg-black text-white relative overflow-hidden">
      {/* Background Gradients */}
      <div className="absolute top-0 left-0 w-full h-full overflow-hidden z-0 pointer-events-none">
        <div className="absolute top-[-20%] left-[20%] w-[800px] h-[800px] bg-blue-900/10 rounded-full blur-[120px]"></div>
        <div className="absolute bottom-[-20%] right-[10%] w-[600px] h-[600px] bg-purple-900/10 rounded-full blur-[100px]"></div>
      </div>

      {/* Header */}
      <header className="w-full z-50 border-b border-white/5 bg-black/50 backdrop-blur-md sticky top-0">
        <div className="max-w-7xl mx-auto px-6 h-16 flex items-center justify-between">
          <Link href="/" className="text-2xl font-bold bg-clip-text text-transparent bg-gradient-to-r from-blue-400 to-purple-500">
            Comote
          </Link>
          <div className="flex items-center gap-4">
            {user ? (
              <div className="flex items-center gap-4">
                <span className="text-sm text-gray-400 hidden sm:inline">{user.email}</span>
                <form action="/auth/signout" method="post">
                  <button className="text-sm text-gray-300 hover:text-white transition-colors">
                    로그아웃
                  </button>
                </form>
              </div>
            ) : (
              <Link href="/login" className="btn-secondary text-sm py-2 px-6 border-white/10 hover:border-white/20">
                로그인
              </Link>
            )}
          </div>
        </div>
      </header>

      {/* Hero Section */}
      <main className="flex-1 z-10">
        <section className="relative pt-32 pb-20 px-6">
          <div className="max-w-5xl mx-auto text-center">
            <div className="inline-block px-4 py-1.5 rounded-full border border-blue-500/20 bg-blue-500/10 text-blue-400 text-sm font-medium mb-8 animate-fade-in-up">
              🚀 차세대 원격 제어 솔루션
            </div>
            <h1 className="text-5xl md:text-7xl font-bold tracking-tight mb-6 bg-clip-text text-transparent bg-gradient-to-b from-white via-white to-gray-500 leading-tight">
              언제 어디서나,<br />
              <span className="text-blue-500">초저지연 원격 제어.</span>
            </h1>
            <p className="text-xl text-gray-400 max-w-2xl mx-auto mb-10 leading-relaxed font-light">
              WebRTC 기술로 끊김 없는 데스크톱 경험을 제공합니다.<br className="hidden sm:block" />
              P2P 보안, 4K/60fps 지원, 그리고 완벽한 제로 딜레이.
            </p>
            <div className="flex flex-col sm:flex-row items-center justify-center gap-4">
              <a href="#download" className="btn-primary w-full sm:w-auto text-lg px-8 py-4">
                지금 다운로드
              </a>
              <Link href="/login" className="btn-secondary w-full sm:w-auto text-lg px-8 py-4 bg-white/5 border border-white/10 hover:bg-white/10">
                무료로 시작하기
              </Link>
            </div>
          </div>
        </section>

        {/* Features Grid */}
        <section className="py-24 bg-black/50 border-y border-white/5">
          <div className="max-w-7xl mx-auto px-6">
            <div className="grid md:grid-cols-3 gap-8">
              <div className="glass-card p-8 hover:bg-white/5 transition-colors group">
                <div className="w-12 h-12 rounded-lg bg-blue-500/20 flex items-center justify-center mb-6 text-2xl group-hover:scale-110 transition-transform">
                  ⚡
                </div>
                <h3 className="text-xl font-semibold mb-3">압도적인 속도</h3>
                <p className="text-gray-400 leading-relaxed">
                  WebRTC 기반의 직접 P2P 연결로 게임이나 고성능 작업에서도 밀리초 단위의 응답 속도를 보장합니다.
                </p>
              </div>
              <div className="glass-card p-8 hover:bg-white/5 transition-colors group">
                <div className="w-12 h-12 rounded-lg bg-purple-500/20 flex items-center justify-center mb-6 text-2xl group-hover:scale-110 transition-transform">
                  🔒
                </div>
                <h3 className="text-xl font-semibold mb-3">완벽한 보안</h3>
                <p className="text-gray-400 leading-relaxed">
                  데이터는 서버를 거치지 않고 오직 기기 간에만 전송됩니다. 종단간 암호화로 사생활을 보호하세요.
                </p>
              </div>
              <div className="glass-card p-8 hover:bg-white/5 transition-colors group">
                <div className="w-12 h-12 rounded-lg bg-green-500/20 flex items-center justify-center mb-6 text-2xl group-hover:scale-110 transition-transform">
                  🎮
                </div>
                <h3 className="text-xl font-semibold mb-3">고성능 지원</h3>
                <p className="text-gray-400 leading-relaxed">
                  4K 해상도, 60fps 이상의 부드러운 화면, 그리고 멀티 모니터 지원까지. 하드웨어 가속으로 쾌적합니다.
                </p>
              </div>
            </div>
          </div>
        </section>

        {/* Download Section */}
        <section id="download" className="py-24 px-6 relative">
          <div className="max-w-4xl mx-auto text-center">
            <h2 className="text-3xl font-bold mb-12">다운로드</h2>
            <div className="grid md:grid-cols-2 gap-8">
              <div className="glass-card p-10 flex flex-col items-center hover:border-blue-500/30 transition-all">
                <div className="text-4xl mb-6">🖥️</div>
                <h3 className="text-2xl font-bold mb-2">Comote Host</h3>
                <p className="text-gray-400 mb-8">제어할 컴퓨터에 설치하세요.</p>
                <a href="https://github.com/In-Duck/Comote/releases/download/v0.1.0/Host.zip" className="btn-primary w-full max-w-xs text-center">
                  Windows용 다운로드 (Host)
                </a>
              </div>
              <div className="glass-card p-10 flex flex-col items-center hover:border-purple-500/30 transition-all">
                <div className="text-4xl mb-6">📱</div>
                <h3 className="text-2xl font-bold mb-2">Comote Viewer</h3>
                <p className="text-gray-400 mb-8">제어하는 기기(내 PC)에 설치하세요.</p>
                <a href="https://github.com/In-Duck/Comote/releases/download/v0.1.0/Viewer.zip" className="btn-secondary w-full max-w-xs text-center">
                  Windows용 다운로드 (Viewer)
                </a>
              </div>
            </div>
            <p className="mt-8 text-gray-500 text-sm">
              현재 버전: v0.1.0-alpha (개발 중)
            </p>
          </div>
        </section>
      </main>

      {/* Footer */}
      <footer className="border-t border-white/5 py-12 bg-black">
        <div className="max-w-7xl mx-auto px-6 flex flex-col md:flex-row justify-between items-center gap-6">
          <div className="flex items-center gap-2">
            <span className="text-xl font-bold bg-clip-text text-transparent bg-gradient-to-r from-blue-400 to-purple-500">
              Comote
            </span>
            <span className="text-gray-600 text-sm">© 2024</span>
          </div>
          <div className="flex gap-6 text-sm text-gray-500">
            <Link href="#" className="hover:text-white transition-colors">개인정보처리방침</Link>
            <Link href="#" className="hover:text-white transition-colors">이용약관</Link>
            <Link href="https://github.com/rkddl/Comote" target="_blank" className="hover:text-white transition-colors">GitHub</Link>
          </div>
        </div>
      </footer>
    </div>
  );
}
