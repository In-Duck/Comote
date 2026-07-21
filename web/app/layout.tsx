import type { Metadata } from "next";
import "./globals.css";

const siteUrl = process.env.NEXT_PUBLIC_SITE_URL ?? "https://comote-remote.dopum54.chatgpt.site";

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: "Comote | Windows 원격 관리",
  description: "같은 계정으로 로그인한 Windows PC를 어디서든 확인하고 제어하세요.",
  openGraph: {
    title: "Comote",
    description: "같은 계정으로, 어디서든 내 PC",
    images: [{ url: "/og.png", width: 1200, height: 630, alt: "Comote 원격 PC 관리" }],
  },
  twitter: {
    card: "summary_large_image",
    title: "Comote",
    description: "같은 계정으로, 어디서든 내 PC",
    images: ["/og.png"],
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="ko"><body>{children}</body></html>;
}