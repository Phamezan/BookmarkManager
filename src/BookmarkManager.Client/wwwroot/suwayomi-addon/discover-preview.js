let active = null;
let openTimer = null;
let closeTimer = null;
let nextId = 0;

export function closeSeriesPreview(restoreFocus = false) {
  clearTimeout(openTimer);
  clearTimeout(closeTimer);
  if (!active) return;
  const { panel, button } = active;
  active = null;
  const focusInside = panel.contains(document.activeElement);
  panel.classList.remove('is-open');
  panel.setAttribute('aria-hidden', 'true');
  button.setAttribute('aria-expanded', 'false');
  if (restoreFocus && focusInside) button.focus({ preventScroll: true });
}

document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && active) {
    closeSeriesPreview(true);
    event.preventDefault();
  }
});
document.addEventListener('pointerdown', event => {
  if (active && !active.card.contains(event.target)) closeSeriesPreview();
});
window.addEventListener('resize', () => closeSeriesPreview());
window.addEventListener('scroll', event => {
  if (!active || active.panel.contains(event.target)) return;
  const bounds = active.card.getBoundingClientRect();
  if (bounds.bottom <= 0 || bounds.top >= window.innerHeight) closeSeriesPreview(true);
  else positionPreview(active.panel, active.card.querySelector('.cover-wrap'));
}, true);

export function attachSeriesPreview(card, item) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'details-btn';
  button.textContent = 'Details';
  button.setAttribute('aria-label', `Details for ${item.title}`);
  button.setAttribute('aria-expanded', 'false');
  const panelId = `series-preview-${++nextId}`;
  button.setAttribute('aria-controls', panelId);
  card.appendChild(button);

  let panel = null;
  function show() {
    clearTimeout(openTimer);
    clearTimeout(closeTimer);
    if (active?.card === card) return;
    closeSeriesPreview();
    if (!panel) {
      panel = buildPreview(item, panelId);
      panel.querySelector('.preview-close').addEventListener('click', () => closeSeriesPreview(true));
      card.appendChild(panel);
    }
    active = { card, panel, button };
    panel.classList.add('is-open');
    panel.setAttribute('aria-hidden', 'false');
    button.setAttribute('aria-expanded', 'true');
    positionPreview(panel, card.querySelector('.cover-wrap'));
  }

  card.addEventListener('pointerenter', event => {
    if (event.pointerType !== 'mouse') return;
    clearTimeout(closeTimer);
    clearTimeout(openTimer);
    openTimer = setTimeout(show, 250);
  });
  card.addEventListener('pointerleave', () => {
    clearTimeout(openTimer);
    clearTimeout(closeTimer);
    closeTimer = setTimeout(() => {
      if (active?.card === card && !card.contains(document.activeElement)) closeSeriesPreview();
    }, 180);
  });
  card.addEventListener('focusin', event => {
    if (event.target !== button) show();
  });
  card.addEventListener('focusout', event => {
    if (!card.contains(event.relatedTarget)) closeSeriesPreview();
  });
  button.addEventListener('click', () => {
    if (active?.card === card) closeSeriesPreview();
    else show();
  });
}

function buildPreview(item, id) {
  const panel = document.createElement('section');
  panel.id = id;
  panel.className = 'series-preview';
  panel.tabIndex = 0;
  panel.setAttribute('role', 'region');
  panel.setAttribute('aria-labelledby', `${id}-title`);
  panel.setAttribute('aria-hidden', 'true');

  const header = document.createElement('div');
  header.className = 'preview-header';
  const title = document.createElement('h2');
  title.id = `${id}-title`;
  title.textContent = item.title;
  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'preview-close';
  close.setAttribute('aria-label', 'Close details');
  close.textContent = '×';
  header.append(title, close);
  panel.appendChild(header);

  const meta = document.createElement('p');
  meta.className = 'preview-meta';
  const status = (item.status || '').toLowerCase().replaceAll('_', ' ');
  meta.textContent = [item.type !== 'Unknown' ? item.type : null,
    status ? status.charAt(0).toUpperCase() + status.slice(1) : null].filter(Boolean).join(' · ');
  panel.appendChild(meta);

  const tags = document.createElement('div');
  tags.className = 'preview-tags';
  tags.setAttribute('aria-label', 'Tags');
  for (const genre of item.genres || []) {
    const tag = document.createElement('span');
    tag.className = 'preview-tag';
    tag.textContent = genre;
    tags.appendChild(tag);
  }
  if (!tags.childElementCount) tags.textContent = 'No tags available.';
  panel.appendChild(tags);

  const description = document.createElement('p');
  description.className = 'preview-description';
  description.textContent = item.description?.trim() || 'No description available.';
  panel.appendChild(description);

  if (item.progress) {
    const progress = document.createElement('p');
    progress.className = 'preview-meta';
    progress.textContent = `Read ${item.progress.read} of ${item.progress.latest} chapters`;
    panel.appendChild(progress);
  }
  const sources = document.createElement('p');
  sources.className = 'preview-meta';
  sources.textContent = `Sources: ${(item.sources || []).join(' · ') || 'Unknown'}`;
  panel.appendChild(sources);
  const link = document.createElement('a');
  link.className = 'preview-link';
  link.href = `/manga/${item.seriesMangaId}`;
  link.textContent = 'Open series →';
  panel.appendChild(link);
  return panel;
}

function positionPreview(panel, cover) {
  const margin = 12;
  const gap = 10;
  const width = document.documentElement.clientWidth;
  const height = window.innerHeight;
  const anchor = cover.getBoundingClientRect();
  const bounds = panel.getBoundingClientRect();
  let left = anchor.right + gap;
  if (left + bounds.width > width - margin) left = anchor.left - bounds.width - gap;
  let top = anchor.top;
  if (left < margin) {
    left = anchor.left;
    top = anchor.bottom + gap;
    if (top + bounds.height > height - margin) top = anchor.top - bounds.height - gap;
  }
  panel.style.left = `${Math.max(margin, Math.min(left, width - bounds.width - margin))}px`;
  panel.style.top = `${Math.max(margin, Math.min(top, height - bounds.height - margin))}px`;
}
