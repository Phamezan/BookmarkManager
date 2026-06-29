export interface FakeFetchResponse {
  status: number;
  body?: unknown;
  headers?: Record<string, string>;
}

export interface FakeFetchCall {
  url: string;
  method: string;
  headers: Record<string, string>;
  redirect?: RequestRedirect;
  body?: string;
}

export class FakeFetch {
  private responses = new Map<string, FakeFetchResponse>();
  private networkErrors = new Set<string>();
  public calls: FakeFetchCall[] = [];

  setResponse(urlPattern: string, response: FakeFetchResponse): void {
    this.responses.set(urlPattern, response);
  }

  setNetworkError(urlPattern: string): void {
    this.networkErrors.add(urlPattern);
  }

  reset(): void {
    this.responses.clear();
    this.networkErrors.clear();
    this.calls = [];
  }

  createFetch(): typeof fetch {
    return async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = init?.method ?? "GET";
      const headers: Record<string, string> = {};
      if (init?.headers) {
        const h = init.headers as Record<string, string>;
        for (const [key, value] of Object.entries(h)) {
          headers[key] = value;
        }
      }

      this.calls.push({
        url,
        method,
        headers,
        ...(init?.redirect ? { redirect: init.redirect } : {}),
        ...(init?.body ? { body: String(init.body) } : {}),
      });

      if (this.networkErrors.has(url) || this.networkErrors.has("*")) {
        throw new TypeError("Failed to fetch");
      }

      for (const [pattern, response] of this.responses) {
        if (url.includes(pattern) || pattern === "*") {
          const bodyStr =
            response.body !== undefined ? JSON.stringify(response.body) : null;
          const init: ResponseInit = { status: response.status };
          if (response.headers) {
            init.headers = response.headers;
          }
          return new Response(bodyStr, init);
        }
      }

      return new Response("", { status: 404 });
    };
  }
}
