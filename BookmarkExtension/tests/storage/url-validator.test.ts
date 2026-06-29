import { describe, it, expect } from "vitest";
import { validateApiBaseUrl } from "../../src/storage/url-validator";

describe("validateApiBaseUrl", () => {
  it("accepts a valid http origin", () => {
    const result = validateApiBaseUrl("http://bookmark-server.local:8080");
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value).toBe("http://bookmark-server.local:8080");
    }
  });

  it("accepts a valid https origin", () => {
    const result = validateApiBaseUrl("https://api.example.com");
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value).toBe("https://api.example.com");
    }
  });

  it("strips trailing slash", () => {
    const result = validateApiBaseUrl("http://localhost:8080/");
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value).toBe("http://localhost:8080");
    }
  });

  it("rejects credentials in URL", () => {
    const result = validateApiBaseUrl("http://user:pass@localhost:8080");
    expect(result.ok).toBe(false);
  });

  it("rejects query strings", () => {
    const result = validateApiBaseUrl("http://localhost:8080?foo=bar");
    expect(result.ok).toBe(false);
  });

  it("rejects fragments", () => {
    const result = validateApiBaseUrl("http://localhost:8080#section");
    expect(result.ok).toBe(false);
  });

  it("rejects paths", () => {
    const result = validateApiBaseUrl("http://localhost:8080/api");
    expect(result.ok).toBe(false);
  });

  it("rejects non-http schemes", () => {
    const result = validateApiBaseUrl("ftp://localhost:8080");
    expect(result.ok).toBe(false);
  });

  it("rejects invalid URLs", () => {
    const result = validateApiBaseUrl("not-a-url");
    expect(result.ok).toBe(false);
  });
});
