import { el, button, storageGet, storageSet } from './explore-util.js?v=3';
import {
  createSection, pressGroup, triChip, syncTri, selectedChip, switchRow, rangeField
} from './explore-filter-ui.js?v=1';
import { createTagCombobox } from './explore-tag-combobox.js?v=2';
import { triState } from './explore-filters.js?v=3';

export const NOVEL_TYPES = [
  ['all', 'All'],
  ['Webnovel', 'Web Novel'],
  ['LightNovel', 'Light Novel']
];

export const NOVEL_STATUS_OPTIONS = [
  ['Ongoing', 'Ongoing'],
  ['Completed', 'Completed'],
  ['Hiatus', 'Hiatus'],
  ['Cancelled', 'Cancelled']
];

export const NOVEL_SORTS = [
  ['', 'Popular'],
  ['rating', 'Top rated'],
  ['updated', 'Recently updated'],
  ['chapters', 'Most chapters'],
  ['title', 'Title']
];

const SECTIONS_KEY = 'explore.novelFilterSections';
const TOP_GENRE_CHIPS = 16;

export const defaultNovelFilters = () => ({
  q: '',
  type: 'all',
  status: [],
  genres: [],
  excludeGenres: [],
  minRating: '',
  minChapters: '',
  maxChapters: '',
  sort: '',
  readable: true,
  hideLibrary: false,
  showDismissed: false
});

const csv = value => [...new Set((value || '').split(',').map(s => s.trim()).filter(Boolean))];
const num = value => (value !== '' && value != null && Number.isFinite(Number(value)) ? String(Number(value)) : '');
const lower = list => new Set(list.map(v => v.toLowerCase()));
const without = (list, value) => list.filter(v => v !== value);
const withValue = (list, value) => (list.includes(value) ? list : [...list, value]);

function setMode(f, inc, exc, name, mode) {
  return {
    [inc]: mode === 'include' ? withValue(f[inc], name) : without(f[inc], name),
    [exc]: mode === 'exclude' ? withValue(f[exc], name) : without(f[exc], name)
  };
}

const stateFor = (f, inc, exc, name) => (f[inc].includes(name) ? 'include' : f[exc].includes(name) ? 'exclude' : 'off');

export function novelFiltersFromParams(params) {
  const f = defaultNovelFilters();
  f.q = params.get('q') || '';

  const rawType = (params.get('type') || '').trim().toLowerCase();
  const matchedType = NOVEL_TYPES.find(([v]) => v.toLowerCase() === rawType);
  f.type = matchedType ? matchedType[0] : 'all';

  const rawSort = (params.get('sort') || '').trim().toLowerCase();
  const matchedSort = NOVEL_SORTS.find(([v]) => v === rawSort);
  f.sort = matchedSort ? matchedSort[0] : '';

  const rawStatuses = csv(params.get('status'));
  f.status = rawStatuses
    .map(s => NOVEL_STATUS_OPTIONS.find(([v]) => v.toLowerCase() === s.toLowerCase())?.[0])
    .filter(Boolean);

  f.excludeGenres = csv(params.get('excludeGenres'));
  const exSet = lower(f.excludeGenres);
  f.genres = csv(params.get('genres')).filter(g => !exSet.has(g.toLowerCase()));

  const minR = num(params.get('minRating'));
  f.minRating = minR !== '' && Number(minR) > 0 && Number(minR) <= 5 ? minR : '';
  f.minChapters = num(params.get('minChapters'));
  f.maxChapters = num(params.get('maxChapters'));
  f.readable = params.get('readable') !== 'false';
  f.hideLibrary = params.get('hideLibrary') === 'true';
  f.showDismissed = params.get('hideDismissed') === 'false';
  return f;
}

