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
    scrollPercent: 0,
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

const novelStats = {
  source: 'hangulplanet',
  path: '/novel/tower-of-time',
  rank: 17,
  rating: 4.4,
  ratingCount: 14,
  views: 1_250_000,
  bookmarks: 3_400,
  chapters: 235,
  status: 'Ongoing',
  latestChapterName: 'Chapter 3: Early Access',
  latestChapterUpdated: '11 hours ago',
  reviewCount: 14,
  reviewAverage: 4.4
};

const longParagraphs = Array.from({ length: 18 }, (_, i) =>
  `<p>Paragraph ${i + 1}: The tower hummed quietly in the dark as the silver pendulum counted each breath across the stone hall.</p>`
).join('');

const chapters = {
  '/novel/tower-of-time/chapter-1': {
    title: 'Chapter 1: Entry',
    html: `<h3>Chapter 1: Entry</h3><p>Life on easy mode.</p><p>The clock stopped at midnight.</p>${longParagraphs}`,
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

const reviewsPage1 = {
  source: 'hangulplanet',
  sourceName: 'HangulPlanet',
  site: 'https://hangulplanet.com',
  path: '/novel/tower-of-time',
  average: 4.4,
  count: 14,
  page: 1,
  hasNextPage: true,
  items: [
    {
      author: 'Moh',
      date: '2 days ago',
      score: 5.0,
      likes: 12,
      paragraphs: [
        { text: 'Really gripping opening arc. '.repeat(22), spoiler: false }
      ]
    }
  ]
};

const reviewsPage2 = {
  ...reviewsPage1,
  page: 2,
  hasNextPage: false,
  items: [
    {
      author: 'SecondPageReader',
      date: '5 days ago',
      score: 4.0,
      likes: 3,
      paragraphs: [{ text: 'Still going strong on floor 20.', spoiler: false }]
    }
  ]
};

const royalRoadReviews = {
  matched: true,
  title: 'Tower of Time',
  path: '/fiction/44444/tower-of-time',
  site: 'https://www.royalroad.com',
  overallScore: 4.7,
  ratings: 890,
  views: 2_000_000,
  followers: 12_000,
  favorites: 4_500,
  count: 32,
  page: 1,
  hasNextPage: false,
  items: [
    {
      author: 'ArcaneReader',
      date: '1 week ago',
      score: 4.5,
      likes: 19,
      paragraphs: [
        { text: 'Great time-loop mechanics.', spoiler: false },
        { text: 'The final floor boss is actually his future self.', spoiler: true }
      ]
    }
  ]
};

async function assertNoOldPrimary(page, label) {
  const offenders = await page.evaluate(() => {
    const bad = [];
    for (const el of document.querySelectorAll('button, a')) {
      const style = getComputedStyle(el);
      if (style.backgroundColor === 'rgb(91, 116, 239)' || style.color === 'rgb(91, 116, 239)') {
        bad.push(`${el.tagName}.${el.className}`);
      }
    }
    return bad;
  });
  assert.deepEqual(offenders, [], `old primary rgb(91, 116, 239) found on ${label}`);
  // backdrop-filter / mask-image re-composite every scroll frame (GPU spikes); keep the add-on free of them.
  const heavy = await page.evaluate(() => [...document.querySelectorAll('*')]
    .filter(el => {
      const s = getComputedStyle(el);
      return (s.backdropFilter && s.backdropFilter !== 'none') || (s.maskImage && s.maskImage !== 'none');
    })
    .map(el => `${el.tagName}.${el.className}`));
  assert.deepEqual(heavy, [], `backdrop-filter/mask-image found on ${label}`);
}

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    page.setDefaultTimeout(5000);

    const errors = [];
    const progressPuts = [];
    const statusPatches = [];
    const reviewCalls = [];
    page.on('pageerror', error => errors.push(error.message));

    const mockRoutes = async route => {
      const request = route.request();
      const url = new URL(request.url());

      if (url.hostname === 'fonts.googleapis.com' || url.hostname === 'fonts.gstatic.com') {
        return route.fulfill({ status: 200, contentType: 'text/css', body: '' });
      }

      if (url.pathname.startsWith('/bm-api/novels/')) {
        const api = url.pathname.slice('/bm-api/novels'.length);
        if (api === '/library' && request.method() === 'GET') {
          return route.fulfill({ json: libraryEntries });
        }
        if (api === '/novel' && request.method() === 'GET') {
          return route.fulfill({ json: novelDetail });
        }
        if (api === '/stats' && request.method() === 'GET') {
          return route.fulfill({ json: novelStats });
        }
        if (api === '/reviews' && request.method() === 'GET') {
          reviewCalls.push(api + url.search);
          const p = Number(url.searchParams.get('page') || 1);
          return route.fulfill({ json: p === 2 ? reviewsPage2 : reviewsPage1 });
        }
        if (api === '/reviews/royalroad' && request.method() === 'GET') {
          reviewCalls.push(api + url.search);
          return route.fulfill({ json: royalRoadReviews });
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
    };
    await page.route('**/*', mockRoutes);

    // 1. Library grid renders + status filter works + no old blue primary.
    await page.goto(`${ORIGIN}/bm/novel.html#/`);
    await page.locator('.ncard').nth(1).waitFor();
    assert.equal(await page.locator('.ncard').count(), 2);
    assert.match(await page.locator(`.ncard[data-id="${ENTRY_ID}"] .ncard-continue`).innerText(), /Ch 1 of 3 · Continue/);
    await assertNoOldPrimary(page, 'novel library');
    await page.getByRole('button', { name: 'Completed', exact: true }).click();
    assert.equal(await page.locator('.ncard').count(), 1);
    assert.match(await page.locator('.ncard .xtitle').innerText(), /Abyss Mage/);
    await page.getByRole('button', { name: 'All', exact: true }).click();
    assert.equal(await page.locator('.ncard').count(), 2);

    // 2. Detail hero renders rank, stars, live stats, summary cards, locked chapter link.
    await page.locator(`.ncard[data-id="${ENTRY_ID}"] .ncard-link`).click();
    await page.locator('.nchapters-list').waitFor();
    await page.locator('.nhero-rank').waitFor();
    assert.equal(await page.locator('.ndetail-info h2').innerText(), 'Tower of Time');
    assert.match(await page.locator('.nhero-rank').innerText(), /RANK 17/);
    assert.match(await page.locator('.nhero-stars').innerText(), /4\.4/);
    assert.match(await page.locator('.nhero-stats').innerText(), /1\.3M|1\.2M|1,250,000/);
    assert.match(await page.locator('.nhero-card-chapters').innerText(), /Updated 11 hours ago/);
    assert.match(await page.locator('.nhero-card-reviews').innerText(), /HangulPlanet 4\.4 · 14 reviews/);
    assert.equal(reviewCalls.length, 0, 'reviews must NOT be fetched until reviews panel opens');
    assert.equal(await page.locator('a.nch-row').count(), 2, 'only unlocked chapters are clickable in-app rows');
    const lockedRow = page.locator('.nch-row.is-locked');
    assert.equal(await lockedRow.count(), 1);
    assert.match(await lockedRow.innerText(), /🔒 Chapter 3: Early Access/);
    assert.equal(await lockedRow.locator('a.nch-ext').getAttribute('href'), 'https://hangulplanet.com/novel/tower-of-time/chapter-3');
    await assertNoOldPrimary(page, 'novel detail');

    // Status change PATCHes API.
    await page.getByLabel('Reading status').selectOption('Plan');
    await page.waitForTimeout(100);
    assert.deepEqual(statusPatches, [{ status: 'Plan' }]);

    // 3. Reviews panel: opens on card click, Read more expands, Load more appends, Royal Road tab reveals spoiler.
    await page.locator('.nhero-card-reviews').click();
    const revPanel = page.locator('.nrev-panel[role="dialog"]');
    await revPanel.waitFor();
    await revPanel.locator('.nrev-card').first().waitFor();
    assert.ok(reviewCalls.length >= 1, 'reviews fetched on panel open');
    const tabFit = await revPanel.locator('.nrev-tabs').evaluate(n => n.scrollHeight <= n.clientHeight);
    assert.ok(tabFit, 'review tab row is not squashed or clipped');
    await assertNoOldPrimary(page, 'reviews panel');
    assert.equal(await revPanel.locator('.nrev-body.is-clamped').count(), 1);
    await revPanel.getByRole('button', { name: 'Read more' }).click();
    assert.equal(await revPanel.locator('.nrev-body.is-clamped').count(), 0);
    await revPanel.getByRole('button', { name: 'Load more' }).click();
    await revPanel.locator('.nrev-card').nth(1).waitFor();
    assert.match(await revPanel.locator('.nrev-card').nth(1).innerText(), /SecondPageReader/);

    // Switch to Royal Road tab and reveal spoiler.
    await revPanel.locator('.nrev-tab[data-source="royalroad"]').waitFor();
    await revPanel.locator('.nrev-tab[data-source="royalroad"]').click();
    const spoilerBox = revPanel.locator('.nrev-spoiler');
    await spoilerBox.waitFor();
    assert.equal(await spoilerBox.evaluate(n => n.classList.contains('is-revealed')), false);
    await spoilerBox.locator('.nrev-spoiler-btn').click();
    assert.equal(await spoilerBox.evaluate(n => n.classList.contains('is-revealed')), true);
    await page.keyboard.press('Escape');
    await revPanel.waitFor({ state: 'hidden' });

    // 4. Reader: header, sanitized HTML, progress PUT, bottom bar hide/show on scroll + tap.
    await page.locator('a.nch-row').first().click();
    await page.locator('.nreader-body').waitFor();
    assert.equal(await page.locator('.nreader-ch-heading').innerText(), 'Chapter 1');
    assert.equal(await page.locator('.nreader-ch-subtitle').innerText(), 'Entry');
    assert.match(await page.locator('.nreader-body').innerText(), /Life on easy mode\./);
    assert.ok(progressPuts.length >= 1, 'progress PUT fires on reader entry');
    assert.equal(progressPuts[0].chapterPath, '/novel/tower-of-time/chapter-1');
    assert.equal(progressPuts[0].chapterIndex, 0);
    await assertNoOldPrimary(page, 'novel reader');

    // Mouse device: dock hidden until the pointer nears the bottom edge; scrolling and text clicks don't reveal it.
    const dockWrap = page.locator('.nreader-dock-wrap');
    const dockHidden = p => p.locator('.nreader-dock-wrap').evaluate(n => n.classList.contains('is-hidden'));
    await page.mouse.move(640, 200);
    assert.equal(await dockHidden(page), true, 'dock hidden without hover');
    await page.evaluate(() => window.scrollTo(0, 320));
    await page.waitForTimeout(80);
    await page.evaluate(() => window.scrollTo(0, 120));
    await page.waitForTimeout(80);
    assert.equal(await dockHidden(page), true, 'scroll up does not reveal dock on mouse devices');
    await page.locator('.nreader-body p').first().click();
    assert.equal(await dockHidden(page), true, 'text click does not reveal dock on mouse devices');
    await page.mouse.move(640, 780);
    assert.equal(await dockHidden(page), false, 'dock shows when pointer nears bottom');
    await page.mouse.move(640, 300);
    assert.equal(await dockHidden(page), true, 'dock hides when pointer leaves');
    await dockWrap.locator('a[href], button:not(:disabled)').first().focus();
    assert.equal(await dockHidden(page), false, 'keyboard focus reveals dock');
    await page.evaluate(() => document.activeElement.blur());
    assert.equal(await dockHidden(page), true, 'dock hides again when focus leaves');
    await page.mouse.move(640, 780);

    // Touch device: scroll down hides, tap shows, scroll up shows (unchanged behaviour).
    const touchContext = await browser.newContext({ viewport: { width: 390, height: 800 }, isMobile: true, hasTouch: true });
    const touchPage = await touchContext.newPage();
    touchPage.setDefaultTimeout(5000);
    touchPage.on('pageerror', error => errors.push(error.message));
    await touchPage.route('**/*', mockRoutes);
    await touchPage.goto(page.url());
    await touchPage.locator('.nreader-body').waitFor();
    await touchPage.evaluate(() => window.scrollTo(0, 320));
    await touchPage.waitForTimeout(80);
    assert.equal(await dockHidden(touchPage), true, 'touch: dock hides when scrolling down');
    await touchPage.locator('.nreader-body p').first().tap();
    assert.equal(await dockHidden(touchPage), false, 'touch: dock reappears on tap');
    await touchPage.evaluate(() => window.scrollTo(0, 560));
    await touchPage.waitForTimeout(80);
    assert.equal(await dockHidden(touchPage), true);
    await touchPage.evaluate(() => window.scrollTo(0, 420));
    await touchPage.waitForTimeout(80);
    assert.equal(await dockHidden(touchPage), false, 'touch: dock reappears on scroll up');
    await touchContext.close();

    // 5. Chapter list slide-in panel: current highlight, search filter, locked link, Esc closes.
    await page.getByRole('button', { name: 'Chapters' }).click();
    const chDrawer = page.locator('.nch-drawer[role="dialog"]');
    await chDrawer.waitFor();
    assert.match(await chDrawer.locator('.nch-drawer-row.is-current').innerText(), /Chapter 1: Entry/);
    assert.equal(
      await chDrawer.locator('.nch-drawer-row.is-locked a.nch-drawer-ext').getAttribute('href'),
      'https://hangulplanet.com/novel/tower-of-time/chapter-3'
    );
    await chDrawer.getByLabel('Search chapters').fill('Gate');
    assert.equal(await chDrawer.locator('.nch-drawer-item').count(), 1);
    await page.keyboard.press('Escape');
    await chDrawer.waitFor({ state: 'hidden' });

    // 6. Settings popover: theme swatch changes reader bg, size stepper, width, align, progress bar toggle, persist across reload, reset.
    await page.mouse.move(640, 790);
    await page.getByRole('button', { name: 'Reader settings' }).click();
    const popover = page.locator('.nset-popover');
    await popover.waitFor();
    const charcoalBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    await popover.locator('.nset-swatch[data-theme="paper"]').click();
    const paperBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    assert.notEqual(paperBg, charcoalBg, 'theme swatch must change reader background');
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-reader-theme')), 'paper');

    await popover.getByRole('button', { name: 'Increase font size' }).click();
    assert.equal(await popover.locator('.nset-step-val').innerText(), '21');
    await popover.getByRole('button', { name: 'Wide', exact: true }).click();
    await popover.getByRole('button', { name: 'Justify', exact: true }).click();
    const progSwitch = popover.getByRole('switch', { name: 'Progress bar' });
    await progSwitch.click();
    assert.equal(await page.locator('.nreader-progress').isHidden(), true, 'progress bar hides when toggled off');

    await page.reload();
    await page.locator('.nreader-body').waitFor();
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-reader-theme')), 'paper');
    const savedV2 = await page.evaluate(() => JSON.parse(localStorage.getItem('novel.readerSettings.v2')));
    assert.equal(savedV2.theme, 'paper');
    assert.equal(savedV2.size, 21);
    assert.equal(savedV2.width, 'wide');
    assert.equal(savedV2.align, 'justify');
    assert.equal(savedV2.progressBar, false);

    // Reset button restores defaults.
    await page.mouse.move(640, 790);
    await page.getByRole('button', { name: 'Reader settings' }).click();
    await page.locator('.nset-popover').waitFor();
    await page.locator('.nset-reset').click();
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-reader-theme')), 'charcoal');
    assert.equal(await page.locator('.nset-step-val').innerText(), '20');

    // Old v1 key migration.
    await page.evaluate(() => {
      localStorage.removeItem('novel.readerSettings.v2');
      localStorage.setItem('novel.readerSettings', JSON.stringify({
        theme: 'sepia', font: 'sans', size: 22, lineHeight: 2.0, width: 880
      }));
    });
    await page.reload();
    await page.locator('.nreader-body').waitFor();
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-reader-theme')), 'sepia');
    const migrated = await page.evaluate(() => JSON.parse(localStorage.getItem('novel.readerSettings.v2')));
    assert.equal(migrated.theme, 'sepia');
    assert.equal(migrated.font, 'inter');
    assert.equal(migrated.size, 22);
    assert.equal(migrated.spacing, 'relaxed');
    assert.equal(migrated.width, 'wide');

    // 7. Navigate to next chapter via button -> Chapter 2 shows locked next notice + Open on site; ArrowLeft returns.
    await page.getByRole('link', { name: /Next chapter/ }).click();
    await page.waitForFunction(() => document.querySelector('.nreader-body')?.textContent?.includes('iron gate'));
    const lockNotice = page.locator('.nlock-notice');
    await lockNotice.waitFor();
    assert.equal(await lockNotice.locator('a').getAttribute('href'), 'https://hangulplanet.com/novel/tower-of-time/chapter-3');
    await page.keyboard.press('ArrowLeft');
    await page.waitForFunction(() => document.querySelector('.nreader-body')?.textContent?.includes('Life on easy mode'));

    // Synopsis renders one <p> per paragraph; long text is clamped behind Show more / Show less.
    const syn = await page.evaluate(async () => {
      const { renderSynopsis } = await import('/bm/novel-hero.js');
      const s = renderSynopsis(`First line.\n\nSecond line.\n${'x'.repeat(700)}`);
      document.body.appendChild(s);
      const toggle = s.querySelector('.nsyn-toggle');
      const before = s.querySelector('.nsyn-body').classList.contains('is-clamped');
      toggle.click();
      const result = { paras: s.querySelectorAll('.nsyn-body p').length, before, after: s.querySelector('.nsyn-body').classList.contains('is-clamped'), label: toggle.textContent };
      s.remove();
      return result;
    });
    assert.deepEqual(syn, { paras: 3, before: true, after: false, label: 'Show less' });

    // 8. Mobile 375px: no horizontal scroll across library, detail, and reader.
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
    console.log('novel.test.js: all assertions passed (library, detail hero/stats/locked, reviews panel, reader dock/chapters/settings/migration, neutral palette, 375px mobile)');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
