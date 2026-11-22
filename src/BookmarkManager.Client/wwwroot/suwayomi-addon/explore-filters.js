import { el, button, STATUS_OPTIONS, storageGet, storageSet } from './explore-util.js?v=3';
import {
  createSection, pressGroup, triChip, syncTri, selectedChip, switchRow, scoreField, rangeField
} from './explore-filter-ui.js?v=1';
import { createTagCombobox } from './explore-tag-combobox.js?v=1';

export const TYPES = [['all', 'All'], ['manhwa', 'Manhwa'], ['manhua', 'Manhua'], ['manga', 'Manga']];
export const SORTS = [
  ['', 'Best match'], ['popularity', 'Popularity'], ['score', 'Score'], ['trending', 'Trending'],
  ['newest', 'Newest'], ['updated', 'Recently updated'], ['recentlyFinished', 'Recently finished'],
  ['chapters', 'Chapters'], ['favourites', 'Favourites'], ['title', 'Title']
];
const LISTS = ['status', 'genres', 'excludeGenres', 'tags', 'excludeTags'];
const NUMS = ['minScore', 'minChapters', 'maxChapters', 'yearFrom', 'yearTo', 'maxPopularity'];
const SECTIONS_KEY = 'explore.filterSections';
const THIS_YEAR = new Date().getFullYear();

export const defaultFilters = () => ({
  q: '', type: 'all', sort: '', status: [], genres: [], excludeGenres: [], tags: [], excludeTags: [],
  minScore: '', minChapters: '', maxChapters: '', yearFrom: '', yearTo: '', maxPopularity: '',
  hideLibrary: false, showDismissed: false, adult: false
});

const csv = value => [...new Set((value || '').split(',').map(s => s.trim()).filter(Boolean))];
const num = value => (value !== '' && value != null && Number.isFinite(Number(value)) ? String(Number(value)) : '');
const lower = list => new Set(list.map(v => v.toLowerCase()));

export function filtersFromParams(params) {
  const f = defaultFilters();
  f.q = params.get('q') || '';
  const type = (params.get('type') || 'all').toLowerCase();
  f.type = TYPES.some(([v]) => v === type) ? type : 'all';
  const sort = params.get('sort') || '';
  f.sort = SORTS.some(([v]) => v === sort) ? sort : '';
  for (const key of LISTS) f[key] = csv(params.get(key));
  f.status = f.status.filter(s => STATUS_OPTIONS.some(([v]) => v === s));
  // A name in both include and exclude lists: exclude wins.
  const exGenres = lower(f.excludeGenres);
  const exTags = lower(f.excludeTags);
  f.genres = f.genres.filter(g => !exGenres.has(g.toLowerCase()));
  f.tags = f.tags.filter(t => !exTags.has(t.toLowerCase()));
  for (const key of NUMS) f[key] = num(params.get(key));
  f.hideLibrary = params.get('hideLibrary') === 'true';
  f.showDismissed = params.get('hideDismissed') === 'false';
  f.adult = params.get('adult') === 'true';
  return f;
}

export function filtersToParams(f) {
  const p = new URLSearchParams();
  if (f.q.trim()) p.set('q', f.q.trim());
  if (f.type !== 'all') p.set('type', f.type);
  if (f.sort) p.set('sort', f.sort);
  for (const key of LISTS) if (f[key].length) p.set(key, f[key].join(','));
  for (const key of NUMS) if (f[key] !== '' && !(key === 'minScore' && Number(f[key]) === 0)) p.set(key, f[key]);
  if (f.hideLibrary) p.set('hideLibrary', 'true');
  if (f.showDismissed) p.set('hideDismissed', 'false');
  if (f.adult) p.set('adult', 'true');
  return p;
}

export const hasFilters = f => filtersToParams(f).size > 0;
const without = (list, value) => list.filter(v => v !== value);
const withValue = (list, value) => (list.includes(value) ? list : [...list, value]);

// Tri-state: off -> include -> exclude -> off.
export function triState(f, inc, exc, name) {
  if (f[inc].includes(name)) return { [inc]: without(f[inc], name), [exc]: withValue(f[exc], name) };
  if (f[exc].includes(name)) return { [inc]: f[inc], [exc]: without(f[exc], name) };
  return { [inc]: withValue(f[inc], name), [exc]: f[exc] };
}

