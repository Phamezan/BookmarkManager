import { el, button, safeUrl } from './explore-util.js?v=3';
import { createNovelDrawer as buildNovelDrawer, novelReaderUrl } from './explore-novels-drawer.js?v=1';

const novelListOverride = new Map();
export const novelStateOf = item => (novelListOverride.has(item.id) ? novelListOverride.get(item.id) : (item.listState ?? null));
export const setNovelStateOf = (id, state) => novelListOverride.set(id, state);
export { novelReaderUrl };

export function createNovelDrawer(opts) {
  return buildNovelDrawer({ ...opts, novelStateOf });
}

// Novelfire catalog rows already know their book page, so they open in the reader without a search.
export function novelfireHit(item) {
  if (item.provider !== 'Novelfire' || !item.sourceUrl) return null;
  try {
    const url = new URL(item.sourceUrl);
    if (url.hostname !== 'novelfire.net' || !url.pathname.startsWith('/book/')) return null;
    return {
      source: 'novelfire',
      sourceName: 'Novel Fire',
      title: item.title,
      path: url.pathname,
      cover: item.coverUrl,
      isExactMatch: true
    };
  } catch {
    return null;
  }
}

export function buildNovelCover(coverUrl, title) {
  const wrap = el('div', 'xcover');
  const src = safeUrl(coverUrl);
  if (src) {
    const img = el('img');
    img.loading = 'lazy';
    img.alt = '';
    img.addEventListener('load', () => img.classList.add('is-loaded'));
    img.src = src;
    wrap.appendChild(img);
  } else {
    wrap.appendChild(el('div', 'xn-placeholder', title || 'Novel'));
  }
  return wrap;
}

export function syncNovelCard(card) {
  const item = card.xNovelItem;
  if (!item) return;
  const state = novelStateOf(item);
  const save = card.querySelector('.xact-save');
  const dismiss = card.querySelector('.xact-dismiss');
  if (save) {
    save.setAttribute('aria-pressed', String(state === 'saved'));
    save.setAttribute('aria-label', `${state === 'saved' ? 'Remove from saved' : 'Save'}: ${item.title}`);
  }
  if (dismiss) {
    dismiss.setAttribute('aria-pressed', String(state === 'dismissed'));
    dismiss.setAttribute('aria-label', `${state === 'dismissed' ? 'Undo not interested' : 'Not interested'}: ${item.title}`);
  }
  card.classList.toggle('is-dismissed', state === 'dismissed');
}

function formatBecauseOf(list) {
  if (!list?.length) return '';
  const allGenres = list.every(x => /^Genre:\s*/i.test(x));
  if (allGenres) {
    return `Because you read ${list.map(x => x.replace(/^Genre:\s*/i, '')).join(', ')}`;
  }
  return `Because you read ${list.join(', ')}`;
}

export function buildNovelCard(item, hooks, { compact = false } = {}) {
  const card = el('article', compact ? 'xcard xncard xncard-rec' : 'xcard xncard');
  card.tabIndex = 0;
  card.dataset.novelId = item.id;
  card.xNovelItem = item;
  card.setAttribute('aria-label', item.title);

  const cover = buildNovelCover(item.coverUrl, item.title);
  if (compact) {
    if (item.readable) cover.appendChild(el('span', 'xbadge xbadge-type xbadge-readable', 'Readable'));
    else if (item.provider) cover.appendChild(el('span', 'xbadge xbadge-type', item.provider));
  } else if (item.mediaType) {
    const label = item.mediaType === 'LightNovel' ? 'Light Novel' : 'Web Novel';
    cover.appendChild(el('span', 'xbadge xbadge-type', label));
  }

  if (hooks.onList) {
    const actions = el('div', 'xactions');
    actions.append(
      button('xact xact-save', '♥', event => {
        event.stopPropagation();
        hooks.onList(item, novelStateOf(item) === 'saved' ? null : 'saved');
      }),
      button('xact xact-dismiss', '✕', event => {
        event.stopPropagation();
        hooks.onList(item, novelStateOf(item) === 'dismissed' ? null : 'dismissed');
      })
    );
    cover.appendChild(actions);
  }

  const overlay = el('div', 'xoverlay');
  if (item.rating != null) {
    const score = el('span', 'xscore');
    score.append(el('span', 'xstar', '★'), document.createTextNode(` ${Number(item.rating).toFixed(1)}`));
    overlay.appendChild(score);
  }
  if (item.inLibrary) {
    overlay.appendChild(el('span', 'xbadge xbadge-lib', 'In library'));
  } else if (!compact && item.readable) {
    overlay.appendChild(el('span', 'xbadge xbadge-readable', 'Readable'));
  }
  cover.appendChild(overlay);
  card.appendChild(cover);

  card.appendChild(el('h3', 'xtitle', item.title));
  if (!compact) {
    const meta = el('div', 'xmeta');
    if (item.status) {
      const done = /^(completed|finished)$/i.test(item.status);
      meta.appendChild(el('span', done ? 'xchip xchip-done' : 'xchip', item.status));
    }
    if (item.chapters != null) meta.appendChild(el('span', 'xmeta-text', `${item.chapters} ch`));
    else if (item.latestChapter) meta.appendChild(el('span', 'xmeta-text', item.latestChapter));
    card.appendChild(meta);

    const genres = (item.genres || []).slice(0, 3).join(' · ');
    if (genres) card.appendChild(el('div', 'xgenres', genres));

    const because = formatBecauseOf(item.becauseOf);
    if (because) card.appendChild(el('div', 'xbecause', because));
  }

  const open = () => hooks.onOpen(item);
  card.addEventListener('click', open);
  card.addEventListener('keydown', event => {
    if (event.target === card && event.key === 'Enter') { event.preventDefault(); open(); }
  });
  syncNovelCard(card);
  return card;
}

