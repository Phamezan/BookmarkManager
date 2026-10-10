// Suwayomi WebUI add-on.
//
// 1. Injects a "Discover" sidebar entry that links to the BookmarkManager Discover page (/bm/,
//    served through the Suwayomi proxy).
// 2. Badges manga cards whose series is already in the user's Suwayomi library under another
//    source or another translated title, and shows a banner linking to the library copy on the
//    manga detail page.
//
// Runs on every WebUI page and must never break it: every entry point is wrapped in try/catch, it
// never leaks globals and it only ever inserts/updates its own elements.
(function () {
  'use strict';

  /* ------------------------------------------------------------------ *
   * Discover sidebar entry (unchanged behaviour)
   * ------------------------------------------------------------------ */

  var EXPLORE_PATH = 'M12 10.9c-.61 0-1.1.49-1.1 1.1s.49 1.1 1.1 1.1c.61 0 1.1-.49 1.1-1.1s-.49-1.1-1.1-1.1zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm2.19 12.19L6 18l3.81-8.19L18 6l-3.81 8.19z';
  // Never tag the WebUI's own link: if React drops our clone but keeps that element, the sibling
  // check below must still see the clone missing and re-insert it.
  var SOURCE_SELECTOR = 'a[href="/browse"]';

  function replaceLabel(clone) {
    var walker = document.createTreeWalker(clone, NodeFilter.SHOW_TEXT, null);
    var node;
    var fallback = null;
    while ((node = walker.nextNode())) {
      if (!node.nodeValue || node.nodeValue.indexOf('Browse') === -1) continue;
      if (node.nodeValue.trim() === 'Browse') {
        node.nodeValue = node.nodeValue.replace('Browse', 'Discover');
        return;
      }
      if (!fallback) fallback = node;
    }
    if (fallback) fallback.nodeValue = fallback.nodeValue.replace('Browse', 'Discover');
  }

  function replaceIcon(clone) {
    var svg = clone.querySelector('svg');
    if (!svg) return;
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', EXPLORE_PATH);
    svg.appendChild(path);
  }

  function removeBadges(clone) {
    // Only the count bubble: MuiBadge-root wraps the icon itself, so a broad match deletes the icon.
    var badges = clone.querySelectorAll('.MuiBadge-badge');
    for (var i = 0; i < badges.length; i++) {
      if (badges[i].parentNode) badges[i].parentNode.removeChild(badges[i]);
    }
  }

  function insertAfter(link) {
    if (link.nextElementSibling && link.nextElementSibling.hasAttribute('data-bm-discover')) {
      return;
    }

    var clone = link.cloneNode(true);
    clone.setAttribute('data-bm-discover', '');
    clone.setAttribute('href', '/bm/');
    if (clone.classList) clone.classList.remove('Mui-selected');

    replaceLabel(clone);
    replaceIcon(clone);
    removeBadges(clone);

    clone.addEventListener('click', function (event) {
      event.preventDefault();
      event.stopPropagation();
      location.assign('/bm/');
    }, true);

    if (link.parentNode) link.parentNode.insertBefore(clone, link.nextSibling);
  }

  /* ------------------------------------------------------------------ *
   * Cross-source "In library" badge / banner
   * ------------------------------------------------------------------ */

  var MATCH_API = '/bm-api/suwayomi/library-match';
  var MAX_TITLES_PER_REQUEST = 200;
  var FLUSH_DELAY_MS = 300;
  // One attempt plus at most one retry, per page load.
  var MAX_ATTEMPTS = 2;
  var CATCH_UP_COLOR = 'rgba(46,160,67,0.92)';
  var PRIMARY_COLOR = 'rgba(91,116,239,0.92)';
  var PRIMARY_HEX = '#5B74EF';
  // Material "LibraryAddCheck".
  var LIBRARY_ICON_PATH = 'M20 2H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2m-7.53 12L9 10.5l1.4-1.41 2.07 2.08L17.6 6 19 7.41zM4 6H2v14c0 1.1.9 2 2 2h14v-2H4z';
  var SVG_NS = 'http://www.w3.org/2000/svg';

  /* ------------------------------------------------------------------ *
   * Pure logic (exported for the Node test)
   * ------------------------------------------------------------------ */

  function isLibraryPath(pathname) {
    return typeof pathname === 'string' && pathname.indexOf('/library') === 0;
  }

  function shouldBadge(cardId, match, pathname) {
    if (!match) return false;
    if (isLibraryPath(pathname)) return false;
    return String(cardId) !== String(match.mangaId);
  }

  function progressText(match) {
    if (!match || !match.read) return 'In library';
    return match.read + '/' + match.latest;
  }

  function isCaughtUp(match) {
    return !!match && match.read >= match.latest;
  }

  function badgeTooltip(match) {
    var libraryTitle = match.libraryTitle || match.title || '';
    var tip = 'In your library as \u201C' + libraryTitle + '\u201D';
    if (match.sourceName) tip += ' (' + match.sourceName + ')';
    return tip + ' \u2014 ' + match.read + '/' + match.latest + ' read';
  }

  /* ------------------------------------------------------------------ *
   * Shared state
   * ------------------------------------------------------------------ */

  var cache = {};        // title -> match object, or null when known-absent
  var attempts = {};     // title -> number of failed requests
  var queuedSet = {};    // title -> true while queued or in flight
  var queue = [];
  var inFlight = false;
  var flushTimer = null;

  function hasOwn(obj, key) {
    return Object.prototype.hasOwnProperty.call(obj, key);
  }

  /* ------------------------------------------------------------------ *
   * Styles / DOM helpers
   * ------------------------------------------------------------------ */

  function ensureStyle() {
    if (document.querySelector('style[data-bm-style]')) return;
    var style = document.createElement('style');
    style.setAttribute('data-bm-style', '');
    style.textContent = [
      '[data-bm-badge]{position:absolute;left:6px;bottom:44px;display:inline-flex;',
      'align-items:center;gap:4px;height:20px;padding:0 7px;border-radius:10px;',
      'background:' + PRIMARY_COLOR + ';color:#fff;font:600 11px/20px Roboto,sans-serif;',
      'letter-spacing:.2px;box-shadow:0 1px 3px rgba(0,0,0,.45);pointer-events:auto;z-index:2;',
      'cursor:pointer;box-sizing:border-box;line-height:20px;}',
      '[data-bm-badge][data-bm-caught-up]{background:' + CATCH_UP_COLOR + ';}',
      '[data-bm-badge] svg{width:12px;height:12px;display:block;fill:currentColor;flex:none;}',
      '[data-bm-banner]{margin:8px 0;padding:8px 12px;border-radius:8px;',
      'background:rgba(91,116,239,.12);border:1px solid rgba(91,116,239,.45);color:inherit;',
      'font:500 14px/1.4 Roboto,sans-serif;box-sizing:border-box;}',
      '[data-bm-banner] a{color:' + PRIMARY_HEX + ';font-weight:600;margin-left:8px;text-decoration:none;}'
    ].join('');
    (document.head || document.documentElement).appendChild(style);
  }

  function buildIcon() {
    var svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    var path = document.createElementNS(SVG_NS, 'path');
    path.setAttribute('d', LIBRARY_ICON_PATH);
    svg.appendChild(path);
    return svg;
  }

  function navigate(event, mangaId) {
    if (event) {
      if (event.preventDefault) event.preventDefault();
      if (event.stopPropagation) event.stopPropagation();
    }
    var url = '/manga/' + mangaId;
    try {
      history.pushState(history.state, '', url);
      if (typeof PopStateEvent === 'function') {
        window.dispatchEvent(new PopStateEvent('popstate', { state: history.state }));
      } else {
        window.dispatchEvent(new Event('popstate'));
      }
      if (location.pathname !== url) location.assign(url);
    } catch (e) {
      try { location.assign(url); } catch (ignored) { /* silent */ }
    }
  }

  /* ------------------------------------------------------------------ *
   * Badges
   * ------------------------------------------------------------------ */

  function updateBadge(badge, match) {
    var text = progressText(match);
    if (badge.getAttribute('data-bm-label') !== text) {
      badge.setAttribute('data-bm-label', text);
      var labelEl = badge.querySelector('[data-bm-badge-label]');
      if (labelEl) labelEl.textContent = text;
    }

    if (isCaughtUp(match)) badge.setAttribute('data-bm-caught-up', '');
    else badge.removeAttribute('data-bm-caught-up');

    var tip = badgeTooltip(match);
    if (badge.getAttribute('title') !== tip) badge.setAttribute('title', tip);
  }

  function buildBadge(match) {
    var badge = document.createElement('span');
    badge.setAttribute('data-bm-badge', '');
    badge.setAttribute('role', 'button');
    badge.setAttribute('tabindex', '0');
    badge.appendChild(buildIcon());
    var label = document.createElement('span');
    label.setAttribute('data-bm-badge-label', '');
    badge.appendChild(label);
    badge.addEventListener('click', function (event) { navigate(event, match.mangaId); });
    badge.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' || event.key === ' ') navigate(event, match.mangaId);
    });
    updateBadge(badge, match);
    return badge;
  }

  function findBadge(container) {
    for (var i = 0; i < container.children.length; i++) {
      if (container.children[i].hasAttribute('data-bm-badge')) return container.children[i];
    }
    return null;
  }

  function removeBadge(container) {
    var badges = container.querySelectorAll('[data-bm-badge]');
    for (var i = 0; i < badges.length; i++) {
      if (badges[i].parentNode) badges[i].parentNode.removeChild(badges[i]);
    }
  }

  function applyBadge(button, cardId, match) {
    var href = '/manga/' + cardId;
    var existing = findBadge(button);
    if (existing && existing.getAttribute('data-bm-for') === href) {
      updateBadge(existing, match);
      return;
    }
    if (existing) button.removeChild(existing);
    var badge = buildBadge(match);
    badge.setAttribute('data-bm-for', href);
    button.appendChild(badge);
  }

  function cardIdOf(card) {
    var href = card.getAttribute('href') || '';
    var match = /\/manga\/(\d+)/.exec(href);
    return match ? match[1] : null;
  }

  function titleOf(card) {
    var el = card.querySelector('h3[aria-label]');
    if (el) {
      var aria = (el.getAttribute('aria-label') || '').trim();
      if (aria) return aria;
    }
    el = card.querySelector('h3');
    if (el && el.textContent && el.textContent.trim()) return el.textContent.trim();
    el = card.querySelector('img[alt]');
    if (el) {
      var alt = (el.getAttribute('alt') || '').trim();
      if (alt) return alt;
    }
    return null;
  }

  function renderCard(card) {
    var cardId = cardIdOf(card);
    var title = titleOf(card);
    if (!cardId || !title) return;
    var button = card.querySelector('button.MuiCardActionArea-root') || card.querySelector('button');
    if (!button) return;

    if (hasOwn(cache, title)) {
      var match = cache[title];
      if (shouldBadge(cardId, match, location.pathname)) applyBadge(button, cardId, match);
      else removeBadge(button);
    } else {
      removeBadge(button);
      enqueue(title);
    }
  }

  function renderCards() {
    var cards = document.querySelectorAll('.virtuoso-grid-item a[href^="/manga/"]');
    for (var i = 0; i < cards.length; i++) {
      try { renderCard(cards[i]); } catch (e) { /* never break the WebUI */ }
    }
  }

  /* ------------------------------------------------------------------ *
   * Detail-page banner
   * ------------------------------------------------------------------ */

  function routeMangaId(pathname) {
    var match = /^\/manga\/(\d+)(?:\/|$)/.exec(pathname || '');
    return match ? match[1] : null;
  }

  function detailTitle() {
    var candidates = document.querySelectorAll('h2.MuiTypography-h5, h2');
    for (var i = 0; i < candidates.length; i++) {
      var el = candidates[i];
      if (el.closest && el.closest('.virtuoso-grid-item')) continue;
      var text = (el.textContent || '').trim();
      if (text) return { el: el, title: text };
    }
    var fallback = (document.title || '').trim();
    return fallback ? { el: null, title: fallback } : null;
  }

  function buildBanner(match) {
    var banner = document.createElement('div');
    banner.setAttribute('data-bm-banner', '');

    var text = 'In your library';
    if (match.sourceName) text += ' on ' + match.sourceName;
    text += ' as \u201C' + (match.libraryTitle || match.title || '') + '\u201D \u00B7 ' +
      match.read + '/' + match.latest + ' read \u00B7 ';
    banner.appendChild(document.createTextNode(text));

    var link = document.createElement('a');
    link.setAttribute('href', '/manga/' + match.mangaId);
    link.textContent = 'Open';
    banner.appendChild(link);
    return banner;
  }

  function existingBanner(routeKey) {
    var banners = document.querySelectorAll('[data-bm-banner]');
    var found = null;
    for (var i = 0; i < banners.length; i++) {
      if (banners[i].getAttribute('data-bm-route') === routeKey) found = banners[i];
      else if (banners[i].parentNode) banners[i].parentNode.removeChild(banners[i]);
    }
    return found;
  }

  function insertBanner(titleEl, pageId, match) {
    if (!titleEl || !titleEl.parentNode) return;
    var routeKey = '/manga/' + pageId;
    var contentKey = String(match.mangaId) + '|' + match.read + '|' + match.latest;
    var existing = existingBanner(routeKey);
    if (existing) {
      if (existing.getAttribute('data-bm-content') === contentKey) return;
      if (existing.parentNode) existing.parentNode.removeChild(existing);
    }

    var banner = buildBanner(match);
    banner.setAttribute('data-bm-route', routeKey);
    banner.setAttribute('data-bm-content', contentKey);

    var anchor = titleEl.closest ? titleEl.closest('a') : null;
    var row = (anchor && anchor.parentNode) ? anchor.parentNode : titleEl;
    if (!row || !row.parentNode) return;
    row.parentNode.insertBefore(banner, row.nextSibling);
  }

  function renderDetail(pathname) {
    var pageId = routeMangaId(pathname);
    if (!pageId) {
      existingBanner(null);
      return;
    }

    var found = detailTitle();
    if (!found) {
      existingBanner('/manga/' + pageId);
      return;
    }
    if (!hasOwn(cache, found.title)) {
      enqueue(found.title);
      existingBanner('/manga/' + pageId);
      return;
    }

    var match = cache[found.title];
    if (!shouldBadge(pageId, match, pathname)) {
      existingBanner(null);
      return;
    }
    insertBanner(found.el, pageId, match);
  }

  /* ------------------------------------------------------------------ *
   * Title lookup queue (debounced, max 200 titles, one request in flight)
   * ------------------------------------------------------------------ */

  function enqueue(title) {
    if (!title || hasOwn(cache, title)) return;
    if ((attempts[title] || 0) >= MAX_ATTEMPTS) return;
    if (queuedSet[title]) return;
    queuedSet[title] = true;
    queue.push(title);
    scheduleFlush();
  }

  function scheduleFlush() {
    if (flushTimer !== null || inFlight) return;
    flushTimer = window.setTimeout(function () {
      flushTimer = null;
      flush();
    }, FLUSH_DELAY_MS);
  }

  function flush() {
    if (inFlight || queue.length === 0) return;
    var batch = queue.splice(0, MAX_TITLES_PER_REQUEST);
    inFlight = true;

    function done() {
      inFlight = false;
      for (var i = 0; i < batch.length; i++) delete queuedSet[batch[i]];
      render();
      if (queue.length > 0) flush();
    }

    var request;
    try {
      request = window.fetch(MATCH_API, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        credentials: 'same-origin',
        body: JSON.stringify({ titles: batch })
      });
    } catch (e) {
      done();
      return;
    }

    request.then(function (response) {
      if (!response.ok) throw new Error('HTTP ' + response.status);
      return response.json();
    }).then(function (items) {
      for (var i = 0; i < batch.length; i++) {
        if (!hasOwn(cache, batch[i])) cache[batch[i]] = null;
      }
      if (items && items.length) {
        for (var j = 0; j < items.length; j++) {
          var item = items[j];
          if (item && typeof item.title === 'string') cache[item.title] = item;
        }
      }
      done();
    }).catch(function () {
      for (var i = 0; i < batch.length; i++) {
        attempts[batch[i]] = (attempts[batch[i]] || 0) + 1;
      }
      // done() re-runs render(), which re-enqueues titles that still have retries left.
      done();
    });
  }

  /* ------------------------------------------------------------------ *
   * Entry points
   * ------------------------------------------------------------------ */

  function clearInjected() {
    var nodes = document.querySelectorAll('[data-bm-badge], [data-bm-banner]');
    for (var i = 0; i < nodes.length; i++) {
      if (nodes[i].parentNode) nodes[i].parentNode.removeChild(nodes[i]);
    }
  }

  function render() {
    try {
      var pathname = location.pathname || '';
      if (isLibraryPath(pathname)) {
        clearInjected();
        return;
      }
      ensureStyle();
      renderCards();
      renderDetail(pathname);
    } catch (e) {
      /* never break the WebUI */
    }
  }

  function run() {
    try {
      var links = document.querySelectorAll(SOURCE_SELECTOR);
      for (var i = 0; i < links.length; i++) insertAfter(links[i]);
    } catch (e) {
      /* never break the WebUI */
    }
    render();
  }

  var scheduled = false;
  function schedule() {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(function () {
      scheduled = false;
      run();
    });
  }

  function start() {
    try {
      ensureStyle();
      run();
      var observer = new MutationObserver(schedule);
      observer.observe(document.body, { childList: true, subtree: true });
    } catch (e) {
      /* never break the WebUI */
    }
  }

  if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', start);
    } else {
      start();
    }
  }

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = {
      shouldBadge: shouldBadge,
      progressText: progressText,
      isCaughtUp: isCaughtUp
    };
  }
})();
