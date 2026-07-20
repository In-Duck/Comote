import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Comote | Windows Fleet Control",
  description: "여러 Windows PC의 화면, 원격 작업과 업데이트를 한곳에서 관리하세요.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="ko"><body>{children}</body></html>;
}
