import http from 'node:http';
import { getSource, listSources } from './plugins';
import { parsePage, validatePath, validateQuery, validateRoyalRoadPath } from './validation';
import { runWithFetchSignal } from './shims/fetch';
import {
  fetchRoyalRoadFiction,
  fetchRoyalRoadSearch,
  getSourceReviews,
  getSourceStats,
  isExtrasSource,
} from './extras';

export const REQUEST_TIMEOUT_MS = 20_000;

function sendJson(res: http.ServerResponse, status: number, body: unknown): void {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(payload),
  });
  res.end(payload);
}

async function withTimeout<T>(
  timeoutMs: number,
  fn: (signal: AbortSignal) => Promise<T>,
): Promise<T> {
  const controller = new AbortController();
  let timer: NodeJS.Timeout | undefined;
  const timeoutPromise = new Promise<never>((_, reject) => {
    timer = setTimeout(() => {
      controller.abort();
      reject(new Error('Upstream request timed out'));
    }, timeoutMs);
  });

  try {
    return await Promise.race([
      runWithFetchSignal(controller.signal, () => fn(controller.signal)),
      timeoutPromise,
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

function safeErrorMessage(err: unknown): string {
  if (err instanceof Error) {
    if (err.name === 'AbortError' || /timed out/i.test(err.message)) {
      return 'Upstream request timed out';
    }
    const firstLine = err.message.split(/\r?\n/, 1)[0]?.trim();
    if (firstLine && firstLine.length <= 200) {
      return firstLine;
    }
  }
  return 'Upstream novel source error';
}

export function createServer(options?: { timeoutMs?: number }): http.Server {
  const timeoutMs = options?.timeoutMs ?? REQUEST_TIMEOUT_MS;

  return http.createServer(async (req, res) => {
    if (req.method !== 'GET') {
      sendJson(res, 405, { error: 'Method not allowed' });
      return;
    }

    let url: URL;
    try {
      url = new URL(req.url || '/', 'http://localhost');
    } catch {
      sendJson(res, 400, { error: 'Invalid request URL' });
      return;
    }

    const { pathname, searchParams } = url;

    if (pathname === '/health') {
      sendJson(res, 200, { status: 'ok' });
      return;
    }

    if (pathname === '/sources') {
      sendJson(res, 200, listSources());
      return;
    }

    if (pathname === '/rr/search') {
      const title = searchParams.get('title');
      const titleErr = validateQuery(title, 'title');
      if (titleErr) {
        sendJson(res, 400, { error: titleErr });
        return;
      }
      try {
        const hits = await withTimeout(timeoutMs, () => fetchRoyalRoadSearch(title!.trim()));
        sendJson(res, 200, hits);
      } catch (err) {
        sendJson(res, 502, { error: safeErrorMessage(err) });
      }
      return;
    }

    if (pathname === '/rr/fiction') {
      const fictionPath = searchParams.get('path');
      const pathErr = validateRoyalRoadPath(fictionPath);
      if (pathErr) {
        sendJson(res, 400, { error: pathErr });
        return;
      }
      const { page, error } = parsePage(searchParams.get('page'));
      if (error) {
        sendJson(res, 400, { error });
        return;
      }
      try {
        const result = await withTimeout(timeoutMs, () => fetchRoyalRoadFiction(fictionPath!, page!));
        sendJson(res, 200, result);
      } catch (err) {
        sendJson(res, 502, { error: safeErrorMessage(err) });
      }
      return;
    }

    if (pathname === '/stats' || pathname === '/reviews') {
      const rawSource = searchParams.get('source');
      if (!isExtrasSource(rawSource)) {
        sendJson(res, 400, { error: 'Unsupported source for extras endpoint' });
        return;
      }
      const source = rawSource.trim().toLowerCase() as 'novelfire' | 'hangulplanet';
      const novelPath = searchParams.get('path');
      const pathErr = validatePath(novelPath);
      if (pathErr) {
        sendJson(res, 400, { error: pathErr });
        return;
      }
      try {
        if (pathname === '/stats') {
          const stats = await withTimeout(timeoutMs, () => getSourceStats(source, novelPath!));
          sendJson(res, 200, stats);
          return;
        }
        const { page, error } = parsePage(searchParams.get('page'));
        if (error) {
          sendJson(res, 400, { error });
          return;
        }
        const reviews = await withTimeout(timeoutMs, () => getSourceReviews(source, novelPath!, page!));
        sendJson(res, 200, reviews);
        return;
      } catch (err) {
        sendJson(res, 502, { error: safeErrorMessage(err) });
        return;
      }
    }

    if (
      pathname !== '/popular' &&
      pathname !== '/search' &&
      pathname !== '/novel' &&
      pathname !== '/chapter'
    ) {
      sendJson(res, 404, { error: 'Not found' });
      return;
    }

    const sourceId = searchParams.get('source');
    const adapter = getSource(sourceId);
    if (!adapter) {
      sendJson(res, 400, { error: 'Unknown source' });
      return;
    }

    try {
      if (pathname === '/popular') {
        const { page, error } = parsePage(searchParams.get('page'));
        if (error) {
          sendJson(res, 400, { error });
          return;
        }
        const items = await withTimeout(timeoutMs, () => adapter.popular(page!));
        sendJson(res, 200, items);
        return;
      }

      if (pathname === '/search') {
        const q = searchParams.get('q');
        const qErr = validateQuery(q);
        if (qErr) {
          sendJson(res, 400, { error: qErr });
          return;
        }
        const { page, error } = parsePage(searchParams.get('page'));
        if (error) {
          sendJson(res, 400, { error });
          return;
        }
        const items = await withTimeout(timeoutMs, () =>
          adapter.search(q!.trim(), page!),
        );
        sendJson(res, 200, items);
        return;
      }

      if (pathname === '/novel') {
        const novelPath = searchParams.get('path');
        const pathErr = validatePath(novelPath);
        if (pathErr) {
          sendJson(res, 400, { error: pathErr });
          return;
        }
        const detail = await withTimeout(timeoutMs, () => adapter.novel(novelPath!));
        sendJson(res, 200, detail);
        return;
      }

      if (pathname === '/chapter') {
        const chapterPath = searchParams.get('path');
        const pathErr = validatePath(chapterPath);
        if (pathErr) {
          sendJson(res, 400, { error: pathErr });
          return;
        }
        const html = await withTimeout(timeoutMs, () => adapter.chapter(chapterPath!));
        sendJson(res, 200, { html });
        return;
      }
    } catch (err) {
      sendJson(res, 502, { error: safeErrorMessage(err) });
    }
  });
}
