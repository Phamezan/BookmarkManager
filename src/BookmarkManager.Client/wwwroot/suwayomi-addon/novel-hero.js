import { el, button, safeUrl, isAbort } from './explore-util.js?v=3';
import { novelsGet } from './novel-api.js?v=1';

const STATS_SOURCES = new Set(['novelfire', 'hangulplanet']);

export function formatCompactStat(value) {
  if (value == null || !Number.isFinite(Number(value))) return null;
  const n = Number(value);
  if (n >= 1_000_000_000) return `${(n / 1_000_000_000).toFixed(1).replace(/\.0$/, '')}B`;
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`;
  if (n >= 10_000) return `${(n / 1_000).toFixed(1).replace(/\.0$/, '')}K`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1).replace(/\.0$/, '')}K`;
  return n.toLocaleString();
}

export function buildStarString(rating) {
  const clamped = Math.max(0, Math.min(5, Number(rating) || 0));
  const filled = Math.round(clamped);
  return '★'.repeat(filled) + '☆'.repeat(5 - filled);
}

function sourceDisplayName(source, fallback) {
  if (fallback) return fallback;
  if (source === 'novelfire') return 'Novel Fire';
  if (source === 'hangulplanet') return 'Hangul Planet';
  return source || 'Source';
}

/**
 * Shared Novelfire-style detail hero + Chapters/Reviews summary cards.
 * Used by both `novel-detail.js` and the Explore novel drawer.
 */
