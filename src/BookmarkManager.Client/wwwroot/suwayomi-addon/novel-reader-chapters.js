import { el, button, safeUrl, trapTab } from './explore-util.js?v=3';
import { readHash } from './novel-api.js?v=1';

export function createChaptersPanel({ source, novelPath, site, chapters, currentPath, onNavigate }) {
  const backdrop = el('div', 'nch-drawer-backdrop');
  backdrop.hidden = true;

  const panel = el('aside', 'nch-drawer');
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-modal', 'true');
  panel.setAttribute('aria-label', 'Chapter list');
  panel.tabIndex = -1;
  backdrop.appendChild(panel);

  let returnFocus = null;
  let query = '';

  const head = el('div', 'nch-drawer-head');
  const titleEl = el('h3', null, `Chapters (${chapters.length})`);
  const closeBtn = button('nch-drawer-close', '×', () => close(), 'Close chapter list');
  head.append(titleEl, closeBtn);

  const searchWrap = el('div', 'nch-drawer-search');
  const searchInput = el('input', 'nch-search-input');
  searchInput.type = 'search';
  searchInput.placeholder = 'Search chapters…';
  searchInput.setAttribute('aria-label', 'Search chapters');
  searchWrap.appendChild(searchInput);

  const list = el('ul', 'nch-drawer-list');
  panel.append(head, searchWrap, list);

  function renderList() {
    const q = query.trim().toLowerCase();
    const filtered = q
      ? chapters.filter(c =>
          (c.name || '').toLowerCase().includes(q) ||
          (c.number != null && String(c.number).includes(q))
        )
      : chapters;

    list.replaceChildren();
    if (!filtered.length) {
      list.appendChild(el('li', 'nch-drawer-empty', 'No matching chapters.'));
      return;
    }

    const frag = document.createDocumentFragment();
    for (const ch of filtered) {
      const li = el('li', 'nch-drawer-item');
      const isCurrent = ch.path === currentPath;

      if (ch.locked) {
        const row = el('div', 'nch-drawer-row is-locked');
        row.appendChild(el('span', 'nch-drawer-name', `🔒 ${ch.name}`));
        const extUrl = safeUrl(`${site}${ch.path}`);
        if (extUrl) {
          const ext = el('a', 'nch-drawer-ext', 'Open on site ↗');
          ext.href = extUrl;
          ext.target = '_blank';
          ext.rel = 'noopener noreferrer';
          row.appendChild(ext);
        }
        li.appendChild(row);
      } else {
        const row = el('a', isCurrent ? 'nch-drawer-row is-current' : 'nch-drawer-row');
        row.href = readHash(source, novelPath, ch.path);
        if (isCurrent) row.setAttribute('aria-current', 'page');
        row.appendChild(el('span', 'nch-drawer-name', ch.name));
        row.addEventListener('click', () => {
          onNavigate?.();
          close();
        });
        li.appendChild(row);
      }
      frag.appendChild(li);
    }
    list.appendChild(frag);
  }

  searchInput.addEventListener('input', () => {
    query = searchInput.value;
    renderList();
  });

  function open() {
    if (!backdrop.isConnected) document.body.appendChild(backdrop);
    if (!backdrop.hidden) return;
    returnFocus = document.activeElement;
    query = '';
    searchInput.value = '';
    renderList();
    backdrop.hidden = false;
    document.body.classList.add('is-locked');
    requestAnimationFrame(() => {
      const current = list.querySelector('.is-current');
      if (current && typeof current.scrollIntoView === 'function') {
        current.scrollIntoView({ block: 'center' });
      }
      searchInput.focus({ preventScroll: true });
    });
  }

  function close() {
    if (backdrop.hidden) return;
    backdrop.hidden = true;
    document.body.classList.remove('is-locked');
    if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    returnFocus = null;
  }

  function destroy() {
    close();
    if (backdrop.isConnected) backdrop.remove();
  }

  backdrop.addEventListener('click', event => {
    if (event.target === backdrop) close();
  });

  backdrop.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
    } else {
      trapTab(panel, event);
    }
  });

  return {
    open,
    close,
    destroy,
    isOpen: () => !backdrop.hidden
  };
}
