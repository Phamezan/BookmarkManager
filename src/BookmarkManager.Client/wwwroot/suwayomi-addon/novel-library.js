import { el, button, safeUrl, stateBox, isAbort } from './explore-util.js?v=3';
import {
  novelsGet, novelHash, readHash, READING_STATUSES, statusDisplay, showErrorBanner
} from './novel-api.js?v=1';

function buildLibraryCard(entry) {
  const card = el('article', 'ncard');
  card.dataset.id = entry.id;

  const detailHref = novelHash(entry.source, entry.novelPath);
  const link = el('a', 'ncard-link');
  link.href = detailHref;

  const cover = el('div', 'xcover');
  const coverSrc = safeUrl(entry.coverUrl);
  if (coverSrc) {
    const img = el('img');
    img.loading = 'lazy';
    img.alt = `${entry.title} cover`;
    img.addEventListener('load', () => img.classList.add('is-loaded'));
    img.src = coverSrc;
    cover.appendChild(img);
  } else {
    cover.appendChild(el('div', 'ncard-cover-placeholder', entry.title));
  }
  cover.appendChild(el('span', 'xbadge xbadge-type', entry.source));
  link.appendChild(cover);
  link.appendChild(el('h3', 'xtitle', entry.title));
  card.appendChild(link);

  const meta = el('div', 'xmeta');
  const statusClass = entry.status === 'Completed' ? 'xchip xchip-done' : 'xchip';
  meta.appendChild(el('span', statusClass, statusDisplay(entry.status)));
  card.appendChild(meta);

  const foot = el('div', 'ncard-foot');
  const chNum = entry.lastChapterIndex != null ? entry.lastChapterIndex + 1 : 0;
  const total = entry.chapterCount != null ? entry.chapterCount : '?';
  const continueHref = entry.lastChapterPath
    ? readHash(entry.source, entry.novelPath, entry.lastChapterPath)
    : detailHref;
  const progressLabel = entry.lastChapterPath
    ? `Ch ${chNum} of ${total} · Continue`
    : (entry.chapterCount != null ? `${entry.chapterCount} ch · Start reading` : 'Open novel');
  const cont = el('a', 'ncard-continue', progressLabel);
  cont.href = continueHref;
  foot.appendChild(cont);
  card.appendChild(foot);

  return card;
}

export async function renderLibrary({ view, banner, signal }) {
  let activeStatus = 'All';
  let entries = [];

  view.replaceChildren(el('div', 'xstate', 'Loading library…'));
  try {
    entries = await novelsGet('/library', signal);
    banner.replaceChildren();
  } catch (error) {
    if (isAbort(error)) return;
    view.replaceChildren();
    showErrorBanner(banner, error, () => renderLibrary({ view, banner, signal: new AbortController().signal }));
    return;
  }

  const root = el('div', 'nlibrary');
  const toolbar = el('div', 'nlib-toolbar');
  const filterWrap = el('div', 'nlib-filters');
  const countEl = el('span', 'xresult-count');
  const gridHost = el('div');

  const filterOptions = [{ value: 'All', label: 'All' }, ...READING_STATUSES];

  function draw() {
    filterWrap.replaceChildren();
    for (const opt of filterOptions) {
      const chip = button('xfchip', opt.label, () => {
        activeStatus = opt.value;
        draw();
      });
      chip.setAttribute('aria-pressed', String(activeStatus === opt.value));
      filterWrap.appendChild(chip);
    }

    const filtered = activeStatus === 'All'
      ? entries
      : entries.filter(e => e.status === activeStatus);

    countEl.textContent = `${filtered.length} ${filtered.length === 1 ? 'novel' : 'novels'}`;

    if (!filtered.length) {
      const exploreLink = el('a', 'xbtn xbtn-primary', 'Browse novels in Explore');
      exploreLink.href = '/bm/explore.html';
      const msg = entries.length === 0
        ? 'Your novel library is empty. Find a novel in Explore to start reading.'
        : `No novels with status "${statusDisplay(activeStatus)}".`;
      gridHost.replaceChildren(stateBox('book', msg, ...(entries.length === 0 ? [exploreLink] : [])));
      return;
    }

    const grid = el('div', 'xgrid');
    for (const entry of filtered) {
      grid.appendChild(buildLibraryCard(entry));
    }
    gridHost.replaceChildren(grid);
  }

  toolbar.append(filterWrap, countEl);
  root.append(toolbar, gridHost);
  view.replaceChildren(root);
  draw();
}