// Put `name` in exactly one list (or none when mode is null).
function setMode(f, inc, exc, name, mode) {
  return {
    [inc]: mode === 'include' ? withValue(f[inc], name) : without(f[inc], name),
    [exc]: mode === 'exclude' ? withValue(f[exc], name) : without(f[exc], name)
  };
}

const stateFor = (f, inc, exc, name) => (f[inc].includes(name) ? 'include' : f[exc].includes(name) ? 'exclude' : 'off');

export function activePills(f) {
  const pills = [];
  const add = (label, patch) => pills.push({ label, patch });
  if (f.q.trim()) add(`"${f.q.trim()}"`, { q: '' });
  if (f.type !== 'all') add(f.type[0].toUpperCase() + f.type.slice(1), { type: 'all' });
  for (const s of f.status) add(STATUS_OPTIONS.find(([v]) => v === s)[1], { status: without(f.status, s) });
  for (const g of f.genres) add(g, { genres: without(f.genres, g) });
  for (const g of f.excludeGenres) add(`Not ${g}`, { excludeGenres: without(f.excludeGenres, g) });
  for (const t of f.tags) add(t, { tags: without(f.tags, t) });
  for (const t of f.excludeTags) add(`Not ${t}`, { excludeTags: without(f.excludeTags, t) });
  if (Number(f.minScore) > 0) add(`Score ${f.minScore}+`, { minScore: '' });
  if (f.minChapters !== '' || f.maxChapters !== '') {
    add(`Chapters ${f.minChapters || 0}–${f.maxChapters || 'any'}`, { minChapters: '', maxChapters: '' });
  }
  if (f.yearFrom !== '' || f.yearTo !== '') add(`Year ${f.yearFrom || 'any'}–${f.yearTo || 'any'}`, { yearFrom: '', yearTo: '' });
  if (f.maxPopularity !== '') add('Hidden gems', { maxPopularity: '' });
  if (f.hideLibrary) add('Hiding my library', { hideLibrary: false });
  if (f.showDismissed) add('Showing dismissed', { showDismissed: false });
  if (f.adult) add('Adult', { adult: false });
  return pills;
}

// Panel-owned filters (the search box has its own clear).
export const panelCount = f => activePills({ ...f, q: '' }).length;

// Per-section active count and the patch that clears it.
const SECTION_DEFS = [
  ['type', 'Type', f => Number(f.type !== 'all'), { type: 'all' }],
  ['status', 'Status', f => f.status.length, { status: [] }],
  ['sort', 'Sort', f => Number(f.sort !== ''), { sort: '' }],
  ['genres', 'Genres', f => f.genres.length + f.excludeGenres.length, { genres: [], excludeGenres: [] }],
  ['tags', 'Tags', f => f.tags.length + f.excludeTags.length, { tags: [], excludeTags: [] }],
  ['score', 'Minimum score', f => Number(Number(f.minScore) > 0), { minScore: '' }],
  ['chapters', 'Chapters', f => Number(f.minChapters !== '' || f.maxChapters !== ''), { minChapters: '', maxChapters: '' }],
  ['year', 'Start year', f => Number(f.yearFrom !== '' || f.yearTo !== ''), { yearFrom: '', yearTo: '' }],
  ['options', 'Options', f => Number(f.hideLibrary) + Number(f.showDismissed) + Number(f.adult) + Number(f.maxPopularity !== ''),
    { hideLibrary: false, showDismissed: false, adult: false, maxPopularity: '' }]
];
const COMPACT_OPEN = new Set(['type', 'status', 'sort', 'genres', 'tags']);

function loadOpenState(compact) {
  let stored = {};
  try { stored = JSON.parse(storageGet(SECTIONS_KEY) || '{}') || {}; } catch { stored = {}; }
  return key => (typeof stored[key] === 'boolean' ? stored[key] : !compact || COMPACT_OPEN.has(key));
}

function saveOpenState(key, open) {
  let stored = {};
  try { stored = JSON.parse(storageGet(SECTIONS_KEY) || '{}') || {}; } catch { stored = {}; }
  storageSet(SECTIONS_KEY, JSON.stringify({ ...stored, [key]: open }));
}

function metaStatus(noun, onRetry) {
  const box = el('div', 'xf-meta');
  const skel = el('div', 'xf-skel');
  for (let i = 0; i < 8; i++) skel.appendChild(el('span', 'xsk-chip'));
  const error = el('p', 'xf-meta-error');
  error.append(el('span', null, `Couldn't load ${noun}.`), button('xlink', 'Retry', onRetry, `Retry loading ${noun}`));
  box.append(skel, error);
  const set = state => { box.hidden = state === 'ready'; skel.hidden = state !== 'loading'; error.hidden = state !== 'error'; };
  return { root: box, set };
}

