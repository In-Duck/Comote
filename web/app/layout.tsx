
import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Comote | 다중 PC 원격 관제",
  description: "여러 대의 Windows PC를 하나의 계정에서 관제하고 원격 제어하세요.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="ko">
      <body className="bg-[#07111f] text-slate-100 antialiased selection:bg-cyan-300 selection:text-[#07111f]">
        {children}
      </body>
    </html>
  );
}
