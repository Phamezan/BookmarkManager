import { el, button, safeUrl, stateBox, isAbort } from './explore-util.js?v=3';
import {
  novelsGet, novelsSend, readHash, READING_STATUSES, showErrorBanner, toast
} from './novel-api.js?v=1';

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
    return;
  }

  view.replaceChildren(el('div', 'xstate', 'Loading novel details…'));
  let detail;
  try {
    const qs = new URLSearchParams({ source, path: novelPath });
    detail = await novelsGet(`/novel?${qs}`, signal);
    banner.replaceChildren();
  } catch (error) {
    if (isAbort(error)) return;
    view.replaceChildren();
    showErrorBanner(banner, error, () => renderDetail({ view, banner, params, signal: new AbortController().signal }));
    return;
  }

  document.title = `${detail.title} · Novels`;

  let libraryEntry = detail.libraryEntry || null;
  let reversed = false;

  const root = el('div', 'ndetail');

  const hero = el('section', 'ndetail-hero');
  const coverSrc = safeUrl(detail.cover);
  if (coverSrc) {
    const img = el('img', 'ndetail-cover');
    img.src = coverSrc;
    img.alt = `${detail.title} cover`;
    hero.appendChild(img);
  }

  const info = el('div', 'ndetail-info');
  info.appendChild(el('h2', null, detail.title));
  if (detail.author) info.appendChild(el('p', 'ndetail-author', detail.author));

  const meta = el('div', 'xmeta');
  meta.appendChild(el('span', 'xchip', detail.sourceName || detail.source));
  if (detail.status) {
    meta.appendChild(el('span', detail.status === 'Completed' ? 'xchip xchip-done' : 'xchip', detail.status));
  }
  for (const g of detail.genres || []) {
    meta.appendChild(el('span', 'xchip', g));
  }
  info.appendChild(meta);

  if (detail.summary) {
    info.appendChild(el('p', 'ndetail-summary', detail.summary));
  }

  const actions = el('div', 'ndetail-actions');
  info.appendChild(actions);
  hero.appendChild(info);
  root.appendChild(hero);

  const firstUnlocked = (detail.chapters || []).find(c => !c.locked) || null;

  function renderActions() {
    actions.replaceChildren();

    const resumePath = libraryEntry?.lastChapterPath;
    if (resumePath) {
      const cont = el('a', 'xbtn xbtn-primary', 'Continue');
      cont.href = readHash(detail.source, detail.path, resumePath);
      actions.appendChild(cont);
    } else if (firstUnlocked) {
      const start = el('a', 'xbtn xbtn-primary', 'Start reading');
      start.href = readHash(detail.source, detail.path, firstUnlocked.path);
      actions.appendChild(start);
    }

    if (libraryEntry) {
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

      const removeBtn = button('xbtn', 'Remove from library', async () => {
        removeBtn.disabled = true;
        try {
          await novelsSend('DELETE', `/library/${libraryEntry.id}`);
          libraryEntry = null;
          detail.libraryEntry = null;
          renderActions();
          toast('Removed from library');
        } catch {
          removeBtn.disabled = false;
          toast('Could not remove from library.');
        }
      });
      actions.appendChild(removeBtn);
    } else {
      const addBtn = button('xbtn', 'Add to library', async () => {
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
          renderActions();
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

  renderActions();

  const chaptersBox = el('section', 'nchapters-box');
  const chHead = el('div', 'nchapters-head');
  const chCount = (detail.chapters || []).length;
  chHead.appendChild(el('h3', null, `Chapters (${chCount})`));
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
    const items = reversed ? [...(detail.chapters || [])].reverse() : (detail.chapters || []);
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
}
