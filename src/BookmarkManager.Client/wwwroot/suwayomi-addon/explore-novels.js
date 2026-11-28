import {
  el, button, isAbort, stateBox, icon, storageGet, storageSet, trapTab
} from './explore-util.js?v=3';
import { buildSkeletons } from './explore-cards.js?v=3';
import { novelsGet, novelsSend } from './novel-api.js?v=1';
import {
  defaultNovelFilters, novelFiltersFromParams, novelFiltersToParams,
  novelActivePills, hasNovelFilters, novelPanelCount, createNovelFilterPanel
} from './explore-novels-filters.js?v=1';
import {
  novelStateOf, setNovelStateOf, novelfireHit, buildNovelCard, syncNovelCard,
  buildNovelShelf, buildLiveSourceShelf, createNovelDrawer
} from './explore-novels-cards.js?v=2';

const FILTER_DEBOUNCE_MS = 300;
const NOVEL_FILTER_KEY = 'explore.novelFilters';
const VALID_TABS = ['home', 'browse', 'foryou', 'saved'];

export function createNovelsExplore({ view, banner, toast, searchInput, drawerMq, backdropEl, onSyncTab }) {
  let active = false;
  let tab = 'home';
  let savedSub = 'saved';
  let filters = defaultNovelFilters();
  let page = 1;
  let hasNextPage = false;
  let loadingCatalog = false;
  let fetchTimer = null;
  let metaPromise = null;
  let filterOpener = null;

  let catalogAbort = null;
  let recAbort = null;
  let shelvesAbort = null;
  let liveAbort = null;
  let tabAbort = null;

  // Mounted browse DOM nodes (shared between Home and Browse views)
  let browseSection = null;
  let filterAside = null;
  let filterPanel = null;
  let browseGrid = null;
  let loadMoreHost = null;
  let countEl = null;
  let pillsEl = null;
  let filterCountEl = null;
  let recSection = null;

  const drawer = createNovelDrawer({
    toast,
    onLibraryAdded: () => {
      if (active && (tab === 'home' || tab === 'browse')) loadCatalog(true);
    },
    onList: (item, state) => setNovelList(item, state)
  });

  const cardHooks = {
    onOpen: item => openCatalogItem(item),
    onList: (item, state) => setNovelList(item, state),
    onSeeAll: shelf => {
      const qs = new URLSearchParams(shelf.browseQuery || '');
      if (qs.get('view') === 'for-you') return switchTab('foryou');
      const next = novelFiltersFromParams(qs);
      next.readable = filters.readable;
      filters = next;
      switchTab('browse');
    }
  };

  function openCatalogItem(item) {
    drawer.open({
      id: item.id,
      title: item.title,
      coverUrl: item.coverUrl,
      authors: item.authors,
      mediaType: item.mediaType,
      status: item.status,
      genres: item.genres,
      synopsis: item.synopsis,
      alternateTitles: item.alternateTitles,
      inLibrary: item.inLibrary,
      rating: item.rating,
      chapters: item.chapters,
      listState: item.listState,
      resolvedHit: novelfireHit(item)
    });
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

  /* ---------- list state (saved / not interested) ---------- */

  function shouldHide(item) {
    const state = novelStateOf(item);
    if (tab === 'saved') return state !== savedSub;
    return state === 'dismissed' && !filters.showDismissed;
  }

  function applyHidden(id) {
    const selector = id ? `.xncard[data-novel-id="${id}"]` : '.xncard[data-novel-id]';
    document.querySelectorAll(selector).forEach(card => {
      syncNovelCard(card);
      const hide = shouldHide(card.xNovelItem);
      card.classList.toggle('is-out', hide);
      clearTimeout(card.xTimer);
      if (hide) card.xTimer = setTimeout(() => { card.hidden = true; }, 180);
      else card.hidden = false;
    });
    drawer.refresh();
  }

  async function setNovelList(item, state) {
    if (!item?.id) return;
    const previous = novelStateOf(item);
    setNovelStateOf(item.id, state);
    applyHidden(item.id);
    try {
      await novelsSend('PUT', `/list/${item.id}`, {
        state,
        title: item.title,
        coverUrl: item.coverUrl ?? null
      });
    } catch {
      setNovelStateOf(item.id, previous);
      applyHidden(item.id);
      toast('Could not save that change. Try again.');
      return;
    }
    const message = state === 'saved' ? 'Saved' : state === 'dismissed' ? 'Marked not interested' : 'Removed';
    toast(`${message}: ${item.title}`, () => setNovelList(item, previous));
  }

  /* ---------- URL + persistence ---------- */

  function syncUrl() {
    if (!active) return;
    const params = (tab === 'home' || tab === 'browse') ? novelFiltersToParams(filters) : new URLSearchParams();
    params.set('mode', 'novels');
    if (tab !== 'home') params.set('tab', tab);
    if (tab === 'saved' && savedSub !== 'saved') params.set('list', savedSub);
    const query = params.toString();
    history.replaceState(history.state, '', query ? `?${query}` : location.pathname);
    if (tab === 'home' || tab === 'browse') {
      storageSet(NOVEL_FILTER_KEY, novelFiltersToParams(filters).toString());
    }
  }

  /* ---------- filter drawer (<= 900px) ---------- */

  function openFilterDrawer() {
    if (!filterAside) return;
    filterOpener = document.activeElement;
    filterAside.classList.add('is-open');
    backdropEl.hidden = false;
    if (drawerMq.matches) {
      filterAside.setAttribute('role', 'dialog');
      filterAside.setAttribute('aria-modal', 'true');
      document.body.classList.add('is-locked');
      filterAside.querySelector('.xf-close')?.focus();
    }
  }

  function closeFilterDrawer() {
    if (!filterAside?.classList.contains('is-open')) return;
    filterPanel?.closePopups();
    document.body.classList.remove('is-locked');
    filterAside.classList.remove('is-open');
    filterAside.removeAttribute('role');
    filterAside.removeAttribute('aria-modal');
    backdropEl.hidden = true;
    if (filterOpener?.isConnected) filterOpener.focus({ preventScroll: true });
  }

  /* ---------- browse / filter panel ---------- */

  function loadMeta(retry) {
    if (metaPromise && !retry) return metaPromise;
    filterPanel?.setMetaState('loading');
    metaPromise = novelsGet('/meta')
      .then(meta => { filterPanel?.setMeta(meta); return meta; })
      .catch(() => { filterPanel?.setMetaState('error'); });
    return metaPromise;
  }

  function renderPills() {
    if (!pillsEl) return;
    pillsEl.replaceChildren();
    for (const pill of novelActivePills(filters)) {
      pillsEl.appendChild(button('xpill', `${pill.label} ×`, () => changeFilters(pill.patch), `Remove filter: ${pill.label}`));
    }
    const count = novelPanelCount(filters);
    if (filterCountEl) filterCountEl.textContent = count ? `(${count})` : '';
  }

  function syncBrowseUi(force) {
    filterPanel?.sync(filters, force);
    renderPills();
  }

  function changeFilters(patch, force) {
    const prevReadable = filters.readable;
    filters = { ...filters, ...patch };
    if ('q' in patch && searchInput) searchInput.value = filters.q;
    syncUrl();
    syncBrowseUi(force);
    browseGrid?.classList.add('is-stale');
    if (countEl) countEl.textContent = '';
    filterPanel?.setTotal(null);
    clearTimeout(fetchTimer);
    fetchTimer = setTimeout(() => {
      browseGrid?.classList.remove('is-stale');
      loadCatalog(true);
      if (tab === 'home' && prevReadable !== filters.readable) {
        loadRecommends();
        loadShelves();
      }
    }, FILTER_DEBOUNCE_MS);
  }

  function resetFilters() {
    const previous = filters;
    if (!hasNovelFilters(previous)) return;
    changeFilters(defaultNovelFilters(), true);
    toast('Filters reset', () => {
      if (active && (tab === 'home' || tab === 'browse')) changeFilters(previous, true);
    });
  }

  function ensureBrowseSection() {
    if (browseSection) return browseSection;
    browseSection = el('section', 'xn-browse-section');
    const layout = el('div', 'xlayout has-filters');
    filterAside = el('aside', 'xfilters xn-filters');
    filterAside.setAttribute('aria-label', 'Novel filters');
    filterAside.addEventListener('keydown', event => {
      if (filterAside.getAttribute('aria-modal')) trapTab(filterAside, event);
    });

    filterPanel = createNovelFilterPanel(filterAside, {
      onChange: changeFilters,
      onReset: resetFilters,
      onClose: closeFilterDrawer,
      onApply: closeFilterDrawer,
      onRetryMeta: () => loadMeta(true),
      compact: drawerMq.matches
    });

    const main = el('div', 'xmain');
    const bar = el('div', 'xbrowse-bar xn-browse-bar');
    bar.appendChild(el('h2', 'xbrowse-title', 'Browse novels'));
    countEl = el('span', 'xresult-count');
    countEl.setAttribute('aria-live', 'polite');
    filterCountEl = el('span');
    const toggle = button('xbtn xfilter-toggle', 'Filters ', openFilterDrawer);
    toggle.appendChild(filterCountEl);
    const resetTop = button('xlink', 'Reset', resetFilters);
    const libLink = el('a', 'xlink', 'Open Novel Library →');
    libLink.href = '/bm/novel.html';
    pillsEl = el('div', 'xpills');
    bar.append(countEl, toggle, resetTop, libLink, pillsEl);

    browseGrid = el('div', 'xgrid xn-browse-grid');
    loadMoreHost = el('div', 'xn-more');
    main.append(bar, browseGrid, loadMoreHost);
    layout.append(filterAside, main);
    browseSection.appendChild(layout);
    return browseSection;
  }

  async function loadCatalog(reset) {
    if (!active || !browseGrid) return;
    if (reset) {
      catalogAbort?.abort();
      page = 1;
      hasNextPage = false;
      browseGrid.replaceChildren(buildSkeletons(12));
      loadMoreHost.replaceChildren();
    }
    if (loadingCatalog && !reset) return;
    loadingCatalog = true;
    catalogAbort = new AbortController();
    const currentPage = page;

    try {
      const qs = novelFiltersToParams(filters);
      qs.set('page', String(currentPage));
      const result = await novelsGet(`/catalog?${qs}`, catalogAbort.signal);
      if (!active) return;
      banner.replaceChildren();
      browseGrid.querySelectorAll('.xskeleton').forEach(n => n.remove());
      hasNextPage = Boolean(result.hasNextPage);
      const total = Number(result.total || 0);
      if (countEl) countEl.textContent = `${total.toLocaleString()} novel${total === 1 ? '' : 's'}`;
      filterPanel?.setTotal(total);

      if (reset && (!result.items || !result.items.length)) {
        browseGrid.replaceChildren(stateBox('book', 'No novels match your current filters.'));
        loadMoreHost.replaceChildren();
        return;
      }

      const frag = document.createDocumentFragment();
      for (const item of result.items || []) frag.appendChild(buildNovelCard(item, cardHooks));
      browseGrid.appendChild(frag);
      applyHidden();
      page = currentPage + 1;

      loadMoreHost.replaceChildren();
      if (hasNextPage) {
        loadMoreHost.appendChild(button('xbtn', 'Load more', () => loadCatalog(false)));
      }
    } catch (error) {
      if (isAbort(error) || !active) return;
      browseGrid.querySelectorAll('.xskeleton').forEach(n => n.remove());
      const box = el('div', 'xerror');
      box.append(icon('alert'), el('span', null, 'Could not load novel catalog.'), button('xbtn', 'Retry', () => loadCatalog(reset)));
      banner.replaceChildren(box);
    } finally {
      loadingCatalog = false;
    }
  }

  /* ---------- home: recommends + catalog shelves + live source shelves ---------- */

  let shelvesHost = null;
  let liveHost = null;

  function createRecommendsHost() {
    const section = el('section', 'xn-recommends');
    section.setAttribute('aria-label', 'Recommended novels');
    const head = el('div', 'xshelf-head');
    const titles = el('div');
    titles.append(el('h2', null, 'Recommends'), el('p', 'xshelf-sub', 'Fresh picks shuffled from trending novels'));
    const shuffleBtn = button('xbtn xn-shuffle', 'Shuffle', () => loadRecommends());
    head.append(titles, shuffleBtn);
    const grid = el('div', 'xn-rec-grid');
    section.append(head, grid);
    return { section, grid, shuffleBtn };
  }

  async function loadRecommends() {
    if (!recSection) return;
    recAbort?.abort();
    recAbort = new AbortController();
    recSection.shuffleBtn.disabled = true;
    recSection.grid.replaceChildren(buildSkeletons(12));
    try {
      const qs = new URLSearchParams({ count: '12' });
      if (!filters.readable) qs.set('readable', 'false');
      const items = await novelsGet(`/recommends?${qs}`, recAbort.signal);
      if (!active || !recSection) return;
      if (!Array.isArray(items) || !items.length) {
        recSection.section.hidden = true;
        return;
      }
      recSection.section.hidden = false;
      const frag = document.createDocumentFragment();
      for (const item of items) frag.appendChild(buildNovelCard(item, cardHooks, { compact: true }));
      recSection.grid.replaceChildren(frag);
      applyHidden();
    } catch (error) {
      if (isAbort(error) || !active) return;
      recSection.section.hidden = true;
    } finally {
      if (recSection) recSection.shuffleBtn.disabled = false;
    }
  }

  async function loadShelves() {
    if (!shelvesHost) return;
    shelvesAbort?.abort();
    shelvesAbort = new AbortController();
    try {
      const qs = filters.readable ? '' : '?readable=false';
      const shelves = await novelsGet(`/shelves${qs}`, shelvesAbort.signal);
      if (!active || !shelvesHost) return;
      const frag = document.createDocumentFragment();
      for (const shelf of shelves || []) {
        if (shelf.items?.length) frag.appendChild(buildNovelShelf(shelf, cardHooks));
      }
      shelvesHost.replaceChildren(frag);
      applyHidden();
    } catch (error) {
      if (isAbort(error) || !active) return;
      shelvesHost.replaceChildren();
    }
  }

  async function loadLiveSourceShelves() {
    if (!liveHost) return;
    liveAbort?.abort();
    liveAbort = new AbortController();
    const { signal } = liveAbort;
    try {
      const sources = await novelsGet('/sources', signal);
      if (!active || !liveHost || !Array.isArray(sources) || !sources.length) return;
      const settled = await Promise.allSettled(
        sources.map(async src => {
          const items = await novelsGet(`/popular?source=${encodeURIComponent(src.id)}&page=1`, signal);
          return { src, items };
        })
      );
      if (!active || !liveHost) return;
      const frag = document.createDocumentFragment();
      for (const entry of settled) {
        if (entry.status !== 'fulfilled') continue;
        const { src, items } = entry.value;
        if (Array.isArray(items) && items.length) {
          frag.appendChild(buildLiveSourceShelf(src, items, openPopularItem));
        }
      }
      liveHost.replaceChildren(frag);
    } catch (error) {
      if (isAbort(error) || !active) return;
      liveHost.replaceChildren();
    }
  }

  /* ---------- tabs: home, browse, foryou, saved ---------- */

  function abortAll() {
    clearTimeout(fetchTimer);
    catalogAbort?.abort();
    recAbort?.abort();
    shelvesAbort?.abort();
    liveAbort?.abort();
    tabAbort?.abort();
  }

  function renderHome() {
    const root = el('div', 'xn-view');
    recSection = createRecommendsHost();
    shelvesHost = el('div', 'xn-shelves');
    liveHost = el('div', 'xn-shelf-host xn-live-shelves');
    const browse = ensureBrowseSection();
    root.append(recSection.section, shelvesHost, liveHost, browse);
    view.replaceChildren(root);

    syncBrowseUi(true);
    loadMeta();
    loadRecommends();
    loadShelves();
    loadLiveSourceShelves();
    loadCatalog(true);
  }

  function renderBrowse() {
    recSection = null;
    shelvesHost = null;
    liveHost = null;
    const root = el('div', 'xn-view');
    root.appendChild(ensureBrowseSection());
    view.replaceChildren(root);
    syncBrowseUi(true);
    loadMeta();
    loadCatalog(true);
  }

  async function renderForYou() {
    recSection = null;
    shelvesHost = null;
    liveHost = null;
    tabAbort = new AbortController();
    const { signal } = tabAbort;

    const root = el('div', 'xn-view');
    const head = el('div', 'xbrowse-bar');
    head.appendChild(el('h2', 'xbrowse-title', 'For you'));
    const grid = el('div', 'xgrid');
    grid.appendChild(buildSkeletons(12));
    const moreHost = el('div', 'xn-more');
    root.append(head, grid, moreHost);
    view.replaceChildren(root);

    let fyPage = 1;
    async function loadPage(reset) {
      try {
        const qs = new URLSearchParams({ page: String(fyPage) });
        if (!filters.readable) qs.set('readable', 'false');
        const res = await novelsGet(`/for-you?${qs}`, signal);
        if (!active) return;
        grid.querySelectorAll('.xskeleton').forEach(n => n.remove());
        if (reset && (!res.items || !res.items.length)) {
          grid.replaceChildren(stateBox('book', 'No recommendations yet. Add novels to your library or bookmarks to personalize this feed.'));
          moreHost.replaceChildren();
          return;
        }
        const frag = document.createDocumentFragment();
        for (const item of res.items || []) frag.appendChild(buildNovelCard(item, cardHooks));
        grid.appendChild(frag);
        applyHidden();
        fyPage += 1;
        moreHost.replaceChildren();
        if (res.hasNextPage) moreHost.appendChild(button('xbtn', 'Load more', () => loadPage(false)));
      } catch (error) {
        if (isAbort(error) || !active) return;
        grid.querySelectorAll('.xskeleton').forEach(n => n.remove());
        const box = el('div', 'xerror');
        box.append(icon('alert'), el('span', null, 'Could not load novel recommendations.'), button('xbtn', 'Retry', () => renderForYou()));
        banner.replaceChildren(box);
      }
    }
    loadPage(true);
  }

  async function renderSaved() {
    recSection = null;
    shelvesHost = null;
    liveHost = null;
    tabAbort = new AbortController();
    const { signal } = tabAbort;

    const root = el('div', 'xn-view');
    const head = el('div', 'xsubtabs');
    for (const [value, label] of [['saved', 'Saved'], ['dismissed', 'Not interested']]) {
      const b = button('xfchip', label, () => { savedSub = value; syncUrl(); renderSaved(); });
      b.setAttribute('aria-pressed', String(savedSub === value));
      head.appendChild(b);
    }
    const holder = el('div');
    const grid = el('div', 'xgrid');
    grid.appendChild(buildSkeletons(8));
    holder.appendChild(grid);
    root.append(head, holder);
    view.replaceChildren(root);

    try {
      const items = await novelsGet(`/list?state=${savedSub}`, signal);
      if (!active) return;
      if (!items?.length) {
        holder.replaceChildren(stateBox(
          savedSub === 'saved' ? 'heart' : 'book',
          savedSub === 'saved'
            ? 'No saved novels yet. Tap the heart on a novel to keep it here.'
            : 'No dismissed novels. Anything you mark not interested shows up here.'
        ));
        return;
      }
      grid.replaceChildren();
      for (const item of items) grid.appendChild(buildNovelCard(item, cardHooks));
      applyHidden();
    } catch (error) {
      if (isAbort(error) || !active) return;
      holder.replaceChildren();
      const box = el('div', 'xerror');
      box.append(icon('alert'), el('span', null, 'Could not load your novel list.'), button('xbtn', 'Retry', renderSaved));
      banner.replaceChildren(box);
    }
  }

  function switchTab(next) {
    tab = VALID_TABS.includes(next) ? next : 'home';
    abortAll();
    closeFilterDrawer();
    banner.replaceChildren();
    onSyncTab?.(tab);
    syncUrl();
    if (searchInput) searchInput.value = filters.q;
    if (tab === 'home') renderHome();
    else if (tab === 'browse') renderBrowse();
    else if (tab === 'foryou') renderForYou();
    else renderSaved();
  }

  function enter({ initialTab = 'home', params = null } = {}) {
    active = true;
    drawer.mount();
    if (params && params.get('mode') === 'novels') {
      const parsed = novelFiltersFromParams(params);
      const hasUrlFilters = novelFiltersToParams(parsed).size > 0;
      filters = hasUrlFilters
        ? parsed
        : (initialTab === 'browse' ? novelFiltersFromParams(new URLSearchParams(storageGet(NOVEL_FILTER_KEY) || '')) : defaultNovelFilters());
      savedSub = params.get('list') === 'dismissed' ? 'dismissed' : 'saved';
      tab = VALID_TABS.includes(initialTab) ? initialTab : (hasUrlFilters ? 'browse' : 'home');
    } else {
      filters = defaultNovelFilters();
      savedSub = 'saved';
      tab = 'home';
    }
    switchTab(tab);
  }

  function leave() {
    active = false;
    abortAll();
    closeFilterDrawer();
    drawer.unmount();
  }

  function setQuery(nextQuery) {
    if (!active) return;
    if (nextQuery === filters.q && (tab === 'home' || tab === 'browse')) return;
    if (tab !== 'home' && tab !== 'browse') {
      filters = { ...filters, q: nextQuery };
      switchTab('browse');
      return;
    }
    changeFilters({ q: nextQuery });
  }

  return {
    enter,
    leave,
    switchTab,
    setQuery,
    closeFilterDrawer,
    isFilterDrawerOpen: () => Boolean(filterAside?.classList.contains('is-open')),
    isOpenDrawer: () => drawer.isOpen(),
    closeDrawer: () => drawer.close()
  };
}