// Builds the panel once; `sync(f, force)` reflects state, `setMeta` / `setMetaState` handle genre + tag data.
export function createFilterPanel(root, { onChange, onReset, onSurprise, onClose, onApply, onRetryMeta, compact }) {
  let last = defaultFilters();
  let meta = null;
  let metaState = 'loading';
  const isOpen = loadOpenState(compact);
  const live = el('p', 'xsr');
  live.setAttribute('aria-live', 'polite');
  const say = text => { live.textContent = ''; requestAnimationFrame(() => { live.textContent = text; }); };

  const head = el('div', 'xf-head');
  const title = el('h2', null, 'Filters');
  const total = el('span', 'xf-badge xf-total');
  title.appendChild(total);
  const resetAll = button('xlink xf-reset', 'Reset all', onReset);
  head.append(title, resetAll, button('xf-close', '×', onClose, 'Close filters'));

  const body = el('div', 'xf-body');
  const sections = new Map();
  for (const [key, label, , clearPatch] of SECTION_DEFS) {
    const sec = createSection(key, label, {
      open: isOpen(key), onToggle: saveOpenState,
      onClear: () => { onChange(clearPatch); say(`${label} cleared`); }
    });
    sections.set(key, sec);
    body.appendChild(sec.root);
  }
  const bodyOf = key => sections.get(key).body;

  const types = pressGroup(TYPES, value => onChange({ type: value }), 'xseg');
  types.group.setAttribute('role', 'group');
  types.group.setAttribute('aria-label', 'Type');
  bodyOf('type').appendChild(types.group);

  const status = pressGroup(STATUS_OPTIONS, value => onChange({
    status: last.status.includes(value) ? without(last.status, value) : [...last.status, value]
  }));
  bodyOf('status').appendChild(status.group);

  const sortSel = el('select', 'xf-select');
  sortSel.setAttribute('aria-label', 'Sort by');
  for (const [value, label] of SORTS) sortSel.appendChild(Object.assign(el('option', null, label), { value }));
  sortSel.addEventListener('change', () => onChange({ sort: sortSel.value }));
  bodyOf('sort').appendChild(sortSel);

  const legend = el('p', 'xf-legend');
  legend.append(el('span', 'xmark xmark-inc'), 'include ', el('span', 'xmark xmark-exc'), 'exclude. Tap a genre to cycle.');
  legend.querySelectorAll('.xmark').forEach(m => m.setAttribute('aria-hidden', 'true'));
  const genreBox = el('div', 'xchips xgenres-box');
  const genreMeta = metaStatus('genres', onRetryMeta);
  bodyOf('genres').append(legend, genreMeta.root, genreBox);

  const tagChips = el('div', 'xchips xsel-list');
  const tagMeta = metaStatus('tags', onRetryMeta);
  const tagMode = (name, mode) => onChange(setMode(last, 'tags', 'excludeTags', name, mode));
  const combo = createTagCombobox({
    onAdd: (name, mode) => { tagMode(name, mode); say(`${name} ${mode === 'include' ? 'included' : 'excluded'}`); },
    onRemoveLast: () => {
      const name = [...last.tags, ...last.excludeTags].pop();
      if (name) { tagMode(name, null); say(`${name} removed`); }
    }
  });
  bodyOf('tags').append(tagChips, tagMeta.root, combo.root);

  const score = scoreField(value => onChange({ minScore: value }));
  bodyOf('score').appendChild(score.root);

  const chapters = rangeField({
    keys: ['minChapters', 'maxChapters'], labels: ['Min', 'Max'], limits: [0, 5000], noun: 'chapters', onCommit: onChange,
    presets: [['Short <50', '', '49'], ['Medium 50–150', '50', '150'], ['Long 150+', '150', '']]
  });
  bodyOf('chapters').appendChild(chapters.root);

  const years = rangeField({
    keys: ['yearFrom', 'yearTo'], labels: ['From', 'To'], limits: [1950, THIS_YEAR + 1], noun: 'year', onCommit: onChange,
    presets: [['This year', String(THIS_YEAR), String(THIS_YEAR)], ['Last 5 yrs', String(THIS_YEAR - 4), ''], ['Classics <2010', '', '2009']]
  });
  bodyOf('year').appendChild(years.root);

  const switches = {
    hideLibrary: switchRow('Hide my library', 'Skip series already in your Suwayomi library.', on => onChange({ hideLibrary: on })),
    showDismissed: switchRow('Show not interested', 'Include series you marked not interested.', on => onChange({ showDismissed: on })),
    adult: switchRow('Include adult', 'Shows 18+ titles and adult tags.', on => onChange({ adult: on }))
  };
  const gems = button('xlink xf-gems', 'Stop limiting to hidden gems', () => onChange({ maxPopularity: '' }));
  bodyOf('options').append(...Object.values(switches).map(s => s.root), gems);

  const foot = el('div', 'xf-foot');
  const apply = button('xbtn xbtn-primary xf-apply', 'Show results', onApply);
  foot.append(button('xbtn xf-surprise', 'Surprise me', onSurprise), apply);
  root.replaceChildren(head, body, foot, live);

  let genreKey = '';
  function renderGenres(f) {
    const known = meta?.genres || [];
    const knownSet = lower(known);
    const unknown = [...f.genres, ...f.excludeGenres].filter(g => !knownSet.has(g.toLowerCase()));
    const key = [...known, '|', ...unknown].join('\n');
    if (key !== genreKey) {
      genreKey = key;
      genreBox.replaceChildren(...known.map(g => triChip(g, cycleGenre)), ...unknown.map(g => {
        const chip = triChip(g, cycleGenre);
        if (metaState === 'ready') { chip.classList.add('is-unknown'); chip.title = `${g} (not in AniList's genre list)`; }
        return chip;
      }));
    }
    for (const chip of genreBox.children) syncTri(chip, stateFor(f, 'genres', 'excludeGenres', chip.xName));
  }
  const cycleGenre = name => onChange(triState(last, 'genres', 'excludeGenres', name));

  function renderTagChips(f) {
    const known = lower((meta?.tags || []).map(t => t.name));
    const chip = mode => name => selectedChip(name, mode, {
      unknown: metaState === 'ready' && !known.has(name.toLowerCase()),
      onFlip: n => { tagMode(n, mode === 'include' ? 'exclude' : 'include'); refocus(n); },
      onRemove: n => { tagMode(n, null); say(`${n} removed`); combo.input.focus(); }
    });
    tagChips.replaceChildren(...f.tags.map(chip('include')), ...f.excludeTags.map(chip('exclude')));
    combo.setSelected([...f.tags, ...f.excludeTags]);
  }
  // Chips are rebuilt on sync; keep keyboard focus on the flipped chip.
  const refocus = name => requestAnimationFrame(() => {
    [...tagChips.querySelectorAll('.xsel-flip')].find(b => b.querySelector('.xsel-name').textContent === name)?.focus();
  });

  function setMetaState(state) {
    metaState = state;
    genreMeta.set(state);
    tagMeta.set(state);
    legend.hidden = state !== 'ready';
    combo.root.hidden = state !== 'ready';
    if (state !== 'ready') combo.close();
    genreKey = '';
    sync(last);
  }

  function setMeta(next) {
    meta = next;
    combo.setTags(next?.tags || []);
    setMetaState('ready');
  }

  function setTotal(count) {
    apply.textContent = count == null ? 'Show results' : `Show ${count.toLocaleString()} result${count === 1 ? '' : 's'}`;
  }

  function sync(f, force) {
    last = f;
    const count = panelCount(f);
    total.textContent = count ? String(count) : '';
    title.setAttribute('aria-label', count ? `Filters, ${count} active` : 'Filters');
    resetAll.disabled = !hasFilters(f);
    for (const [key, , counter] of SECTION_DEFS) sections.get(key).setCount(counter(f));
    types.sync(value => f.type === value);
    status.sync(value => f.status.includes(value));
    // Without a search query the server sorts by popularity, so say that instead of "Best match".
    const hasQuery = Boolean(f.q.trim());
    sortSel.options[0].textContent = hasQuery ? 'Best match' : 'Popularity';
    sortSel.options[1].hidden = !hasQuery && f.sort !== 'popularity';
    sortSel.value = f.sort;
    score.sync(f.minScore);
    chapters.sync(f, force);
    years.sync(f, force);
    for (const [key, s] of Object.entries(switches)) s.set(f[key]);
    gems.hidden = f.maxPopularity === '';
    renderGenres(f);
    renderTagChips(f);
  }

  setMetaState('loading');
  return { sync, setMeta, setMetaState, setTotal, closePopups: () => combo.close() };
}
