import {
  el, button, getJson, send, isAbort, errorMessage, stateOf, setStateOf, storageGet, storageSet, stateBox, icon, trapTab
} from './explore-util.js?v=3';
import { buildCard, buildShelf, buildSkeletons, syncCard } from './explore-cards.js?v=3';
import { buildHero } from './explore-hero.js?v=3';
import { createGridLoader } from './explore-grid.js?v=3';
import { createDetail } from './explore-detail.js?v=3';
import { createNovelsExplore } from './explore-novels.js?v=2';
import {
  createFilterPanel, filtersFromParams, filtersToParams, defaultFilters, activePills, hasFilters, panelCount
} from './explore-filters.js?v=3';

const TABS = ['home', 'browse', 'foryou', 'saved'];
const SEARCH_DEBOUNCE_MS = 350;
const FILTER_DEBOUNCE_MS = 300;
const TOAST_MS = 6000;
const FILTER_KEY = 'explore.filters';
const MODE_KEY = 'explore.mode';
const $ = id => document.getElementById(id);

const view = $('view');
const banner = $('banner');
const searchInput = $('search');
const filtersEl = $('filters');
const toolbar = $('browse-bar');
const tabsNav = document.querySelector('.xtabs');
const pillsEl = $('pills');
const countEl = $('count');
const drawerMq = matchMedia('(max-width: 900px)');
const headEl = document.querySelector('.xhead');

let mode = 'manga';
let tab = 'home';
let savedSub = 'saved';
let filters = defaultFilters();
let shelvesAbort = null;
let metaPromise = null;
let searchTimer = null;
let fetchTimer = null;
let filterOpener = null;
let hero = null;

const hooks = {
  onOpen: item => detail.open(item.anilistId),
  onList: (item, state) => setList(item, state),
  onSeeAll: shelf => {
    const query = new URLSearchParams(shelf.browseQuery || '');
    if (query.get('view') === 'for-you') return switchTab('foryou');
    filters = filtersFromParams(query);
    switchTab('browse');
  }
};

const detail = createDetail({
  onList: (item, state) => setList(item, state),
  onTag: (kind, name) => detail.closeThen(() => {
    const f = defaultFilters();
    filters = kind === 'genre' ? { ...f, genres: [name] } : { ...f, tags: [name] };
    switchTab('browse');
  }),
  onLibraryAdded: id => document.querySelectorAll(`.xcard[data-id="${id}"]`).forEach(syncCard),
  toast: message => toast(message)
});

const novelsExplore = createNovelsExplore({
  view,
  banner,
  toast,
  searchInput,
  drawerMq,
  backdropEl: $('filter-backdrop'),
  onSyncTab: nextTab => {
    document.querySelectorAll('[data-tab]').forEach(b => b.setAttribute('aria-current', b.dataset.tab === nextTab ? 'page' : 'false'));
  }
});

const grid = createGridLoader({
  container: view, banner, hooks, emptyText: 'Nothing found. Try removing a filter or searching a different title.',
  onPage: (result, page) => {
    if (tab === 'browse' && page === 1) {
      countEl.textContent = result.total != null ? `${result.total.toLocaleString()} results` : '';
      panel.setTotal(result.total ?? null);
    }
    applyHidden();
  }
});

/* ---------- toasts ---------- */

function toast(message, undo) {
  const box = el('div', 'xtoast');
  box.appendChild(el('span', null, message));
  const remove = () => box.remove();
  if (undo) box.appendChild(button('xbtn', 'Undo', () => { remove(); undo(); }));
  $('toasts').appendChild(box);
  setTimeout(remove, TOAST_MS);
}

/* ---------- list state (saved / not interested) ---------- */

function shouldHide(item) {
  const state = stateOf(item);
  if (tab === 'saved') return state !== savedSub;
  return state === 'dismissed' && !filters.showDismissed;
}

function applyHidden(id) {
  const selector = id ? `.xcard[data-id="${id}"]` : '.xcard';
  document.querySelectorAll(selector).forEach(card => {
    syncCard(card);
    const hide = shouldHide(card.xItem);
    card.classList.toggle('is-out', hide);
    clearTimeout(card.xTimer);
    if (hide) card.xTimer = setTimeout(() => { card.hidden = true; }, 180);
    else card.hidden = false;
  });
  detail.refresh();
  hero?.sync();
}

