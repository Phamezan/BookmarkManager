import type { Result } from "../shared/result";

export function validateApiBaseUrl(raw: string): Result<string, string> {
  let parsed: URL;
  try {
    parsed = new URL(raw);
  } catch {
    return { ok: false, error: "Invalid URL" };
  }

  if (parsed.username || parsed.password) {
    return { ok: false, error: "URL must not contain credentials" };
  }
  if (parsed.search) {
    return { ok: false, error: "URL must not contain a query string" };
  }
  if (parsed.hash) {
    return { ok: false, error: "URL must not contain a fragment" };
  }
  if (parsed.pathname !== "/" && parsed.pathname !== "") {
    return { ok: false, error: "URL must be an origin without a path" };
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    return { ok: false, error: "URL must use http or https scheme" };
  }

  const origin = parsed.origin;
  return { ok: true, value: origin };
}

export function normalizeBaseUrl(raw: string): string {
  return raw.replace(/\/+$/, "");
}
