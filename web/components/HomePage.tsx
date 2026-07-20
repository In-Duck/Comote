import Link from "next/link";

const features = [
  ["Fleet overview", "실시간 썸네일, 온라인 상태, 버전과 업데이트 결과를 한 화면에서 확인합니다."],
  ["Remote operations", "선택한 PC에 파일을 보내고 프로그램 실행·종료 명령을 안전하게 전달합니다."],
  ["Reliable updates", "Client가 패키지를 직접 내려받아 SHA-256을 검증하고 설정을 보존한 채 재시작합니다."],
  ["Hub or local", "인터넷 계정 기반 Hub와 같은 네트워크의 직접 연결 모드를 상황에 맞게 사용합니다."],
];

const steps = [
  ["01", "Manager 설치", "관리할 PC에서 Manager를 실행하고 Hub를 엽니다."],
  ["02", "Client 등록", "대상 PC에 Client를 설치하고 Manager 주소와 등록 암호를 입력합니다."],
  ["03", "바로 제어", "Fleet 화면에서 PC를 선택해 원격 제어, 작업 실행 또는 업데이트를 시작합니다."],
];

export default function HomePage() {
  const managerUrl = process.env.NEXT_PUBLIC_MANAGER_DOWNLOAD_URL || "/api/downloads/manager";
  const clientUrl = process.env.NEXT_PUBLIC_CLIENT_DOWNLOAD_URL || "/api/downloads/client";

  return (
    <main>
      <nav className="nav">
        <Link href="/" className="brand"><span>C</span>Comote</Link>
        <div className="navlinks">
          <a href="#features">기능</a><a href="#install">설치</a><a href="#docs">문서</a>
          <Link href="/login" className="navcta">Manager 열기</Link>
        </div>
      </nav>

      <section className="hero">
        <div className="hero-copy">
          <p className="eyebrow">WINDOWS FLEET CONTROL · PREVIEW</p>
          <h1>멀리 있는 모든 PC를<br/><em>하나의 작업 공간에서.</em></h1>
          <p className="lead">실시간 화면, 원격 입력, 파일과 프로세스 작업, 안전한 원격 업데이트까지. Comote는 여러 Windows PC를 관리하는 가장 단순한 방법입니다.</p>
          <div className="actions">
            <a className="primary" href={managerUrl}>Manager 다운로드</a>
            <a className="secondary" href={clientUrl}>Client 다운로드</a>
          </div>
          <div className="trust"><span>✓ Client 포트포워딩 불필요</span><span>✓ SHA-256 검증</span><span>✓ 설정 보존 업데이트</span></div>
        </div>
        <div className="product">
          <div className="product-top"><div><b>Fleet overview</b><small>서울 오피스 · 8 devices</small></div><span className="healthy">● 7 online</span></div>
          <div className="metrics"><div><small>온라인</small><b>7</b></div><div><small>업데이트 필요</small><b>2</b></div><div><small>작업 중</small><b>1</b></div></div>
          <div className="device-list">
            {[
              ["디자인-01","1.6.0","최신","98%"],
              ["스튜디오-02","1.5.8","업데이트 가능","74%"],
              ["회의실-01","1.6.0","최신","46%"],
              ["렌더링-03","1.5.8","업데이트 68%","68%"],
            ].map(([name, version, state, cpu]) => (
              <div className="device" key={name}>
                <i/><div><b>{name}</b><small>Windows 11 · CPU {cpu}</small></div>
                <code>v{version}</code><span className={state.includes("최신") ? "ok" : "warn"}>{state}</span>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section id="features" className="section">
        <div className="section-head"><p className="eyebrow">ONE CONTROL PLANE</p><h2>관리 업무에 필요한 것만,<br/>빠르고 명확하게.</h2></div>
        <div className="feature-grid">{features.map(([title, body], index) => <article key={title}><span>0{index + 1}</span><h3>{title}</h3><p>{body}</p></article>)}</div>
      </section>

      <section id="install" className="install section">
        <div><p className="eyebrow">READY IN MINUTES</p><h2>설치부터 첫 연결까지<br/>세 단계면 충분합니다.</h2></div>
        <div className="steps">{steps.map(([n,title,body]) => <div key={n}><span>{n}</span><h3>{title}</h3><p>{body}</p></div>)}</div>
      </section>

      <section id="docs" className="cta section">
        <div><p className="eyebrow">START YOUR FLEET</p><h2>테스트할 준비가 되셨나요?</h2><p>샘플 설정과 로컬·Hub 테스트 절차를 README와 설치 가이드에서 확인하세요.</p></div>
        <div className="actions"><a className="primary" href={managerUrl}>Manager 받기</a><a className="secondary" href="https://github.com/In-Duck/Comote#readme">설치 문서</a></div>
      </section>

      <footer><Link href="/" className="brand"><span>C</span>Comote</Link><p>Remote Windows fleet control, designed for clarity.</p><small>© 2026 Comote</small></footer>
    </main>
  );
}