export function buildPopularCard(item, onOpen) {
  const card = el('article', 'xcard xncard xncard-pop');
  card.tabIndex = 0;
  card.setAttribute('aria-label', item.title);

  const cover = buildNovelCover(item.cover, item.title);
  cover.appendChild(el('span', 'xbadge xbadge-type', item.sourceName || item.source));
  card.appendChild(cover);
  card.appendChild(el('h3', 'xtitle', item.title));

  const open = () => onOpen(item);
  card.addEventListener('click', open);
  card.addEventListener('keydown', event => {
    if (event.target === card && event.key === 'Enter') { event.preventDefault(); open(); }
  });
  return card;
}

export function buildNovelShelf(shelf, hooks) {
  const section = el('section', 'xshelf xn-shelf');
  section.dataset.shelfKey = shelf.key;
  const head = el('div', 'xshelf-head');
  const titles = el('div');
  titles.appendChild(el('h2', null, shelf.title));
  if (shelf.subtitle) titles.appendChild(el('p', 'xshelf-sub', shelf.subtitle));
  head.appendChild(titles);
  if (hooks.onSeeAll && shelf.browseQuery) {
    head.appendChild(button('xlink', 'See all', () => hooks.onSeeAll(shelf), `See all: ${shelf.title}`));
  }

  const wrap = el('div', 'xshelf-wrap');
  const track = el('div', 'xshelf-track');
  const ranked = shelf.key === 'trending';
  (shelf.items || []).forEach((item, index) => {
    const card = buildNovelCard(item, hooks);
    if (ranked) {
      card.classList.add('has-rank');
      card.querySelector('.xcover').appendChild(el('span', 'xrank', `#${index + 1}`));
    }
    track.appendChild(card);
  });

  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  const page = dir => track.scrollBy({ left: dir * track.clientWidth * 0.85, behavior: reduce.matches ? 'auto' : 'smooth' });
  const prev = button('xshelf-nav xshelf-prev', '‹', () => page(-1), `Scroll ${shelf.title} left`);
  const next = button('xshelf-nav xshelf-next', '›', () => page(1), `Scroll ${shelf.title} right`);
  prev.tabIndex = next.tabIndex = -1;
  const update = () => {
    wrap.classList.toggle('can-prev', track.scrollLeft > 4);
    wrap.classList.toggle('can-next', track.scrollLeft + track.clientWidth < track.scrollWidth - 4);
  };
  track.addEventListener('scroll', update, { passive: true });
  new ResizeObserver(update).observe(track);
  wrap.append(track, prev, next);
  section.append(head, wrap);
  return section;
}

export function buildLiveSourceShelf(source, items, onOpen) {
  const section = el('section', 'xshelf xn-live-shelf');
  section.dataset.sourceId = source.id;
  const head = el('div', 'xshelf-head');
  head.appendChild(el('h2', null, `Popular on ${source.name || source.id}`));
  const wrap = el('div', 'xshelf-wrap');
  const track = el('div', 'xshelf-track');
  for (const item of items) track.appendChild(buildPopularCard(item, onOpen));
  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  const page = dir => track.scrollBy({ left: dir * track.clientWidth * 0.85, behavior: reduce.matches ? 'auto' : 'smooth' });
  const prev = button('xshelf-nav xshelf-prev', '‹', () => page(-1), `Scroll Popular on ${source.name} left`);
  const next = button('xshelf-nav xshelf-next', '›', () => page(1), `Scroll Popular on ${source.name} right`);
  prev.tabIndex = next.tabIndex = -1;
  const update = () => {
    wrap.classList.toggle('can-prev', track.scrollLeft > 4);
    wrap.classList.toggle('can-next', track.scrollLeft + track.clientWidth < track.scrollWidth - 4);
  };
  track.addEventListener('scroll', update, { passive: true });
  new ResizeObserver(update).observe(track);
  wrap.append(track, prev, next);
  section.append(head, wrap);
  return section;
}
