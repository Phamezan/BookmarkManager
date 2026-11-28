import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { after, before, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildBundle } from '../build.mjs';

const testDir = path.dirname(fileURLToPath(import.meta.url));
const fixturesDir = path.join(testDir, 'fixtures', 'hangulplanet');

let server;
let baseUrl;
let originalFetch;
let fetchHandler = async url => {
  throw new Error(`Unexpected live fetch in test: ${url}`);
};

function requestJson(reqPath, method = 'GET') {
  return new Promise((resolve, reject) => {
    const req = http.request(`${baseUrl}${reqPath}`, { method }, res => {
      const chunks = [];
      res.on('data', chunk => chunks.push(chunk));
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        let json = null;
        try {
          json = JSON.parse(text);
        } catch {
          // leave null
        }
        resolve({ status: res.statusCode, body: json, text });
      });
    });
    req.on('error', reject);
    req.end();
  });
}

before(async () => {
  await buildBundle('src/app.ts', 'dist/app.js');
  const { createServer } = await import('../dist/app.js');

  originalFetch = globalThis.fetch;
  globalThis.fetch = async (input, init) => {
    const url = typeof input === 'string' ? input : input.toString();
    return await fetchHandler(url, init);
  };

  server = createServer({ timeoutMs: 2000 });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  baseUrl = `http://127.0.0.1:${port}`;
});

after(async () => {
  globalThis.fetch = originalFetch;
  if (server) {
    await new Promise(resolve => server.close(resolve));
  }
});

