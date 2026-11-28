import { el, button, safeUrl, stateBox, isAbort } from './explore-util.js?v=3';
import { novelsGet, novelsSend, novelHash, readHash, showErrorBanner } from './novel-api.js?v=1';
import {
  DEFAULT_SETTINGS,
  loadReaderSettings,
  saveReaderSettings,
  applyReaderSettings,
  clearReaderTheme,
  supportsWakeLock,
  buildSettingsPopover
} from './novel-reader-settings.js?v=1';
import { createChaptersPanel } from './novel-reader-chapters.js?v=1';

export { clearReaderTheme };

const prefetchCache = new Map();

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
    prefetchCache.clear();
    prefetchCache.set(key, data);
  }).catch(() => { /* best-effort prefetch */ });
}

export function parseChapterHeading(chapterTitle, chapterIndex, chapterNumber) {
  const raw = (chapterTitle || '').trim();
  const numMatch = /^(?:chapter|ch\.?)\s*(\d+(?:\.\d+)?)\s*(?:[:\-–—]\s*(.*))?$/i.exec(raw);
  if (numMatch) {
    return {
      label: `Chapter ${numMatch[1]}`,
      subtitle: (numMatch[2] || '').trim()
    };
  }
  const fallbackNum = chapterNumber != null ? chapterNumber : (chapterIndex + 1);
  return {
    label: `Chapter ${fallbackNum}`,
    subtitle: raw
  };
}

