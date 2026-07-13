/**
 * Resolves the https origin serving the /palette page from the configured API
 * base URL. The embedded palette must load over https: the palette-host.html
 * extension page cannot frame active mixed content, so a plain-http API URL is
 * mapped onto the paired TLS port of the dual Kestrel binding.
 */

const HTTP_TO_HTTPS_PORT: Record<string, string> = {
  "8080": "8443",
  "5080": "5443",
  "80": "443",
};

const DEFAULT_HTTPS_PORT = "8443";

export function resolvePaletteBaseUrl(
  apiBaseUrl: string | null | undefined,
): string | null {
  if (!apiBaseUrl) return null;

  let url: URL;
  try {
    url = new URL(apiBaseUrl);
  } catch {
    return null;
  }

  if (url.protocol === "https:") {
    return url.origin;
  }
  if (url.protocol !== "http:") {
    return null;
  }

  const httpPort = url.port || "80";
  url.protocol = "https:";
  url.port = HTTP_TO_HTTPS_PORT[httpPort] ?? DEFAULT_HTTPS_PORT;
  return url.origin;
}
