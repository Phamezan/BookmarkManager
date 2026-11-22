import { el, button, icon } from './explore-util.js?v=3';

// ARIA 1.2 combobox for AniList tags: type-ahead (prefix ranked above substring), grouped browse list when empty.
// Keys: ↑/↓ move, Enter include, Shift/Alt+Enter exclude, ←/→ or Enter collapse/expand a category,
// Esc closes then clears, Backspace on empty input removes the last selected tag, Tab leaves.

const MAX_MATCHES = 60;

// Case- and diacritic-insensitive text, plus a map from each folded char back to its source index.
export function fold(text) {
  let out = '';
  const map = [];
  Array.from(text).forEach((ch, i) => {
    const folded = ch.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
    for (const c of folded) { out += c; map.push(i); }
  });
  return { out, map };
}

// "Theme-Game-Card & Board Game" -> "Theme · Game · Card & Board Game"; "Sci-Fi" is a word, not a level.
export const prettyCategory = category => (category || 'Other').split(/-(?!Fi\b)/).join(' · ');

export function rankTags(tags, query, isSelected = () => false) {
  const q = fold(query.trim()).out;
  if (!q) return [];
  const hits = [];
  for (const tag of tags) {
    if (isSelected(tag.name)) continue;
    const name = tag.folded;
    const at = name.indexOf(q);
    if (at < 0) continue;
    const rank = at === 0 ? 0 : /[\s\-/(]/.test(name[at - 1]) ? 1 : 2;
    hits.push({ tag, rank, at });
  }
  hits.sort((a, b) => a.rank - b.rank || a.tag.name.localeCompare(b.tag.name));
  return hits.slice(0, MAX_MATCHES);
}

// Name with the matched span wrapped in <mark>, built from text nodes only.
function highlighted(name, at, length) {
  const span = el('span', 'xcb-name');
  if (at < 0) { span.textContent = name; return span; }
  const { map } = fold(name);
  const chars = Array.from(name);
  const start = map[at];
  const end = map[at + length - 1] + 1;
  span.append(chars.slice(0, start).join(''), el('mark', null, chars.slice(start, end).join('')), chars.slice(end).join(''));
  return span;
}

export function createTagCombobox({ onAdd, onRemoveLast, label = 'Add a tag' }) {
  const root = el('div', 'xcb');
  const field = el('div', 'xcb-field');
  const input = el('input', 'xcb-input');
  const listId = `xcb-list-${Math.random().toString(36).slice(2, 8)}`;
  Object.assign(input, { type: 'text', autocomplete: 'off', spellcheck: false, placeholder: 'Search or browse tags' });
  input.setAttribute('role', 'combobox');
  input.setAttribute('aria-autocomplete', 'list');
  input.setAttribute('aria-expanded', 'false');
  input.setAttribute('aria-controls', listId);
  input.setAttribute('aria-label', label);
  const hint = el('p', 'xcb-hint', 'Enter to include, Shift+Enter to exclude');
  hint.id = `${listId}-hint`;
  input.setAttribute('aria-describedby', hint.id);
  const opener = button('xcb-open', null, () => (isOpen() ? close() : (open(), input.focus())), 'Browse all tags');
  opener.tabIndex = -1;
  opener.appendChild(icon('chevron', 'xicon'));
  field.append(icon('search', 'xicon xcb-icon'), input, opener);

  const pop = el('div', 'xcb-pop');
  pop.hidden = true;
  const list = el('ul', 'xcb-list');
  list.id = listId;
  list.setAttribute('role', 'listbox');
  list.setAttribute('aria-label', 'Tags');
  const empty = el('p', 'xcb-empty');
  empty.setAttribute('role', 'status');
  pop.append(list, empty, hint);
  root.append(field, pop);
  // Keep focus in the input while clicking inside the popup.
  pop.addEventListener('mousedown', event => event.preventDefault());

  let tags = [];
  let categories = [];
  let selected = new Set();
  const openGroups = new Set();
  let rows = [];
  let active = -1;

  const isOpen = () => !pop.hidden;
  const isSelected = name => selected.has(name.toLowerCase());

  function setTags(list_) {
    tags = (list_ || []).map(t => ({ ...t, folded: fold(t.name).out, pretty: prettyCategory(t.category) }));
    const byCat = new Map();
    for (const tag of tags) {
      if (!byCat.has(tag.pretty)) byCat.set(tag.pretty, []);
      byCat.get(tag.pretty).push(tag);
    }
    categories = [...byCat].sort(([a], [b]) => a.localeCompare(b))
      .map(([name, items]) => ({ name, items: items.sort((a, b) => a.name.localeCompare(b.name)) }));
    if (isOpen()) render();
  }

  function setSelected(names) {
    selected = new Set(names.map(n => n.toLowerCase()));
    if (isOpen()) render(true);
  }

  function optionRow(tag, at, length, showCat) {
    const li = el('li', 'xcb-opt');
    li.setAttribute('role', 'option');
    li.setAttribute('aria-selected', 'false');
    li.title = tag.description ? `${tag.name}: ${tag.description}` : tag.name;
    li.appendChild(highlighted(tag.name, at, length));
    if (showCat) li.appendChild(el('span', 'xcb-cat', tag.pretty));
    const ex = button('xcb-ex', '−', event => { event.stopPropagation(); pick({ kind: 'tag', tag }, 'exclude'); });
    ex.tabIndex = -1;
    ex.setAttribute('aria-hidden', 'true');
    ex.title = `Exclude ${tag.name}`;
    li.appendChild(ex);
    return li;
  }

  function groupRow(cat, count) {
    const li = el('li', 'xcb-group');
    const expanded = openGroups.has(cat.name);
    li.setAttribute('role', 'option');
    li.setAttribute('aria-selected', 'false');
    li.dataset.open = String(expanded);
    li.setAttribute('aria-label', `${cat.name}, ${count} tags, ${expanded ? 'expanded' : 'collapsed'}`);
    li.append(icon('chevron', 'xicon xf-chev'), el('span', 'xcb-gname', cat.name), el('span', 'xcb-count', String(count)));
    return li;
  }

  function buildRows() {
    const query = input.value.trim();
    if (query) {
      const length = fold(query).out.length;
      return rankTags(tags, query, isSelected).map(hit => ({ kind: 'tag', tag: hit.tag, node: optionRow(hit.tag, hit.at, length, true) }));
    }
    const out = [];
    for (const cat of categories) {
      const items = cat.items.filter(t => !isSelected(t.name));
      if (!items.length) continue;
      out.push({ kind: 'group', cat, node: groupRow(cat, items.length) });
      if (openGroups.has(cat.name)) for (const tag of items) out.push({ kind: 'tag', tag, node: optionRow(tag, -1, 0, false) });
    }
    return out;
  }

  function render(keepActive) {
    const previous = keepActive ? rows[active] : null;
    rows = buildRows();
    list.replaceChildren();
    rows.forEach((row, i) => {
      row.node.id = `${listId}-${i}`;
      row.node.addEventListener('click', () => (row.kind === 'group' ? toggleGroup(row.cat) : pick(row, 'include')));
      list.appendChild(row.node);
    });
    const query = input.value.trim();
    empty.textContent = rows.length ? '' : query ? `No tags match "${query}"` : 'All tags are already selected.';
    empty.hidden = rows.length > 0;
    list.hidden = rows.length === 0;
    if (previous?.kind === 'group') active = rows.findIndex(r => r.kind === 'group' && r.cat.name === previous.cat.name);
    setActive(keepActive ? Math.min(active, rows.length - 1) : -1);
  }

  function setActive(index) {
    rows[active]?.node.setAttribute('aria-selected', 'false');
    rows[active]?.node.classList.remove('is-active');
    active = index;
    const row = rows[active];
    if (!row) { input.removeAttribute('aria-activedescendant'); return; }
    row.node.setAttribute('aria-selected', 'true');
    row.node.classList.add('is-active');
    input.setAttribute('aria-activedescendant', row.node.id);
    row.node.scrollIntoView({ block: 'nearest' });
  }

  function open() {
    if (isOpen()) return;
    pop.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    root.classList.add('is-open');
    render();
  }

  function close() {
    if (!isOpen()) return;
    pop.hidden = true;
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
    root.classList.remove('is-open');
    active = -1;
  }

  function toggleGroup(cat, force) {
    const want = force ?? !openGroups.has(cat.name);
    if (want) openGroups.add(cat.name); else openGroups.delete(cat.name);
    if (active < 0) active = rows.findIndex(r => r.kind === 'group' && r.cat.name === cat.name);
    render(true);
  }

  function pick(row, mode) {
    const typed = Boolean(input.value.trim());
    onAdd(row.tag.name, mode); // re-renders through setSelected
    if (typed) { input.value = ''; close(); } else render(true);
  }

  input.addEventListener('input', () => { open(); render(); });
  input.addEventListener('click', open);
  input.addEventListener('keydown', event => {
    const row = rows[active];
    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp': {
        event.preventDefault();
        if (!isOpen()) { open(); if (event.key === 'ArrowUp') setActive(rows.length - 1); return; }
        if (!rows.length) return;
        const step = event.key === 'ArrowDown' ? 1 : -1;
        setActive(active < 0 ? (step > 0 ? 0 : rows.length - 1) : (active + step + rows.length) % rows.length);
        return;
      }
      case 'ArrowRight':
      case 'ArrowLeft':
        if (row?.kind === 'group') { event.preventDefault(); toggleGroup(row.cat, event.key === 'ArrowRight'); }
        return;
      case 'Enter': {
        if (!isOpen()) return;
        event.preventDefault();
        const target = row || (input.value.trim() && rows[0]);
        if (!target) return;
        if (target.kind === 'group') toggleGroup(target.cat);
        else pick(target, event.shiftKey || event.altKey ? 'exclude' : 'include');
        return;
      }
      case 'Escape':
        if (isOpen()) close();
        else if (input.value) input.value = '';
        else return;
        event.preventDefault();
        event.stopPropagation();
        return;
      case 'Backspace':
        if (input.value === '') onRemoveLast();
        return;
      case 'Tab':
        close();
        return;
      default:
    }
  });
  document.addEventListener('pointerdown', event => { if (!root.contains(event.target)) close(); });

  return { root, setTags, setSelected, close, input };
}
