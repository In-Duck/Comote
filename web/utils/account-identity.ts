const accountIdPattern = /^[a-z0-9._-]{4,32}$/i;
const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function normalizeAccountEmail(value: string): string | null {
  const normalized = value.trim().toLowerCase();
  if (!normalized) return null;
  if (normalized.includes("@")) return emailPattern.test(normalized) ? normalized : null;
  return accountIdPattern.test(normalized)
    ? `${normalized}@accounts.kymote.app`
    : null;
}

export function displayAccount(email: string): string {
  return email.endsWith("@accounts.kymote.app")
    ? email.slice(0, -"@accounts.kymote.app".length)
    : email;
}