import { el, button, safeUrl, isAbort, trapTab } from './explore-util.js?v=3';
import { novelsGet, novelsSend, novelHash } from './novel-api.js?v=1';

const novelListOverride = new Map();
export const novelStateOf = item => (novelListOverride.has(item.id) ? novelListOverride.get(item.id) : (item.listState ?? null));
export const setNovelStateOf = (id, state) => novelListOverride.set(id, state);

export function novelReaderUrl(source, path) {
  return `/bm/novel.html${novelHash(source, path)}`;
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

export function createNovelDrawer({ toast, onLibraryAdded, onList }) {
  const backdrop = el('div', 'xdrawer-backdrop');
  backdrop.hidden = true;
  const drawer = el('aside', 'xdrawer xndrawer');
  drawer.setAttribute('role', 'dialog');
  drawer.setAttribute('aria-modal', 'true');
  drawer.setAttribute('aria-label', 'Novel details');
  drawer.tabIndex = -1;
  backdrop.appendChild(drawer);

  let controller = null;
  let returnFocus = null;
  let currentItem = null;

  const mount = () => { if (!backdrop.isConnected) document.body.appendChild(backdrop); };
  const unmount = () => { close(); if (backdrop.isConnected) backdrop.remove(); };

  function close() {
    controller?.abort();
    currentItem = null;
    if (backdrop.hidden) return;
    backdrop.hidden = true;
    document.getElementById('page').inert = false;
    document.body.classList.remove('is-locked');
    if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    returnFocus = null;
  }

  function refresh() {
    if (!currentItem?.id) return;
    const state = novelStateOf(currentItem);
    const saveBtn = drawer.querySelector('.xn-drawer-save');
    const dismissBtn = drawer.querySelector('.xn-drawer-dismiss');
    if (saveBtn) {
      saveBtn.setAttribute('aria-pressed', String(state === 'saved'));
      saveBtn.textContent = state === 'saved' ? '♥ Saved' : '♥ Save';
    }
    if (dismissBtn) {
      dismissBtn.setAttribute('aria-pressed', String(state === 'dismissed'));
      dismissBtn.textContent = state === 'dismissed' ? '✕ Not interested' : 'Not interested';
    }
  }

  function renderHitRow(hit) {
    const row = el('div', 'xhit xnhit');
    const coverSrc = safeUrl(hit.cover);
    if (coverSrc) {
      const img = el('img', 'xnhit-cover');
      img.src = coverSrc;
      img.alt = '';
      row.appendChild(img);
    }
    const text = el('div', 'xhit-text');
    const titleEl = el('span', 'xhit-title', hit.title);
    const srcText = hit.sourceName || hit.source;
    const sub = el('span', 'xhit-src', hit.isExactMatch ? `${srcText} · Exact match` : srcText);
    text.append(titleEl, sub);
    row.appendChild(text);

    const readLink = el('a', 'xbtn xbtn-primary', 'Read');
    readLink.href = novelReaderUrl(hit.source, hit.path);
    readLink.setAttribute('aria-label', `Read ${hit.title} (${srcText})`);
    row.appendChild(readLink);

    const addBtn = button('xbtn', 'Add to library', async () => {
      addBtn.disabled = true;
      try {
        await novelsSend('PUT', '/library', {
          source: hit.source,
          novelPath: hit.path,
          title: hit.title,
          coverUrl: hit.cover || null,
          status: 'Reading'
        });
        addBtn.textContent = 'In library';
        addBtn.setAttribute('aria-label', `In library: ${hit.title} (${srcText})`);
        onLibraryAdded?.(hit.title);
        toast('Added to novel library');
      } catch {
        addBtn.disabled = false;
        toast('Could not add to novel library.');
      }
    });
    addBtn.setAttribute('aria-label', `Add to library: ${hit.title} (${srcText})`);
    row.appendChild(addBtn);
    return row;
  }

  function open(item) {
    controller?.abort();
    controller = new AbortController();
    currentItem = item;
    if (backdrop.hidden) {
      returnFocus = document.activeElement;
      backdrop.hidden = false;
      document.getElementById('page').inert = true;
      document.body.classList.add('is-locked');
    }

    const { title, coverUrl, authors, mediaType, status, genres, synopsis, alternateTitles, inLibrary, resolvedHit, rating, chapters } = item;
    const frag = document.createDocumentFragment();
    frag.appendChild(el('div', 'xbanner'));
    const closeBtn = button('xdrawer-close', '×', close, 'Close details');
    frag.appendChild(closeBtn);

    const body = el('div', 'xdrawer-body');
    const top = el('div', 'xdetail-top');
    const safeCover = safeUrl(coverUrl);
    if (safeCover) {
      const img = el('img', 'xdetail-cover');
      img.alt = `${title} cover`;
      img.src = safeCover;
      top.appendChild(img);
    }

    const titles = el('div', 'xdetail-titles');
    const h2 = el('h2', null, title);
    h2.id = 'xndetail-title';
    drawer.setAttribute('aria-labelledby', 'xndetail-title');
    titles.appendChild(h2);
    if (authors?.length) titles.appendChild(el('p', 'xalt', authors.join(', ')));
    if (alternateTitles?.length) {
      const det = el('details', 'xsyn');
      det.appendChild(el('summary', null, `Alternative titles (${alternateTitles.length})`));
      const ul = el('ul');
      for (const a of alternateTitles) ul.appendChild(el('li', null, a));
      det.appendChild(ul);
      titles.appendChild(det);
    }

    const chips = el('div', 'xmeta');
    if (mediaType) chips.appendChild(el('span', 'xchip', mediaType === 'LightNovel' ? 'Light Novel' : 'Web Novel'));
    if (status) chips.appendChild(el('span', /^(completed|finished)$/i.test(status) ? 'xchip xchip-done' : 'xchip', status));
    if (rating != null) chips.appendChild(el('span', 'xchip', `★ ${Number(rating).toFixed(1)}`));
    if (chapters != null) chips.appendChild(el('span', 'xchip', `${chapters} ch`));
    if (inLibrary) chips.appendChild(el('span', 'xchip xchip-done', 'In library'));
    titles.appendChild(chips);
    top.appendChild(titles);
    body.appendChild(top);

    if (item.id && onList) {
      const actRow = el('div', 'xdetail-actions xn-drawer-actions');
      actRow.append(
        button('xbtn xn-drawer-save', '♥ Save', () => onList(item, novelStateOf(item) === 'saved' ? null : 'saved')),
        button('xbtn xn-drawer-dismiss', 'Not interested', () => onList(item, novelStateOf(item) === 'dismissed' ? null : 'dismissed'))
      );
      body.appendChild(actRow);
    }

    const readBox = el('section', 'xblock xread');
    readBox.appendChild(el('h3', null, 'Read novel'));
    const hitsHost = el('div', 'xsources');

    if (resolvedHit) {
      hitsHost.appendChild(renderHitRow(resolvedHit));
    } else {
      const findBtn = button('xbtn xbtn-primary', 'Find on novel sources', async () => {
        findBtn.disabled = true;
        hitsHost.replaceChildren(el('p', 'xempty', 'Searching novel sources…'));
        try {
          const qs = new URLSearchParams({ q: title });
          const hits = await novelsGet(`/search?${qs}`, controller.signal);
          hitsHost.replaceChildren();
          if (!hits.length) {
            hitsHost.appendChild(el('p', 'xempty', 'Not found on enabled novel sources.'));
            return;
          }
          for (const hit of hits) hitsHost.appendChild(renderHitRow(hit));
        } catch (error) {
          if (isAbort(error)) return;
          hitsHost.replaceChildren(
            el('p', 'xempty', 'Could not search novel sources right now.'),
            button('xbtn', 'Retry', () => findBtn.click())
          );
          findBtn.disabled = false;
        }
      });
      hitsHost.appendChild(findBtn);
    }

    readBox.appendChild(hitsHost);
    body.appendChild(readBox);

    if (synopsis) {
      const synSec = el('section', 'xblock');
      synSec.append(el('h3', null, 'Synopsis'), el('p', 'xdesc', synopsis));
      body.appendChild(synSec);
    }

    if (genres?.length) {
      const gSec = el('section', 'xblock');
      const wrap = el('div', 'xchips');
      for (const g of genres) wrap.appendChild(el('span', 'xchip', g));
      gSec.append(el('h3', null, 'Genres'), wrap);
      body.appendChild(gSec);
    }

    frag.appendChild(body);
    drawer.replaceChildren(frag);
    refresh();
    drawer.scrollTop = 0;
    closeBtn.focus({ preventScroll: true });
  }

  backdrop.addEventListener('click', event => { if (event.target === backdrop) close(); });
  backdrop.addEventListener('keydown', event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
    else trapTab(drawer, event);
  });

  return { open, close, mount, unmount, refresh, isOpen: () => !backdrop.hidden };
}
