// Suwayomi WebUI add-on: injects a "Discover" sidebar entry that links to the BookmarkManager
// Discover page (/bm/, served through the Suwayomi proxy). Runs on every WebUI page and must never
// break the WebUI: everything is wrapped in try/catch and it only ever inserts its own clone.
(function () {
  'use strict';

  var EXPLORE_PATH = 'M12 10.9c-.61 0-1.1.49-1.1 1.1s.49 1.1 1.1 1.1c.61 0 1.1-.49 1.1-1.1s-.49-1.1-1.1-1.1zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm2.19 12.19L6 18l3.81-8.19L18 6l-3.81 8.19z';
  var SOURCE_SELECTOR = 'a[href="/browse"]:not([data-bm-discover])';

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
    var badges = clone.querySelectorAll('[class*="Badge"], [class*="badge"]');
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

    link.setAttribute('data-bm-discover', '');
    if (link.parentNode) link.parentNode.insertBefore(clone, link.nextSibling);
  }

  function run() {
    try {
      var links = document.querySelectorAll(SOURCE_SELECTOR);
      for (var i = 0; i < links.length; i++) insertAfter(links[i]);
    } catch (e) {
      /* never break the WebUI */
    }
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
      run();
      var observer = new MutationObserver(schedule);
      observer.observe(document.body, { childList: true, subtree: true });
    } catch (e) {
      /* never break the WebUI */
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