async function putState(item, state) {
  await send('PUT', `/list/${item.anilistId}`, {
    state, title: item.title, coverUrl: item.coverUrl ?? null, type: item.type || 'Unknown'
  });
}

async function setList(item, state) {
  const previous = stateOf(item);
  setStateOf(item.anilistId, state);
  applyHidden(item.anilistId);
  try {
    await putState(item, state);
  } catch {
    setStateOf(item.anilistId, previous);
    applyHidden(item.anilistId);
    toast('Could not save that change. Try again.');
    return;
  }
  const message = state === 'saved' ? 'Saved' : state === 'dismissed' ? 'Marked not interested' : 'Removed';
  toast(`${message}: ${item.title}`, () => setList(item, previous));
}

/* ---------- URL + persistence ---------- */

function syncUrl() {
  const params = tab === 'browse' ? filtersToParams(filters) : new URLSearchParams();
  if (tab !== 'home') params.set('tab', tab);
  if (tab === 'saved' && savedSub !== 'saved') params.set('list', savedSub);
  const id = new URLSearchParams(location.search).get('id');
  if (id) params.set('id', id);
  const query = params.toString();
  history.replaceState(history.state, '', query ? `?${query}` : location.pathname);
  if (tab === 'browse') storageSet(FILTER_KEY, filtersToParams(filters).toString());
}

function readInitialState() {
  const params = new URLSearchParams(location.search);
  const urlMode = params.get('mode');
  const urlTab = params.get('tab');
  const filterParams = filtersToParams(filtersFromParams(params));
  const hasMangaUrl = urlMode !== 'novels' && Boolean(params.get('id') || urlTab || filterParams.size);
  const storedMode = storageGet(MODE_KEY);
  mode = urlMode === 'novels' ? 'novels' : (!hasMangaUrl && storedMode === 'novels' ? 'novels' : 'manga');
  if (TABS.includes(urlTab)) tab = urlTab;
  else if (filterParams.size) tab = 'browse';
  savedSub = params.get('list') === 'dismissed' ? 'dismissed' : 'saved';
  if (mode === 'manga') {
    if (filterParams.size) filters = filtersFromParams(params);
    else if (tab === 'browse') filters = filtersFromParams(new URLSearchParams(storageGet(FILTER_KEY) || ''));
    searchInput.value = filters.q;
  } else {
    searchInput.value = params.get('q') || '';
  }
}

