import { describe, it, expect } from "vitest";
import { ApiError, classifyError } from "../../src/api/errors";

describe("classifyError", () => {
  it("classifies network failure as retryable", () => {
    expect(classifyError(0, null)).toBe("retryable");
  });

  it("classifies 429 as retryable", () => {
    expect(classifyError(429, null)).toBe("retryable");
  });

  it("classifies 500 as retryable", () => {
    expect(classifyError(500, null)).toBe("retryable");
  });

  it("classifies 503 as retryable", () => {
    expect(classifyError(503, null)).toBe("retryable");
  });

  it("classifies 400 as permanent", () => {
    expect(classifyError(400, null)).toBe("permanent");
  });

  it("classifies 401 as permanent", () => {
    expect(classifyError(401, null)).toBe("permanent");
  });

  it("classifies 403 as permanent", () => {
    expect(classifyError(403, null)).toBe("permanent");
  });

  it("classifies 409 CONFIG_STALE as config_stale", () => {
    expect(classifyError(409, "CONFIG_STALE")).toBe("config_stale");
  });

  it("classifies 409 LEASE_STALE as lease_stale", () => {
    expect(classifyError(409, "LEASE_STALE")).toBe("lease_stale");
  });

  it("classifies unknown 409 as permanent", () => {
    expect(classifyError(409, "OTHER")).toBe("permanent");
  });
});

describe("ApiError", () => {
  it("stores status, code, and message", () => {
    const error = new ApiError(401, "AUTH_FAILED", "Authentication failed");
    expect(error.status).toBe(401);
    expect(error.code).toBe("AUTH_FAILED");
    expect(error.message).toBe("Authentication failed");
    expect(error.name).toBe("ApiError");
  });
});
