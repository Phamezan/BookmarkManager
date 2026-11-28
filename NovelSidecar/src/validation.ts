export const MAX_PATH_LENGTH = 512;
export const MAX_QUERY_LENGTH = 200;
export const MIN_PAGE = 1;
export const MAX_PAGE = 50;

export function validatePath(value: string | null): string | null {
  if (!value) return 'Missing required parameter: path';
  if (value.length > MAX_PATH_LENGTH) {
    return `path must be at most ${MAX_PATH_LENGTH} characters`;
  }
  if (!value.startsWith('/')) {
    return 'path must start with /';
  }
  if (value.includes('//') || value.includes('://')) {
    return 'path must not contain // or ://';
  }
  if (/[\r\n\0]/.test(value)) {
    return 'path contains invalid characters';
  }
  return null;
}

export function parsePage(value: string | null): { page?: number; error?: string } {
  if (value === null || value === '') return { page: 1 };
  if (!/^\d+$/.test(value)) {
    return { error: `page must be an integer between ${MIN_PAGE} and ${MAX_PAGE}` };
  }
  const parsed = Number.parseInt(value, 10);
  if (parsed < MIN_PAGE || parsed > MAX_PAGE) {
    return { error: `page must be between ${MIN_PAGE} and ${MAX_PAGE}` };
  }
  return { page: parsed };
}

export function validateQuery(value: string | null, fieldName = 'q'): string | null {
  if (!value || !value.trim()) return `Missing required parameter: ${fieldName}`;
  if (value.length > MAX_QUERY_LENGTH) {
    return `${fieldName} must be at most ${MAX_QUERY_LENGTH} characters`;
  }
  return null;
}

const ROYALROAD_PATH_RE = /^\/fiction\/\d+\/[a-z0-9-]+$/;

export function validateRoyalRoadPath(value: string | null): string | null {
  if (!value) return 'Missing required parameter: path';
  if (value.length > MAX_PATH_LENGTH) {
    return `path must be at most ${MAX_PATH_LENGTH} characters`;
  }
  if (!ROYALROAD_PATH_RE.test(value)) {
    return 'path must match /fiction/{id}/{slug}';
  }
  return null;
}
