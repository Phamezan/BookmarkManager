import { describe, expect, it } from "vitest";
import { resolvePaletteBaseUrl } from "../../src/palette/palette-url";

describe("resolvePaletteBaseUrl", () => {
  it("returns null when no API base URL is configured", () => {
    expect(resolvePaletteBaseUrl(null)).toBeNull();
    expect(resolvePaletteBaseUrl(undefined)).toBeNull();
    expect(resolvePaletteBaseUrl("")).toBeNull();
  });

  it("returns null for unparseable URLs", () => {
    expect(resolvePaletteBaseUrl("not a url")).toBeNull();
  });

  it("returns null for non-http(s) schemes", () => {
    expect(resolvePaletteBaseUrl("ftp://192.168.1.10:8080")).toBeNull();
  });

  it("uses an https API base URL as-is", () => {
    expect(resolvePaletteBaseUrl("https://192.168.1.10:8443")).toBe(
      "https://192.168.1.10:8443",
    );
  });

  it("strips paths and trailing slashes down to the origin", () => {
    expect(resolvePaletteBaseUrl("https://192.168.1.10:8443/some/path/")).toBe(
      "https://192.168.1.10:8443",
    );
  });

  it("maps the docker http port 8080 to the paired TLS port 8443", () => {
    expect(resolvePaletteBaseUrl("http://192.168.1.10:8080")).toBe(
      "https://192.168.1.10:8443",
    );
  });

  it("maps the dev http port 5080 to the paired TLS port 5443", () => {
    expect(resolvePaletteBaseUrl("http://localhost:5080")).toBe(
      "https://localhost:5443",
    );
  });

  it("maps implicit port 80 to 443", () => {
    expect(resolvePaletteBaseUrl("http://bookmarks.lan")).toBe(
      "https://bookmarks.lan",
    );
  });

  it("falls back to 8443 for unknown http ports", () => {
    expect(resolvePaletteBaseUrl("http://192.168.1.10:9000")).toBe(
      "https://192.168.1.10:8443",
    );
  });
});