export function createNovelHero({
  model,
  signal,
  renderPrimaryActions,
  renderSecondaryActions,
  onChaptersClick,
  chaptersHref,
  onReviewsClick
}) {
  const root = el('div', 'nhero-wrap');
  const hero = el('section', 'nhero');

  // Cover
  const coverBox = el('div', 'nhero-cover-wrap');
  const coverSrc = safeUrl(model.coverUrl);
  if (coverSrc) {
    const img = el('img', 'nhero-cover');
    img.src = coverSrc;
    img.alt = `${model.title} cover`;
    coverBox.appendChild(img);
  } else {
    coverBox.appendChild(el('div', 'nhero-cover nhero-cover-empty', model.title || 'Novel'));
  }
  hero.appendChild(coverBox);

  // Info column (keeps .ndetail-info class for compatibility)
  const info = el('div', 'nhero-info ndetail-info');
  const titleEl = el('h2', 'nhero-title', model.title);
  if (model.titleId) titleEl.id = model.titleId;
  info.appendChild(titleEl);

  const authorText = Array.isArray(model.authors)
    ? model.authors.filter(Boolean).join(', ')
    : (model.author || '');
  if (authorText) {
    const authorLine = el('p', 'nhero-author');
    authorLine.append(el('span', 'nhero-author-label', 'Author: '), document.createTextNode(authorText));
    info.appendChild(authorLine);
  }

  // Rank + Stars + Rating row (reserved min-height so live stats never jump)
  const ratingRow = el('div', 'nhero-rating-row');
  info.appendChild(ratingRow);

  // Stats grid (Chapters, Views, Bookmarked, Status)
  const statsRow = el('div', 'nhero-stats');
  info.appendChild(statsRow);

  // Genres
  const genresRow = el('div', 'nhero-genres');
  for (const g of model.genres || []) {
    genresRow.appendChild(el('span', 'xchip', g));
  }
  if (genresRow.childElementCount > 0) info.appendChild(genresRow);

  // Actions
  const actionsRow = el('div', 'nhero-actions ndetail-actions');
  info.appendChild(actionsRow);

  const secondaryRow = el('div', 'nhero-secondary');
  info.appendChild(secondaryRow);

  hero.appendChild(info);
  root.appendChild(hero);

  // Two summary cards below hero: Chapters + Reviews
  const cardsRow = el('div', 'nhero-cards');

  const chaptersCardTag = chaptersHref ? 'a' : 'button';
  const chaptersCard = el(chaptersCardTag, 'nhero-card nhero-card-chapters');
  if (chaptersHref) {
    chaptersCard.href = chaptersHref;
  } else {
    chaptersCard.type = 'button';
    if (onChaptersClick) chaptersCard.addEventListener('click', onChaptersClick);
  }

  const reviewsCard = button('nhero-card nhero-card-reviews', '', () => {
    onReviewsClick?.(state);
  });

  cardsRow.append(chaptersCard, reviewsCard);
  root.appendChild(cardsRow);

  const state = {
    source: model.source || null,
    sourceName: model.sourceName || null,
    path: model.path || null,
    site: model.site || null,
    rank: model.rank ?? null,
    rating: model.rating ?? null,
    ratingCount: model.ratingCount ?? null,
    views: model.views ?? null,
    bookmarks: model.bookmarks ?? null,
    chapters: model.chapters ?? null,
    status: model.status ?? null,
    latestChapterName: model.latestChapterName ?? null,
    latestChapterUpdated: model.latestChapterUpdated ?? null,
    reviewCount: model.reviewCount ?? null,
    reviewAverage: model.reviewAverage ?? null,
    loadingStats: false
  };

  function paintRating() {
    ratingRow.replaceChildren();
    if (state.rank != null) {
      const rankBadge = el('span', 'nhero-rank');
      rankBadge.append(el('span', 'nhero-crown', '👑'), document.createTextNode(` RANK ${state.rank}`));
      ratingRow.appendChild(rankBadge);
    }

    if (state.rating != null) {
      const starsWrap = el('span', 'nhero-stars');
      starsWrap.setAttribute('aria-label', `Rated ${Number(state.rating).toFixed(1)} out of 5`);
      starsWrap.append(
        el('span', 'nhero-stars-glyph', buildStarString(state.rating)),
        el('strong', 'nhero-rating-num', Number(state.rating).toFixed(1))
      );
      if (state.ratingCount != null) {
        starsWrap.appendChild(el('span', 'nhero-rating-count', `(${formatCompactStat(state.ratingCount)})`));
      }
      ratingRow.appendChild(starsWrap);
    } else if (state.loadingStats) {
      ratingRow.appendChild(el('span', 'nhero-skel nhero-skel-sm'));
    }
  }

  function paintStats() {
    statsRow.replaceChildren();
    const cols = [];
    if (state.chapters != null) cols.push({ label: 'Chapters', value: String(state.chapters) });
    if (state.views != null) cols.push({ label: 'Views', value: formatCompactStat(state.views) });
    if (state.bookmarks != null) cols.push({ label: 'Bookmarked', value: formatCompactStat(state.bookmarks) });
    if (state.status && state.status !== 'Unknown') cols.push({ label: 'Status', value: state.status });

    if (!cols.length && state.loadingStats) {
      statsRow.classList.remove('is-empty');
      for (let i = 0; i < 3; i++) {
        const box = el('div', 'nhero-stat');
        box.append(el('span', 'nhero-skel nhero-skel-val'), el('span', 'nhero-skel nhero-skel-lbl'));
        statsRow.appendChild(box);
      }
      return;
    }

    statsRow.classList.toggle('is-empty', cols.length === 0);
    for (const c of cols) {
      const box = el('div', 'nhero-stat');
      box.append(el('span', 'nhero-stat-label', c.label), el('strong', 'nhero-stat-val', c.value));
      statsRow.appendChild(box);
    }
  }

  function paintCards() {
    // Chapters card
    chaptersCard.replaceChildren();
    const chBody = el('div', 'nhero-card-body');
    chBody.appendChild(el('span', 'nhero-card-title', 'Chapters'));
    const latestLine = state.latestChapterName
      || (state.chapters != null ? `${state.chapters} chapters available` : 'Browse chapters');
    chBody.appendChild(el('span', 'nhero-card-main', latestLine));
    if (state.latestChapterUpdated) {
      const updatedText = /^updated\s/i.test(state.latestChapterUpdated)
        ? state.latestChapterUpdated
        : `Updated ${state.latestChapterUpdated}`;
      chBody.appendChild(el('span', 'nhero-card-sub', updatedText));
    } else if (state.loadingStats) {
      chBody.appendChild(el('span', 'nhero-skel nhero-skel-sm'));
    }
    chaptersCard.append(chBody, el('span', 'nhero-card-arrow', '›'));

    // Reviews card
    reviewsCard.replaceChildren();
    const revBody = el('div', 'nhero-card-body');
    revBody.appendChild(el('span', 'nhero-card-title', 'Reviews'));

    const srcName = state.source ? sourceDisplayName(state.source, state.sourceName) : null;
    const avg = state.reviewAverage ?? state.rating;
    const cnt = state.reviewCount ?? state.ratingCount;

    if (srcName && (avg != null || cnt != null)) {
      const parts = [srcName];
      if (avg != null) parts.push(Number(avg).toFixed(1));
      const mainText = cnt != null
        ? `${parts.join(' ')} · ${cnt.toLocaleString()} review${cnt === 1 ? '' : 's'}`
        : parts.join(' ');
      revBody.appendChild(el('span', 'nhero-card-main', mainText));
      revBody.appendChild(el('span', 'nhero-card-sub', 'Read reader reviews'));
    } else if (state.loadingStats) {
      revBody.appendChild(el('span', 'nhero-skel nhero-skel-md'));
      revBody.appendChild(el('span', 'nhero-card-sub', 'Loading review summary…'));
    } else {
      revBody.appendChild(el('span', 'nhero-card-main', 'Reader reviews'));
      revBody.appendChild(el('span', 'nhero-card-sub', 'Open reader reviews'));
    }
    reviewsCard.append(revBody, el('span', 'nhero-card-arrow', '›'));
  }

  function refreshActions() {
    actionsRow.replaceChildren();
    renderPrimaryActions?.(actionsRow, state);
    secondaryRow.replaceChildren();
    renderSecondaryActions?.(secondaryRow, state);
    secondaryRow.hidden = secondaryRow.childElementCount === 0;
  }

  function paintAll() {
    paintRating();
    paintStats();
    paintCards();
    refreshActions();
  }

  async function loadLiveStats(source, path, sourceName) {
    if (!source || !path || !STATS_SOURCES.has(source.toLowerCase())) return;
    state.source = source.toLowerCase();
    state.path = path;
    if (sourceName) state.sourceName = sourceName;
    state.loadingStats = true;
    paintRating();
    paintStats();
    paintCards();

    try {
      const qs = new URLSearchParams({ source: state.source, path: state.path });
      const stats = await novelsGet(`/stats?${qs}`, signal);
      state.loadingStats = false;
      if (stats.rank != null) state.rank = stats.rank;
      if (stats.rating != null) state.rating = stats.rating;
      if (stats.ratingCount != null) state.ratingCount = stats.ratingCount;
      if (stats.views != null) state.views = stats.views;
      if (stats.bookmarks != null) state.bookmarks = stats.bookmarks;
      if (stats.chapters != null) state.chapters = stats.chapters;
      if (stats.status) state.status = stats.status;
      if (stats.latestChapterName) state.latestChapterName = stats.latestChapterName;
      if (stats.latestChapterUpdated) state.latestChapterUpdated = stats.latestChapterUpdated;
      if (stats.reviewCount != null) state.reviewCount = stats.reviewCount;
      if (stats.reviewAverage != null) state.reviewAverage = stats.reviewAverage;
      paintRating();
      paintStats();
      paintCards();
    } catch (err) {
      if (isAbort(err)) return;
      state.loadingStats = false;
      paintRating();
      paintStats();
      paintCards();
    }
  }

  paintAll();
  if (state.source && state.path) {
    loadLiveStats(state.source, state.path, state.sourceName);
  }

  return {
    element: root,
    state,
    refreshActions,
    loadLiveStats,
    setChaptersHref(href) {
      if (chaptersCard.tagName === 'A' && href) chaptersCard.href = href;
    }
  };
}