export function novelFiltersToParams(f) {
  const p = new URLSearchParams();
  if (f.q.trim()) p.set('q', f.q.trim());
  if (f.type !== 'all') p.set('type', f.type);
  if (f.sort) p.set('sort', f.sort);
  if (f.status.length) p.set('status', f.status.join(','));
  if (f.genres.length) p.set('genres', f.genres.join(','));
  if (f.excludeGenres.length) p.set('excludeGenres', f.excludeGenres.join(','));
  if (f.minRating !== '' && Number(f.minRating) > 0) p.set('minRating', f.minRating);
  if (f.minChapters !== '') p.set('minChapters', f.minChapters);
  if (f.maxChapters !== '') p.set('maxChapters', f.maxChapters);
  if (!f.readable) p.set('readable', 'false');
  if (f.hideLibrary) p.set('hideLibrary', 'true');
  if (f.showDismissed) p.set('hideDismissed', 'false');
  return p;
}

export const hasNovelFilters = f => novelFiltersToParams(f).size > 0;

export function novelActivePills(f) {
  const pills = [];
  const add = (label, patch) => pills.push({ label, patch });
  if (f.q.trim()) add(`"${f.q.trim()}"`, { q: '' });
  if (f.type !== 'all') {
    const label = NOVEL_TYPES.find(([v]) => v === f.type)?.[1] || f.type;
    add(label, { type: 'all' });
  }
  for (const s of f.status) add(s, { status: without(f.status, s) });
  for (const g of f.genres) add(g, { genres: without(f.genres, g) });
  for (const g of f.excludeGenres) add(`Not ${g}`, { excludeGenres: without(f.excludeGenres, g) });
  if (Number(f.minRating) > 0) add(`Rating ${f.minRating}+`, { minRating: '' });
  if (f.minChapters !== '' || f.maxChapters !== '') {
    add(`Chapters ${f.minChapters || 0}–${f.maxChapters || 'any'}`, { minChapters: '', maxChapters: '' });
  }
  if (!f.readable) add('All catalog (incl. non-readable)', { readable: true });
  if (f.hideLibrary) add('Hiding my library', { hideLibrary: false });
  if (f.showDismissed) add('Showing dismissed', { showDismissed: false });
  return pills;
}

export const novelPanelCount = f => novelActivePills({ ...f, q: '' }).length + (f.sort ? 1 : 0);

const SECTION_DEFS = [
  ['type', 'Type', f => Number(f.type !== 'all'), { type: 'all' }],
  ['status', 'Status', f => f.status.length, { status: [] }],
  ['sort', 'Sort', f => Number(f.sort !== ''), { sort: '' }],
  ['genres', 'Genres', f => f.genres.length + f.excludeGenres.length, { genres: [], excludeGenres: [] }],
  ['rating', 'Minimum rating', f => Number(Number(f.minRating) > 0), { minRating: '' }],
  ['chapters', 'Chapters', f => Number(f.minChapters !== '' || f.maxChapters !== ''), { minChapters: '', maxChapters: '' }],
  ['options', 'Options', f => Number(!f.readable) + Number(f.hideLibrary) + Number(f.showDismissed),
    { readable: true, hideLibrary: false, showDismissed: false }]
];
const COMPACT_OPEN = new Set(['type', 'status', 'sort', 'genres', 'options']);

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

function ratingField(onCommit) {
  const root = el('div', 'xscore-field');
  const row = el('div', 'xscore-row');
  const range = el('input');
  Object.assign(range, { type: 'range', min: '0', max: '5', step: '0.5', value: '0' });
  range.setAttribute('aria-label', 'Minimum rating');
  const out = el('output', 'xf-out', 'Any');
  out.setAttribute('aria-hidden', 'true');
  const show = () => {
    const val = Number(range.value);
    out.textContent = val > 0 ? `${val.toFixed(1)}+` : 'Any';
    range.setAttribute('aria-valuetext', val > 0 ? `${val.toFixed(1)} or higher` : 'Any rating');
    range.style.setProperty('--fill', `${(val / 5) * 100}%`);
  };
  range.addEventListener('input', show);
  range.addEventListener('change', () => onCommit(Number(range.value) > 0 ? range.value : ''));
  row.append(range, out);
  let current = '';
  const quick = pressGroup(
    [['3.5', '3.5+'], ['4', '4.0+'], ['4.5', '4.5+']],
    value => onCommit(current === value ? '' : value)
  );
  root.append(row, quick.group);
  function sync(minRating) {
    current = minRating === '' ? '' : String(Number(minRating));
    range.value = current || '0';
    show();
    quick.sync(value => value === current);
  }
  show();
  return { root, sync };
}