function switchMode(next, initialParams = null) {
  mode = next === 'novels' ? 'novels' : 'manga';
  storageSet(MODE_KEY, mode);
  document.querySelectorAll('[data-mode]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.mode === mode)));
  if (mode === 'novels') {
    shelvesAbort?.abort();
    clearTimeout(fetchTimer);
    hero?.stop();
    hero = null;
    grid.stop();
    detail.close();
    closeFilterDrawer();
    tabsNav.hidden = false;
    toolbar.hidden = true;
    filtersEl.hidden = true;
    $('layout').classList.remove('has-filters');
    searchInput.placeholder = 'Search novels  ( / )';
    searchInput.setAttribute('aria-label', 'Search novels');
    novelsExplore.enter({ initialTab: initialParams?.get('tab') || 'home', params: initialParams });
    return;
  }

  novelsExplore.leave();
  tabsNav.hidden = false;
  searchInput.placeholder = 'Search series  ( / )';
  searchInput.setAttribute('aria-label', 'Search series');
  switchTab(tab);
}

/* ---------- tabs ---------- */

function switchTab(next) {
  tab = next;
  shelvesAbort?.abort();
  clearTimeout(fetchTimer);
  hero?.stop();
  hero = null;
  grid.stop();
  banner.replaceChildren();
  document.querySelectorAll('[data-tab]').forEach(b => b.setAttribute('aria-current', b.dataset.tab === tab ? 'page' : 'false'));
  const browse = tab === 'browse';
  toolbar.hidden = !browse;
  filtersEl.hidden = !browse;
  $('layout').classList.toggle('has-filters', browse);
  closeFilterDrawer();
  syncUrl();
  if (browse) { searchInput.value = filters.q; renderBrowse(); }
  else if (tab === 'home') renderHome();
  else if (tab === 'foryou') renderForYou();
  else renderSaved();
}

function homeSkeleton() {
  const frag = document.createDocumentFragment();
  frag.appendChild(el('div', 'xhero-skel'));
  const track = el('div', 'xshelf-track');
  track.appendChild(buildSkeletons(10));
  frag.appendChild(track);
  return frag;
}

function showError(error, retry) {
  const box = el('div', 'xerror');
  box.append(icon('alert'), el('span', null, errorMessage(error)), button('xbtn', 'Retry', retry));
  banner.replaceChildren(box);
}

async function renderHome() {
  view.replaceChildren(homeSkeleton());
  shelvesAbort = new AbortController();
  const { signal } = shelvesAbort;
  try {
    const shelves = await getJson('/shelves', signal);
    banner.replaceChildren();
    const frag = document.createDocumentFragment();
    const spotlight = shelves.find(s => s.key === 'trending' && s.items?.length) || shelves.find(s => s.items?.length);
    hero = spotlight ? buildHero(spotlight.items, hooks) : null;
    if (hero) frag.appendChild(hero.root);
    for (const shelf of shelves) if (shelf.items?.length) frag.appendChild(buildShelf(shelf, hooks));
    if (!frag.childElementCount) frag.appendChild(stateBox('book', 'No shelves available right now. Try again in a few minutes.'));
    view.replaceChildren(frag);
    applyHidden();
  } catch (error) {
    if (isAbort(error)) return;
    view.replaceChildren();
    showError(error, renderHome);
  }
}

function renderForYou() {
  grid.reset(grid.listFetcher('/for-you', new URLSearchParams()));
}

function renderSaved() {
  const head = el('div', 'xsubtabs');
  for (const [value, label] of [['saved', 'Saved'], ['dismissed', 'Not interested']]) {
    const b = button('xfchip', label, () => { savedSub = value; syncUrl(); renderSaved(); });
    b.setAttribute('aria-pressed', String(savedSub === value));
    head.appendChild(b);
  }
  const holder = el('div');
  view.replaceChildren(head, holder);
  grid.reset(async (_page, signal) => {
    const items = await getJson(`/list?state=${savedSub}`, signal);
    return { items, hasNextPage: false };
  }, { target: holder, emptyIcon: savedSub === 'saved' ? 'heart' : 'book',
    empty: savedSub === 'saved' ? 'Nothing saved yet. Tap the heart on a series to keep it here.' : 'No dismissed series. Anything you mark not interested shows up here.' });
}

/* ---------- browse ---------- */

// Meta failures are not retried on every filter change; the panel's Retry calls loadMeta(true).
function loadMeta(retry) {
  if (metaPromise && !retry) return metaPromise;
  panel.setMetaState('loading');
  metaPromise = getJson('/meta').then(meta => { panel.setMeta(meta); return meta; })
    .catch(() => { panel.setMetaState('error'); });
  return metaPromise;
}

function syncBrowseUi(force) {
  panel.sync(filters, force);
  renderPills();
}

function fetchBrowse() {
  clearTimeout(fetchTimer);
  view.classList.remove('is-stale');
  countEl.textContent = '';
  panel.setTotal(null);
  const params = filtersToParams(filters);
  params.delete('q');
  if (filters.q.trim()) params.set('q', filters.q.trim());
  grid.reset(grid.listFetcher('/browse', params));
}

function renderBrowse() {
  syncBrowseUi();
  loadMeta();
  fetchBrowse();
}

function renderPills() {
  pillsEl.replaceChildren();
  for (const pill of activePills(filters)) {
    const b = button('xpill', `${pill.label} ×`, () => changeFilters(pill.patch), `Remove filter: ${pill.label}`);
    pillsEl.appendChild(b);
  }
  const count = panelCount(filters);
  $('filter-count').textContent = count ? `(${count})` : '';
}

// Panel + pills update at once; the grid request waits for a short pause so bursts of clicks send one fetch.
function changeFilters(patch, force) {
  filters = { ...filters, ...patch };
  if ('q' in patch) searchInput.value = filters.q;
  syncUrl();
  syncBrowseUi(force);
  grid.stop();
  view.classList.add('is-stale');
  countEl.textContent = '';
  panel.setTotal(null);
  clearTimeout(fetchTimer);
  fetchTimer = setTimeout(fetchBrowse, FILTER_DEBOUNCE_MS);
}

function resetFilters() {
  const previous = filters;
  if (!hasFilters(previous)) return;
  changeFilters(defaultFilters(), true);
  toast('Filters reset', () => { if (tab === 'browse') changeFilters(previous, true); });
}

const panel = createFilterPanel(filtersEl, {
  onChange: changeFilters,
  onReset: resetFilters,
  onSurprise: surprise,
  onClose: closeFilterDrawer,
  onApply: closeFilterDrawer,
  onRetryMeta: () => loadMeta(true),
  compact: drawerMq.matches
});

async function surprise() {
  const params = filtersToParams(filters);
  try {
    const item = await getJson(`/random?${params}`);
    closeFilterDrawer();
    detail.open(item.anilistId);
  } catch (error) {
    toast(error?.status === 404 ? 'Nothing matches these filters.' : errorMessage(error));
  }
}

/* ---------- filter drawer (<= 900px) ---------- */

function openFilterDrawer() {
  filterOpener = document.activeElement;
  filtersEl.classList.add('is-open');
  $('filter-backdrop').hidden = false;
  if (drawerMq.matches) {
    filtersEl.setAttribute('role', 'dialog');
    filtersEl.setAttribute('aria-modal', 'true');
    document.body.classList.add('is-locked');
    filtersEl.querySelector('.xf-close').focus();
  }
}

function closeFilterDrawer() {
  if (!filtersEl.classList.contains('is-open')) return;
  panel.closePopups();
  document.body.classList.remove('is-locked');
  filtersEl.classList.remove('is-open');
  filtersEl.removeAttribute('role');
  filtersEl.removeAttribute('aria-modal');
  $('filter-backdrop').hidden = true;
  if (filterOpener?.isConnected) filterOpener.focus({ preventScroll: true });
}

/* ---------- search + keyboard ---------- */

function applySearch() {
  clearTimeout(searchTimer);
  const q = searchInput.value;
  if (mode === 'novels') {
    novelsExplore.setQuery(q);
    return;
  }
  if (tab === 'browse' && q === filters.q) return;
  if (tab !== 'browse' && !q.trim() && q === filters.q) return;
  filters = { ...filters, q };
  if (tab === 'browse') { syncUrl(); renderBrowse(); } else switchTab('browse');
}

searchInput.addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(applySearch, SEARCH_DEBOUNCE_MS);
});
searchInput.addEventListener('keydown', event => { if (event.key === 'Enter') applySearch(); });

