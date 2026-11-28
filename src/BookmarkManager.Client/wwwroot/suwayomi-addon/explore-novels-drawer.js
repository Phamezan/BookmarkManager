import { el, button, safeUrl, isAbort, trapTab } from './explore-util.js?v=3';
import { novelsGet, novelsSend, novelHash } from './novel-api.js?v=1';
import { createNovelHero } from './novel-hero.js?v=1';
import { createReviewsPanel } from './novel-reviews.js?v=1';

export function novelReaderUrl(source, path) {
  return `/bm/novel.html${novelHash(source, path)}`;
}

export function createNovelDrawer({ toast, onLibraryAdded, onList, novelStateOf }) {
  const backdrop = el('div', 'xdrawer-backdrop');
  backdrop.hidden = true;
  const drawer = el('aside', 'xdrawer xndrawer');
  drawer.setAttribute('role', 'dialog');
  drawer.setAttribute('aria-modal', 'true');
  drawer.setAttribute('aria-label', 'Novel details');
  drawer.tabIndex = -1;
  backdrop.appendChild(drawer);

  const reviewsPanel = createReviewsPanel();

  let controller = null;
  let returnFocus = null;
  let currentItem = null;
  let currentHero = null;

  const mount = () => { if (!backdrop.isConnected) document.body.appendChild(backdrop); };
  const unmount = () => {
    close();
    reviewsPanel.destroy();
    if (backdrop.isConnected) backdrop.remove();
  };

  function close() {
    if (reviewsPanel.isOpen()) reviewsPanel.close();
    controller?.abort();
    currentItem = null;
    currentHero = null;
    if (backdrop.hidden) return;
    backdrop.hidden = true;
    document.getElementById('page').inert = false;
    document.body.classList.remove('is-locked');
    if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    returnFocus = null;
  }

  function refresh() {
    if (!currentItem?.id) return;
    const state = novelStateOf(currentItem);
    const saveBtn = drawer.querySelector('.xn-drawer-save');
    const dismissBtn = drawer.querySelector('.xn-drawer-dismiss');
    if (saveBtn) {
      saveBtn.setAttribute('aria-pressed', String(state === 'saved'));
      saveBtn.textContent = state === 'saved' ? '♥ Saved' : '♥ Save';
    }
    if (dismissBtn) {
      dismissBtn.setAttribute('aria-pressed', String(state === 'dismissed'));
      dismissBtn.textContent = state === 'dismissed' ? '✕ Not interested' : 'Not interested';
    }
  }

  function renderHitRow(hit) {
    const row = el('div', 'xhit xnhit');
    const coverSrc = safeUrl(hit.cover);
    if (coverSrc) {
      const img = el('img', 'xnhit-cover');
      img.src = coverSrc;
      img.alt = '';
      row.appendChild(img);
    }
    const text = el('div', 'xhit-text');
    const titleEl = el('span', 'xhit-title', hit.title);
    const srcText = hit.sourceName || hit.source;
    const sub = el('span', 'xhit-src', hit.isExactMatch ? `${srcText} · Exact match` : srcText);
    text.append(titleEl, sub);
    row.appendChild(text);

    const readLink = el('a', 'xbtn xbtn-primary', 'Read');
    readLink.href = novelReaderUrl(hit.source, hit.path);
    readLink.setAttribute('aria-label', `Read ${hit.title} (${srcText})`);
    row.appendChild(readLink);

    const addBtn = button('xbtn', 'Add to library', async () => {
      addBtn.disabled = true;
      try {
        await novelsSend('PUT', '/library', {
          source: hit.source,
          novelPath: hit.path,
          title: hit.title,
          coverUrl: hit.cover || null,
          status: 'Reading'
        });
        addBtn.textContent = 'In library';
        addBtn.setAttribute('aria-label', `In library: ${hit.title} (${srcText})`);
        onLibraryAdded?.(hit.title);
        toast('Added to novel library');
      } catch {
        addBtn.disabled = false;
        toast('Could not add to novel library.');
      }
    });
    addBtn.setAttribute('aria-label', `Add to library: ${hit.title} (${srcText})`);
    row.appendChild(addBtn);
    return row;
  }

  function open(item) {
    controller?.abort();
    controller = new AbortController();
    currentItem = item;
    if (backdrop.hidden) {
      returnFocus = document.activeElement;
      backdrop.hidden = false;
      document.getElementById('page').inert = true;
      document.body.classList.add('is-locked');
    }

    const {
      title, coverUrl, authors, status, genres, synopsis, alternateTitles,
      rating, chapters, resolvedHit
    } = item;
    let inLibrary = Boolean(item.inLibrary);
    let activeHit = resolvedHit || null;

    const frag = document.createDocumentFragment();
    const closeBtn = button('xdrawer-close', '×', close, 'Close details');
    frag.appendChild(closeBtn);

    const body = el('div', 'xndrawer-body');
    const sourcesBox = el('section', 'xblock xread');
    sourcesBox.hidden = true;
    sourcesBox.appendChild(el('h3', null, 'Available sources'));
    const hitsHost = el('div', 'xsources');
    sourcesBox.appendChild(hitsHost);

    async function runSourceSearch(triggerBtn) {
      if (triggerBtn) triggerBtn.disabled = true;
      sourcesBox.hidden = false;
      hitsHost.replaceChildren(el('p', 'xempty', 'Searching novel sources…'));
      try {
        const qs = new URLSearchParams({ q: title });
        const hits = await novelsGet(`/search?${qs}`, controller.signal);
        hitsHost.replaceChildren();
        if (!hits.length) {
          hitsHost.appendChild(el('p', 'xempty', 'Not found on enabled novel sources.'));
          return;
        }
        for (const hit of hits) hitsHost.appendChild(renderHitRow(hit));
        const best = hits.find(h => h.isExactMatch) || hits[0];
        if (best) {
          activeHit = best;
          hero.setChaptersHref(novelReaderUrl(best.source, best.path));
          hero.loadLiveStats(best.source, best.path, best.sourceName);
        }
      } catch (error) {
        if (isAbort(error)) return;
        hitsHost.replaceChildren(
          el('p', 'xempty', 'Could not search novel sources right now.'),
          button('xbtn', 'Retry', () => runSourceSearch(triggerBtn))
        );
        if (triggerBtn) triggerBtn.disabled = false;
      }
    }

    const hero = createNovelHero({
      model: {
        titleId: 'xndetail-title',
        title,
        coverUrl,
        authors,
        status,
        genres,
        rating,
        chapters,
        source: activeHit?.source || null,
        sourceName: activeHit?.sourceName || null,
        path: activeHit?.path || null
      },
      signal: controller.signal,
      chaptersHref: activeHit ? novelReaderUrl(activeHit.source, activeHit.path) : null,
      onChaptersClick: () => {
        if (activeHit) {
          location.assign(novelReaderUrl(activeHit.source, activeHit.path));
        } else {
          runSourceSearch(drawer.querySelector('.xn-find-btn'));
        }
      },
      onReviewsClick: heroState => {
        reviewsPanel.open({
          title,
          source: heroState.source || activeHit?.source || null,
          sourceName: heroState.sourceName || activeHit?.sourceName || null,
          site: heroState.site || null,
          path: heroState.path || activeHit?.path || null
        });
      },
      renderPrimaryActions: actions => {
        if (activeHit) {
          const readLink = el('a', 'xbtn xbtn-primary xbtn-lg xn-read-now', 'READ NOW');
          readLink.href = novelReaderUrl(activeHit.source, activeHit.path);
          readLink.setAttribute('aria-label', `Read ${title} (${activeHit.sourceName || activeHit.source})`);
          actions.appendChild(readLink);

          const libBtn = button('xbtn xbtn-lg', inLibrary ? 'IN LIBRARY' : 'ADD TO LIBRARY', async () => {
            if (inLibrary) return;
            libBtn.disabled = true;
            try {
              await novelsSend('PUT', '/library', {
                source: activeHit.source,
                novelPath: activeHit.path,
                title: activeHit.title || title,
                coverUrl: activeHit.cover || coverUrl || null,
                status: 'Reading'
              });
              inLibrary = true;
              item.inLibrary = true;
              hero.refreshActions();
              onLibraryAdded?.(title);
              toast('Added to novel library');
            } catch {
              libBtn.disabled = false;
              toast('Could not add to novel library.');
            }
          });
          if (inLibrary) {
            libBtn.disabled = true;
            libBtn.setAttribute('aria-pressed', 'true');
          }
          actions.appendChild(libBtn);
        } else {
          const findBtn = button('xbtn xbtn-primary xbtn-lg xn-find-btn', 'Find on novel sources', () => {
            runSourceSearch(findBtn);
          });
          actions.appendChild(findBtn);
        }
      },
      renderSecondaryActions: sec => {
        if (item.id && onList) {
          sec.append(
            button('xbtn xbtn-sm xn-drawer-save', '♥ Save', () => onList(item, novelStateOf(item) === 'saved' ? null : 'saved')),
            button('xbtn xbtn-sm xn-drawer-dismiss', 'Not interested', () => onList(item, novelStateOf(item) === 'dismissed' ? null : 'dismissed'))
          );
        }
      }
    });
    currentHero = hero;
    drawer.setAttribute('aria-labelledby', 'xndetail-title');
    body.appendChild(hero.element);

    if (alternateTitles?.length) {
      const det = el('details', 'xsyn');
      det.appendChild(el('summary', null, `Alternative titles (${alternateTitles.length})`));
      const ul = el('ul');
      for (const a of alternateTitles) ul.appendChild(el('li', null, a));
      det.appendChild(ul);
      body.appendChild(det);
    }

    body.appendChild(sourcesBox);

    if (synopsis) {
      const synSec = el('section', 'xblock');
      synSec.append(el('h3', null, 'Synopsis'), el('p', 'xdesc', synopsis));
      body.appendChild(synSec);
    }

    frag.appendChild(body);
    drawer.replaceChildren(frag);
    refresh();
    drawer.scrollTop = 0;
    closeBtn.focus({ preventScroll: true });
  }

  backdrop.addEventListener('click', event => { if (event.target === backdrop) close(); });
  backdrop.addEventListener('keydown', event => {
    if (reviewsPanel.isOpen()) return;
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
    else trapTab(drawer, event);
  });

  return { open, close, mount, unmount, refresh, isOpen: () => !backdrop.hidden };
}
