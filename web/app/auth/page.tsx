import { Suspense } from "react";
import AuthPanel from "@/components/AuthPanel";

export default function AuthPage() {
  return (
    <Suspense fallback={<main className="min-h-screen bg-[#f5f5f2]" />}>
      <AuthPanel />
    </Suspense>
  );
}
