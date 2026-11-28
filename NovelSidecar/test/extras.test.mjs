import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { after, before, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { buildBundle } from '../build.mjs';

const testDir = path.dirname(fileURLToPath(import.meta.url));
const nfDir = path.join(testDir, 'fixtures', 'novelfire');
const hpDir = path.join(testDir, 'fixtures', 'hangulplanet');
const rrDir = path.join(testDir, 'fixtures', 'royalroad');

let server;
let baseUrl;
let originalFetch;
let fetchHandler = async url => {
  throw new Error(`Unexpected live fetch in test: ${url}`);
};

function requestJson(reqPath) {
  return new Promise((resolve, reject) => {
    const req = http.request(`${baseUrl}${reqPath}`, { method: 'GET' }, res => {
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

describe('NovelSidecar extras endpoints (stats, reviews, RoyalRoad)', () => {
  test('GET /stats parses novelfire book fixture', async () => {
    const html = await fs.readFile(path.join(nfDir, 'book.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://novelfire.net/book/the-primal-hunter');
      return new Response(html, { status: 200 });
    };

    const res = await requestJson('/stats?source=novelfire&path=/book/the-primal-hunter');
    assert.equal(res.status, 200);
    assert.equal(res.body.rank, 24);
    assert.equal(res.body.rating, 4.1);
    assert.equal(res.body.ratingCount, 54);
    assert.equal(res.body.chapters, 1459);
    assert.equal(res.body.views, 4_600_000);
    assert.equal(res.body.bookmarks, 5_900);
    assert.equal(res.body.status, 'Ongoing');
    assert.match(res.body.latestChapterName, /Chapter 1433/);
    assert.equal(res.body.latestChapterUpdated, '1 day ago');
    assert.equal(res.body.reviewCount, 54);
    assert.equal(res.body.reviewAverage, 4.1);
  });

  test('GET /reviews parses novelfire reviews fixture as plain text', async () => {
    const html = await fs.readFile(path.join(nfDir, 'reviews.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://novelfire.net/book/the-primal-hunter/reviews?page=1');
      return new Response(html, { status: 200 });
    };

    const res = await requestJson('/reviews?source=novelfire&path=/book/the-primal-hunter&page=1');
    assert.equal(res.status, 200);
    assert.equal(res.body.average, 4.1);
    assert.equal(res.body.count, 54);
    assert.equal(res.body.page, 1);
    assert.equal(res.body.hasNextPage, true);
    assert.ok(res.body.items.length >= 20);
    const first = res.body.items[0];
    assert.equal(first.author, 'Yahrim');
    assert.equal(first.date, '1 day ago');
    assert.equal(first.score, 3.5);
    assert.equal(first.likes, 0);
    assert.ok(first.paragraphs.length >= 3);
    assert.equal(first.paragraphs[0].spoiler, false);
    assert.ok(!first.paragraphs[0].text.includes('<p>'), 'paragraphs must be plain text');
  });

  test('GET /stats parses hangulplanet novel fixture', async () => {
    const html = await fs.readFile(path.join(hpDir, 'novel-page1.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://hangulplanet.com/novel/climbing-the-tower-with-time-stop-ability');
      return new Response(html, { status: 200 });
    };

    const res = await requestJson(
      '/stats?source=hangulplanet&path=/novel/climbing-the-tower-with-time-stop-ability',
    );
    assert.equal(res.status, 200);
    assert.equal(res.body.rank, null);
    assert.equal(res.body.rating, 4.4);
    assert.equal(res.body.ratingCount, 14);
    assert.equal(res.body.chapters, 235);
    assert.equal(res.body.status, 'Ongoing');
    assert.equal(res.body.reviewCount, 14);
    assert.equal(res.body.reviewAverage, 4.4);
    assert.equal(res.body.latestChapterUpdated, '16 hours ago');
    assert.match(res.body.latestChapterName, /209/);
  });

  test('GET /reviews parses hangulplanet streamed reviews fixture', async () => {
    const html = await fs.readFile(path.join(hpDir, 'novel-page1.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://hangulplanet.com/novel/climbing-the-tower-with-time-stop-ability');
      return new Response(html, { status: 200 });
    };

    const res = await requestJson(
      '/reviews?source=hangulplanet&path=/novel/climbing-the-tower-with-time-stop-ability&page=1',
    );
    assert.equal(res.status, 200);
    assert.equal(res.body.average, 4.4);
    assert.equal(res.body.count, 14);
    assert.equal(res.body.page, 1);
    assert.equal(res.body.hasNextPage, true);
    assert.equal(res.body.items.length, 10);
    const first = res.body.items[0];
    assert.equal(first.author, 'Moh');
    assert.equal(first.date, '2 days ago');
    assert.equal(first.score, 5);
    assert.equal(first.likes, 0);
    assert.deepEqual(first.paragraphs, [{ text: 'Perfect', spoiler: false }]);
  });

  test('GET /rr/search parses RoyalRoad search fixture', async () => {
    const html = await fs.readFile(path.join(rrDir, 'search.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://www.royalroad.com/fictions/search?title=The%20Primal%20Hunter');
      return new Response(html, { status: 200 });
    };

    const res = await requestJson('/rr/search?title=The%20Primal%20Hunter');
    assert.equal(res.status, 200);
    assert.equal(res.body.length, 2);
    assert.deepEqual(res.body[0], {
      title: 'The Primal Hunter',
      path: '/fiction/36049/the-primal-hunter',
      cover: 'https://www.royalroadcdn.com/public/covers-large/36049-the-primal-hunter.jpg',
    });
  });

  test('GET /rr/fiction parses RoyalRoad fiction stats, reviews, and spoiler blocks', async () => {
    const html = await fs.readFile(path.join(rrDir, 'fiction.html'), 'utf8');
    fetchHandler = async url => {
      assert.equal(url, 'https://www.royalroad.com/fiction/36049/the-primal-hunter');
      return new Response(html, { status: 200 });
    };

    const res = await requestJson('/rr/fiction?path=/fiction/36049/the-primal-hunter&page=1');
    assert.equal(res.status, 200);
    assert.equal(res.body.title, 'The Primal Hunter');
    assert.equal(res.body.overallScore, 4.68);
    assert.equal(res.body.views, 84_210_500);
    assert.equal(res.body.followers, 62_400);
    assert.equal(res.body.favorites, 18_900);
    assert.equal(res.body.ratings, 12_340);
    assert.equal(res.body.count, 420);
    assert.equal(res.body.page, 1);
    assert.equal(res.body.hasNextPage, true);
    assert.equal(res.body.items.length, 2);

    const first = res.body.items[0];
    assert.equal(first.author, 'ArcaneReader');
    assert.equal(first.date, '1 month ago');
    assert.equal(first.score, 5);
    assert.equal(first.likes, 42);
    assert.deepEqual(first.paragraphs, [
      { text: 'Tight progression system and satisfying bow combat throughout the early arcs.', spoiler: false },
      { text: "The Malefic Viper becomes Jake's patron right after the tutorial.", spoiler: true },
      { text: 'Highly recommended if you enjoy solo hunter LitRPG.', spoiler: false },
    ]);
  });

  test('extras endpoints validate inputs (400) and map upstream errors (502)', async () => {
    assert.equal((await requestJson('/stats?source=wetriedtls&path=/series/a')).status, 400);
    assert.equal((await requestJson('/stats?source=unknown&path=/book/a')).status, 400);
    assert.equal((await requestJson('/stats?source=novelfire&path=bad-path')).status, 400);
    assert.equal((await requestJson('/reviews?source=fenrirrealm&path=/series/a')).status, 400);
    assert.equal((await requestJson('/reviews?source=novelfire&path=/book/a&page=0')).status, 400);
    assert.equal((await requestJson('/rr/search?title=')).status, 400);
    assert.equal((await requestJson('/rr/fiction?path=/fiction/abc/bad')).status, 400);
    assert.equal((await requestJson('/rr/fiction?path=/fiction/36049/Bad_Caps')).status, 400);
    assert.equal((await requestJson('/rr/fiction?path=//evil.com/fiction/1/x')).status, 400);
    assert.equal((await requestJson('/rr/fiction?path=/fiction/36049/the-primal-hunter&page=51')).status, 400);

    fetchHandler = async () => new Response('Forbidden', { status: 403 });
    const errRes = await requestJson('/rr/search?title=Test');
    assert.equal(errRes.status, 502);
    assert.ok(typeof errRes.body.error === 'string' && errRes.body.error.length > 0);
  });
});
