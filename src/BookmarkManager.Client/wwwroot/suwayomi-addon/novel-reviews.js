import { el, button, safeUrl, isAbort, trapTab } from './explore-util.js?v=3';
import { novelsGet } from './novel-api.js?v=1';

const REVIEWS_SOURCES = new Set(['novelfire', 'hangulplanet']);

function sourceLabel(source, fallback) {
  if (fallback) return fallback;
  if (source === 'novelfire') return 'Novel Fire';
  if (source === 'hangulplanet') return 'Hangul Planet';
  if (source === 'royalroad') return 'Royal Road';
  return source || 'Reviews';
}

function siteReviewsUrl(tab) {
  if (tab.id === 'novelfire' && tab.site && tab.path) {
    return safeUrl(`${tab.site.replace(/\/+$/, '')}${tab.path.replace(/\/+$/, '')}/reviews`);
  }
  if (tab.id === 'hangulplanet' && tab.site && tab.path) {
    return safeUrl(`${tab.site.replace(/\/+$/, '')}${tab.path.replace(/\/+$/, '')}#reviews`);
  }
  if (tab.id === 'royalroad' && tab.site && tab.path) {
    return safeUrl(`${tab.site.replace(/\/+$/, '')}${tab.path.replace(/\/+$/, '')}#reviews`);
  }
  return null;
}

function renderReviewCard(review) {
  const card = el('article', 'nrev-card');

  const head = el('div', 'nrev-card-head');
  const who = el('div', 'nrev-who');
  who.appendChild(el('strong', 'nrev-author', review.author || 'Anonymous'));
  if (review.date) who.appendChild(el('span', 'nrev-date', review.date));

  const meta = el('div', 'nrev-card-meta');
  if (review.score != null) {
    const score = el('span', 'nrev-score');
    score.append(el('span', 'xstar', '★'), document.createTextNode(` ${Number(review.score).toFixed(1)}`));
    meta.appendChild(score);
  }
  if (review.likes != null) {
    meta.appendChild(el('span', 'nrev-likes', `▲ ${review.likes}`));
  }
  head.append(who, meta);
  card.appendChild(head);

  const body = el('div', 'nrev-body');
  const paragraphs = Array.isArray(review.paragraphs) ? review.paragraphs : [];
  let totalChars = 0;

  for (const p of paragraphs) {
    const text = p?.text || '';
    if (!text) continue;
    totalChars += text.length;

    if (p.spoiler) {
      const box = el('div', 'nrev-spoiler');
      const revealBtn = button('nrev-spoiler-btn', 'Spoiler — click to reveal', () => {
        const revealed = box.classList.toggle('is-revealed');
        revealBtn.setAttribute('aria-expanded', String(revealed));
        revealBtn.textContent = revealed ? 'Hide spoiler' : 'Spoiler — click to reveal';
      });
      revealBtn.setAttribute('aria-expanded', 'false');
      const spoilerText = el('p', 'nrev-spoiler-text', text);
      spoilerText.addEventListener('click', () => {
        if (!box.classList.contains('is-revealed')) revealBtn.click();
      });
      box.append(revealBtn, spoilerText);
      body.appendChild(box);
    } else {
      body.appendChild(el('p', 'nrev-para', text));
    }
  }

  card.appendChild(body);

  const shouldClamp = totalChars > 340 || paragraphs.length > 4;
  if (shouldClamp) {
    body.classList.add('is-clamped');
    let expanded = false;
    const moreBtn = button('nrev-more-btn', 'Read more', () => {
      expanded = !expanded;
      body.classList.toggle('is-clamped', !expanded);
      moreBtn.textContent = expanded ? 'Show less' : 'Read more';
    });
    card.appendChild(moreBtn);
  }

  return card;
}

/**
 * Shared Reviews modal/panel for both the Explore novel drawer and `novel.html` detail page.
 * Review text is fetched ONLY when `open(...)` is called.
 */
