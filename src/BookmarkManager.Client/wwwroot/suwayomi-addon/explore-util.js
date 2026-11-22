// Shared helpers for the Explore page: safe DOM building, URL checks, API access, list overrides.
const BASE = '/bm-api/explore';

export class ApiError extends Error {
  constructor(status) { super(`HTTP ${status}`); this.status = status; }
}

export async function getJson(path, signal) {
  const response = await fetch(BASE + path, { signal, headers: { Accept: 'application/json' } });
  if (!response.ok) throw new ApiError(response.status);
  return response.json();
}

export async function send(method, path, body) {
  const response = await fetch(BASE + path, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
  if (!response.ok) throw new ApiError(response.status);
}

export const isAbort = error => error?.name === 'AbortError';

export function errorMessage(error) {
  return error?.status === 502
    ? 'AniList is busy, try again in a minute.'
    : "Couldn't load Explore. Check that BookmarkManager is running.";
}

// Only http(s) URLs may reach href/src; anything else (javascript:, data:) is dropped.
export function safeUrl(value) {
  if (typeof value !== 'string' || !value) return null;
  try {
    const url = new URL(value, location.href);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null;
  } catch { return null; }
}

export function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

export function button(className, text, onClick, label) {
  const node = el('button', className, text);
  node.type = 'button';
  if (label) node.setAttribute('aria-label', label);
  if (onClick) node.addEventListener('click', onClick);
  return node;
}

export function mangaHref(library) {
  const id = Number(library?.mangaId);
  return Number.isInteger(id) && id > 0 ? `/manga/${id}` : null;
}

export function cssColor(value) {
  return typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value) ? value : null;
}

const STATUS = {
  FINISHED: 'Completed', RELEASING: 'Ongoing', HIATUS: 'Hiatus',
  CANCELLED: 'Cancelled', NOT_YET_RELEASED: 'Upcoming'
};
export const statusLabel = status => STATUS[status] || (status ? String(status) : '');
export const STATUS_OPTIONS = Object.entries(STATUS);

export function formatCount(value) {
  if (value == null) return '–';
  return value >= 1000 ? `${(value / 1000).toFixed(value >= 10000 ? 0 : 1)}k` : String(value);
}

// Per-session overrides so every card/drawer showing a series agrees after a change.
const listOverride = new Map();
const libraryOverride = new Map();
export const stateOf = item => listOverride.has(item.anilistId) ? listOverride.get(item.anilistId) : (item.listState ?? null);
export const setStateOf = (id, state) => listOverride.set(id, state);
export const libraryOf = item => libraryOverride.get(item.anilistId) ?? item.library ?? null;
export const setLibraryOf = (id, library) => libraryOverride.set(id, library);

export function storageGet(key) {
  try { return localStorage.getItem(key); } catch { return null; }
}
export function storageSet(key, value) {
  try { localStorage.setItem(key, value); } catch { /* storage unavailable */ }
}

const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select,textarea,summary,[tabindex]:not([tabindex="-1"])';
export function trapTab(container, event) {
  if (event.key !== 'Tab') return;
  const nodes = Array.from(container.querySelectorAll(FOCUSABLE)).filter(n => n.offsetParent !== null);
  if (!nodes.length) return;
  const first = nodes[0];
  const last = nodes[nodes.length - 1];
  if (event.shiftKey && (document.activeElement === first || !container.contains(document.activeElement))) {
    last.focus(); event.preventDefault();
  } else if (!event.shiftKey && (document.activeElement === last || !container.contains(document.activeElement))) {
    first.focus(); event.preventDefault();
  }
}

// Static, trusted SVG path data only (never remote content).
const ICONS = {
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
  heart: '<path d="M12 20s-7-4.4-9-9a5 5 0 0 1 9-3 5 5 0 0 1 9 3c-2 4.600-9 9-9 9Z"/>',
  alert: '<circle cx="12" cy="12" r="9"/><path d="M12 7.500v5.500M12 16.500v.01"/>',
  book: '<path d="M4 5.500A2.500 2.500 0 0 1 6.500 3H20v16H6.500A2.500 2.500 0 0 0 4 21.500Z"/><path d="M4 18.500V5.500"/>',
  chevron: '<path d="m9 6 6 6-6 6"/>',
  sliders: '<path d="M4 7h10M18 7h2M4 17h4M12 17h8"/><circle cx="16" cy="7" r="2"/><circle cx="10" cy="17" r="2"/>'
};
export function icon(name, className = 'xicon') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', className);
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.6');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.innerHTML = ICONS[name] || '';
  return svg;
}

// Empty / error panel: icon, one helpful line, optional action node.
export function stateBox(iconName, text, ...actions) {
  const box = el('div', 'xstate');
  const badge = el('span', 'xstate-icon');
  badge.appendChild(icon(iconName));
  box.append(badge, el('p', null, text), ...actions);
  return box;
}
