'use strict';

// Pure-logic tests for the Suwayomi WebUI add-on. There is no browser harness for addon.js, so
// this loads the file under Node (its DOM bootstrap is guarded) and exercises the exported
// decision helpers. Run with: node tests/suwayomi-addon/addon-logic.test.js

const assert = require('assert');
const path = require('path');

const addon = require(path.join(
  __dirname, '..', '..',
  'src', 'BookmarkManager.Client', 'wwwroot', 'suwayomi-addon', 'addon.js'
));

const match = {
  title: 'Heavenly Demon Sovereign',
  mangaId: 4360,
  read: 65,
  latest: 68,
  libraryTitle: "The Divine Demon's Grand Ascension",
  sourceName: 'Asura Scans'
};

// Same manga id as the library entry: the WebUI already marks it, so no badge.
assert.strictEqual(addon.shouldBadge('4360', match, '/manga/4360'), false);

// Different id on a source browse page: cross-source duplicate, badge it.
assert.strictEqual(addon.shouldBadge('9999', match, '/manga/9999'), true);

// Numeric vs string id comparison must still treat them as equal.
assert.strictEqual(addon.shouldBadge(4360, match, '/manga/4360'), false);

// Library pages are skipped entirely.
assert.strictEqual(addon.shouldBadge('9999', match, '/library'), false);
assert.strictEqual(addon.shouldBadge('9999', match, '/library/all'), false);

// No match -> never badge.
assert.strictEqual(addon.shouldBadge('9999', null, '/manga/9999'), false);

// Badge label: progress when started, "In library" when nothing is read yet.
assert.strictEqual(addon.progressText(match), '65/68');
assert.strictEqual(addon.progressText({ mangaId: 1, read: 0, latest: 68 }), 'In library');

// Caught-up colour choice.
assert.strictEqual(addon.isCaughtUp(match), false);
assert.strictEqual(addon.isCaughtUp({ read: 68, latest: 68 }), true);
assert.strictEqual(addon.isCaughtUp({ read: 70, latest: 68 }), true);

// Reader route parsing: only /manga/{id}/chapter/{order}.
assert.deepStrictEqual(addon.readerRoute('/manga/12292/chapter/5'), { mangaId: 12292, sourceOrder: 5 });
assert.strictEqual(addon.readerRoute('/manga/12292'), null);
assert.strictEqual(addon.readerRoute('/library'), null);

// Alternates: drop current manga and its source, prefer exact (normalized) title matches.
const current = { mangaId: 12292, title: 'Golden Martial God', sourceName: 'Manganato' };
const results = [
  { mangaId: 7987, title: 'Golden Martial God', sourceName: 'Vortex Scans' },
  { mangaId: 12292, title: 'Golden Martial God', sourceName: 'Manganato' },
  { mangaId: 9173, title: 'Golden Martial God', sourceName: 'Manganato' },
  { mangaId: 13088, title: 'Star Martial God Technique', sourceName: 'MangaDex' },
  { mangaId: 13162, title: 'Golden Martial God!', sourceName: 'MangaRead.org' }
];
assert.deepStrictEqual(addon.alternateSources(results, current).map(r => r.mangaId), [7987, 13162]);
// No exact match: fall back to every other-source hit.
assert.deepStrictEqual(
  addon.alternateSources([results[3], results[2]], current).map(r => r.mangaId), [13088]);
assert.deepStrictEqual(addon.alternateSources(null, current), []);

// Chapter mapping by chapter number, not source order.
const chapters = [{ sourceOrder: 1, chapterNumber: 0 }, { sourceOrder: 6, chapterNumber: 5 }];
assert.strictEqual(addon.chapterOrderFor(chapters, 5), 6);
assert.strictEqual(addon.chapterOrderFor(chapters, 0), 1);
assert.strictEqual(addon.chapterOrderFor(chapters, 7), null);

console.log('addon-logic.test.js: all assertions passed');
