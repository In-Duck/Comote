import Link from "next/link";

const features = [
  ["계정 기반 등록", "Client와 Manager에서 같은 계정으로 로그인하면 PC가 자동으로 등록됩니다."],
  ["원격 화면과 입력", "실시간 화면을 확인하고 키보드·마우스 입력을 안전한 세션으로 전달합니다."],
  ["파일과 작업", "허용된 경로 안에서 파일을 전송하고 필요한 프로그램 작업을 실행합니다."],
  ["검증된 업데이트", "Client가 HTTPS 패키지의 SHA-256을 확인한 뒤 설정을 보존해 업데이트합니다."],
];
const steps = [
  ["01", "Client 로그인", "원격 PC에서 Client를 실행하고 Comote 계정으로 로그인합니다."],
  ["02", "Manager 로그인", "관리할 PC에서 Manager를 열고 같은 계정으로 로그인합니다."],
  ["03", "PC 선택", "자동으로 나타난 PC를 선택해 바로 연결합니다."],
];
const devices = [
  ["OFFICE-PC-01", "1.6.0", "18%"],
  ["DESKTOP-02", "1.6.0", "11%"],
  ["WORKSTATION-03", "1.6.0", "24%"],
];

export default function HomePage() {
  const managerUrl = process.env.NEXT_PUBLIC_MANAGER_DOWNLOAD_URL || "/api/downloads/manager";
  const clientUrl = process.env.NEXT_PUBLIC_CLIENT_DOWNLOAD_URL || "/api/downloads/client";
  return (
    <main>
      <nav className="nav">
        <Link href="/" className="brand"><span>C</span>Comote</Link>
        <div className="navlinks"><a href="#features">기능</a><a href="#install">시작하기</a><a href="#docs">문서</a><Link href="/login" className="navcta">로그인</Link></div>
      </nav>
      <section className="hero">
        <div className="hero-copy">
          <p className="eyebrow">WINDOWS REMOTE MANAGEMENT</p>
          <h1>같은 계정으로,<br/><em>어디서든 내 PC.</em></h1>
          <p className="lead">IP 주소, 포트, VPN 주소를 입력하지 않아도 됩니다. Client와 Manager에 같은 계정으로 로그인하면 등록된 Windows PC를 한곳에서 확인하고 연결할 수 있습니다.</p>
          <div className="actions"><a className="primary" href={managerUrl}>Manager 다운로드</a><a className="secondary" href={clientUrl}>Client 다운로드</a></div>
          <div className="trust"><span>Client 포트포워딩 불필요</span><span>계정별 장치 분리</span><span>SHA-256 업데이트 검증</span></div>
        </div>
        <div className="product">
          <div className="product-top"><div><b>내 컴퓨터</b><small>Comote Manager</small></div><span className="healthy">3 online</span></div>
          <div className="metrics"><div><small>온라인</small><b>3</b></div><div><small>전체</small><b>3</b></div><div><small>업데이트</small><b>0</b></div></div>
          <div className="device-list">{devices.map(([name, version, cpu]) => <div className="device" key={name}><i/><div><b>{name}</b><small>Windows · CPU {cpu}</small></div><code>v{version}</code><span className="ok">온라인</span></div>)}</div>
        </div>
      </section>
      <section id="features" className="section">
        <div className="section-head"><p className="eyebrow">WHAT YOU NEED</p><h2>원격 관리에 필요한 기능을<br/>한곳에 모았습니다.</h2></div>
        <div className="feature-grid">{features.map(([title, body], index) => <article key={title}><span>0{index + 1}</span><h3>{title}</h3><p>{body}</p></article>)}</div>
      </section>
      <section id="install" className="install section">
        <div><p className="eyebrow">START IN THREE STEPS</p><h2>주소 설정 없이<br/>로그인으로 시작합니다.</h2></div>
        <div className="steps">{steps.map(([number, title, body]) => <div key={number}><span>{number}</span><h3>{title}</h3><p>{body}</p></div>)}</div>
      </section>
      <section id="docs" className="cta section">
        <div><p className="eyebrow">READY TO TEST</p><h2>Preview 16를 사용해 보세요.</h2><p>설치와 연결, 가상 HID, 운영 전 필수 보안 설정을 문서에서 확인할 수 있습니다.</p></div>
        <div className="actions"><a className="primary" href={managerUrl}>Manager 받기</a><a className="secondary" href="https://github.com/In-Duck/Comote#readme">사용 설명서</a></div>
      </section>
      <footer><Link href="/" className="brand"><span>C</span>Comote</Link><p>Remote Windows management for small teams.</p><small>© 2026 Comote</small></footer>
    </main>
  );
}