export async function renderReader({ view, banner, params, signal }) {
  const source = params.get('source') || '';
  const novelPath = params.get('novel') || '';
  const chapterPath = params.get('path') || '';

  if (!source || !novelPath || !chapterPath) {
    view.replaceChildren(stateBox('alert', 'Missing chapter parameters.'));
    return () => {};
  }

  let settings = loadReaderSettings();
  applyReaderSettings(settings);
  view.replaceChildren(el('div', 'xstate', 'Loading chapter…'));

  let chapter;
  let novelData = null;
  let libraryEntry = null;
  try {
    const novelQs = new URLSearchParams({ source, path: novelPath });
    const [chData, detail] = await Promise.all([
      fetchChapter(source, novelPath, chapterPath, signal),
      novelsGet(`/novel?${novelQs}`, signal).catch(() => null)
    ]);
    chapter = chData;
    novelData = detail;
    libraryEntry = detail?.libraryEntry || null;
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

  const novelTitle = novelData?.title || 'Novel';
  const matchedChapter = (novelData?.chapters || []).find(c => c.path === chapterPath);
  const heading = parseChapterHeading(chapter.title, chapter.index, matchedChapter?.number);
  document.title = `${heading.label} · ${novelTitle}`;

  let settingsOpen = false;
  let progressTimer = null;
  let wakeLockSentinel = null;
  let autoScrollRaf = null;
  let autoScrollLastTs = 0;
  let autoScrollCarry = 0;
  let lastScrollY = window.scrollY;
  let programmaticScroll = false;

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

  /* ---------- Wake Lock ---------- */

  async function syncWakeLock() {
    if (!supportsWakeLock()) return;
    if (settings.wakeLock && document.visibilityState === 'visible') {
      if (wakeLockSentinel && !wakeLockSentinel.released) return;
      try {
        wakeLockSentinel = await navigator.wakeLock.request('screen');
      } catch { /* user denied or battery saver */ }
    } else if (wakeLockSentinel) {
      const sentinel = wakeLockSentinel;
      wakeLockSentinel = null;
      sentinel.release().catch(() => {});
    }
  }

  const onVisibilityChange = () => { syncWakeLock(); };
  document.addEventListener('visibilitychange', onVisibilityChange);

  /* ---------- Auto-scroll ---------- */

  function stopAutoScroll(persistOff = false) {
    if (autoScrollRaf !== null) {
      cancelAnimationFrame(autoScrollRaf);
      autoScrollRaf = null;
    }
    autoScrollLastTs = 0;
    autoScrollCarry = 0;
    if (persistOff && settings.autoScroll) {
      settings.autoScroll = false;
      saveReaderSettings(settings);
      if (settingsOpen) drawSettings();
    }
  }

  function stepAutoScroll(ts) {
    if (!settings.autoScroll) { stopAutoScroll(false); return; }
    const doc = document.documentElement;
    if (window.scrollY + window.innerHeight >= doc.scrollHeight - 2) {
      stopAutoScroll(true);
      setDockHidden(false);
      return;
    }
    if (autoScrollLastTs > 0) {
      const dt = Math.min(100, ts - autoScrollLastTs) / 1000;
      const pxPerSec = 18 + settings.autoScrollSpeed * 16;
      autoScrollCarry += pxPerSec * dt;
      if (autoScrollCarry >= 1) {
        const delta = Math.floor(autoScrollCarry);
        autoScrollCarry -= delta;
        programmaticScroll = true;
        window.scrollBy(0, delta);
        programmaticScroll = false;
      }
    }
    autoScrollLastTs = ts;
    autoScrollRaf = requestAnimationFrame(stepAutoScroll);
  }

  function syncAutoScroll() {
    if (settings.autoScroll) {
      if (autoScrollRaf === null) {
        autoScrollLastTs = 0;
        autoScrollRaf = requestAnimationFrame(stepAutoScroll);
      }
    } else {
      stopAutoScroll(false);
    }
  }

  /* ---------- DOM structure ---------- */

  const root = el('div', 'nreader-root');

  const progressBar = el('div', 'nreader-progress');
  progressBar.setAttribute('aria-hidden', 'true');
  const progressFill = el('div', 'nreader-progress-fill');
  progressBar.appendChild(progressFill);
  progressBar.hidden = !settings.progressBar;
  root.appendChild(progressBar);

  function updateProgressBar() {
    progressBar.hidden = !settings.progressBar;
    if (!settings.progressBar) return;
    const pct = Math.round(computeScrollPercent() * 100);
    progressFill.style.width = `${pct}%`;
  }

  const wrap = el('div', 'nreader-wrap');

  const header = el('header', 'nreader-header');
  const novelBackTitle = el('a', 'nreader-novel-title', novelTitle);
  novelBackTitle.href = novelHash(source, novelPath);
  header.append(novelBackTitle, el('h1', 'nreader-ch-heading', heading.label));
  if (heading.subtitle) {
    header.appendChild(el('p', 'nreader-ch-subtitle', heading.subtitle));
  }
  wrap.appendChild(header);

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

  /* ---------- Chapters slide-in panel ---------- */

  const chaptersPanel = createChaptersPanel({
    source,
    novelPath,
    site: chapter.site || novelData?.site || '',
    chapters: novelData?.chapters || [{ name: chapter.title, path: chapterPath, number: matchedChapter?.number ?? (chapter.index + 1), locked: false }],
    currentPath: chapterPath,
    onNavigate: () => saveProgress(true)
  });

  /* ---------- Floating bottom dock + Settings popover ---------- */

  const dockWrap = el('div', 'nreader-dock-wrap');
  const settingsHost = el('div', 'nset-host');
  const dock = el('div', 'nreader-dock');
  dock.setAttribute('role', 'toolbar');
  dock.setAttribute('aria-label', 'Reader controls');

  function setDockHidden(hidden) {
    if (hidden && (settingsOpen || chaptersPanel.isOpen())) return;
    dockWrap.classList.toggle('is-hidden', hidden);
  }

  let dockPrev;
  if (chapter.prevPath) {
    dockPrev = el('a', 'ndock-btn', '‹');
    dockPrev.href = readHash(source, novelPath, chapter.prevPath);
    dockPrev.setAttribute('aria-label', 'Prev');
    dockPrev.addEventListener('click', () => saveProgress(true));
  } else {
    dockPrev = button('ndock-btn', '‹', null, 'Prev');
    dockPrev.disabled = true;
  }

  const dockHome = el('a', 'ndock-btn', '⌂');
  dockHome.href = novelHash(source, novelPath);
  dockHome.setAttribute('aria-label', 'Back to novel');

  const dockInfo = button('ndock-info', '', () => {
    closeSettings();
    chaptersPanel.open();
  }, 'Open chapter list');
  dockInfo.append(
    el('span', 'ndock-novel', novelTitle),
    el('span', 'ndock-ch', heading.label)
  );

  const dockChapters = button('ndock-btn', '☰', () => {
    closeSettings();
    chaptersPanel.open();
  }, 'Chapters');

  const dockSettings = button('ndock-btn ndock-settings', 'T', () => {
    settingsOpen = !settingsOpen;
    dockSettings.setAttribute('aria-expanded', String(settingsOpen));
    if (settingsOpen) setDockHidden(false);
    drawSettings();
  }, 'Reader settings');
  dockSettings.setAttribute('aria-expanded', 'false');

  let dockNext;
  if (chapter.nextPath && !chapter.nextLocked) {
    dockNext = el('a', 'ndock-btn', '›');
    dockNext.href = readHash(source, novelPath, chapter.nextPath);
    dockNext.setAttribute('aria-label', 'Next');
    dockNext.addEventListener('click', () => saveProgress(true));
  } else {
    dockNext = button('ndock-btn', '›', null, chapter.nextLocked ? 'Next locked' : 'Next');
    dockNext.disabled = true;
  }

  dock.append(dockPrev, dockHome, dockInfo, dockChapters, dockSettings, dockNext);
  dockWrap.append(settingsHost, dock);
  root.appendChild(dockWrap);

  function closeSettings() {
    if (!settingsOpen) return;
    settingsOpen = false;
    dockSettings.setAttribute('aria-expanded', 'false');
    settingsHost.replaceChildren();
  }

  function applyAll({ rerender = true } = {}) {
    applyReaderSettings(settings);
    saveReaderSettings(settings);
    updateProgressBar();
    syncWakeLock();
    syncAutoScroll();
    if (rerender && settingsOpen) drawSettings();
  }

  function drawSettings() {
    if (!settingsOpen) {
      settingsHost.replaceChildren();
      return;
    }
    settingsHost.replaceChildren(
      buildSettingsPopover(settings, {
        onUpdate: opts => applyAll(opts),
        onReset: () => {
          settings = { ...DEFAULT_SETTINGS };
          applyAll({ rerender: true });
        },
        onClose: () => {
          closeSettings();
          dockSettings.focus({ preventScroll: true });
        }
      })
    );
  }

  view.replaceChildren(root);

  /* ---------- Initial scroll resume, progress save, prefetch ---------- */

  requestAnimationFrame(() => {
    if (libraryEntry?.lastChapterPath === chapterPath && libraryEntry.scrollPercent > 0) {
      const max = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
      window.scrollTo(0, Math.round(max * libraryEntry.scrollPercent));
    } else {
      window.scrollTo(0, 0);
    }
    lastScrollY = window.scrollY;
    updateProgressBar();
  });

  if (libraryEntry?.id) saveProgress(false);
  if (chapter.nextPath && !chapter.nextLocked) {
    prefetchNextChapter(source, novelPath, chapter.nextPath);
  }
  syncWakeLock();
  syncAutoScroll();

  /* ---------- Events ---------- */

  const onScroll = () => {
    scheduleProgressSave();
    updateProgressBar();

    const y = window.scrollY;
    const doc = document.documentElement;
    const nearEnd = y + window.innerHeight >= doc.scrollHeight - 80;
    const delta = y - lastScrollY;
    lastScrollY = y;

    if (nearEnd || y <= 40) {
      setDockHidden(false);
    } else if (!programmaticScroll && delta > 14) {
      setDockHidden(true);
    } else if (!programmaticScroll && delta < -8) {
      setDockHidden(false);
    }
  };

  const onUserInterrupt = () => {
    if (settings.autoScroll) stopAutoScroll(true);
  };

  const onArticleClick = event => {
    if (event.target?.closest?.('a, button')) return;
    if (settingsOpen) { closeSettings(); return; }
    setDockHidden(false);
  };

  const onDocumentClick = event => {
    if (!settingsOpen) return;
    const path = typeof event.composedPath === 'function' ? event.composedPath() : [];
    if (!path.includes(dockWrap) && !dockWrap.contains(event.target)) {
      closeSettings();
    }
  };

  const onPageHide = () => {
    clearTimeout(progressTimer);
    saveProgress(true);
  };

  const onKeyDown = event => {
    if (event.key === 'Escape' && settingsOpen) {
      event.preventDefault();
      closeSettings();
      dockSettings.focus({ preventScroll: true });
      return;
    }
    if (chaptersPanel.isOpen()) return;
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(event.target?.tagName || '')) return;
    if (event.key === 'ArrowLeft' && chapter.prevPath) {
      saveProgress(true);
      location.hash = readHash(source, novelPath, chapter.prevPath);
    } else if (event.key === 'ArrowRight' && chapter.nextPath && !chapter.nextLocked) {
      saveProgress(true);
      location.hash = readHash(source, novelPath, chapter.nextPath);
    }
  };

  article.addEventListener('click', onArticleClick);
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('wheel', onUserInterrupt, { passive: true });
  window.addEventListener('touchstart', onUserInterrupt, { passive: true });
  window.addEventListener('pagehide', onPageHide);
  document.addEventListener('click', onDocumentClick);
  document.addEventListener('keydown', onKeyDown);

  return () => {
    clearTimeout(progressTimer);
    stopAutoScroll(false);
    if (wakeLockSentinel) {
      wakeLockSentinel.release().catch(() => {});
      wakeLockSentinel = null;
    }
    chaptersPanel.destroy();
    article.removeEventListener('click', onArticleClick);
    window.removeEventListener('scroll', onScroll);
    window.removeEventListener('wheel', onUserInterrupt);
    window.removeEventListener('touchstart', onUserInterrupt);
    window.removeEventListener('pagehide', onPageHide);
    document.removeEventListener('visibilitychange', onVisibilityChange);
    document.removeEventListener('click', onDocumentClick);
    document.removeEventListener('keydown', onKeyDown);
    saveProgress(true);
  };
}
