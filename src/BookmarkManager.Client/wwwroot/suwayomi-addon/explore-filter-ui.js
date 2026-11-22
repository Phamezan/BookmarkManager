import { el, button, icon } from './explore-util.js?v=3';

// Small, stateless-ish building blocks for the Browse filter panel.

let uid = 0;
const nextId = prefix => `${prefix}-${++uid}`;

// Collapsible section: header toggle (aria-expanded) with an active-count badge and a "Clear" link.
export function createSection(key, title, { open, onToggle, onClear }) {
  const root = el('section', 'xf-sec');
  root.dataset.key = key;
  const head = el('div', 'xf-sec-head');
  const heading = el('h3', 'xf-sec-title');
  const toggle = button('xf-sec-toggle');
  const body = el('div', 'xf-sec-body');
  body.id = nextId('xf-body');
  toggle.setAttribute('aria-controls', body.id);
  const badge = el('span', 'xf-badge');
  badge.setAttribute('aria-hidden', 'true');
  toggle.append(icon('chevron', 'xicon xf-chev'), el('span', 'xf-sec-label', title), badge);
  heading.appendChild(toggle);
  // The link hides once cleared, so hand focus back to the section toggle.
  const clear = button('xlink xf-clear', 'Clear', () => { onClear(); toggle.focus(); }, `Clear ${title.toLowerCase()}`);
  clear.hidden = true;
  head.append(heading, clear);
  root.append(head, body);

  const setOpen = value => { toggle.setAttribute('aria-expanded', String(value)); body.hidden = !value; };
  toggle.addEventListener('click', () => { setOpen(body.hidden); onToggle(key, !body.hidden); });
  setOpen(open);

  function setCount(count) {
    badge.textContent = count ? String(count) : '';
    clear.hidden = !count;
    toggle.setAttribute('aria-label', count ? `${title}, ${count} active` : title);
  }
  return { root, body, setCount, setOpen };
}

// Row of aria-pressed buttons; `isOn(value)` decides pressed state on sync.
export function pressGroup(options, onPick, className = 'xchips') {
  const group = el('div', className);
  const nodes = new Map();
  for (const [value, label] of options) {
    const node = button('xfchip', label, () => onPick(value));
    nodes.set(value, node);
    group.appendChild(node);
  }
  const sync = isOn => { for (const [value, node] of nodes) node.setAttribute('aria-pressed', String(isOn(value))); };
  return { group, nodes, sync };
}

const STATE_WORD = { off: 'not filtered', include: 'included', exclude: 'excluded' };
const NEXT_WORD = { off: 'include', include: 'exclude', exclude: 'clear' };

// Genre chip that cycles off -> include -> exclude -> off. The +/− mark is CSS; state is in aria-label too.
export function triChip(name, onCycle) {
  const chip = button('xfchip xtri', null, () => onCycle(name));
  chip.dataset.name = name.toLowerCase();
  chip.xName = name;
  const mark = el('span', 'xmark');
  mark.setAttribute('aria-hidden', 'true');
  chip.append(mark, el('span', 'xtri-name', name));
  chip.title = name;
  return chip;
}

export function syncTri(chip, state) {
  chip.dataset.state = state;
  chip.setAttribute('aria-pressed', String(state !== 'off'));
  chip.setAttribute('aria-label', `${chip.xName}, ${STATE_WORD[state]}. Activate to ${NEXT_WORD[state]}`);
}

// Selected include/exclude chip: main part flips the state, × removes it.
export function selectedChip(name, state, { onFlip, onRemove, unknown }) {
  const wrap = el('span', 'xsel');
  wrap.dataset.state = state;
  wrap.classList.toggle('is-unknown', Boolean(unknown));
  const other = state === 'include' ? 'exclude' : 'include';
  const flip = button('xsel-flip', null, () => onFlip(name), `${name}, ${STATE_WORD[state]}. Toggle to ${other}`);
  const mark = el('span', 'xmark');
  mark.setAttribute('aria-hidden', 'true');
  flip.append(mark, el('span', 'xsel-name', name));
  flip.title = unknown ? `${name} (not in AniList's tag list)` : `${name}: click to ${other}`;
  const remove = button('xsel-x', '×', () => onRemove(name), `Remove ${name}`);
  wrap.append(flip, remove);
  return wrap;
}

// Switch row: whole row is the control (role=switch); helper text is its description.
export function switchRow(label, help, onFlip) {
  const node = button('xswitch');
  node.setAttribute('role', 'switch');
  const labelEl = el('span', 'xswitch-label', label);
  const helpEl = el('span', 'xswitch-help', help);
  labelEl.id = nextId('xsw');
  helpEl.id = nextId('xsw');
  node.setAttribute('aria-labelledby', labelEl.id);
  node.setAttribute('aria-describedby', helpEl.id);
  const text = el('span', 'xswitch-text');
  text.append(labelEl, helpEl);
  const track = el('span', 'xswitch-track');
  track.setAttribute('aria-hidden', 'true');
  track.appendChild(el('span', 'xswitch-thumb'));
  node.append(text, track);
  node.addEventListener('click', () => onFlip(node.getAttribute('aria-checked') !== 'true'));
  return { root: node, set: on => node.setAttribute('aria-checked', String(Boolean(on))) };
}

