import { renderLibrary } from './novel-library.js?v=1';
import { renderDetail } from './novel-detail.js?v=1';
import { renderReader, clearReaderTheme } from './novel-reader.js?v=1';

const view = document.getElementById('view');
const banner = document.getElementById('banner');
const head = document.getElementById('novel-head');

let activeAbort = null;
let activeCleanup = null;

function parseHash() {
  const raw = (location.hash || '#/').replace(/^#/, '');
  const [routePart, queryPart = ''] = raw.split('?');
  const route = routePart.startsWith('/') ? routePart : `/${routePart}`;
  return { route, params: new URLSearchParams(queryPart) };
}

async function navigate() {
  activeAbort?.abort();
  if (activeCleanup) {
    const fn = activeCleanup;
    activeCleanup = null;
    fn();
  }
  banner.replaceChildren();

  activeAbort = new AbortController();
  const { signal } = activeAbort;
  const { route, params } = parseHash();

  const isReader = route === '/read';
  head.hidden = isReader;
  if (!isReader) clearReaderTheme();

  document.querySelectorAll('[data-nav="library"]').forEach(link => {
    link.setAttribute('aria-current', route === '/' ? 'page' : 'false');
  });

  if (route === '/read') {
    activeCleanup = await renderReader({ view, banner, params, signal });
  } else if (route === '/novel') {
    await renderDetail({ view, banner, params, signal });
  } else {
    document.title = 'Novels · Suwayomi';
    await renderLibrary({ view, banner, signal });
  }
}

window.addEventListener('hashchange', navigate);
navigate();
