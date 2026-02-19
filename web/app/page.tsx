
export default function Home() {
  return (
    <div className="min-h-screen flex flex-col relative overflow-hidden selection:bg-amber-500 selection:text-black">
      {/* CRT Scanline Effect */}
      <div className="scanlines"></div>

      {/* Background Grid */}
      <div className="fixed inset-0 grid grid-cols-[repeat(40,1fr)] opacity-10 pointer-events-none">
        {Array.from({ length: 40 }).map((_, i) => (
          <div key={i} className="border-r border-amber-900/30 h-full"></div>
        ))}
      </div>

      {/* Navbar */}
      <nav className="sticky top-0 z-40 w-full px-6 py-4 flex justify-between items-center bg-black/90 backdrop-blur-md border-b border-amber-900/50">
        <div className="text-xl font-bold tracking-tighter text-amber-500 drop-shadow-[0_0_10px_rgba(245,158,11,0.5)]">
          KYMOTE <span className="text-xs align-top opacity-70">v1.2.1</span>
        </div>
        <div className="flex gap-4">
          <a href="/login" className="px-6 py-2 border border-amber-500/70 text-amber-500 text-sm font-bold hover:bg-amber-500 hover:text-black transition-all uppercase tracking-widest">
            [ 로그인 / 회원가입 ]
          </a>
        </div>
      </nav>

      <main className="flex-grow flex flex-col">
        {/* Hero Section */}
        <section className="flex-grow flex flex-col items-center justify-center text-center px-4 py-20">
          <div className="mb-4 text-green-500 font-bold tracking-[0.5em] animate-pulse">
            시스템 연결 초기화 중...
          </div>
          <h1 className="text-6xl md:text-9xl font-black mb-6 tracking-tighter text-glow transition-all hover:scale-105 duration-500 cursor-default">
            KYMOTE
          </h1>
          <p className="text-xl md:text-2xl mb-12 max-w-2xl opacity-80 leading-relaxed font-bold">
            초고성능 원격 제어 프로토콜<br />
            <span className="text-sm opacity-50 font-normal">v1.2.1 // 보안 // 속도 // 안정성</span>
          </p>

          <div className="flex flex-col md:flex-row gap-6 w-full max-w-2xl justify-center">
            <a href="https://github.com/In-Duck/Comote/releases/download/v1.2.1/ComoteHost_Setup.exe" className="btn-retro flex-1">
              [ 호스트 다운로드 ]
            </a>
            <a href="https://github.com/In-Duck/Comote/releases/download/v1.2.1/ComoteViewer_Setup.exe" className="btn-retro-green flex-1">
              [ 뷰어 다운로드 ]
            </a>
          </div>

          <p className="mt-8 text-xs opacity-40">
            설치 시 SHA256 무결성 검증이 필수적으로 수행됩니다.
          </p>
        </section>

        {/* Features Grid */}
        <section className="border-t border-amber-900/50 bg-black/50">
          <div className="max-w-7xl mx-auto grid grid-cols-1 md:grid-cols-3 divide-y md:divide-y-0 md:divide-x divide-amber-900/50">
            {/* Feature 1 */}
            <div className="p-10 hover:bg-amber-500/5 transition-colors group">
              <div className="text-4xl mb-4 opacity-50 group-hover:opacity-100 transition-opacity">
                01_보안
              </div>
              <h3 className="text-xl font-bold mb-2 text-green-500">암호화 터널링</h3>
              <p className="opacity-60 text-sm">
                AES-256 군사 등급 암호화 및 커스텀 핸드쉐이크 프로토콜 탑재. Login.dat 위변조 방지.
              </p>
            </div>

            {/* Feature 2 */}
            <div className="p-10 hover:bg-amber-500/5 transition-colors group">
              <div className="text-4xl mb-4 opacity-50 group-hover:opacity-100 transition-opacity">
                02_속도
              </div>
              <h3 className="text-xl font-bold mb-2 text-green-500">초저지연 전송</h3>
              <p className="opacity-60 text-sm">
                P2P WebRTC 다이렉트 스트리밍. 터미널급 반응속도와 최적화된 프레임 레이트.
              </p>
            </div>

            {/* Feature 3 */}
            <div className="p-10 hover:bg-amber-500/5 transition-colors group">
              <div className="text-4xl mb-4 opacity-50 group-hover:opacity-100 transition-opacity">
                03_제어
              </div>
              <h3 className="text-xl font-bold mb-2 text-green-500">완벽한 제어권</h3>
              <p className="opacity-60 text-sm">
                시스템 레벨 입력 주입. 클립보드 양방향 동기화. 멀티 모니터 완벽 지원.
              </p>
            </div>
          </div>
        </section>
      </main>

      {/* Footer */}
      <footer className="border-t border-amber-900/50 py-8 text-center text-xs opacity-40 hover:opacity-100 transition-opacity">
        <p>COPYRIGHT © 2026 KYMOTE PROTOCOL. ALL RIGHTS RESERVED.</p>
        <p className="mt-2 text-[10px]">시스템 ID: AION-ALPHA-01 // 모드: 가동 중</p>
      </footer>
    </div>
  );
}