document.addEventListener('keydown', event => {
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName);
  const anyDrawerOpen = detail.isOpen() || novelsExplore.isOpenDrawer();
  if (event.key === '/' && !typing && !anyDrawerOpen) { event.preventDefault(); searchInput.focus(); }
  else if (event.key === 'Escape' && filtersEl.classList.contains('is-open')) closeFilterDrawer();
  else if (event.key === 'Escape' && novelsExplore.isFilterDrawerOpen()) novelsExplore.closeFilterDrawer();
  else if (event.key === 'Escape' && detail.isOpen()) detail.close();
  else if (event.key === 'Escape' && novelsExplore.isOpenDrawer()) novelsExplore.closeDrawer();
});
filtersEl.addEventListener('keydown', event => {
  if (filtersEl.getAttribute('aria-modal')) trapTab(filtersEl, event);
});
// Leaving the drawer layout (rotate / resize) must not leave a modal, scroll-locked page behind.
drawerMq.addEventListener('change', () => {
  if (!drawerMq.matches) {
    closeFilterDrawer();
    novelsExplore.closeFilterDrawer();
  }
});

/* ---------- init ---------- */

document.querySelectorAll('[data-mode]').forEach(b => b.addEventListener('click', () => { if (b.dataset.mode !== mode) switchMode(b.dataset.mode); }));
document.querySelectorAll('[data-tab]').forEach(b => b.addEventListener('click', () => {
  if (mode === 'novels') { novelsExplore.switchTab(b.dataset.tab); return; }
  if (b.dataset.tab !== tab) switchTab(b.dataset.tab);
}));
$('filter-toggle').addEventListener('click', openFilterDrawer);
$('filter-backdrop').addEventListener('click', () => { closeFilterDrawer(); novelsExplore.closeFilterDrawer(); });
$('reset-top').addEventListener('click', resetFilters);

// Sticky sidebar offsets itself below the (variable-height) sticky header.
new ResizeObserver(() => document.documentElement.style.setProperty('--head-h', `${headEl.offsetHeight}px`)).observe(headEl);

readInitialState();
if (mode === 'novels') {
  switchMode('novels', new URLSearchParams(location.search));
} else {
  document.querySelectorAll('[data-mode]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.mode === 'manga')));
  switchTab(tab);
  detail.syncFromLocation();
}