export function createReviewsPanel() {
  const backdrop = el('div', 'nrev-backdrop');
  backdrop.hidden = true;

  const dialog = el('aside', 'nrev-panel');
  dialog.setAttribute('role', 'dialog');
  dialog.setAttribute('aria-modal', 'true');
  dialog.setAttribute('aria-label', 'Reviews');
  dialog.tabIndex = -1;
  backdrop.appendChild(dialog);

  let controller = null;
  let returnFocus = null;
  let tabs = [];
  let activeTabId = null;

  const head = el('div', 'nrev-head');
  const headTitles = el('div', 'nrev-head-titles');
  const titleEl = el('h3', null, 'Reviews');
  const subEl = el('p', 'nrev-head-sub');
  headTitles.append(titleEl, subEl);
  const closeBtn = button('nrev-close', '×', () => close(), 'Close reviews');
  head.append(headTitles, closeBtn);

  const tabBar = el('div', 'nrev-tabs');
  tabBar.setAttribute('role', 'tablist');
  const content = el('div', 'nrev-content');
  dialog.append(head, tabBar, content);

  function close() {
    controller?.abort();
    if (backdrop.hidden) return;
    backdrop.hidden = true;
    if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    returnFocus = null;
  }

  function destroy() {
    close();
    if (backdrop.isConnected) backdrop.remove();
  }

  function getTab(id) {
    return tabs.find(t => t.id === id) || null;
  }

  function renderTabs() {
    tabBar.replaceChildren();
    const visible = tabs.filter(t => t.visible);
    tabBar.hidden = visible.length === 0;
    for (const tab of visible) {
      const countPart = tab.count != null ? ` (${tab.count})` : '';
      const btn = button('nrev-tab', `${tab.label}${countPart}`, () => {
        activeTabId = tab.id;
        renderTabs();
        renderActiveTab();
      });
      btn.setAttribute('role', 'tab');
      btn.setAttribute('aria-selected', String(tab.id === activeTabId));
      btn.dataset.source = tab.id;
      tabBar.appendChild(btn);
    }
  }

  function renderActiveTab() {
    content.replaceChildren();
    const tab = getTab(activeTabId);
    if (!tab || !tab.visible) {
      const anyLoading = tabs.some(t => t.loading);
      content.appendChild(el('p', 'nrev-empty', anyLoading ? 'Loading reviews…' : 'No reviews available for this novel.'));
      return;
    }

    // Tab summary header (average + count + Open on site)
    const summary = el('div', 'nrev-summary');
    const summaryLeft = el('div', 'nrev-summary-stats');
    if (tab.average != null) {
      const avg = el('span', 'nrev-summary-avg');
      avg.append(el('span', 'xstar', '★'), document.createTextNode(` ${Number(tab.average).toFixed(1)}`));
      summaryLeft.appendChild(avg);
    }
    if (tab.count != null) {
      summaryLeft.appendChild(el('span', 'nrev-summary-count', `${tab.count.toLocaleString()} review${tab.count === 1 ? '' : 's'}`));
    }
    summary.appendChild(summaryLeft);

    const extUrl = siteReviewsUrl(tab);
    if (extUrl) {
      const ext = el('a', 'xbtn nrev-ext', 'Open on site ↗');
      ext.href = extUrl;
      ext.target = '_blank';
      ext.rel = 'noopener noreferrer';
      summary.appendChild(ext);
    }
    content.appendChild(summary);

    if (tab.error && !tab.items.length) {
      const errBox = el('div', 'nrev-error');
      errBox.append(
        el('p', null, tab.error),
        button('xbtn', 'Retry', () => loadTabPage(tab, 1))
      );
      content.appendChild(errBox);
      return;
    }

    if (tab.loading && !tab.items.length) {
      content.appendChild(el('p', 'nrev-empty', 'Loading reviews…'));
      return;
    }

    if (!tab.items.length) {
      content.appendChild(el('p', 'nrev-empty', 'No reviews posted yet.'));
      return;
    }

    const list = el('div', 'nrev-list');
    for (const review of tab.items) {
      list.appendChild(renderReviewCard(review));
    }
    content.appendChild(list);

    if (tab.hasNextPage) {
      const foot = el('div', 'nrev-foot');
      const loadMore = button('xbtn', tab.loading ? 'Loading…' : 'Load more', () => {
        loadTabPage(tab, tab.page + 1);
      });
      loadMore.disabled = tab.loading;
      foot.appendChild(loadMore);
      content.appendChild(foot);
    }
  }

  async function loadTabPage(tab, pageNum) {
    tab.loading = true;
    tab.error = null;
    if (tab.id === activeTabId) renderActiveTab();

    try {
      if (tab.id === 'royalroad') {
        const qs = new URLSearchParams({ title: tab.novelTitle, page: String(pageNum) });
        const data = await novelsGet(`/reviews/royalroad?${qs}`, controller.signal);
        tab.loading = false;
        if (!data?.matched) {
          tab.visible = false;
          if (activeTabId === 'royalroad') {
            activeTabId = tabs.find(t => t.visible)?.id || null;
          }
          renderTabs();
          renderActiveTab();
          return;
        }
        tab.visible = true;
        tab.site = data.site || 'https://www.royalroad.com';
        tab.path = data.path || tab.path;
        tab.average = data.overallScore ?? tab.average;
        tab.count = data.count ?? tab.count;
        tab.page = data.page || pageNum;
        tab.hasNextPage = Boolean(data.hasNextPage);
        tab.items = pageNum === 1 ? (data.items || []) : [...tab.items, ...(data.items || [])];
        if (!activeTabId) activeTabId = 'royalroad';
      } else {
        const qs = new URLSearchParams({ source: tab.id, path: tab.path, page: String(pageNum) });
        const data = await novelsGet(`/reviews?${qs}`, controller.signal);
        tab.loading = false;
        tab.site = data.site || tab.site;
        tab.label = sourceLabel(tab.id, data.sourceName || tab.label);
        tab.average = data.average ?? tab.average;
        tab.count = data.count ?? tab.count;
        tab.page = data.page || pageNum;
        tab.hasNextPage = Boolean(data.hasNextPage);
        tab.items = pageNum === 1 ? (data.items || []) : [...tab.items, ...(data.items || [])];
      }
      renderTabs();
      if (tab.id === activeTabId || !activeTabId) {
        if (!activeTabId) activeTabId = tab.id;
        renderActiveTab();
      }
    } catch (error) {
      if (isAbort(error)) return;
      tab.loading = false;
      if (tab.id === 'royalroad' && pageNum === 1) {
        // Hide Royal Road tab cleanly when disabled (404) or unavailable, unless it's the only option.
        if (error?.status === 404 || tabs.some(t => t.id !== 'royalroad' && t.visible)) {
          tab.visible = false;
        } else {
          tab.visible = true;
          tab.error = 'Could not load Royal Road reviews right now.';
        }
      } else {
        tab.error = error?.status === 502
          ? 'Review source is unavailable right now.'
          : 'Could not load reviews.';
      }
      renderTabs();
      renderActiveTab();
    }
  }

  function open({ title, source, sourceName, site, path }) {
    controller?.abort();
    controller = new AbortController();

    if (!backdrop.isConnected) document.body.appendChild(backdrop);
    if (backdrop.hidden) {
      returnFocus = document.activeElement;
      backdrop.hidden = false;
    }

    subEl.textContent = title || '';
    tabs = [];

    const normalizedSource = source ? source.toLowerCase() : null;
    if (normalizedSource && path && REVIEWS_SOURCES.has(normalizedSource)) {
      tabs.push({
        id: normalizedSource,
        label: sourceLabel(normalizedSource, sourceName),
        site: site || (normalizedSource === 'novelfire' ? 'https://novelfire.net' : 'https://hangulplanet.com'),
        path,
        visible: true,
        loading: false,
        error: null,
        average: null,
        count: null,
        page: 1,
        hasNextPage: false,
        items: []
      });
    }

    if (title) {
      tabs.push({
        id: 'royalroad',
        label: 'Royal Road',
        novelTitle: title,
        site: 'https://www.royalroad.com',
        path: null,
        visible: false,
        loading: false,
        error: null,
        average: null,
        count: null,
        page: 1,
        hasNextPage: false,
        items: []
      });
    }

    activeTabId = tabs.find(t => t.visible)?.id || (tabs[0]?.id ?? null);
    renderTabs();
    renderActiveTab();
    closeBtn.focus({ preventScroll: true });

    for (const tab of tabs) {
      loadTabPage(tab, 1);
    }
  }

  backdrop.addEventListener('click', event => {
    if (event.target === backdrop) close();
  });

  backdrop.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
    } else {
      trapTab(dialog, event);
    }
  });

  return {
    open,
    close,
    destroy,
    isOpen: () => !backdrop.hidden
  };
}
