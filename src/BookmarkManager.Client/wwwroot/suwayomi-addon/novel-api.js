import { ApiError, el, button, icon } from './explore-util.js?v=3';

const NOVELS_BASE = '/bm-api/novels';

export const READING_STATUSES = [
  { value: 'Reading', label: 'Reading' },
  { value: 'Plan', label: 'Plan to read' },
  { value: 'Completed', label: 'Completed' },
  { value: 'Dropped', label: 'Dropped' }
];

export function statusDisplay(status) {
  return READING_STATUSES.find(s => s.value === status)?.label || status || 'Unknown';
}

export async function novelsGet(path, signal) {
  const response = await fetch(NOVELS_BASE + path, {
    signal,
    headers: { Accept: 'application/json' }
  });
  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    try {
      const payload = await response.json();
      if (payload?.error) message = payload.error;
      else if (payload?.detail) message = payload.detail;
    } catch { /* ignore */ }
    const err = new ApiError(response.status);
    err.message = message;
    throw err;
  }
  return response.json();
}

export async function novelsSend(method, path, body, options = {}) {
  const init = {
    method,
    headers: { Accept: 'application/json' },
    ...options
  };
  if (body !== undefined) {
    init.headers = { ...init.headers, 'Content-Type': 'application/json' };
    init.body = JSON.stringify(body);
  }
  const response = await fetch(NOVELS_BASE + path, init);
  if (!response.ok) throw new ApiError(response.status);
  if (response.status === 204) return null;
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

export function novelHash(source, path) {
  const params = new URLSearchParams({ source, path });
  return `#/novel?${params}`;
}

export function readHash(source, novel, path) {
  const params = new URLSearchParams({ source, novel, path });
  return `#/read?${params}`;
}

export function novelErrorMessage(error) {
  if (error?.status === 502) return 'Novel source is unavailable right now. Try again shortly.';
  if (error?.status === 403) return 'This chapter is locked on the source site.';
  if (error?.status === 404) return 'Could not find that novel or chapter.';
  return 'Could not load novel data. Check that BookmarkManager is running.';
}

export function showErrorBanner(banner, error, retry) {
  const box = el('div', 'xerror');
  box.append(icon('alert'), el('span', null, novelErrorMessage(error)));
  if (retry) box.appendChild(button('xbtn', 'Retry', retry));
  banner.replaceChildren(box);
}

export function toast(message) {
  const holder = document.getElementById('toasts');
  if (!holder) return;
  const box = el('div', 'xtoast');
  box.appendChild(el('span', null, message));
  holder.appendChild(box);
  setTimeout(() => box.remove(), 4500);
}