function metaStatus(onRetry) {
  const box = el('div', 'xf-meta');
  const skel = el('div', 'xf-skel');
  for (let i = 0; i < 8; i++) skel.appendChild(el('span', 'xsk-chip'));
  const error = el('p', 'xf-meta-error');
  error.append(el('span', null, "Couldn't load genres."), button('xlink', 'Retry', onRetry, 'Retry loading genres'));
  box.append(skel, error);
  const set = state => { box.hidden = state === 'ready'; skel.hidden = state !== 'loading'; error.hidden = state !== 'error'; };
  return { root: box, set };
}

export function createNovelFilterPanel(root, { onChange, onReset, onClose, onApply, onRetryMeta, compact }) {
  let last = defaultNovelFilters();
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
      open: isOpen(key),
      onToggle: saveOpenState,
      onClear: () => { onChange(clearPatch); say(`${label} cleared`); }
    });
    sections.set(key, sec);
    body.appendChild(sec.root);
  }
  const bodyOf = key => sections.get(key).body;

  const types = pressGroup(NOVEL_TYPES, value => onChange({ type: value }), 'xseg');
  types.group.setAttribute('role', 'group');
  types.group.setAttribute('aria-label', 'Novel type');
  bodyOf('type').appendChild(types.group);

  const status = pressGroup(NOVEL_STATUS_OPTIONS, value => onChange({
    status: last.status.includes(value) ? without(last.status, value) : [...last.status, value]
  }));
  bodyOf('status').appendChild(status.group);

  const sortSel = el('select', 'xf-select');
  sortSel.setAttribute('aria-label', 'Sort novels by');
  for (const [value, label] of NOVEL_SORTS) sortSel.appendChild(Object.assign(el('option', null, label), { value }));
  sortSel.addEventListener('change', () => onChange({ sort: sortSel.value }));
  bodyOf('sort').appendChild(sortSel);

  const legend = el('p', 'xf-legend');
  legend.append(el('span', 'xmark xmark-inc'), 'include ', el('span', 'xmark xmark-exc'), 'exclude. Tap or search genres.');
  legend.querySelectorAll('.xmark').forEach(m => m.setAttribute('aria-hidden', 'true'));
  const selectedExtras = el('div', 'xchips xsel-list');
  const genreBox = el('div', 'xchips xgenres-box');
  const genreMeta = metaStatus(onRetryMeta);
  const genreMode = (name, mode) => onChange(setMode(last, 'genres', 'excludeGenres', name, mode));
  const combo = createTagCombobox({
    label: 'Search genres',
    placeholder: 'Search or browse genres',
    noun: 'genres',
    browseLabel: 'Browse all genres',
    listLabel: 'Genres',
    onAdd: (name, mode) => { genreMode(name, mode); say(`${name} ${mode === 'include' ? 'included' : 'excluded'}`); },
    onRemoveLast: () => {
      const name = [...last.genres, ...last.excludeGenres].pop();
      if (name) { genreMode(name, null); say(`${name} removed`); }
    }
  });
  bodyOf('genres').append(legend, genreMeta.root, selectedExtras, genreBox, combo.root);

  const rating = ratingField(value => onChange({ minRating: value }));
  bodyOf('rating').appendChild(rating.root);

  const chapters = rangeField({
    keys: ['minChapters', 'maxChapters'],
    labels: ['Min', 'Max'],
    limits: [0, 10000],
    noun: 'chapters',
    onCommit: onChange,
    presets: [['Short <100', '', '99'], ['Medium 100–500', '100', '500'], ['Long 500+', '500', '']]
  });
  bodyOf('chapters').appendChild(chapters.root);

  const switches = {
    readable: switchRow('Readable now', 'Only novels that open directly in the reader.', on => onChange({ readable: on })),
    hideLibrary: switchRow('Hide my library', 'Skip novels already in your novel library.', on => onChange({ hideLibrary: on })),
    showDismissed: switchRow('Show not interested', 'Include novels you marked not interested.', on => onChange({ showDismissed: on }))
  };
  bodyOf('options').append(...Object.values(switches).map(s => s.root));

  const foot = el('div', 'xf-foot');
  const apply = button('xbtn xbtn-primary xf-apply', 'Show results', onApply);
  foot.appendChild(apply);
  root.replaceChildren(head, body, foot, live);

  const cycleGenre = name => onChange(triState(last, 'genres', 'excludeGenres', name));
  let chipKey = '';

  function renderGenres(f) {
    const all = (meta?.genres || []).map(g => g.name);
    const top = all.slice(0, TOP_GENRE_CHIPS);
    const topSet = lower(top);
    const knownSet = lower(all);

    const key = top.join('\n');
    if (key !== chipKey) {
      chipKey = key;
      genreBox.replaceChildren(...top.map(g => triChip(g, cycleGenre)));
    }
    for (const chip of genreBox.children) {
      syncTri(chip, stateFor(f, 'genres', 'excludeGenres', chip.xName));
    }

    const extraInc = f.genres.filter(g => !topSet.has(g.toLowerCase()));
    const extraExc = f.excludeGenres.filter(g => !topSet.has(g.toLowerCase()));
    const makeExtra = mode => name => selectedChip(name, mode, {
      unknown: metaState === 'ready' && !knownSet.has(name.toLowerCase()),
      onFlip: n => genreMode(n, mode === 'include' ? 'exclude' : 'include'),
      onRemove: n => { genreMode(n, null); say(`${n} removed`); }
    });
    selectedExtras.replaceChildren(...extraInc.map(makeExtra('include')), ...extraExc.map(makeExtra('exclude')));
    combo.setSelected([...f.genres, ...f.excludeGenres]);
  }

  function setMetaState(state) {
    metaState = state;
    genreMeta.set(state);
    legend.hidden = state !== 'ready';
    combo.root.hidden = state !== 'ready';
    if (state !== 'ready') combo.close();
    chipKey = '';
    sync(last);
  }

  function setMeta(next) {
    meta = next;
    combo.setTags((next?.genres || []).map(g => ({
      name: g.name,
      category: 'Genres',
      description: `${g.count} novel${g.count === 1 ? '' : 's'}`
    })));
    setMetaState('ready');
  }

  function setTotal(count) {
    apply.textContent = count == null ? 'Show results' : `Show ${count.toLocaleString()} novel${count === 1 ? '' : 's'}`;
  }

  function sync(f, force) {
    last = f;
    const count = novelPanelCount(f);
    total.textContent = count ? String(count) : '';
    title.setAttribute('aria-label', count ? `Filters, ${count} active` : 'Filters');
    resetAll.disabled = !hasNovelFilters(f);
    for (const [key, , counter] of SECTION_DEFS) sections.get(key).setCount(counter(f));
    types.sync(value => f.type === value);
    status.sync(value => f.status.includes(value));
    sortSel.value = f.sort;
    rating.sync(f.minRating);
    chapters.sync(f, force);
    for (const [key, s] of Object.entries(switches)) s.set(f[key]);
    renderGenres(f);
  }

  setMetaState('loading');
  return { sync, setMeta, setMetaState, setTotal, closePopups: () => combo.close() };
}