const scoreText = value => (Number(value) > 0 ? `${value}+` : 'Any');

// Minimum score: slider (step 5) with live value, plus quick presets that toggle.
export function scoreField(onCommit) {
  const root = el('div', 'xscore-field');
  const row = el('div', 'xscore-row');
  const range = el('input');
  Object.assign(range, { type: 'range', min: '0', max: '100', step: '5', value: '0' });
  range.setAttribute('aria-label', 'Minimum score');
  const out = el('output', 'xf-out', 'Any');
  out.setAttribute('aria-hidden', 'true');
  const show = () => {
    out.textContent = scoreText(range.value);
    range.setAttribute('aria-valuetext', Number(range.value) > 0 ? `${range.value} or higher` : 'Any score');
    range.style.setProperty('--fill', `${range.value}%`);
  };
  range.addEventListener('input', show);
  range.addEventListener('change', () => onCommit(Number(range.value) > 0 ? range.value : ''));
  row.append(range, out);
  let current = '';
  const quick = pressGroup([['60', '60+'], ['70', '70+'], ['80', '80+']], value => onCommit(current === value ? '' : value));
  root.append(row, quick.group);
  function sync(minScore) {
    current = minScore === '' ? '' : String(minScore);
    range.value = current || '0';
    show();
    quick.sync(value => value === current);
  }
  show();
  return { root, sync };
}

// Integer min/max pair with clamping, presets and inline validation.
// Invalid pairs (min > max) are never committed: the error stays until fixed and filters keep their last valid values.
export function rangeField({ keys: [kMin, kMax], labels, limits: [lo, hi], noun, presets, onCommit }) {
  const root = el('div', 'xrange');
  const row = el('div', 'xrange-row');
  const msg = el('p', 'xf-msg');
  msg.id = nextId('xmsg');
  msg.setAttribute('aria-live', 'polite');
  const inputs = labels.map(text => {
    const wrap = el('label', 'xf-field');
    const input = el('input');
    Object.assign(input, { type: 'text', inputMode: 'numeric', autocomplete: 'off', maxLength: 6, placeholder: 'Any' });
    input.setAttribute('pattern', '[0-9]*');
    input.setAttribute('aria-describedby', msg.id);
    wrap.append(el('span', null, text), input);
    row.appendChild(wrap);
    return input;
  });
  row.insertBefore(el('span', 'xrange-dash', '–'), row.lastChild);
  const [minIn, maxIn] = inputs;
  let synced = ['', ''];

  const setMessage = (text, isError) => {
    msg.textContent = text;
    msg.classList.toggle('is-error', Boolean(isError));
    for (const input of inputs) input.toggleAttribute('aria-invalid', Boolean(isError));
  };

  function commit() {
    const notes = [];
    const values = inputs.map(input => {
      if (input.value === '') return '';
      const n = Number(input.value);
      const clamped = Math.min(hi, Math.max(lo, n));
      if (clamped !== n) notes.push(`${n} adjusted to ${clamped} (allowed ${lo}–${hi}).`);
      input.value = String(clamped);
      return String(clamped);
    });
    if (values[0] !== '' && values[1] !== '' && Number(values[0]) > Number(values[1])) {
      setMessage(`Min ${noun} can't be higher than max. Fix one to apply.`, true);
      return;
    }
    setMessage(notes.join(' '), false);
    if (values[0] !== synced[0] || values[1] !== synced[1]) onCommit({ [kMin]: values[0], [kMax]: values[1] });
  }

  for (const input of inputs) {
    input.addEventListener('input', () => {
      const digits = input.value.replace(/\D/g, '');
      if (digits !== input.value) input.value = digits;
      if (msg.classList.contains('is-error')) setMessage('', false);
    });
    input.addEventListener('change', commit);
    input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } });
  }

  const quick = pressGroup(presets.map(([label], i) => [String(i), label]), value => {
    const [, from, to] = presets[Number(value)];
    const on = synced[0] === from && synced[1] === to;
    setMessage('', false);
    onCommit(on ? { [kMin]: '', [kMax]: '' } : { [kMin]: from, [kMax]: to });
  });
  root.append(row, msg, quick.group);

  // Drafts survive unrelated re-syncs; `force` (reset) or a changed filter value overwrites them.
  function sync(f, force) {
    const next = [f[kMin], f[kMax]];
    if (force || next[0] !== synced[0] || next[1] !== synced[1]) {
      [minIn.value, maxIn.value] = next;
      setMessage('', false);
    }
    synced = next;
    quick.sync(value => presets[Number(value)][1] === next[0] && presets[Number(value)][2] === next[1]);
  }
  return { root, sync };
}
