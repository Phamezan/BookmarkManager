'use strict';

// Run with Node and Playwright installed: node tests/suwayomi-addon/novel.test.js
// Serves the real add-on files through request interception; no running API is needed.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

const addonDir = path.join(__dirname, '../../src/BookmarkManager.Client/wwwroot/suwayomi-addon');
const ORIGIN = 'http://novel.test';

const ENTRY_ID = '11111111-1111-1111-1111-111111111111';
const ENTRY_ID_2 = '22222222-2222-2222-2222-222222222222';

const libraryEntries = [
  {
    id: ENTRY_ID,
    source: 'hangulplanet',
    novelPath: '/novel/tower-of-time',
    title: 'Tower of Time',
    coverUrl: null,
    status: 'Reading',
    lastChapterPath: '/novel/tower-of-time/chapter-1',
    lastChapterName: 'Chapter 1: Entry',
    lastChapterIndex: 0,
    chapterCount: 3,
    scrollPercent: 0.4,
    addedAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-27T00:00:00Z',
    lastReadAt: '2026-11-27T00:00:00Z'
  },
  {
    id: ENTRY_ID_2,
    source: 'hangulplanet',
    novelPath: '/novel/abyss-mage',
    title: 'Abyss Mage',
    coverUrl: null,
    status: 'Completed',
    lastChapterPath: '/novel/abyss-mage/chapter-10',
    lastChapterName: 'Chapter 10: Finale',
    lastChapterIndex: 9,
    chapterCount: 10,
    scrollPercent: 1,
    addedAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-15T00:00:00Z',
    lastReadAt: '2026-10-15T00:00:00Z'
  }
];

const novelDetail = {
  source: 'hangulplanet',
  sourceName: 'HangulPlanet',
  site: 'https://hangulplanet.com',
  title: 'Tower of Time',
  path: '/novel/tower-of-time',
  cover: null,
  author: 'Author Kim',
  status: 'Ongoing',
  genres: ['Action', 'Fantasy'],
  summary: 'A climber resets time on every floor.',
  chapters: [
    { name: 'Chapter 1: Entry', path: '/novel/tower-of-time/chapter-1', number: 1, releaseTime: '2026-11-01T00:00:00Z', locked: false },
    { name: 'Chapter 2: The Gate', path: '/novel/tower-of-time/chapter-2', number: 2, releaseTime: '2026-11-02T00:00:00Z', locked: false },
    { name: 'Chapter 3: Early Access', path: '/novel/tower-of-time/chapter-3', number: 3, releaseTime: '2026-11-03T00:00:00Z', locked: true }
  ],
  libraryEntry: libraryEntries[0]
};

