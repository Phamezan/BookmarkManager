'use strict';

// Run with Node and Playwright installed: node tests/suwayomi-addon/explore.test.js
// Add --preview to browse the page interactively with sample data.
// Add --shots=<dir> to write screenshots (Home, Browse + filters, detail drawer) at 1920, 2560 and 375px wide.
// Preview mode opens a maximized window with no fixed viewport so the page fills the real screen.
// Serves the real add-on files through request interception; no running API is needed.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const interactive = process.argv.includes('--preview');
const shotsArg = process.argv.find(a => a.startsWith('--shots='));
const shotsDir = shotsArg ? shotsArg.slice('--shots='.length) : null;

const addonDir = path.join(__dirname, '../../src/BookmarkManager.Client/wwwroot/suwayomi-addon');
const ORIGIN = 'http://explore.test';
const XSS = '<img src=x onerror="window.injected=true">';

const escapeRe = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
// Real AniList trending data (tests/suwayomi-addon/fixtures/explore-sample.json); ids are remapped per call site.
const sample = JSON.parse(require('node:fs').readFileSync(path.join(__dirname, 'fixtures/explore-sample.json'), 'utf8'));
const item = (id, extra = {}) => ({
  ...sample[id % sample.length], anilistId: id, siteUrl: `https://anilist.co/manga/${id}`, library: null, listState: null, becauseOf: [], ...extra
});
const titleOf = id => item(id).title;

const shelves = [
  { key: 'trending', title: 'Trending now', subtitle: 'What everyone reads', browseQuery: 'sort=trending',
    items: Array.from({ length: 18 }, (_, i) => item(i + 1, i === 0 ? { library: { mangaId: 77, read: 12, latest: 40, sourceName: 'Asura' } } : i === 1 ? { coverUrl: 'javascript:alert(1)' } : {})) },
  { key: 'top-finished', title: 'Best completed series', subtitle: 'Finished and loved', browseQuery: 'status=FINISHED&sort=score',
    items: Array.from({ length: 18 }, (_, i) => item(i + 20, { status: 'FINISHED' })) }
];
const detailOf = id => ({
  ...item(id, { description: `Line one.\n${XSS}`, tags: ['Isekai', '"><img src=x onerror=window.injected=true>'] }),
  startDate: '2015-04-01', endDate: '2019-09', staff: [{ name: 'Writer San', role: 'Story' }],
  relations: [{ relationType: 'SEQUEL', item: item(900) }], recommendations: [item(901), item(902)],
  externalLinks: [{ site: 'Official', url: 'https://example.com' }, { site: 'Bad', url: 'javascript:alert(1)' }]
});
const sources = [
  { mangaId: 501, title: titleOf(1), sourceName: 'Asura Scans', inLibrary: false, isExactMatch: true },
  { mangaId: 502, title: `${titleOf(1)}: Remastered`, sourceName: 'MangaDex', inLibrary: false, isExactMatch: false }
];
// Real AniList genres + non-adult tags (tests/suwayomi-addon/fixtures/explore-meta.json).
const meta = JSON.parse(require('node:fs').readFileSync(path.join(__dirname, 'fixtures/explore-meta.json'), 'utf8'));
const THIS_YEAR = new Date().getFullYear();

