import { el, button, safeUrl, stateBox, isAbort } from './explore-util.js?v=3';
import {
  novelsGet, novelsSend, readHash, READING_STATUSES, showErrorBanner, toast
} from './novel-api.js?v=1';
import { createNovelHero, renderSynopsis } from './novel-hero.js?v=2';
import { createReviewsPanel } from './novel-reviews.js?v=1';

function buildChapterRow(chapter, detail) {
  const li = el('li', 'nch-item');
  if (chapter.locked) {
    const row = el('div', 'nch-row is-locked');
    const name = el('span', 'nch-name', `🔒 ${chapter.name}`);
    const meta = el('span', 'nch-meta');
    const extUrl = safeUrl(`${detail.site}${chapter.path}`);
    if (extUrl) {
      const ext = el('a', 'nch-ext', 'Open on site ↗');
      ext.href = extUrl;
      ext.target = '_blank';
      ext.rel = 'noopener noreferrer';
      meta.appendChild(ext);
    }
    row.append(name, meta);
    li.appendChild(row);
    return li;
  }

  const row = el('a', 'nch-row');
  row.href = readHash(detail.source, detail.path, chapter.path);
  if (detail.libraryEntry?.lastChapterPath === chapter.path) {
    row.classList.add('is-current');
  }
  row.appendChild(el('span', 'nch-name', chapter.name));
  const meta = el('span', 'nch-meta');
  if (chapter.releaseTime) {
    const dateText = chapter.releaseTime.slice(0, 10);
    meta.appendChild(el('span', null, dateText));
  }
  row.appendChild(meta);
  li.appendChild(row);
  return li;
}

