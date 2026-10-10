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

console.log('addon-logic.test.js: all assertions passed');
