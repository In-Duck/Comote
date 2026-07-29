const accountIdPattern = /^[a-z0-9][a-z0-9._-]{3,31}$/;
const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function normalizeAccountId(value: string): string | null {
  const normalized = value.trim().toLowerCase();
  return accountIdPattern.test(normalized) ? normalized : null;
}

export function normalizeEmail(value: string): string | null {
  const normalized = value.trim().toLowerCase();
  return normalized.length <= 254 && emailPattern.test(normalized) ? normalized : null;
}

export function isEmail(value: string): boolean {
  return value.includes("@");
}

export function isStrongEnoughPassword(value: string): boolean {
  return value.length >= 8 && value.length <= 128;
}

export function safeNextPath(value: string | null, fallback = "/dashboard"): string {
  if (!value || !value.startsWith("/") || value.startsWith("//")) return fallback;
  return value;
}
