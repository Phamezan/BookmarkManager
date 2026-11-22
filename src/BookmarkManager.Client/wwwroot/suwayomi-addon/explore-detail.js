import {
  el, button, safeUrl, mangaHref, cssColor, statusLabel, formatCount, stateOf, libraryOf, setLibraryOf,
  getJson, send, isAbort, errorMessage, trapTab, stateBox
} from './explore-util.js?v=3';
import { buildMini } from './explore-cards.js?v=3';

// Detail drawer. `hooks`: onList(item, state), onTag(kind, name), toast(msg), onLibraryAdded(id).
export function createDetail(hooks) {
  const backdrop = el('div', 'xdrawer-backdrop');
  backdrop.hidden = true;
  const drawer = el('aside', 'xdrawer');
  drawer.setAttribute('role', 'dialog');
  drawer.setAttribute('aria-modal', 'true');
  drawer.setAttribute('aria-label', 'Series details');
  drawer.tabIndex = -1;
  backdrop.appendChild(drawer);
  document.body.appendChild(backdrop);

  let depth = 0;
  let openId = null;
  let controller = null;
  let returnFocus = null;
  let current = null;
  let closing = false;

  const idFromUrl = () => {
    const id = Number(new URLSearchParams(location.search).get('id'));
    return Number.isInteger(id) && id > 0 ? id : null;
  };

  function urlWithId(id) {
    const url = new URL(location.href);
    if (id) url.searchParams.set('id', String(id)); else url.searchParams.delete('id');
    return url.pathname + url.search;
  }

  function show() {
    if (!backdrop.hidden) return;
    returnFocus = document.activeElement;
    backdrop.hidden = false;
    document.getElementById('page').inert = true;
    document.body.classList.add('is-locked');
  }

  function hide() {
    controller?.abort();
    openId = null;
    current = null;
    depth = 0;
    if (backdrop.hidden) return;
    backdrop.hidden = true;
    document.getElementById('page').inert = false;
    document.body.classList.remove('is-locked');
    if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    returnFocus = null;
  }

  function open(id, { push = true } = {}) {
    if (push) {
      depth += 1;
      history.pushState({ xd: depth }, '', urlWithId(id));
    }
    show();
    load(id);
  }

  // Unwinds entries pushed by this drawer (so Back never re-opens it), hides, then runs `then`.
  function unwind(then) {
    if (backdrop.hidden) { then?.(); return; }
    if (depth === 0) {
      if (idFromUrl()) history.replaceState(history.state, '', urlWithId(null));
      hide();
      then?.();
      return;
    }
    closing = true;
    window.addEventListener('popstate', () => {
      closing = false;
      if (idFromUrl()) history.replaceState(history.state, '', urlWithId(null));
      hide();
      then?.();
    }, { once: true });
    history.go(-depth);
  }

  const close = () => unwind();
  const closeThen = fn => unwind(fn);

  function syncFromLocation() {
    if (closing) return;
    const id = idFromUrl();
    if (!id) { hide(); return; }
    depth = history.state?.xd ?? 0;
    if (id !== openId) { show(); load(id); }
  }

  async function load(id) {
    controller?.abort();
    controller = new AbortController();
    openId = id;
    drawer.replaceChildren(el('div', 'xstate', 'Loading…'), closeButton());
    try {
      const item = await getJson(`/series/${id}`, controller.signal);
      if (openId !== id) return;
      current = item;
      render(item);
    } catch (error) {
      if (isAbort(error) || openId !== id) return;
      drawer.replaceChildren(stateBox('alert', errorMessage(error), button('xbtn', 'Retry', () => load(id))), closeButton());
    }
    (drawer.querySelector('.xdrawer-close') || drawer).focus({ preventScroll: true });
  }

  const closeButton = () => button('xdrawer-close', '×', close, 'Close details');

  function stat(label, value) {
    const box = el('div', 'xstat');
    box.append(el('strong', null, value), el('span', null, label));
    return box;
  }

  function render(item) {
    const frag = document.createDocumentFragment();
    const banner = el('div', 'xbanner');
    const bannerSrc = safeUrl(item.bannerUrl);
    const accent = cssColor(item.color);
    if (accent) banner.style.setProperty('--accent', accent);
    if (bannerSrc) banner.style.backgroundImage = `url("${bannerSrc.replace(/["\\\n\r]/g, '')}")`;
    frag.append(banner, closeButton());

    const body = el('div', 'xdrawer-body');
    const top = el('div', 'xdetail-top');
    const coverSrc = safeUrl(item.coverUrl);
    if (coverSrc) {
      const img = el('img', 'xdetail-cover');
      img.alt = `${item.title} cover`;
      img.src = coverSrc;
      top.appendChild(img);
    }
    const titles = el('div', 'xdetail-titles');
    titles.appendChild(el('h2', null, item.title));
    titles.id = 'xdetail-title';
    drawer.setAttribute('aria-labelledby', 'xdetail-title');
    if (item.romajiTitle && item.romajiTitle !== item.title) titles.appendChild(el('p', 'xalt', item.romajiTitle));
    if (item.nativeTitle) titles.appendChild(el('p', 'xalt', item.nativeTitle));
    if (item.synonyms?.length) {
      const details = el('details', 'xsyn');
      details.appendChild(el('summary', null, `Alternative titles (${item.synonyms.length})`));
      const list = el('ul');
      for (const s of item.synonyms) list.appendChild(el('li', null, s));
      details.appendChild(list);
      titles.appendChild(details);
    }
    const chips = el('div', 'xmeta');
    for (const text of [item.type !== 'Unknown' ? item.type : null, item.format].filter(Boolean)) chips.appendChild(el('span', 'xchip', text));
    if (item.status) chips.appendChild(el('span', item.status === 'FINISHED' ? 'xchip xchip-done' : 'xchip', statusLabel(item.status)));
    titles.appendChild(chips);
    top.appendChild(titles);
    body.appendChild(top);

    const stats = el('div', 'xstats');
    const dates = [item.startDate, item.endDate].filter(Boolean).join(' → ');
    stats.append(
      stat('Score', item.averageScore != null ? (item.averageScore / 10).toFixed(1) : '–'),
      stat('Popularity', formatCount(item.popularity)),
      stat('Favourites', formatCount(item.favourites)),
      stat('Chapters', item.chapters != null ? String(item.chapters) : '–'),
      stat('Volumes', item.volumes != null ? String(item.volumes) : '–'),
      stat('Published', dates || (item.startYear ? String(item.startYear) : '–'))
    );
    body.appendChild(stats);

    const actions = el('div', 'xdetail-actions');
    const save = button('xbtn', '', () => hooks.onList(item, stateOf(item) === 'saved' ? null : 'saved'));
    const dismiss = button('xbtn', '', () => hooks.onList(item, stateOf(item) === 'dismissed' ? null : 'dismissed'));
    save.classList.add('xd-save');
    dismiss.classList.add('xd-dismiss');
    actions.append(save, dismiss);
    const site = safeUrl(item.siteUrl);
    if (site) {
      const link = el('a', 'xbtn', 'AniList ↗');
      link.href = site; link.target = '_blank'; link.rel = 'noopener noreferrer';
      actions.appendChild(link);
    }
    body.appendChild(actions);

    body.appendChild(readSection(item));
    if (item.description) body.appendChild(block('Description', el('p', 'xdesc', item.description)));
    body.appendChild(chipBlock(item));
    if (item.staff?.length) {
      const list = el('ul', 'xstaff');
      for (const s of item.staff) list.appendChild(el('li', null, `${s.name}${s.role ? ` · ${s.role}` : ''}`));
      body.appendChild(block('Staff', list));
    }
    const relations = (item.relations || []).filter(r => r.item);
    if (relations.length) body.appendChild(block('Related', row(relations.map(r =>
      buildMini(r.item, r.relationType.replaceAll('_', ' ').toLowerCase(), () => open(r.item.anilistId))))));
    if (item.recommendations?.length) body.appendChild(block('Readers also liked', row(item.recommendations.map(r =>
      buildMini(r, '', () => open(r.anilistId))))));
    const links = (item.externalLinks || []).map(l => ({ site: l.site, url: safeUrl(l.url) })).filter(l => l.url);
    if (links.length) {
      const wrap = el('div', 'xlinks');
      for (const l of links) {
        const a = el('a', 'xlink', l.site);
        a.href = l.url; a.target = '_blank'; a.rel = 'noopener noreferrer';
        wrap.appendChild(a);
      }
      body.appendChild(block('Links', wrap));
    }
    frag.appendChild(body);
    drawer.replaceChildren(frag);
    drawer.scrollTop = 0;
    refresh();
    loadSources(item, drawer.querySelector('.xsources'));
  }

  const row = nodes => { const r = el('div', 'xrow'); r.append(...nodes); return r; };

  function block(title, content) {
    const section = el('section', 'xblock');
    section.append(el('h3', null, title), content);
    return section;
  }

  function chipBlock(item) {
    const wrap = el('div', 'xchips');
    for (const g of item.genres || []) wrap.appendChild(button('xfchip', g, () => hooks.onTag('genre', g)));
    for (const t of item.tags || []) wrap.appendChild(button('xfchip xfchip-tag', t, () => hooks.onTag('tag', t)));
    return block('Genres & tags', wrap);
  }

  function readSection(item) {
    const section = el('section', 'xblock xread');
    section.appendChild(el('h3', null, 'Read on Suwayomi'));
    const slot = el('div', 'xcontinue');
    section.appendChild(slot);
    section.appendChild(el('div', 'xsources', 'Searching your sources…'));
    return section;
  }

  // Keeps drawer buttons/continue link in step with list state and library changes.
  function refresh() {
    if (!current || backdrop.hidden) return;
    const state = stateOf(current);
    const save = drawer.querySelector('.xd-save');
    const dismiss = drawer.querySelector('.xd-dismiss');
    if (!save || !dismiss) return;
    save.textContent = state === 'saved' ? '♥ Saved' : '♡ Save';
    save.setAttribute('aria-pressed', String(state === 'saved'));
    dismiss.textContent = state === 'dismissed' ? 'Not interested ✓' : 'Not interested';
    dismiss.setAttribute('aria-pressed', String(state === 'dismissed'));
    const slot = drawer.querySelector('.xcontinue');
    const href = mangaHref(libraryOf(current));
    slot.replaceChildren();
    if (href) {
      const link = el('a', 'xbtn xbtn-primary', 'Continue reading');
      link.href = href;
      slot.appendChild(link);
    }
  }

  async function loadSources(item, box) {
    const id = item.anilistId;
    try {
      const hits = await getJson(`/series/${id}/sources`, controller.signal);
      if (openId !== id) return;
      renderSources(item, box, Array.isArray(hits) ? hits : []);
    } catch (error) {
      if (isAbort(error) || openId !== id) return;
      box.replaceChildren(el('p', null, errorMessage(error)), button('xbtn', 'Retry', () => {
        box.textContent = 'Searching your sources…';
        loadSources(item, box);
      }));
    }
  }

  function renderSources(item, box, hits) {
    box.replaceChildren();
    if (!hits.length) {
      box.appendChild(el('p', 'xempty', 'Not found on your sources.'));
      const link = el('a', 'xlink', 'Search all sources in Suwayomi');
      link.href = `/sources/all/search?query=${encodeURIComponent(item.title)}`;
      box.appendChild(link);
      return;
    }
    const exact = hits.filter(h => h.isExactMatch);
    const others = hits.filter(h => !h.isExactMatch);
    if (exact.length) box.appendChild(hitGroup('Best match', exact, item));
    if (others.length) box.appendChild(hitGroup(exact.length ? 'Other results' : 'Results', others, item));
  }

  function hitGroup(title, hits, item) {
    const group = el('div', 'xhits');
    group.appendChild(el('h4', null, title));
    for (const hit of hits) {
      const rowEl = el('div', 'xhit');
      const text = el('div', 'xhit-text');
      text.append(el('span', 'xhit-title', hit.title), el('span', 'xhit-src', hit.sourceName));
      rowEl.appendChild(text);
      const href = mangaHref(hit);
      if (href) {
        const open = el('a', 'xbtn', 'Open');
        open.href = href;
        open.setAttribute('aria-label', `Open ${hit.title} (${hit.sourceName})`);
        rowEl.appendChild(open);
      }
      rowEl.appendChild(addButton(hit, item));
      group.appendChild(rowEl);
    }
    return group;
  }

  function addButton(hit, item) {
    const btn = button('xbtn', hit.inLibrary ? 'In library' : 'Add to library');
    btn.disabled = Boolean(hit.inLibrary);
    btn.setAttribute('aria-label', `${hit.inLibrary ? 'In library' : 'Add to library'}: ${hit.title} (${hit.sourceName})`);
    btn.addEventListener('click', async () => {
      btn.disabled = true;
      try {
        await send('POST', '/library', { mangaId: hit.mangaId });
        btn.textContent = 'In library';
        btn.setAttribute('aria-label', `In library: ${hit.title} (${hit.sourceName})`);
        hit.inLibrary = true;
        if (!libraryOf(item)) setLibraryOf(item.anilistId, { mangaId: hit.mangaId, read: 0, latest: 0, sourceName: hit.sourceName });
        hooks.onLibraryAdded(item.anilistId);
        refresh();
        hooks.toast('Added to your Suwayomi library');
      } catch {
        btn.disabled = false;
        hooks.toast('Could not add to library. Try again.');
      }
    });
    return btn;
  }

  backdrop.addEventListener('click', event => { if (event.target === backdrop) close(); });
  backdrop.addEventListener('keydown', event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
    else trapTab(drawer, event);
  });
  window.addEventListener('popstate', syncFromLocation);

  return { open, close, closeThen, refresh, syncFromLocation, isOpen: () => !backdrop.hidden, currentId: () => openId };
}
