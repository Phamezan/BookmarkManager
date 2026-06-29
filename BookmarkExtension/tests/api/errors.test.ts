import { describe, it, expect } from "vitest";
import {
  ApiError,
  classifyError,
  isRetryable,
  backoffDelay,
} from "../../src/api/errors";

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

describe("isRetryable", () => {
  it("returns true for retryable", () => {
    expect(isRetryable("retryable")).toBe(true);
  });

  it("returns true for config_stale", () => {
    expect(isRetryable("config_stale")).toBe(true);
  });

  it("returns false for permanent", () => {
    expect(isRetryable("permanent")).toBe(false);
  });

  it("returns false for lease_stale", () => {
    expect(isRetryable("lease_stale")).toBe(false);
  });
});

describe("backoffDelay", () => {
  it("starts at 5 seconds", () => {
    expect(backoffDelay(0)).toBe(5000);
  });

  it("doubles on each attempt", () => {
    expect(backoffDelay(1)).toBe(10000);
    expect(backoffDelay(2)).toBe(20000);
    expect(backoffDelay(3)).toBe(40000);
  });

  it("caps at 5 minutes", () => {
    expect(backoffDelay(10)).toBe(300000);
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
