
import type { Metadata } from "next";
import { Inter } from "next/font/google";
import "./globals.css";

const inter = Inter({ subsets: ["latin"] });

export const metadata: Metadata = {
  title: "KYMOTE - The Golden Standard of Remote Control",
  description: "Experience ultra-low latency, premium remote control with KYMOTE. Designed for perfection.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body className={`${inter.className} bg-black text-white antialiased selection:bg-amber-500 selection:text-black`}>
        {children}
      </body>
    </html>
  );
}