(async () => {
  const browser = await chromium.launch({ headless: !interactive, args: interactive ? ['--start-maximized'] : [] });
  try {
    const context = await browser.newContext(interactive ? { viewport: null } : { viewport: { width: 1440, height: 900 }, hasTouch: true });
    const page = await context.newPage();
    page.setDefaultTimeout(5000);
    const errors = [];
    const calls = [];
    const puts = [];
    const posts = [];
    const novelCalls = [];
    const novelLibPuts = [];
    let failShelves = false;
    let failMeta = false;
    page.on('pageerror', error => errors.push(error.message));

    await page.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.pathname.startsWith('/bm-api/novels/')) {
        const api = url.pathname.slice('/bm-api/novels'.length);
        novelCalls.push(api + url.search);
        if (api === '/popular') {
          return route.fulfill({
            json: [
              { source: 'hangulplanet', sourceName: 'HangulPlanet', title: 'Popular Tower Novel', path: '/novel/popular-tower', cover: null }
            ]
          });
        }
        if (api === '/catalog') {
          return route.fulfill({
            json: {
              page: 1,
              hasNextPage: false,
              total: 1,
              items: [
                {
                  id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                  provider: 'Novelfire',
                  title: 'Shadow Monarch Novel',
                  alternateTitles: ['Kage no Ou'],
                  authors: ['Chugong'],
                  mediaType: 'Webnovel',
                  coverUrl: null,
                  synopsis: 'Arise from the shadows.',
                  genres: ['Action', 'Fantasy'],
                  rating: 4.8,
                  status: 'Completed',
                  latestChapter: 'Ch 270',
                  inLibrary: false
                }
              ]
            }
          });
        }
        if (api === '/search') {
          return route.fulfill({
            json: [
              { source: 'hangulplanet', sourceName: 'HangulPlanet', title: 'Shadow Monarch Novel', path: '/novel/shadow-monarch', cover: null, isExactMatch: true }
            ]
          });
        }
        if (api === '/library' && request.method() === 'PUT') {
          novelLibPuts.push(request.postDataJSON());
          return route.fulfill({ json: { id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', ...request.postDataJSON() } });
        }
        return route.fulfill({ status: 404, json: {} });
      }
      if (url.pathname.startsWith('/bm-api/explore/')) {
        const api = url.pathname.slice('/bm-api/explore'.length);
        calls.push(api + url.search);
        if (api === '/shelves') {
          return failShelves ? route.fulfill({ status: 502, json: { error: 'AniList unavailable' } }) : route.fulfill({ json: shelves });
        }
        if (api === '/browse') {
          const pageNo = Number(url.searchParams.get('page') || 1);
          return route.fulfill({ json: { page: pageNo, hasNextPage: pageNo < 2, total: 36, items: Array.from({ length: 18 }, (_, i) => item(100 + (pageNo - 1) * 18 + i)) } });
        }
        if (api === '/meta') return failMeta ? route.fulfill({ status: 500, json: {} }) : route.fulfill({ json: meta });
        if (api === '/for-you') return route.fulfill({ json: { page: 1, hasNextPage: false, total: 1, items: [item(300, { becauseOf: ['Solo Leveling', 'Tower of God'] })] } });
        if (api === '/random') return route.fulfill({ json: item(5) });
        if (api === '/list') return route.fulfill({ json: [item(400, { listState: 'saved' })] });
        if (api.startsWith('/list/') && request.method() === 'PUT') { puts.push(request.postDataJSON()); return route.fulfill({ status: 204 }); }
        if (api === '/library' && request.method() === 'POST') { posts.push(request.postDataJSON()); return route.fulfill({ status: 204 }); }
        if (/^\/series\/\d+\/sources$/.test(api)) return route.fulfill({ json: sources });
        if (/^\/series\/\d+$/.test(api)) return route.fulfill({ json: detailOf(Number(api.split('/')[2])) });
        return route.fulfill({ status: 404, json: {} });
      }
      if (url.hostname.endsWith('anilist.co')) return route.continue(); // real cover / banner images
      if (url.pathname.startsWith('/bm/')) {
        const name = url.pathname === '/bm/' ? 'explore.html' : path.basename(url.pathname);
        return route.fulfill({
          body: await fs.readFile(path.join(addonDir, name)),
          contentType: name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html'
        });
      }
      return route.fulfill({ contentType: 'text/html', body: '<p>Suwayomi page (stub)</p>' });
    });

    const shot = async name => { if (shotsDir) await page.screenshot({ path: path.join(shotsDir, name) }); };
    const home = `${ORIGIN}/bm/explore.html`;
    if (shotsDir) await fs.mkdir(shotsDir, { recursive: true });

    await page.goto(home);
    await page.locator('.xshelf').nth(1).waitFor();

    if (interactive) {
      console.log('Interactive preview opened. Close the browser window to finish.');
      await new Promise(resolve => page.once('close', resolve));
      return;
    }

    // Home: shelves render, shelf scrolls inside itself, hostile cover URL is dropped.
    assert.equal(await page.locator('.xshelf').count(), 2);
    assert.equal(await page.locator('.xshelf').first().locator('.xcard').count(), 18);
    assert.equal(await page.locator('.xcard[data-id="2"] img').count(), 0, 'javascript: cover must not become an img');
    assert.match(await page.locator('.xcard[data-id="1"] .xbadge-lib').innerText(), /Reading 12\/40/);
    // Score and library badge sit side by side in the cover overlay (never overlapping).
    const [score, lib] = await Promise.all(['.xscore', '.xbadge-lib'].map(sel => page.locator(`.xcard[data-id="1"] ${sel}`).boundingBox()));
    assert.ok(score.x + score.width <= lib.x + 1, 'score and library badge must not overlap');
    // Hero spotlight: 5 slides, dots, first is active, shows #1 trending title.
    assert.equal(await page.locator('.xhero-slide').count(), 5);
    assert.equal(await page.locator('.xhero-dot').count(), 5);
    assert.equal(await page.locator('.xhero-slide.is-active .xhero-title').textContent(), titleOf(1));
    await page.locator('.xhero-dot').nth(2).click();
    assert.equal(await page.locator('.xhero-slide.is-active .xhero-title').textContent(), titleOf(3));
    assert.equal(await page.locator('.xhero-slide:not(.is-active)').first().evaluate(n => n.inert), true, 'inactive slides are inert');
    // Trending shelf shows rank numbers.
    assert.equal(await page.locator('.xcard[data-id="1"] .xrank').innerText(), '#1');
    await page.waitForTimeout(150);
    await shot('home-1440.png');

    // Card Enter opens the drawer; sources load; description stays inert; links are filtered.
    await page.locator('.xcard[data-id="1"]').focus();
    await page.keyboard.press('Enter');
    const drawer = page.locator('.xdrawer[role="dialog"]');
    await drawer.waitFor();
    assert.match(page.url(), /[?&]id=1(&|$)/, 'drawer id is in the URL');
    await drawer.locator('.xhit').first().waitFor();
    assert.match(await drawer.locator('.xhits').first().innerText(), /Best match/);
    assert.match(await drawer.innerText(), /Other results/);
    assert.match(await drawer.locator('.xdesc').innerText(), /<img src=x/);
    assert.equal(await drawer.locator('.xdesc img').count(), 0);
    assert.equal(await drawer.locator('.xchips img, .xstaff img, .xhits img').count(), 0);
    assert.equal(await drawer.locator('a[href^="javascript:"]').count(), 0);
    assert.equal(await page.evaluate(() => window.injected), undefined, 'XSS payload must not execute');
    assert.equal(await page.locator('#page').evaluate(n => n.inert), true, 'page behind the drawer is inert');
    await page.waitForTimeout(150);
    await shot('detail-1440.png');

    // Add to library posts the Suwayomi id, then the card badge and button update.
    await drawer.getByRole('button', { name: new RegExp(`Add to library: ${escapeRe(titleOf(1))} \\(Asura Scans\\)`) }).click();
    await drawer.getByRole('button', { name: new RegExp(`In library: ${escapeRe(titleOf(1))} \\(Asura Scans\\)`) }).waitFor();
    assert.deepEqual(posts, [{ mangaId: 501 }]);

    // Focus is trapped, Esc closes and restores focus, the URL loses the id.
    for (let i = 0; i < 40; i++) await page.keyboard.press('Tab');
    assert.equal(await drawer.evaluate(n => n.contains(document.activeElement)), true, 'focus stays in the drawer');
    await page.keyboard.press('Escape');
    await drawer.waitFor({ state: 'hidden' });
    assert.doesNotMatch(page.url(), /id=/);
    assert.equal(await page.evaluate(() => document.activeElement?.dataset?.id), '1');

    // Back button closes the drawer; relation click pushes a second entry.
    await page.locator('.xcard[data-id="3"]').click();
    await drawer.waitFor();
    await page.goBack();
    await drawer.waitFor({ state: 'hidden' });
    await page.locator('.xcard[data-id="3"]').click();
    await drawer.locator('.xmini').first().click();
    await page.waitForFunction(() => /id=900/.test(location.search));
    await page.keyboard.press('Escape');
    await drawer.waitFor({ state: 'hidden' });
    assert.doesNotMatch(page.url(), /id=/);
    await page.goBack().catch(() => {});

    // Dismiss hides the card and sends the state; Undo restores it.
    await page.goto(home);
    await page.locator('.xshelf').nth(1).waitFor();
    puts.length = 0;
    const target = page.locator('.xshelf').first().locator('.xcard[data-id="4"]');
    await target.hover();
    await target.getByRole('button', { name: new RegExp(`Not interested: ${escapeRe(titleOf(4))}`) }).click();
    await target.waitFor({ state: 'hidden' });
    assert.equal(puts[0].state, 'dismissed');
    assert.equal(puts[0].title, titleOf(4));
    await page.getByRole('button', { name: 'Undo' }).click();
    await target.waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelector('.xcard[data-id="4"]').style.display !== 'none');
    assert.equal(puts[1].state, null);

    // Deep link opens the drawer directly.
    await page.goto(`${home}?id=7`);
    await drawer.waitFor();
    assert.match(await drawer.locator('h2').first().innerText(), new RegExp(escapeRe(titleOf(7))));
    await drawer.getByRole('button', { name: 'Close details' }).click();
    await drawer.waitFor({ state: 'hidden' });
    assert.doesNotMatch(page.url(), /id=/);

    // See all -> Browse applies the shelf query.
    await page.goto(home);
    await page.locator('.xshelf').nth(1).waitFor();
    await page.getByRole('button', { name: 'See all: Best completed series' }).click();
    await page.locator('.xgrid .xcard:not(.xskeleton)').first().waitFor();
    assert.ok(calls.some(c => /\/browse\?.*sort=score/.test(c) && /status=FINISHED/.test(c)));
    assert.equal(await page.locator('.xtri').first().isVisible(), true);

    // Infinite scroll fetches page 2 exactly once; result count shows.
    await page.waitForFunction(() => document.querySelectorAll('.xgrid .xcard:not(.xskeleton)').length === 36);
    const browseCalls = calls.filter(c => c.startsWith('/browse?'));
    assert.equal(browseCalls.filter(c => /page=2/.test(c)).length, 1, 'page 2 requested once');
    assert.equal(browseCalls.filter(c => /page=1/.test(c)).length >= 1, true);
    assert.match(await page.locator('#count').innerText(), /36 results/);

    // Filter changes are debounced, so wait for the request itself rather than a fixed delay.
    const browseReq = test => page.waitForRequest(r => r.url().includes('/bm-api/explore/browse?') && test(new URL(r.url()).searchParams));
    const lastBrowse = () => calls.filter(c => c.startsWith('/browse?')).pop();

    // Tri-state genre: include -> exclude -> off, reflected in query params and in aria-label.
    const genre = page.locator('.xtri[data-name="action"]');
    let req = browseReq(p => p.get('genres') === 'Action');
    await genre.click();
    await req;
    assert.equal(await genre.getAttribute('data-state'), 'include');
    assert.match(await genre.getAttribute('aria-label'), /^Action, included\. Activate to exclude/);
    req = browseReq(p => p.get('excludeGenres') === 'Action' && !p.has('genres'));
    await genre.click();
    await req;
    assert.equal(await genre.getAttribute('data-state'), 'exclude');
    assert.equal(await genre.getAttribute('aria-pressed'), 'true');
    await shot('browse-1440.png');
    await genre.click();
    await page.waitForFunction(() => !/enres=Action/.test(location.search));
    assert.equal(await genre.getAttribute('data-state'), 'off');

    // A burst of panel clicks sends one page-1 request (debounced), with the final state.
    await page.waitForTimeout(400);
    const beforeBurst = calls.length;
    req = browseReq(p => p.get('status') === 'FINISHED,RELEASING,HIATUS');
    for (const name of ['Ongoing', 'Hiatus']) await page.locator('#filters').getByRole('button', { name, exact: true }).click();
    await req;
    await page.waitForTimeout(400);
    assert.equal(calls.slice(beforeBurst).filter(c => c.startsWith('/browse?') && /page=1/.test(c)).length, 1, 'panel changes are debounced');
    await page.locator('#filters').getByRole('button', { name: 'Clear status' }).click();
    await page.waitForFunction(() => !/status=/.test(location.search));

    // Debounced search: one request for a burst of typing.
    const before = calls.filter(c => c.startsWith('/browse?')).length;
    await page.locator('#search').pressSequentially('solo', { delay: 40 });
    await page.waitForFunction(() => /q=solo/.test(location.search));
    await page.waitForTimeout(150);
    const firstPages = calls.filter(c => c.startsWith('/browse?') && /q=solo/.test(c) && /page=1/.test(c));
    assert.equal(firstPages.length, 1, 'search is debounced');
    assert.ok(calls.filter(c => c.startsWith('/browse?')).length > before);

    // Sort: "Best match" only with a query; otherwise the effective default (Popularity) is named.
    const sortSel = page.getByRole('combobox', { name: 'Sort by' });
    assert.equal(await sortSel.locator('option').first().textContent(), 'Best match');

    // Tag combobox: empty input lists categories; typing ranks prefix matches first; highlights the match.
    const combo = page.getByRole('combobox', { name: 'Add a tag' });
    await combo.click();
    assert.equal(await combo.getAttribute('aria-expanded'), 'true');
    assert.ok(await page.locator('.xcb-group').count() > 10, 'grouped browse list shows categories');
    assert.equal(await page.locator('.xcb-group').filter({ hasText: 'Theme · Sci-Fi · Mecha' }).count(), 1, 'category is prettified');
    await combo.fill('school');
    const names = await page.locator('.xcb-opt .xcb-name').allTextContents();
    assert.deepEqual(names, ['School', 'School Club', 'Boarding School'], 'prefix matches rank above word matches');
    assert.equal(await page.locator('.xcb-opt').first().locator('mark').textContent(), 'School');
    await combo.fill('zzqx');
    assert.equal(await page.locator('.xcb-empty').textContent(), 'No tags match "zzqx"');
    // Diacritic-insensitive; ArrowDown + Enter includes.
    await combo.fill('isékai');
    await page.keyboard.press('ArrowDown');
    assert.equal(await combo.getAttribute('aria-activedescendant'), await page.locator('.xcb-opt').first().getAttribute('id'));
    req = browseReq(p => p.get('tags') === 'Isekai');
    await page.keyboard.press('Enter');
    await req;
    assert.equal(await combo.inputValue(), '');
    // Shift+Enter excludes.
    await combo.fill('time');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    req = browseReq(p => p.get('excludeTags') === 'Time Manipulation');
    await page.keyboard.press('Shift+Enter');
    await req;
    assert.equal(await page.locator('.xsel[data-state="exclude"] .xsel-name').textContent(), 'Time Manipulation');
    // Already selected tags are hidden from suggestions.
    await combo.fill('isekai');
    assert.deepEqual(await page.locator('.xcb-opt .xcb-name').allTextContents(), ['Reverse Isekai']);
    // Esc closes, a second Esc clears.
    await page.keyboard.press('Escape');
    assert.equal(await combo.getAttribute('aria-expanded'), 'false');
    assert.equal(await combo.inputValue(), 'isekai');
    await page.keyboard.press('Escape');
    assert.equal(await combo.inputValue(), '');
    // Backspace on an empty input removes the last selected tag.
    await page.keyboard.press('Backspace');
    await page.waitForFunction(() => !/excludeTags=/.test(location.search));
    assert.equal(await page.locator('.xsel').count(), 1);
    // Mouse: the row's − button excludes; clicking outside closes.
    await combo.fill('magic');
    req = browseReq(p => p.get('excludeTags') === 'Magic');
    await page.locator('.xcb-opt').first().hover();
    await page.locator('.xcb-opt').first().locator('.xcb-ex').click();
    await req;
    await page.locator('.xbrowse-title').click();
    assert.equal(await combo.getAttribute('aria-expanded'), 'false');

    // Selected chip flips include <-> exclude (request follows), × removes.
    const isekaiChip = page.getByRole('button', { name: 'Isekai, included. Toggle to exclude' });
    req = browseReq(p => (p.get('excludeTags') || '').split(',').includes('Isekai') && !p.has('tags'));
    await isekaiChip.click();
    await req;
    await page.getByRole('button', { name: 'Isekai, excluded. Toggle to include' }).waitFor();
    await page.getByRole('button', { name: 'Remove Magic' }).click();
    await page.waitForFunction(() => !/Magic/.test(location.search));

    // Pills above the grid stay in sync with the panel.
    await page.getByRole('button', { name: 'Remove filter: Not Isekai' }).click();
    await page.waitForFunction(() => !/Isekai/.test(location.search));
    assert.equal(await page.locator('.xsel').count(), 0);

    // Chapter presets show as selected when the values match; toggling a pressed preset clears it.
    const medium = page.getByRole('button', { name: 'Medium 50–150' });
    await medium.click();
    await page.waitForFunction(() => /minChapters=50/.test(location.search) && /maxChapters=150/.test(location.search));
    assert.equal(await medium.getAttribute('aria-pressed'), 'true');
    assert.equal(await page.getByLabel('Min', { exact: true }).inputValue(), '50');

    // min > max: inline error, nothing sent; fixing it applies.
    const minCh = page.getByLabel('Min', { exact: true });
    const maxCh = page.getByLabel('Max', { exact: true });
    await page.waitForTimeout(400);
    const beforeInvalid = calls.length;
    await minCh.fill('200');
    await maxCh.fill('100');
    await maxCh.press('Enter');
    await page.getByText("Min chapters can't be higher than max. Fix one to apply.").waitFor();
    assert.equal(await maxCh.getAttribute('aria-invalid'), '');
    await page.waitForTimeout(500);
    assert.equal(calls.slice(beforeInvalid).filter(c => /minChapters=200/.test(c)).length, 0, 'invalid range is not sent');
    assert.match(page.url(), /minChapters=50/);
    if (shotsDir) { await minCh.scrollIntoViewIfNeeded(); await shot('filters-1440-error.png'); }
    req = browseReq(p => p.get('minChapters') === '200' && p.get('maxChapters') === '300');
    await maxCh.fill('300');
    await maxCh.press('Enter');
    await req;
    assert.equal(await maxCh.getAttribute('aria-invalid'), null);
    assert.equal(await medium.getAttribute('aria-pressed'), 'false');
    // Clamp + non-digits stripped.
    await maxCh.fill('7,500');
    assert.equal(await maxCh.inputValue(), '7500');
    await maxCh.press('Enter');
    await page.waitForFunction(() => /maxChapters=5000/.test(location.search));
    // Year preset lights up when typed values match it.
    const fromYear = page.getByLabel('From', { exact: true });
    await fromYear.fill(String(THIS_YEAR - 4));
    await fromYear.press('Enter');
    await page.waitForFunction(y => location.search.includes(`yearFrom=${y}`), THIS_YEAR - 4);
    assert.equal(await page.getByRole('button', { name: 'Last 5 yrs' }).getAttribute('aria-pressed'), 'true');

    // Score slider: keyboard accessible with a spoken value.
    const scoreRange = page.getByRole('slider', { name: 'Minimum score' });
    await scoreRange.focus();
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(() => /minScore=5(&|$)/.test(location.search));
    assert.equal(await scoreRange.getAttribute('aria-valuetext'), '5 or higher');
    await page.getByRole('button', { name: '70+' }).click();
    await page.waitForFunction(() => /minScore=70/.test(location.search));

    // Switches + section collapse state is remembered.
    const hideLib = page.getByRole('switch', { name: 'Hide my library' });
    await hideLib.click();
    assert.equal(await hideLib.getAttribute('aria-checked'), 'true');
    await page.waitForFunction(() => /hideLibrary=true/.test(location.search));
    const optToggle = page.locator('#filters .xf-sec[data-key="options"] .xf-sec-toggle');
    assert.equal(await optToggle.getAttribute('aria-label'), 'Options, 1 active');
    await optToggle.click();
    assert.equal(await optToggle.getAttribute('aria-expanded'), 'false');
    assert.match(await page.evaluate(() => localStorage.getItem('explore.filterSections')), /"options":false/);
    await optToggle.click();

    // Reset all clears everything; Undo restores it.
    const filteredUrl = page.url();
    await page.locator('#filters').getByRole('button', { name: 'Reset all' }).click();
    await page.waitForFunction(() => !/minScore|hideLibrary|Chapters/.test(location.search));
    assert.equal(await minCh.inputValue(), '');
    await page.getByRole('button', { name: 'Undo' }).click();
    await page.waitForFunction(url => location.href === url, filteredUrl);
    assert.equal(await minCh.inputValue(), '200');

    // Surprise me opens a detail drawer.
    await page.getByRole('button', { name: 'Surprise me' }).click();
    await drawer.waitFor();
    assert.match(page.url(), /id=5/);
    await page.keyboard.press('Escape');
    await drawer.waitFor({ state: 'hidden' });

    // For you shows the "because" line; Saved lists stored items.
    await page.getByRole('button', { name: 'For you', exact: true }).click();
    await page.locator('.xbecause').waitFor();
    assert.match(await page.locator('.xbecause').innerText(), /Because you read Solo Leveling, Tower of God/);
    await page.getByRole('button', { name: 'Saved', exact: true }).click();
    await page.locator('.xcard[data-id="400"]').waitFor();

    // 502 -> friendly banner with Retry that recovers.
    failShelves = true;
    await page.goto(home);
    await page.getByText('AniList is busy, try again in a minute.').waitFor();
    failShelves = false;
    await page.getByRole('button', { name: 'Retry' }).click();
    await page.locator('.xshelf').first().waitFor();

    // /meta failure: genres + tags show an error with Retry; the rest of the panel keeps working.
    failMeta = true;
    await page.goto(`${home}?tab=browse`);
    await page.getByText("Couldn't load tags.").waitFor();
    assert.equal(await page.getByText("Couldn't load genres.").isVisible(), true);
    assert.equal(await page.getByRole('combobox', { name: 'Add a tag' }).isVisible(), false);
    req = browseReq(p => p.get('status') === 'FINISHED');
    await page.locator('#filters').getByRole('button', { name: 'Completed', exact: true }).click();
    await req;
    failMeta = false;
    await page.getByRole('button', { name: 'Retry loading tags' }).click();
    await page.getByRole('combobox', { name: 'Add a tag' }).waitFor();
    assert.equal(await page.locator('.xgenres-box .xtri').count(), meta.genres.length);
    assert.equal(await page.getByText("Couldn't load tags.").isVisible(), false);

    // URL edge cases: unknown names stay visible and removable; include+exclude of one name -> exclude wins.
    await page.goto(`${home}?tab=browse&tags=Isekai,Made%20Up%20Tag&excludeTags=Isekai&genres=Action,Nonsense&excludeGenres=Action`);
    await page.locator('.xsel').first().waitFor();
    await page.locator('.xgenres-box .xtri[data-name="action"]').waitFor();
    assert.equal(await page.locator('.xgenres-box .xtri[data-name="action"]').getAttribute('data-state'), 'exclude');
    assert.equal(await page.locator('.xsel[data-state="exclude"] .xsel-name').textContent(), 'Isekai');
    assert.equal(await page.locator('.xsel[data-state="include"].is-unknown .xsel-name').textContent(), 'Made Up Tag');
    assert.equal(await page.locator('.xtri.is-unknown[data-name="nonsense"]').getAttribute('data-state'), 'include');
    assert.ok(calls.some(c => c.startsWith('/browse?') && /excludeTags=Isekai/.test(c) && !/[?&]tags=Isekai/.test(c)), 'conflict resolved before the request');
    await page.getByRole('button', { name: 'Remove Made Up Tag' }).click();
    await page.waitForFunction(() => !/Made/.test(location.search));
    await page.locator('.xtri[data-name="nonsense"]').click(); // include -> exclude
    await page.locator('.xtri[data-name="nonsense"]').click(); // exclude -> off: chip disappears
    await page.waitForFunction(() => !/Nonsense/.test(location.search) && !document.querySelector('.xtri[data-name="nonsense"]'));
    // Very long tag names truncate instead of widening the panel.
    const panelWidth = await page.locator('#filters').evaluate(n => n.getBoundingClientRect().width);
    await page.goto(`${home}?tab=browse&tags=${'Extraordinarily Long Made Up Tag Name '.repeat(4).trim()}`);
    await page.locator('.xsel').first().waitFor();
    assert.equal(await page.locator('#filters').evaluate(n => n.getBoundingClientRect().width), panelWidth);
    assert.ok(await page.locator('.xsel-name').evaluate(n => n.scrollWidth > n.clientWidth), 'long name truncates');

    // Sort without a query names the real default.
    const sortNoQuery = page.getByRole('combobox', { name: 'Sort by' });
    assert.equal(await sortNoQuery.locator('option').first().textContent(), 'Popularity');
    assert.equal(await sortNoQuery.locator('option[value="popularity"]').evaluate(o => o.hidden), true);

    // Mobile: no horizontal scroll anywhere, filter drawer, 2-col grid, drawer fits.
    await page.setViewportSize({ width: 375, height: 800 });
    const noOverflow = async label => assert.ok(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `horizontal scroll on ${label}`);
    await page.goto(home);
    await page.locator('.xshelf').nth(1).waitFor();
    await noOverflow('home');
    await page.waitForTimeout(150);
    await shot('home-375.png');
    await page.getByRole('button', { name: 'Browse', exact: true }).click();
    await page.locator('.xgrid .xcard:not(.xskeleton)').first().waitFor();
    await noOverflow('browse');
    const cols = await page.locator('.xgrid').evaluate(g => getComputedStyle(g).gridTemplateColumns.split(' ').length);
    assert.equal(cols, 2);
    await page.getByRole('button', { name: /^Filters/ }).click();
    await page.locator('#filters.is-open').waitFor();
    await page.waitForTimeout(250);
    await shot('browse-filters-375.png');
    await page.keyboard.press('Escape');
    await page.locator('#filters.is-open').waitFor({ state: 'detached' }).catch(() => {});
    assert.equal(await page.locator('#filters').evaluate(n => n.classList.contains('is-open')), false);
    await page.locator('.xgrid .xcard:not(.xskeleton)').first().click();
    await drawer.waitFor();
    await drawer.locator('.xhit').first().waitFor();
    await noOverflow('detail drawer');
    await page.waitForTimeout(150);
    await shot('detail-375.png');
    await page.keyboard.press('Escape');

    // Mobile filter drawer: "Filters (n)", focus moves in, live changes, "Show N results" applies and returns focus.
    await page.goto(`${home}?tab=browse&genres=Action&status=FINISHED`);
    await page.locator('.xgrid .xcard:not(.xskeleton)').first().waitFor();
    const toggleBtn = page.getByRole('button', { name: 'Filters (2)' });
    await toggleBtn.click();
    await page.locator('#filters.is-open[role="dialog"][aria-modal="true"]').waitFor();
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Close filters');
    req = browseReq(p => p.get('type') === 'manga');
    await page.locator('#filters').getByRole('button', { name: 'Manga', exact: true }).click();
    await req;
    const applyBtn = page.locator('#filters').getByRole('button', { name: 'Show 36 results' });
    await applyBtn.waitFor();
    await noOverflow('filter drawer');
    for (let i = 0; i < 60; i++) await page.keyboard.press('Tab');
    assert.equal(await page.locator('#filters').evaluate(n => n.contains(document.activeElement)), true, 'focus trapped in filter drawer');
    await applyBtn.click();
    assert.equal(await page.locator('#filters').evaluate(n => n.classList.contains('is-open')), false);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'filter-toggle');
    await page.getByRole('button', { name: 'Filters (3)' }).waitFor();
    // Overlay click closes too.
    await page.getByRole('button', { name: 'Filters (3)' }).click();
    await page.locator('#filters.is-open').waitFor();
    await page.mouse.click(360, 400);
    assert.equal(await page.locator('#filters').evaluate(n => n.classList.contains('is-open')), false);

    if (shotsDir) {
      // Filter panel states at desktop and phone widths.
      for (const [w, h] of [[1920, 1080], [375, 812]]) {
        const mobile = w < 900;
        const openPanel = async () => { if (mobile) { await page.getByRole('button', { name: /^Filters/ }).click(); await page.waitForTimeout(300); } };
        await page.setViewportSize({ width: w, height: h });
        await page.evaluate(() => localStorage.clear());
        await page.goto(`${home}?tab=browse`);
        await page.locator('.xgrid .xcard:not(.xskeleton)').first().waitFor();
        await page.waitForLoadState('networkidle').catch(() => {});
        await openPanel();
        await shot(`filters-${w}-default.png`);
        await page.goto(`${home}?tab=browse&type=manhwa&status=RELEASING&genres=Action,Fantasy&excludeGenres=Romance&tags=Isekai,Reincarnation&excludeTags=Love%20Triangle&minScore=70&q=hero`);
        await page.locator('.xgrid .xcard:not(.xskeleton)').first().waitFor();
        await page.waitForLoadState('networkidle').catch(() => {});
        await openPanel();
        const combo = page.getByRole('combobox', { name: 'Add a tag' });
        await combo.scrollIntoViewIfNeeded();
        await combo.click();
        await combo.fill('re');
        await page.keyboard.press('ArrowDown');
        await page.keyboard.press('ArrowDown');
        await page.locator('#filters .xf-body').evaluate(b => { b.scrollTop = b.querySelector('.xf-sec[data-key="tags"]').offsetTop - 8; });
        await page.waitForTimeout(150);
        await shot(`filters-${w}-active-autocomplete.png`);
        await combo.fill('');
        await page.waitForTimeout(100);
        await page.locator('.xcb-group').nth(3).click();
        await page.waitForTimeout(100);
        await shot(`filters-${w}-browse-tags.png`);
        await page.keyboard.press('Escape');
        const minCh = page.getByLabel('Min', { exact: true });
        const chapToggle = page.locator('#filters .xf-sec[data-key="chapters"] .xf-sec-toggle');
        if (await chapToggle.getAttribute('aria-expanded') === 'false') await chapToggle.click();
        await minCh.scrollIntoViewIfNeeded();
        await minCh.fill('300');
        await page.getByLabel('Max', { exact: true }).fill('120');
        await page.getByLabel('Max', { exact: true }).press('Enter');
        await page.locator('#filters .xf-body').evaluate(b => { b.scrollTop = b.querySelector('.xf-sec[data-key="score"]').offsetTop - 8; });
        await page.waitForTimeout(500);
        await shot(`filters-${w}-validation.png`);
        if (mobile) { await page.keyboard.press('Escape'); await page.keyboard.press('Escape'); }
      }
    }

    if (shotsDir) {
      for (const [w, h] of [[1920, 1080], [2560, 1440], [375, 812]]) {
        await page.setViewportSize({ width: w, height: h });
        await page.goto(home);
        await page.locator('.xshelf').nth(1).waitFor();
        await page.waitForLoadState('networkidle').catch(() => {});
        await shot(`matrix-${w}-home.png`);
        await page.goto(`${home}?tab=browse&genres=Action&status=FINISHED`);
        await page.locator('.xgrid .xcard:not(.xskeleton)').first().waitFor();
        await page.waitForLoadState('networkidle').catch(() => {});
        if (w < 600) await page.getByRole('button', { name: /^Filters/ }).click();
        await page.waitForTimeout(300);
        await shot(`matrix-${w}-browse.png`);
        await page.goto(`${home}?id=1`);
        await drawer.locator('.xhit').first().waitFor();
        await page.waitForLoadState('networkidle').catch(() => {});
        await page.waitForTimeout(300);
        await shot(`matrix-${w}-detail.png`);
      }
    }

    // Novels mode: toggle, popular shelf, catalog card -> Find on novel sources -> Read & Add to library; popular card skips search.
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(home);
    await page.locator('.xshelf').nth(1).waitFor();
    await page.locator('.xmode').getByRole('button', { name: 'Novels', exact: true }).click();
    await page.locator('.xncard').first().waitFor();
    assert.equal(await page.evaluate(() => localStorage.getItem('explore.mode')), 'novels');
    assert.match(await page.locator('.xn-shelf-host h2').innerText(), /Popular on Hangul Planet/);

    // Popular shelf item opens drawer pre-resolved without calling /search.
    const searchesBeforePopular = novelCalls.filter(c => c.startsWith('/search')).length;
    await page.locator('.xncard-pop').first().click();
    const novelDrawer = page.locator('.xndrawer[role="dialog"]');
    await novelDrawer.waitFor();
    assert.equal(novelCalls.filter(c => c.startsWith('/search')).length, searchesBeforePopular, 'popular item must skip /search');
    assert.equal(
      await novelDrawer.locator('a', { hasText: 'Read' }).getAttribute('href'),
      '/bm/novel.html#/novel?source=hangulplanet&path=%2Fnovel%2Fpopular-tower'
    );
    await page.keyboard.press('Escape');
    await novelDrawer.waitFor({ state: 'hidden' });

    // Catalog item opens drawer -> Find on novel sources -> Add to library.
    await page.locator('.xncard[data-novel-id]').first().click();
    await novelDrawer.waitFor();
    await novelDrawer.getByRole('button', { name: 'Find on novel sources' }).click();
    await novelDrawer.locator('.xnhit').first().waitFor();
    assert.equal(
      await novelDrawer.locator('.xnhit a', { hasText: 'Read' }).getAttribute('href'),
      '/bm/novel.html#/novel?source=hangulplanet&path=%2Fnovel%2Fshadow-monarch'
    );
    await novelDrawer.getByRole('button', { name: /Add to library: Shadow Monarch Novel/ }).click();
    await novelDrawer.getByRole('button', { name: 'In library' }).waitFor();
    assert.equal(novelLibPuts.length, 1);
    assert.equal(novelLibPuts[0].novelPath, '/novel/shadow-monarch');
    await page.keyboard.press('Escape');
    await novelDrawer.waitFor({ state: 'hidden' });

    // Switch back to Manga mode.
    await page.locator('.xmode').getByRole('button', { name: 'Manga', exact: true }).click();
    await page.locator('.xshelf').nth(1).waitFor();
    assert.equal(await page.evaluate(() => localStorage.getItem('explore.mode')), 'manga');

    // Sidebar: addon.js injects Discover, Explore, then Novels after Browse and re-inserts on removal.
    const sidebar = await browser.newPage();
    await sidebar.setContent('<nav id="n"><a href="/library">Library</a><a href="/browse"><svg viewBox="0 0 24 24"><path d="M0 0"/></svg><span>Browse</span></a><a href="/downloads">Downloads</a></nav>');
    await sidebar.addScriptTag({ path: path.join(addonDir, 'addon.js') });
    await sidebar.waitForFunction(() => document.querySelector('[data-bm-novels]'));
    const order = await sidebar.$$eval('#n a', links => links.map(a => `${a.textContent.trim()}|${a.getAttribute('href')}`));
    assert.deepEqual(order, ['Library|/library', 'Browse|/browse', 'Discover|/bm/', 'Explore|/bm/explore.html', 'Novels|/bm/novel.html', 'Downloads|/downloads']);
    await sidebar.evaluate(() => document.querySelector('[data-bm-explore]').remove());
    await sidebar.waitForFunction(() => document.querySelector('[data-bm-explore]'));
    assert.equal(await sidebar.locator('[data-bm-explore]').count(), 1);
    assert.equal(await sidebar.locator('[data-bm-discover]').count(), 1);
    assert.equal(await sidebar.locator('[data-bm-novels]').count(), 1);

    assert.deepEqual(errors, []);
    console.log('explore.test.js: all assertions passed (home, drawer, filters, dismiss, 502, mobile, novels mode, sidebar)');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
