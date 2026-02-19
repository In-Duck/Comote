
import type { Metadata } from "next";
import "./globals.css";

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
      <body className="bg-black text-white antialiased selection:bg-amber-500 selection:text-black">
        {children}
      </body>
    </html>
  );
}
