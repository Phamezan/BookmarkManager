'use strict';

// Run with Node and Playwright installed: node tests/suwayomi-addon/discover-preview.test.js
// Add --preview to browse the actual page interactively with sample data.
// Serves the real add-on files through request interception; no running API is needed.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const interactive = process.argv.includes('--preview');

const addonDir = path.join(__dirname, '../../src/BookmarkManager.Client/wwwroot/suwayomi-addon');
const now = new Date().toISOString();
const feed = {
  weekStart: now, weekEnd: now, hasOlder: true, lastRunAt: now,
  items: Array.from({ length: 12 }, (_, index) => ({
    title: `Series ${index + 1}`, titleKey: `series ${index + 1}`,
    type: 'Manhwa', seriesMangaId: index + 1, coverMangaId: index + 1,
    sources: ['Asura Scans', 'Vortex Scans'],
    genres: index === 1 ? [] : ['Action', 'Fantasy'],
    description: index === 1 ? null : 'A hunter\'s second chance.\n'.repeat(80) + '<img src=x onerror="window.injected=true">',
    status: 'ONGOING', progress: { read: 4, latest: 20 },
    chapters: [{ mangaId: index + 1, sourceOrder: 20, number: 20, isNew: true }]
  }))
};

(async () => {
  const browser = await chromium.launch({ headless: !interactive });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, hasTouch: true });
    page.setDefaultTimeout(5000);
    const errors = [];
    let apiCalls = 0;
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.pathname.startsWith('/bm-api/discover')) {
        apiCalls++;
        const previewFeed = interactive ? {
          ...feed,
          items: feed.items.map((item, index) => ({
            ...item,
            description: index === 1 ? null :
              "Once the weakest hunter in his guild, a young adventurer returns from a failed expedition with an unexpected ability: every challenge makes him stronger. As forgotten gates begin to reopen, he must uncover the truth behind his second chance.\n\nThis is sample data for trying the hover card. The next series demonstrates missing tags and descriptions."
          }))
        } : feed;
        return route.fulfill({ json: url.pathname.endsWith('/status') ? { lastRunAt: now } : previewFeed });
      }
      if (url.pathname.endsWith('/thumbnail')) {
        const id = Number(url.pathname.split('/')[4]);
        return route.fulfill({
          contentType: 'image/svg+xml',
          body: `<svg xmlns="http://www.w3.org/2000/svg" width="300" height="450" viewBox="0 0 300 450"><rect width="300" height="450" fill="${['#162347', '#332149', '#164139'][id % 3]}"/><circle cx="150" cy="180" r="85" fill="none" stroke="#ffffff" stroke-opacity=".15" stroke-width="2"/><text x="150" y="195" text-anchor="middle" fill="#fff" font-family="Arial" font-size="40">${id}</text><text x="150" y="360" text-anchor="middle" fill="#fff" font-family="Arial" font-size="18">SAMPLE SERIES</text></svg>`
        });
      }
      if (!url.pathname.startsWith('/bm/')) {
        return route.fulfill({
          contentType: 'text/html',
          body: '<p>Series and chapter navigation require Suwayomi. This preview uses sample data.</p><a href="/bm/">Return to Discover preview</a>'
        });
      }
      const name = url.pathname === '/bm/' ? 'discover.html' : path.basename(url.pathname);
      return route.fulfill({
        body: await fs.readFile(path.join(addonDir, name)),
        contentType: name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html'
      });
    });
    await page.goto('http://discover.test/bm/');
    await page.locator('.card').nth(11).waitFor();

    if (interactive) {
      await page.evaluate(() => {
        document.title = 'Discover preview · Sample data';
        document.querySelector('.subtitle').textContent = 'Local preview · Sample data · Hover a card or click Details';
      });
      console.log('Interactive preview opened. Close the browser window to finish.');
      await new Promise(resolve => page.once('close', resolve));
      return;
    }

    const first = page.locator('.card').first();
    const panel = first.locator('.series-preview');
    await first.locator('.cover-wrap').hover();
    await panel.waitFor({ state: 'visible' });
    assert.match(await panel.innerText(), /Action/);
    assert.match(await panel.innerText(), /A hunter's second chance/);
    assert.match(await panel.innerText(), /Ongoing/);
    assert.match(await panel.innerText(), /4 of 20/);
    assert.equal(await panel.locator('img').count(), 0, 'source description must remain inert text');
    await panel.hover();
    await page.waitForTimeout(250);
    assert.equal(await panel.isVisible(), true, 'pointer can enter the panel');
    await panel.evaluate(element => { element.scrollTop = 80; });
    assert.equal(await panel.isVisible(), true, 'scrolling a long description keeps the panel open');
    await page.keyboard.press('Escape');
    assert.equal(await panel.isVisible(), false);

    await first.locator('.card-title').focus();
    await panel.waitFor({ state: 'visible' });
    await page.keyboard.press('Escape');
    assert.equal(await panel.isVisible(), false);
    const details = first.getByRole('button', { name: 'Details for Series 1', exact: true });
    await details.click();
    await panel.waitFor({ state: 'visible' });
    await panel.getByRole('button', { name: 'Close details' }).click();
    assert.equal(await panel.isVisible(), false);
    assert.equal(await details.getAttribute('aria-expanded'), 'false');

    const second = page.locator('.card').nth(1);
    await second.locator('.cover-wrap').hover();
    await second.locator('.series-preview').waitFor({ state: 'visible' });
    assert.match(await second.locator('.series-preview').innerText(), /No description available/);
    assert.match(await second.locator('.series-preview').innerText(), /No tags available/);

    await page.locator('.card').nth(5).locator('.cover-wrap').hover();
    const rightPanel = page.locator('.card').nth(5).locator('.series-preview');
    await rightPanel.waitFor({ state: 'visible' });
    let bounds = await rightPanel.boundingBox();
    assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= 1440, 'right edge stays inside the viewport');
    assert.equal(await page.locator('.series-preview:visible').count(), 1);
    assert.equal(apiCalls, 2, 'hovering must use cached details');

    const last = page.locator('.card').last();
    await last.locator('.cover-wrap').focus();
    await page.waitForTimeout(200);
    assert.equal(await last.locator('.series-preview').isVisible(), true, 'keyboard focus survives scrolling a card into view');
    bounds = await last.locator('.series-preview').boundingBox();
    assert.ok(bounds.y >= 0 && bounds.y + bounds.height <= 900, 'bottom edge stays inside the viewport');

    await page.getByRole('radio', { name: 'Manhwa', exact: true }).click();
    await page.locator('.card').nth(11).waitFor();
    assert.equal(await page.locator('.series-preview:visible').count(), 0, 'changing filters dismisses the preview');
    await page.setViewportSize({ width: 375, height: 667 });
    await first.getByRole('button', { name: 'Details for Series 1', exact: true }).tap();
    await first.locator('.series-preview').waitFor({ state: 'visible' });
    bounds = await first.locator('.series-preview').boundingBox();
    assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= 375);
    assert.ok(bounds.y >= 0 && bounds.y + bounds.height <= 667);
    await page.mouse.click(1, 1);
    assert.equal(await first.locator('.series-preview').isVisible(), false);
    assert.deepEqual(errors, []);
    console.log('discover-preview.test.js: all assertions passed (desktop, keyboard, narrow viewport)');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
