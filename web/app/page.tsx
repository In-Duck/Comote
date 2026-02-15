
import Image from "next/image";
import Link from "next/link";

export default function Home() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center bg-gradient-to-b from-zinc-900 to-black text-white">
      <main className="flex flex-col items-center gap-8 p-12 text-center">
        <div className="relative flex place-items-center">
          <h1 className="text-6xl font-bold tracking-tighter sm:text-7xl">
            Comote
          </h1>
        </div>

        <p className="max-w-xl text-lg text-zinc-400">
          Seamless Remote Control Service. <br />
          Control your devices from anywhere with low latency and high quality.
        </p>

        <div className="flex gap-4 mt-8">
          <Link
            href="/login"
            className="rounded-full bg-white px-8 py-3 text-sm font-semibold text-black transition-colors hover:bg-zinc-200"
          >
            Get Started
          </Link>
          <a
            href="https://github.com/your-repo/comote"
            target="_blank"
            rel="noopener noreferrer"
            className="rounded-full border border-zinc-700 px-8 py-3 text-sm font-semibold transition-colors hover:bg-zinc-800"
          >
            Learn More
          </a>
        </div>
      </main>

      <footer className="absolute bottom-4 text-xs text-zinc-600">
        &copy; {new Date().getFullYear()} Comote. All rights reserved.
      </footer>
    </div>
  );
}
