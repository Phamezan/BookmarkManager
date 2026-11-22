import { el, button, safeUrl, mangaHref, cssColor, statusLabel, stateOf, libraryOf } from './explore-util.js?v=3';

function cover(item) {
  const wrap = el('div', 'xcover');
  const src = safeUrl(item.coverUrl);
  if (src) {
    const img = el('img');
    img.loading = 'lazy';
    img.alt = '';
    img.addEventListener('load', () => img.classList.add('is-loaded'));
    img.src = src;
    wrap.appendChild(img);
  }
  return wrap;
}

function libraryBadge(item) {
  const library = libraryOf(item);
  const href = mangaHref(library);
  if (!href) return null;
  const link = el('a', 'xbadge xbadge-lib');
  link.href = href;
  const { read = 0, latest = 0 } = library;
  link.textContent = read > 0 && latest > 0 ? (read >= latest ? 'Caught up' : `Reading ${read}/${latest}`) : 'In library';
  link.addEventListener('click', event => event.stopPropagation());
  return link;
}

// Re-applies list state / library badge to an existing card (called after any change).
export function syncCard(card) {
  const item = card.xItem;
  const state = stateOf(item);
  const save = card.querySelector('.xact-save');
  const dismiss = card.querySelector('.xact-dismiss');
  save.setAttribute('aria-pressed', String(state === 'saved'));
  save.setAttribute('aria-label', `${state === 'saved' ? 'Remove from saved' : 'Save'}: ${item.title}`);
  dismiss.setAttribute('aria-pressed', String(state === 'dismissed'));
  dismiss.setAttribute('aria-label', `${state === 'dismissed' ? 'Undo not interested' : 'Not interested'}: ${item.title}`);
  card.classList.toggle('is-dismissed', state === 'dismissed');
  const old = card.querySelector('.xbadge-lib');
  const fresh = libraryBadge(item);
  if (old) old.remove();
  if (fresh) card.querySelector('.xoverlay').appendChild(fresh);
}

export function buildCard(item, hooks) {
  const card = el('article', 'xcard');
  card.tabIndex = 0;
  card.dataset.id = String(item.anilistId);
  card.xItem = item;
  card.setAttribute('aria-label', item.title);
  const accent = cssColor(item.color);
  if (accent) card.style.setProperty('--accent', accent);

  const coverEl = cover(item);
  if (accent) coverEl.style.setProperty('--accent', accent);
  if (item.type && item.type !== 'Unknown') coverEl.appendChild(el('span', 'xbadge xbadge-type', item.type));
  const actions = el('div', 'xactions');
  actions.append(
    button('xact xact-save', '♥', event => { event.stopPropagation(); hooks.onList(item, stateOf(item) === 'saved' ? null : 'saved'); }),
    button('xact xact-dismiss', '✕', event => { event.stopPropagation(); hooks.onList(item, stateOf(item) === 'dismissed' ? null : 'dismissed'); })
  );
  coverEl.appendChild(actions);
  // Score and library badge share one gradient strip so they can never overlap.
  const overlay = el('div', 'xoverlay');
  if (item.averageScore != null) {
    const score = el('span', 'xscore');
    score.append(el('span', 'xstar', '★'), document.createTextNode(` ${(item.averageScore / 10).toFixed(1)}`));
    overlay.appendChild(score);
  }
  coverEl.appendChild(overlay);
  card.appendChild(coverEl);

  card.appendChild(el('h3', 'xtitle', item.title));
  const meta = el('div', 'xmeta');
  if (item.status) {
    meta.appendChild(el('span', item.status === 'FINISHED' ? 'xchip xchip-done' : 'xchip', statusLabel(item.status)));
  }
  if (item.chapters != null) meta.appendChild(el('span', 'xmeta-text', `${item.chapters} ch`));
  card.appendChild(meta);
  const genres = (item.genres || []).slice(0, 3).join(' · ');
  if (genres) card.appendChild(el('div', 'xgenres', genres));
  if (item.becauseOf?.length) card.appendChild(el('div', 'xbecause', `Because you read ${item.becauseOf.join(', ')}`));

  const open = () => hooks.onOpen(item);
  card.addEventListener('click', open);
  card.addEventListener('keydown', event => {
    if (event.target === card && event.key === 'Enter') { event.preventDefault(); open(); }
  });
  syncCard(card);
  return card;
}

export function buildSkeletons(count = 12) {
  const frag = document.createDocumentFragment();
  for (let i = 0; i < count; i++) {
    const card = el('div', 'xcard xskeleton');
    card.append(el('div', 'xcover'), el('div', 'xsk-bar'), el('div', 'xsk-bar short'));
    frag.appendChild(card);
  }
  return frag;
}

export function buildGrid() { return el('div', 'xgrid'); }

export function buildShelf(shelf, hooks) {
  const section = el('section', 'xshelf');
  const head = el('div', 'xshelf-head');
  const titles = el('div');
  titles.appendChild(el('h2', null, shelf.title));
  if (shelf.subtitle) titles.appendChild(el('p', 'xshelf-sub', shelf.subtitle));
  head.append(titles, button('xlink', 'See all', () => hooks.onSeeAll(shelf), `See all: ${shelf.title}`));
  const wrap = el('div', 'xshelf-wrap');
  const track = el('div', 'xshelf-track');
  const ranked = shelf.key === 'trending';
  (shelf.items || []).forEach((item, index) => {
    const card = buildCard(item, hooks);
    if (ranked) {
      card.classList.add('has-rank');
      card.querySelector('.xcover').appendChild(el('span', 'xrank', `#${index + 1}`));
    }
    track.appendChild(card);
  });
  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  const page = dir => track.scrollBy({ left: dir * track.clientWidth * 0.85, behavior: reduce.matches ? 'auto' : 'smooth' });
  const prev = button('xshelf-nav xshelf-prev', '‹', () => page(-1), `Scroll ${shelf.title} left`);
  const next = button('xshelf-nav xshelf-next', '›', () => page(1), `Scroll ${shelf.title} right`);
  prev.tabIndex = next.tabIndex = -1;
  const update = () => {
    wrap.classList.toggle('can-prev', track.scrollLeft > 4);
    wrap.classList.toggle('can-next', track.scrollLeft + track.clientWidth < track.scrollWidth - 4);
  };
  track.addEventListener('scroll', update, { passive: true });
  new ResizeObserver(update).observe(track);
  wrap.append(track, prev, next);
  section.append(head, wrap);
  return section;
}

// Compact cover button used for relations and recommendations in the detail drawer.
export function buildMini(item, caption, onClick) {
  const node = button('xmini', null, onClick);
  node.setAttribute('aria-label', `${caption ? caption + ': ' : ''}${item.title}`);
  node.appendChild(cover(item));
  node.appendChild(el('span', 'xmini-title', item.title));
  if (caption) node.appendChild(el('span', 'xmini-cap', caption));
  return node;
}