export async function renderDetail({ view, banner, params, signal }) {
  const source = params.get('source') || '';
  const novelPath = params.get('path') || '';

  if (!source || !novelPath) {
    view.replaceChildren(stateBox('alert', 'Missing novel source or path.'));
    return () => {};
  }

  view.replaceChildren(el('div', 'xstate', 'Loading novel details…'));
  let detail;
  try {
    const qs = new URLSearchParams({ source, path: novelPath });
    detail = await novelsGet(`/novel?${qs}`, signal);
    banner.replaceChildren();
  } catch (error) {
    if (isAbort(error)) return () => {};
    view.replaceChildren();
    showErrorBanner(banner, error, () => renderDetail({ view, banner, params, signal: new AbortController().signal }));
    return () => {};
  }

  document.title = `${detail.title} · Novels`;

  let libraryEntry = detail.libraryEntry || null;
  let reversed = false;

  const reviewsPanel = createReviewsPanel();
  const root = el('div', 'ndetail');
  const chapters = detail.chapters || [];
  const firstUnlocked = chapters.find(c => !c.locked) || null;
  const latestChapter = chapters.length ? chapters[chapters.length - 1] : null;

  const chaptersBox = el('section', 'nchapters-box');
  chaptersBox.id = 'novel-chapters';

  const hero = createNovelHero({
    model: {
      title: detail.title,
      coverUrl: detail.cover,
      author: detail.author,
      status: detail.status,
      genres: detail.genres,
      chapters: chapters.length || null,
      latestChapterName: latestChapter?.name || null,
      source: detail.source,
      sourceName: detail.sourceName,
      path: detail.path,
      site: detail.site
    },
    signal,
    onChaptersClick: () => {
      chaptersBox.scrollIntoView({ behavior: 'smooth', block: 'start' });
    },
    onReviewsClick: () => {
      reviewsPanel.open({
        title: detail.title,
        source: detail.source,
        sourceName: detail.sourceName,
        site: detail.site,
        path: detail.path
      });
    },
    renderPrimaryActions: actions => {
      const resumePath = libraryEntry?.lastChapterPath;
      if (resumePath) {
        const chLabel = libraryEntry.lastChapterIndex != null
          ? `CONTINUE CH ${libraryEntry.lastChapterIndex + 1}`
          : 'CONTINUE READING';
        const cont = el('a', 'xbtn xbtn-primary xbtn-lg', chLabel);
        cont.href = readHash(detail.source, detail.path, resumePath);
        actions.appendChild(cont);
      } else if (firstUnlocked) {
        const start = el('a', 'xbtn xbtn-primary xbtn-lg', 'READ NOW');
        start.href = readHash(detail.source, detail.path, firstUnlocked.path);
        actions.appendChild(start);
      }

      if (libraryEntry) {
        const inLibBtn = button('xbtn xbtn-lg', 'IN LIBRARY', async () => {
          inLibBtn.disabled = true;
          try {
            await novelsSend('DELETE', `/library/${libraryEntry.id}`);
            libraryEntry = null;
            detail.libraryEntry = null;
            hero.refreshActions();
            toast('Removed from library');
          } catch {
            inLibBtn.disabled = false;
            toast('Could not remove from library.');
          }
        }, 'Remove from library');
        inLibBtn.setAttribute('aria-pressed', 'true');
        actions.appendChild(inLibBtn);

        const select = el('select', 'nselect');
        select.setAttribute('aria-label', 'Reading status');
        for (const s of READING_STATUSES) {
          const opt = el('option', null, s.label);
          opt.value = s.value;
          if (s.value === libraryEntry.status) opt.selected = true;
          select.appendChild(opt);
        }
        select.addEventListener('change', async () => {
          try {
            libraryEntry = await novelsSend('PATCH', `/library/${libraryEntry.id}`, { status: select.value });
            toast(`Status set to ${select.value}`);
          } catch {
            toast('Could not update status.');
            select.value = libraryEntry.status;
          }
        });
        actions.appendChild(select);
      } else {
        const addBtn = button('xbtn xbtn-lg', 'ADD TO LIBRARY', async () => {
          addBtn.disabled = true;
          try {
            libraryEntry = await novelsSend('PUT', '/library', {
              source: detail.source,
              novelPath: detail.path,
              title: detail.title,
              coverUrl: detail.cover || null,
              status: 'Reading'
            });
            detail.libraryEntry = libraryEntry;
            hero.refreshActions();
            toast('Added to library');
          } catch {
            addBtn.disabled = false;
            toast('Could not add to library.');
          }
        });
        actions.appendChild(addBtn);
      }

      const siteUrl = safeUrl(`${detail.site}${detail.path}`);
      if (siteUrl) {
        const ext = el('a', 'xbtn', 'Open on site ↗');
        ext.href = siteUrl;
        ext.target = '_blank';
        ext.rel = 'noopener noreferrer';
        actions.appendChild(ext);
      }
    }
  });

  root.appendChild(hero.element);

  if (detail.summary) {
    root.appendChild(renderSynopsis(detail.summary, 'nsummary-box'));
  }

  const chHead = el('div', 'nchapters-head');
  chHead.appendChild(el('h3', null, `Chapters (${chapters.length})`));
  const sortBtn = button('xbtn', 'Oldest first', () => {
    reversed = !reversed;
    sortBtn.textContent = reversed ? 'Newest first' : 'Oldest first';
    renderChapters();
  });
  chHead.appendChild(sortBtn);
  chaptersBox.appendChild(chHead);

  const listEl = el('ul', 'nchapters-list');
  chaptersBox.appendChild(listEl);

  function renderChapters() {
    listEl.replaceChildren();
    const items = reversed ? [...chapters].reverse() : chapters;
    if (!items.length) {
      listEl.appendChild(el('li', 'xstate', 'No chapters available.'));
      return;
    }
    const frag = document.createDocumentFragment();
    for (const ch of items) frag.appendChild(buildChapterRow(ch, detail));
    listEl.appendChild(frag);
  }

  renderChapters();
  root.appendChild(chaptersBox);
  view.replaceChildren(root);

  return () => {
    reviewsPanel.destroy();
  };
}