describe('NovelSidecar endpoints', () => {
  test('GET /health returns 200', async () => {
    const res = await requestJson('/health');
    assert.equal(res.status, 200);
    assert.equal(res.body.status, 'ok');
  });

  test('GET /sources lists all 4 bundled plugins', async () => {
    const res = await requestJson('/sources');
    assert.equal(res.status, 200);
    const ids = res.body.map(s => s.id);
    assert.deepEqual(ids, ['hangulplanet', 'wetriedtls', 'fenrirrealm', 'novelfire']);
    for (const s of res.body) {
      assert.ok(s.name.length > 0);
      assert.match(s.site, /^https:\/\//);
      assert.ok(!s.site.endsWith('/'), 'site should not have trailing slash');
    }
  });

  test('GET /popular parses hangulplanet fixture', async () => {
    const html = await fs.readFile(path.join(fixturesDir, 'popular.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://hangulplanet.com/browse?sort=popular');
      return new Response(html, { status: 200, headers: { 'content-type': 'text/html' } });
    };

    const res = await requestJson('/popular?source=hangulplanet&page=1');
    assert.equal(res.status, 200);
    assert.ok(Array.isArray(res.body) && res.body.length > 0);
    assert.equal(res.body[0].title, 'Climbing the Tower with Time-Stop Ability');
    assert.equal(res.body[0].path, '/novel/climbing-the-tower-with-time-stop-ability');
    assert.match(res.body[0].cover, /^https:\/\/hangulplanet\.com\//);
  });

  test('GET /search parses hangulplanet fixture', async () => {
    const html = await fs.readFile(path.join(fixturesDir, 'popular.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://hangulplanet.com/browse?q=tower');
      return new Response(html, { status: 200, headers: { 'content-type': 'text/html' } });
    };

    const res = await requestJson('/search?source=hangulplanet&q=tower&page=1');
    assert.equal(res.status, 200);
    assert.ok(Array.isArray(res.body) && res.body.length > 0);
    assert.equal(res.body[0].path, '/novel/climbing-the-tower-with-time-stop-ability');
  });

  test('GET /novel parses hangulplanet fixture with pagination and free chapters', async () => {
    const page1 = await fs.readFile(path.join(fixturesDir, 'novel-page1.html'), 'utf8');
    const page2 = await fs.readFile(path.join(fixturesDir, 'novel-page2.html'), 'utf8');

    fetchHandler = async url => {
      if (url === 'https://hangulplanet.com/novel/climbing-the-tower-with-time-stop-ability') {
        return new Response(page1, { status: 200 });
      }
      if (url === 'https://hangulplanet.com/novel/climbing-the-tower-with-time-stop-ability?cpage=2') {
        return new Response(page2, { status: 200 });
      }
      return new Response('<html><body></body></html>', { status: 200 });
    };

    const res = await requestJson(
      '/novel?source=hangulplanet&path=/novel/climbing-the-tower-with-time-stop-ability',
    );
    assert.equal(res.status, 200);
    assert.equal(res.body.title, 'Climbing the Tower with Time-Stop Ability');
    assert.equal(res.body.status, 'Ongoing');
    assert.deepEqual(res.body.genres, ['Action', 'Fantasy', 'Seinen']);
    assert.ok(res.body.chapters.length > 50);
    assert.equal(res.body.chapters[0].path, '/novel/climbing-the-tower-with-time-stop-ability/chapter-1');
    assert.equal(res.body.chapters[0].locked, false);
  });

  test('GET /novel detects locked chapters on wetriedtls and strips lock prefix', async () => {
    fetchHandler = async url => {
      if (url === 'https://api.wetriedtls.com/series/demo-novel') {
        return new Response(
          JSON.stringify({
            id: 42,
            title: 'Demo Novel',
            author: 'Author Name',
            status: 'Ongoing',
            thumbnail: 'https://cdn.example.com/cover.jpg',
            description: '<p>A test summary.</p>',
            tags: [{ name: 'Action' }, { name: 'Fantasy' }],
          }),
          { status: 200 },
        );
      }
      if (url.startsWith('https://api.wetriedtls.com/chapters/42?')) {
        return new Response(
          JSON.stringify({
            meta: { last_page: 1 },
            data: [
              {
                chapter_slug: 'chapter-1',
                chapter_name: 'Chapter 1',
                chapter_title: 'Beginning',
                index: '1',
                created_at: '2026-01-01T00:00:00Z',
              },
            ],
          }),
          { status: 200 },
        );
      }
      if (url.startsWith('https://api.wetriedtls.com/chapters/42/paid?')) {
        return new Response(
          JSON.stringify({
            meta: { last_page: 1 },
            data: [
              {
                chapter_slug: 'chapter-2',
                chapter_name: 'Chapter 2',
                chapter_title: 'Paywalled',
                index: '2',
                created_at: '2026-01-02T00:00:00Z',
              },
            ],
          }),
          { status: 200 },
        );
      }
      return new Response('Not found', { status: 404 });
    };

    const res = await requestJson('/novel?source=wetriedtls&path=/series/demo-novel');
    assert.equal(res.status, 200);
    assert.equal(res.body.title, 'Demo Novel');
    assert.equal(res.body.chapters.length, 2);
    assert.equal(res.body.chapters[0].locked, false);
    assert.equal(res.body.chapters[0].name, 'Chapter 1: Beginning');
    assert.equal(res.body.chapters[0].path, '/series/demo-novel/chapter-1');
    assert.equal(res.body.chapters[1].locked, true);
    assert.equal(res.body.chapters[1].name, 'Chapter 2: Paywalled');
    assert.equal(res.body.chapters[1].path, '/series/demo-novel/chapter-2');
  });

  test('GET /novel detects locked chapters on fenrirrealm and strips lock prefix + token suffix', async () => {
    fetchHandler = async url => {
      if (url === 'https://fenrirealm.com/api/new/v2/series/fenrir-demo/chapters') {
        return new Response(
          JSON.stringify([
            {
              id: 101,
              slug: 'chapter-1',
              title: 'Free One',
              number: 1,
              locked: null,
              group: null,
              created_at: '2026-01-01',
            },
            {
              id: 102,
              slug: 'chapter-2',
              title: 'Locked One',
              number: 2,
              locked: { price: 50 },
              group: null,
              created_at: '2026-01-02',
            },
          ]),
          { status: 200 },
        );
      }
      if (url === 'https://fenrirealm.com/api/new/v2/series/fenrir-demo') {
        return new Response(
          JSON.stringify({
            title: 'Fenrir Demo',
            slug: 'fenrir-demo',
            cover: 'covers/demo.jpg',
            description: '<p>Summary</p>',
            status: 'Ongoing',
            genres: [{ name: 'Action' }],
            user: { name: 'Translator' },
          }),
          { status: 200 },
        );
      }
      return new Response('Not found', { status: 404 });
    };

    const res = await requestJson('/novel?source=fenrirrealm&path=/series/fenrir-demo');
    assert.equal(res.status, 200);
    assert.equal(res.body.chapters.length, 2);
    assert.equal(res.body.chapters[0].locked, false);
    assert.equal(res.body.chapters[0].name, 'Chapter 1 - Free One');
    assert.equal(res.body.chapters[1].locked, true);
    assert.equal(res.body.chapters[1].name, 'Chapter 2 - Locked One');
    assert.equal(res.body.chapters[1].path, '/series/fenrir-demo/chapter-2');
  });

  test('GET /chapter parses hangulplanet fixture', async () => {
    const html = await fs.readFile(path.join(fixturesDir, 'chapter.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(
        url,
        'https://hangulplanet.com/novel/climbing-the-tower-with-time-stop-ability/chapter-1',
      );
      return new Response(html, { status: 200 });
    };

    const res = await requestJson(
      '/chapter?source=hangulplanet&path=/novel/climbing-the-tower-with-time-stop-ability/chapter-1',
    );
    assert.equal(res.status, 200);
    assert.ok(res.body.html.includes('Chapter 1: Entry'));
    assert.ok(res.body.html.includes('<p>Life on easy mode.</p>'));
  });

  test('validation errors return 400 and plugin errors return 502 without stack trace', async () => {
    assert.equal((await requestJson('/popular?source=unknown')).status, 400);
    assert.equal((await requestJson('/popular?source=hangulplanet&page=0')).status, 400);
    assert.equal((await requestJson('/popular?source=hangulplanet&page=51')).status, 400);
    assert.equal((await requestJson('/popular?source=hangulplanet&page=abc')).status, 400);
    assert.equal((await requestJson('/search?source=hangulplanet&q=')).status, 400);
    assert.equal(
      (await requestJson(`/search?source=hangulplanet&q=${'a'.repeat(201)}`)).status,
      400,
    );
    assert.equal((await requestJson('/novel?source=hangulplanet&path=no-leading-slash')).status, 400);
    assert.equal((await requestJson('/novel?source=hangulplanet&path=//evil.com/a')).status, 400);
    assert.equal(
      (await requestJson('/novel?source=hangulplanet&path=/https://evil.com/a')).status,
      400,
    );
    assert.equal(
      (await requestJson(`/chapter?source=hangulplanet&path=/${'a'.repeat(512)}`)).status,
      400,
    );

    fetchHandler = async () => new Response('Server Error', { status: 500 });
    const errRes = await requestJson('/popular?source=hangulplanet&page=1');
    assert.equal(errRes.status, 502);
    assert.ok(typeof errRes.body.error === 'string' && errRes.body.error.length > 0);
    assert.ok(!errRes.text.includes('at '), '502 response must not include stack trace');
  });
});
