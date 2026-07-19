export type RetryClassification =
  | "retryable"
  | "permanent"
  | "config_stale"
  | "lease_stale";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export function classifyError(
  status: number,
  code: string | null,
): RetryClassification {
  if (status === 0 || status === 429 || status >= 500) {
    return "retryable";
  }
  if (status === 400) {
    return "permanent";
  }
  if (status === 401) {
    return "permanent";
  }
  if (status === 403) {
    return "permanent";
  }
  if (status === 409) {
    if (code === "CONFIG_STALE") return "config_stale";
    if (code === "LEASE_STALE") return "lease_stale";
    return "permanent";
  }
  return "permanent";
}
