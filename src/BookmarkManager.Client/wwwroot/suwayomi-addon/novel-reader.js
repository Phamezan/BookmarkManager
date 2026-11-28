import { el, button, safeUrl, storageGet, storageSet, stateBox, isAbort } from './explore-util.js?v=3';
import { novelsGet, novelsSend, novelHash, readHash, showErrorBanner } from './novel-api.js?v=1';

const SETTINGS_KEY = 'novel.readerSettings';
const DEFAULT_SETTINGS = {
  font: 'serif',
  size: 18,
  lineHeight: 1.75,
  width: 720,
  theme: 'dark'
};

const prefetchCache = new Map();

function loadSettings() {
  try {
    const raw = storageGet(SETTINGS_KEY);
    if (!raw) return { ...DEFAULT_SETTINGS };
    const parsed = JSON.parse(raw);
    return {
      font: parsed.font === 'sans' ? 'sans' : 'serif',
      size: Math.min(28, Math.max(14, Number(parsed.size) || DEFAULT_SETTINGS.size)),
      lineHeight: Math.min(2.2, Math.max(1.3, Number(parsed.lineHeight) || DEFAULT_SETTINGS.lineHeight)),
      width: Math.min(960, Math.max(540, Number(parsed.width) || DEFAULT_SETTINGS.width)),
      theme: ['dark', 'light', 'sepia'].includes(parsed.theme) ? parsed.theme : 'dark'
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

function saveSettings(settings) {
  storageSet(SETTINGS_KEY, JSON.stringify(settings));
}

function applySettings(settings) {
  document.body.classList.add('is-reading');
  document.body.setAttribute('data-reader-theme', settings.theme);
  const root = document.documentElement.style;
  root.setProperty(
    '--reader-font',
    settings.font === 'sans'
      ? 'Inter, "Segoe UI Variable", Roboto, system-ui, sans-serif'
      : 'Georgia, "Times New Roman", Cambria, serif'
  );
  root.setProperty('--reader-size', `${settings.size}px`);
  root.setProperty('--reader-lh', String(settings.lineHeight));
  root.setProperty('--reader-width', `${settings.width}px`);
}

export function clearReaderTheme() {
  document.body.classList.remove('is-reading');
  document.body.removeAttribute('data-reader-theme');
}

function computeScrollPercent() {
  const doc = document.documentElement;
  const max = Math.max(1, doc.scrollHeight - window.innerHeight);
  return Math.min(1, Math.max(0, Number((window.scrollY / max).toFixed(4))));
}

function chapterCacheKey(source, novelPath, chapterPath) {
  return `${source}|${novelPath}|${chapterPath}`;
}

async function fetchChapter(source, novelPath, chapterPath, signal) {
  const key = chapterCacheKey(source, novelPath, chapterPath);
  if (prefetchCache.has(key)) {
    const cached = prefetchCache.get(key);
    prefetchCache.delete(key);
    return cached;
  }
  const qs = new URLSearchParams({ source, novelPath, path: chapterPath });
  return novelsGet(`/chapter?${qs}`, signal);
}

function prefetchNextChapter(source, novelPath, nextPath) {
  const key = chapterCacheKey(source, novelPath, nextPath);
  if (prefetchCache.has(key)) return;
  const qs = new URLSearchParams({ source, novelPath, path: nextPath });
  novelsGet(`/chapter?${qs}`).then(data => {
    // Keep cache bounded to 1 prefetched chapter.
    prefetchCache.clear();
    prefetchCache.set(key, data);
  }).catch(() => { /* best-effort prefetch */ });
}

function buildSettingsPanel(settings, onChange) {
  const panel = el('section', 'nsettings');
  panel.setAttribute('aria-label', 'Reader settings');

  function chipGroup(label, options, current, onPick) {
    const group = el('div', 'nset-group');
    group.appendChild(el('span', 'nset-label', label));
    const row = el('div', 'nset-row');
    for (const [val, text] of options) {
      const btn = button('nset-chip', text, () => {
        onPick(val);
        onChange();
      });
      btn.setAttribute('aria-pressed', String(current === val));
      row.appendChild(btn);
    }
    group.appendChild(row);
    return group;
  }

  panel.append(
    chipGroup('Theme', [['dark', 'Dark'], ['light', 'Light'], ['sepia', 'Sepia']], settings.theme, v => { settings.theme = v; }),
    chipGroup('Font', [['serif', 'Serif'], ['sans', 'Sans']], settings.font, v => { settings.font = v; }),
    chipGroup('Text size', [[16, 'S'], [18, 'M'], [20, 'L'], [22, 'XL']], settings.size, v => { settings.size = v; }),
    chipGroup('Line height', [[1.5, 'Tight'], [1.75, 'Normal'], [2.0, 'Relaxed']], settings.lineHeight, v => { settings.lineHeight = v; }),
    chipGroup('Width', [[600, 'Narrow'], [720, 'Medium'], [880, 'Wide']], settings.width, v => { settings.width = v; })
  );
  return panel;
}

export async function renderReader({ view, banner, params, signal }) {
  const source = params.get('source') || '';
  const novelPath = params.get('novel') || '';
  const chapterPath = params.get('path') || '';

  if (!source || !novelPath || !chapterPath) {
    view.replaceChildren(stateBox('alert', 'Missing chapter parameters.'));
    return () => {};
  }

  const settings = loadSettings();
  applySettings(settings);
  view.replaceChildren(el('div', 'xstate', 'Loading chapter…'));

  let chapter;
  let libraryEntry = null;
  try {
    const novelQs = new URLSearchParams({ source, path: novelPath });
    const [chData, novelData] = await Promise.all([
      fetchChapter(source, novelPath, chapterPath, signal),
      novelsGet(`/novel?${novelQs}`, signal).catch(() => null)
    ]);
    chapter = chData;
    libraryEntry = novelData?.libraryEntry || null;
    banner.replaceChildren();
  } catch (error) {
    if (isAbort(error)) return () => {};
    if (error?.status === 403) {
      const back = el('a', 'xbtn', 'Back to novel');
      back.href = novelHash(source, novelPath);
      view.replaceChildren(stateBox('alert', 'This chapter is locked on the source site and cannot be read in-app.', back));
      return () => {};
    }
    view.replaceChildren();
    showErrorBanner(banner, error, () => renderReader({ view, banner, params, signal: new AbortController().signal }));
    return () => {};
  }

  document.title = `${chapter.title} · Novels`;

  let settingsOpen = false;
  let progressTimer = null;

  function saveProgress(keepalive = false) {
    if (!libraryEntry?.id) return;
    const payload = {
      chapterPath,
      chapterName: chapter.title,
      chapterIndex: chapter.index,
      chapterCount: chapter.count,
      scrollPercent: computeScrollPercent()
    };
    novelsSend('PUT', `/library/${libraryEntry.id}/progress`, payload, keepalive ? { keepalive: true } : {}).catch(() => {});
  }

  function scheduleProgressSave() {
    if (!libraryEntry?.id) return;
    clearTimeout(progressTimer);
    progressTimer = setTimeout(() => saveProgress(false), 2000);
  }

  const root = el('div', 'nreader-root');

  const topBar = el('div', 'nreader-bar');
  const backLink = el('a', 'nreader-btn', '← Novel');
  backLink.href = novelHash(source, novelPath);

  const titleWrap = el('div', 'nreader-bar-title');
  titleWrap.append(
    el('h2', null, chapter.title),
    el('div', 'nreader-bar-sub', `Chapter ${chapter.index + 1} of ${chapter.count}`)
  );

  const settingsHost = el('div');
  const settingsBtn = button('nreader-btn', 'Aa Settings', () => {
    settingsOpen = !settingsOpen;
    settingsBtn.setAttribute('aria-expanded', String(settingsOpen));
    drawSettings();
  });
  settingsBtn.setAttribute('aria-expanded', 'false');

  function drawSettings() {
    if (!settingsOpen) {
      settingsHost.replaceChildren();
      return;
    }
    settingsHost.replaceChildren(
      buildSettingsPanel(settings, () => {
        applySettings(settings);
        saveSettings(settings);
        drawSettings();
      })
    );
  }

  topBar.append(backLink, titleWrap, settingsBtn);
  root.append(topBar, settingsHost);

  const wrap = el('div', 'nreader-wrap');
  const article = el('article', 'nreader-body');
  // Sanitized server-side by NovelHtmlSanitizer before caching/returning.
  article.innerHTML = chapter.html || '';
  wrap.appendChild(article);

  const nav = el('nav', 'nreader-nav');
  nav.setAttribute('aria-label', 'Chapter navigation');

  if (chapter.prevPath) {
    const prev = el('a', 'nreader-btn', '← Previous chapter');
    prev.href = readHash(source, novelPath, chapter.prevPath);
    prev.addEventListener('click', () => saveProgress(true));
    nav.appendChild(prev);
  } else {
    const prevDis = button('nreader-btn', '← Previous chapter');
    prevDis.disabled = true;
    nav.appendChild(prevDis);
  }

  if (chapter.nextPath && !chapter.nextLocked) {
    const next = el('a', 'nreader-btn', 'Next chapter →');
    next.href = readHash(source, novelPath, chapter.nextPath);
    next.addEventListener('click', () => saveProgress(true));
    nav.appendChild(next);
  } else if (chapter.nextPath && chapter.nextLocked) {
    const nextLockedBtn = button('nreader-btn', '🔒 Next chapter locked');
    nextLockedBtn.disabled = true;
    nav.appendChild(nextLockedBtn);
  } else {
    const nextDis = button('nreader-btn', 'End of chapters');
    nextDis.disabled = true;
    nav.appendChild(nextDis);
  }
  wrap.appendChild(nav);

  if (chapter.nextPath && chapter.nextLocked) {
    const notice = el('div', 'nlock-notice');
    notice.appendChild(el('span', null, '🔒 The next chapter is locked on the source site.'));
    const extUrl = safeUrl(`${chapter.site}${chapter.nextPath}`);
    if (extUrl) {
      const link = el('a', 'xbtn xbtn-primary', 'Open on site ↗');
      link.href = extUrl;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      notice.appendChild(link);
    }
    wrap.appendChild(notice);
  }

  root.appendChild(wrap);
  view.replaceChildren(root);

  // Resume scroll position if opening the saved chapter, else top of chapter.
  requestAnimationFrame(() => {
    if (libraryEntry?.lastChapterPath === chapterPath && libraryEntry.scrollPercent > 0) {
      const max = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
      window.scrollTo(0, Math.round(max * libraryEntry.scrollPercent));
    } else {
      window.scrollTo(0, 0);
    }
  });

  // Immediately record chapter change on entry (if in library).
  if (libraryEntry?.id) {
    saveProgress(false);
  }

  // Prefetch next chapter only (when unlocked).
  if (chapter.nextPath && !chapter.nextLocked) {
    prefetchNextChapter(source, novelPath, chapter.nextPath);
  }

  const onScroll = () => scheduleProgressSave();
  const onPageHide = () => {
    clearTimeout(progressTimer);
    saveProgress(true);
  };
  const onKeyDown = event => {
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(event.target?.tagName || '')) return;
    if (event.key === 'ArrowLeft' && chapter.prevPath) {
      saveProgress(true);
      location.hash = readHash(source, novelPath, chapter.prevPath);
    } else if (event.key === 'ArrowRight' && chapter.nextPath && !chapter.nextLocked) {
      saveProgress(true);
      location.hash = readHash(source, novelPath, chapter.nextPath);
    }
  };

  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('pagehide', onPageHide);
  document.addEventListener('keydown', onKeyDown);

  return () => {
    clearTimeout(progressTimer);
    window.removeEventListener('scroll', onScroll);
    window.removeEventListener('pagehide', onPageHide);
    document.removeEventListener('keydown', onKeyDown);
    saveProgress(true);
  };
}
