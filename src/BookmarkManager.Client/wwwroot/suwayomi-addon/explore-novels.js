import {
  el, button, safeUrl, isAbort, trapTab, stateBox, icon
} from './explore-util.js?v=3';
import { novelsGet, novelsSend, novelHash } from './novel-api.js?v=1';

const NOVEL_STATUSES = [
  ['', 'All'],
  ['Ongoing', 'Ongoing'],
  ['Completed', 'Completed'],
  ['Hiatus', 'Hiatus'],
  ['Cancelled', 'Cancelled']
];

function novelReaderUrl(source, path) {
  return `/bm/novel.html${novelHash(source, path)}`;
}

function buildCover(coverUrl, title) {
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

function buildCatalogCard(item, onOpen) {
  const card = el('article', 'xcard xncard');
  card.tabIndex = 0;
  card.dataset.novelId = item.id;
  card.setAttribute('aria-label', item.title);

  const cover = buildCover(item.coverUrl, item.title);
  if (item.mediaType) {
    const label = item.mediaType === 'LightNovel' ? 'Light Novel' : 'Web Novel';
    cover.appendChild(el('span', 'xbadge xbadge-type', label));
  }
  const overlay = el('div', 'xoverlay');
  if (item.rating != null) {
    const score = el('span', 'xscore');
    score.append(el('span', 'xstar', '★'), document.createTextNode(` ${Number(item.rating).toFixed(1)}`));
    overlay.appendChild(score);
  }
  if (item.inLibrary) {
    overlay.appendChild(el('span', 'xbadge xbadge-lib', 'In library'));
  }
  cover.appendChild(overlay);
  card.appendChild(cover);

  card.appendChild(el('h3', 'xtitle', item.title));
  const meta = el('div', 'xmeta');
  if (item.status) {
    const done = /^completed$/i.test(item.status);
    meta.appendChild(el('span', done ? 'xchip xchip-done' : 'xchip', item.status));
  }
  if (item.latestChapter) meta.appendChild(el('span', 'xmeta-text', item.latestChapter));
  card.appendChild(meta);
  const genres = (item.genres || []).slice(0, 3).join(' · ');
  if (genres) card.appendChild(el('div', 'xgenres', genres));

  const open = () => onOpen(item);
  card.addEventListener('click', open);
  card.addEventListener('keydown', event => {
    if (event.target === card && event.key === 'Enter') { event.preventDefault(); open(); }
  });
  return card;
}

function buildPopularCard(item, onOpen) {
  const card = el('article', 'xcard xncard xncard-pop');
  card.tabIndex = 0;
  card.setAttribute('aria-label', item.title);

  const cover = buildCover(item.cover, item.title);
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

function createNovelDrawer({ toast, onLibraryAdded }) {
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

  function mount() {
    if (!backdrop.isConnected) document.body.appendChild(backdrop);
  }

  function unmount() {
    close();
    if (backdrop.isConnected) backdrop.remove();
  }

  function close() {
    controller?.abort();
    if (backdrop.hidden) return;
    backdrop.hidden = true;
    document.getElementById('page').inert = false;
    document.body.classList.remove('is-locked');
    if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    returnFocus = null;
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

  function open({ title, coverUrl, authors, mediaType, status, genres, synopsis, alternateTitles, inLibrary, resolvedHit }) {
    controller?.abort();
    controller = new AbortController();
    if (backdrop.hidden) {
      returnFocus = document.activeElement;
      backdrop.hidden = false;
      document.getElementById('page').inert = true;
      document.body.classList.add('is-locked');
    }

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
    if (status) chips.appendChild(el('span', /^completed$/i.test(status) ? 'xchip xchip-done' : 'xchip', status));
    if (inLibrary) chips.appendChild(el('span', 'xchip xchip-done', 'In library'));
    titles.appendChild(chips);
    top.appendChild(titles);
    body.appendChild(top);

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
    drawer.scrollTop = 0;
    closeBtn.focus({ preventScroll: true });
  }

  backdrop.addEventListener('click', event => { if (event.target === backdrop) close(); });
  backdrop.addEventListener('keydown', event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
    else trapTab(drawer, event);
  });

  return { open, close, mount, unmount, isOpen: () => !backdrop.hidden };
}

export function createNovelsExplore({ view, banner, toast }) {
  let active = false;
  let query = '';
  let status = '';
  let page = 1;
  let hasNextPage = false;
  let loading = false;
  let catalogAbort = null;
  let popularAbort = null;
  let gridEl = null;
  let loadMoreHost = null;
  let countEl = null;
  let shelfHost = null;

  const drawer = createNovelDrawer({
    toast,
    onLibraryAdded: () => {
      if (active) loadCatalog(true);
    }
  });

  function openCatalogItem(item) {
    drawer.open({
      title: item.title,
      coverUrl: item.coverUrl,
      authors: item.authors,
      mediaType: item.mediaType,
      status: item.status,
      genres: item.genres,
      synopsis: item.synopsis,
      alternateTitles: item.alternateTitles,
      inLibrary: item.inLibrary,
      resolvedHit: novelfireHit(item)
    });
  }

  // Novelfire catalog rows already know their book page, so they open in the reader without a search.
  function novelfireHit(item) {
    if (item.provider !== 'Novelfire') return null;
    try {
      const url = new URL(item.sourceUrl);
      if (url.hostname !== 'novelfire.net' || !url.pathname.startsWith('/book/')) return null;
      return { source: 'novelfire', sourceName: 'Novel Fire', title: item.title, path: url.pathname, cover: item.coverUrl, isExactMatch: true };
    } catch {
      return null;
    }
  }

  function openPopularItem(item) {
    drawer.open({
      title: item.title,
      coverUrl: item.cover,
      authors: [],
      mediaType: 'Webnovel',
      status: null,
      genres: [],
      synopsis: '',
      alternateTitles: [],
      inLibrary: false,
      resolvedHit: {
        source: item.source,
        sourceName: item.sourceName || item.source,
        title: item.title,
        path: item.path,
        cover: item.cover,
        isExactMatch: true
      }
    });
  }

  async function loadPopularShelf() {
    popularAbort?.abort();
    popularAbort = new AbortController();
    try {
      const items = await novelsGet('/popular?source=hangulplanet&page=1', popularAbort.signal);
      if (!active || !shelfHost || !Array.isArray(items) || !items.length) return;
      const section = el('section', 'xshelf');
      const head = el('div', 'xshelf-head');
      head.appendChild(el('h2', null, 'Popular on Hangul Planet'));
      const track = el('div', 'xshelf-track');
      for (const item of items) track.appendChild(buildPopularCard(item, openPopularItem));
      section.append(head, track);
      shelfHost.replaceChildren(section);
    } catch {
      // Hide shelf quietly if source is disabled or errors.
      if (shelfHost) shelfHost.replaceChildren();
    }
  }

  async function loadCatalog(reset) {
    if (!active) return;
    if (reset) {
      catalogAbort?.abort();
      page = 1;
      hasNextPage = false;
      gridEl?.replaceChildren();
      loadMoreHost?.replaceChildren();
    }
    if (loading && !reset) return;
    loading = true;
    catalogAbort = new AbortController();
    const currentPage = page;

    try {
      const qs = new URLSearchParams({ page: String(currentPage) });
      if (query.trim()) qs.set('q', query.trim());
      if (status) qs.set('status', status);
      const result = await novelsGet(`/catalog?${qs}`, catalogAbort.signal);
      if (!active) return;
      banner.replaceChildren();
      hasNextPage = Boolean(result.hasNextPage);
      if (countEl) countEl.textContent = `${Number(result.total || 0).toLocaleString()} novels`;

      if (reset && (!result.items || !result.items.length)) {
        gridEl.replaceChildren(stateBox('book', 'No novels match your search or status filter.'));
        loadMoreHost.replaceChildren();
        return;
      }

      const frag = document.createDocumentFragment();
      for (const item of result.items || []) frag.appendChild(buildCatalogCard(item, openCatalogItem));
      gridEl.appendChild(frag);
      page = currentPage + 1;

      loadMoreHost.replaceChildren();
      if (hasNextPage) {
        loadMoreHost.appendChild(button('xbtn', 'Load more', () => loadCatalog(false)));
      }
    } catch (error) {
      if (isAbort(error) || !active) return;
      const box = el('div', 'xerror');
      box.append(icon('alert'), el('span', null, 'Could not load novel catalog.'), button('xbtn', 'Retry', () => loadCatalog(reset)));
      banner.replaceChildren(box);
    } finally {
      loading = false;
    }
  }

  function enter(initialQuery = '') {
    active = true;
    query = initialQuery;
    drawer.mount();
    banner.replaceChildren();

    const root = el('div', 'xn-view');
    shelfHost = el('div', 'xn-shelf-host');

    const bar = el('div', 'xn-bar');
    const chips = el('div', 'xchips');
    function drawChips() {
      chips.replaceChildren();
      for (const [val, label] of NOVEL_STATUSES) {
        const b = button('xfchip', label, () => {
          if (status === val) return;
          status = val;
          drawChips();
          loadCatalog(true);
        });
        b.setAttribute('aria-pressed', String(status === val));
        chips.appendChild(b);
      }
    }
    drawChips();

    countEl = el('span', 'xresult-count');
    const libLink = el('a', 'xlink', 'Open Novel Library →');
    libLink.href = '/bm/novel.html';
    bar.append(chips, countEl, libLink);

    gridEl = el('div', 'xgrid');
    loadMoreHost = el('div', 'xn-more');

    root.append(shelfHost, bar, gridEl, loadMoreHost);
    view.replaceChildren(root);

    loadPopularShelf();
    loadCatalog(true);
  }

  function leave() {
    active = false;
    catalogAbort?.abort();
    popularAbort?.abort();
    drawer.unmount();
  }

  function setQuery(nextQuery) {
    query = nextQuery;
    if (active) loadCatalog(true);
  }

  return {
    enter,
    leave,
    setQuery,
    isOpenDrawer: () => drawer.isOpen(),
    closeDrawer: () => drawer.close()
  };
}
