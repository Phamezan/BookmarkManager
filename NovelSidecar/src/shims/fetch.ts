import { AsyncLocalStorage } from 'node:async_hooks';

export const DEFAULT_USER_AGENT =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

const requestContext = new AsyncLocalStorage<{ signal?: AbortSignal }>();

export function runWithFetchSignal<T>(
  signal: AbortSignal | undefined,
  fn: () => Promise<T>,
): Promise<T> {
  return requestContext.run({ signal }, fn);
}

export type FetchInit = {
  headers?: Record<string, string | undefined> | Headers;
  method?: string;
  body?: FormData | string;
  signal?: AbortSignal;
  [x: string]: unknown;
};

export type WebviewFetchOptions = {
  waitForSelector?: string;
  timeoutMs?: number;
  settleMs?: number;
};

export type WebviewFetchResult = {
  ok: boolean;
  html: string;
};

function buildInit(init?: FetchInit): RequestInit {
  const defaultHeaders: Record<string, string> = {
    'User-Agent': DEFAULT_USER_AGENT,
    Accept:
      'text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7',
    'Accept-Language': 'en-US,en;q=0.9',
  };

  let headers: Record<string, string> | Headers;
  if (init?.headers instanceof Headers) {
    headers = new Headers(init.headers);
    for (const [k, v] of Object.entries(defaultHeaders)) {
      if (!headers.has(k)) headers.set(k, v);
    }
  } else {
    const merged: Record<string, string> = { ...defaultHeaders };
    if (init?.headers) {
      for (const [k, v] of Object.entries(init.headers)) {
        if (v !== undefined) merged[k] = v;
      }
    }
    headers = merged;
  }

  const ctxSignal = requestContext.getStore()?.signal;
  const signal = init?.signal ?? ctxSignal;

  return {
    ...(init as RequestInit),
    headers,
    ...(signal ? { signal } : {}),
  };
}

export async function fetchApi(
  url: string,
  init?: FetchInit,
): Promise<Response> {
  return await fetch(url, buildInit(init));
}

export async function fetchText(
  url: string,
  init?: FetchInit,
  encoding?: string,
): Promise<string> {
  const reqInit = buildInit(init);
  try {
    const res = await fetch(url, reqInit);
    if (!res.ok) return '';
    const buf = await res.arrayBuffer();
    return new TextDecoder(encoding || 'utf-8').decode(buf);
  } catch (err) {
    if (reqInit.signal?.aborted) throw err;
    return '';
  }
}

export async function fetchFile(
  url: string,
  init?: FetchInit,
): Promise<string> {
  const reqInit = buildInit(init);
  try {
    const res = await fetch(url, reqInit);
    if (!res.ok) return '';
    const buf = await res.arrayBuffer();
    return Buffer.from(buf).toString('base64');
  } catch (err) {
    if (reqInit.signal?.aborted) throw err;
    return '';
  }
}

export async function fetchViaWebview(
  _url: string,
  _options?: WebviewFetchOptions,
): Promise<WebviewFetchResult> {
  return { ok: false, html: '' };
}