const chapters = {
  '/novel/tower-of-time/chapter-1': {
    title: 'Chapter 1: Entry',
    html: '<h3>Chapter 1: Entry</h3><p>Life on easy mode.</p><p>The clock stopped at midnight.</p>',
    prevPath: null,
    nextPath: '/novel/tower-of-time/chapter-2',
    nextLocked: false,
    site: 'https://hangulplanet.com',
    index: 0,
    count: 3
  },
  '/novel/tower-of-time/chapter-2': {
    title: 'Chapter 2: The Gate',
    html: '<h3>Chapter 2: The Gate</h3><p>The iron gate creaked open.</p>',
    prevPath: '/novel/tower-of-time/chapter-1',
    nextPath: '/novel/tower-of-time/chapter-3',
    nextLocked: true,
    site: 'https://hangulplanet.com',
    index: 1,
    count: 3
  }
};

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    page.setDefaultTimeout(5000);

    const errors = [];
    const progressPuts = [];
    const statusPatches = [];
    page.on('pageerror', error => errors.push(error.message));

    await page.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());

      if (url.pathname.startsWith('/bm-api/novels/')) {
        const api = url.pathname.slice('/bm-api/novels'.length);
        if (api === '/library' && request.method() === 'GET') {
          return route.fulfill({ json: libraryEntries });
        }
        if (api === '/novel' && request.method() === 'GET') {
          return route.fulfill({ json: novelDetail });
        }
        if (api === '/chapter' && request.method() === 'GET') {
          const p = url.searchParams.get('path');
          if (p === '/novel/tower-of-time/chapter-3') {
            return route.fulfill({ status: 403, json: { error: 'Locked chapter' } });
          }
          const ch = chapters[p];
          return ch ? route.fulfill({ json: ch }) : route.fulfill({ status: 404, json: {} });
        }
        if (api === `/library/${ENTRY_ID}/progress` && request.method() === 'PUT') {
          progressPuts.push(request.postDataJSON());
          return route.fulfill({ json: libraryEntries[0] });
        }
        if (api === `/library/${ENTRY_ID}` && request.method() === 'PATCH') {
          statusPatches.push(request.postDataJSON());
          return route.fulfill({ json: { ...libraryEntries[0], status: request.postDataJSON().status } });
        }
        return route.fulfill({ status: 404, json: {} });
      }

      if (url.pathname.startsWith('/bm/')) {
        const name = path.basename(url.pathname);
        return route.fulfill({
          body: await fs.readFile(path.join(addonDir, name)),
          contentType: name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html'
        });
      }
      return route.fulfill({ contentType: 'text/html', body: '<p>stub</p>' });
    });

    // 1. Library grid renders + status filter works.
    await page.goto(`${ORIGIN}/bm/novel.html#/`);
    await page.locator('.ncard').nth(1).waitFor();
    assert.equal(await page.locator('.ncard').count(), 2);
    assert.match(await page.locator(`.ncard[data-id="${ENTRY_ID}"] .ncard-continue`).innerText(), /Ch 1 of 3 · Continue/);
    await page.getByRole('button', { name: 'Completed', exact: true }).click();
    assert.equal(await page.locator('.ncard').count(), 1);
    assert.match(await page.locator('.ncard .xtitle').innerText(), /Abyss Mage/);
    await page.getByRole('button', { name: 'All', exact: true }).click();
    assert.equal(await page.locator('.ncard').count(), 2);

    // 2. Detail view renders metadata + locked chapter as external link, NOT in-app reader link.
    await page.locator(`.ncard[data-id="${ENTRY_ID}"] .ncard-link`).click();
    await page.locator('.nchapters-list').waitFor();
    assert.equal(await page.locator('.ndetail-info h2').innerText(), 'Tower of Time');
    assert.equal(await page.locator('a.nch-row').count(), 2, 'only unlocked chapters are clickable in-app rows');
    const lockedRow = page.locator('.nch-row.is-locked');
    assert.equal(await lockedRow.count(), 1);
    assert.match(await lockedRow.innerText(), /🔒 Chapter 3: Early Access/);
    const extLink = lockedRow.locator('a.nch-ext');
    assert.equal(await extLink.getAttribute('href'), 'https://hangulplanet.com/novel/tower-of-time/chapter-3');

    // Status change PATCHes API.
    await page.getByLabel('Reading status').selectOption('Plan');
    await page.waitForTimeout(100);
    assert.deepEqual(statusPatches, [{ status: 'Plan' }]);

    // 3. Reader renders sanitized HTML, fires progress PUT, navigates prev/next, shows locked next notice.
    await page.locator('a.nch-row').first().click();
    await page.locator('.nreader-body').waitFor();
    assert.match(await page.locator('.nreader-body').innerText(), /Life on easy mode\./);
    assert.ok(progressPuts.length >= 1, 'progress PUT fires on reader entry');
    assert.equal(progressPuts[0].chapterPath, '/novel/tower-of-time/chapter-1');
    assert.equal(progressPuts[0].chapterIndex, 0);

    // Navigate to next chapter via button -> Chapter 2 shows locked next notice + Open on site.
    await page.getByRole('link', { name: /Next chapter/ }).click();
    await page.waitForFunction(() => document.querySelector('.nreader-body')?.textContent?.includes('iron gate'));
    const lockNotice = page.locator('.nlock-notice');
    await lockNotice.waitFor();
    assert.equal(
      await lockNotice.locator('a').getAttribute('href'),
      'https://hangulplanet.com/novel/tower-of-time/chapter-3'
    );

    // ArrowLeft keyboard shortcut returns to Chapter 1.
    await page.keyboard.press('ArrowLeft');
    await page.waitForFunction(() => document.querySelector('.nreader-body')?.textContent?.includes('Life on easy mode'));

    // 4. Settings persist in localStorage across reload.
    await page.getByRole('button', { name: /Settings/ }).click();
    await page.locator('.nsettings').waitFor();
    await page.getByRole('button', { name: 'Sepia', exact: true }).click();
    await page.getByRole('button', { name: 'Sans', exact: true }).click();
    await page.getByRole('button', { name: 'L', exact: true }).click();
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-reader-theme')), 'sepia');
    await page.reload();
    await page.locator('.nreader-body').waitFor();
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-reader-theme')), 'sepia');
    const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('novel.readerSettings')));
    assert.equal(saved.theme, 'sepia');
    assert.equal(saved.font, 'sans');
    assert.equal(saved.size, 20);

    // 5. Mobile 375px: no horizontal scroll across library, detail, and reader.
    await page.setViewportSize({ width: 375, height: 800 });
    const noOverflow = async label => assert.ok(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      `horizontal scroll on ${label}`
    );
    await page.goto(`${ORIGIN}/bm/novel.html#/`);
    await page.locator('.ncard').first().waitFor();
    await noOverflow('library');

    await page.goto(`${ORIGIN}/bm/novel.html#/novel?source=hangulplanet&path=%2Fnovel%2Ftower-of-time`);
    await page.locator('.nchapters-list').waitFor();
    await noOverflow('detail');

    await page.goto(`${ORIGIN}/bm/novel.html#/read?source=hangulplanet&novel=%2Fnovel%2Ftower-of-time&path=%2Fnovel%2Ftower-of-time%2Fchapter-1`);
    await page.locator('.nreader-body').waitFor();
    await noOverflow('reader');

    assert.deepEqual(errors, []);
    console.log('novel.test.js: all assertions passed (library, detail/locked, reader/prev-next, progress PUT, settings persist, 375px mobile)');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
