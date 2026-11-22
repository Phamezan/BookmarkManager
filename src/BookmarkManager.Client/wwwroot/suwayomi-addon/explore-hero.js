import { el, button, safeUrl, cssColor, statusLabel, stateOf } from './explore-util.js?v=3';

const ROTATE_MS = 8000;
const MAX_SLIDES = 5;

function bgLayer(item) {
  const banner = safeUrl(item.bannerUrl);
  const src = banner || safeUrl(item.coverUrl);
  const layer = el('div', banner ? 'xhero-bg' : 'xhero-bg xhero-bg-cover');
  const accent = cssColor(item.color);
  if (accent) layer.style.setProperty('--accent', accent);
  if (src) layer.style.backgroundImage = `url("${src.replace(/["\\n\r]/g, '')}")`;
  return layer;
}

function buildSlide(item, rank, hooks) {
  const slide = el('div', 'xhero-slide');
  slide.dataset.id = String(item.anilistId);
  const inner = el('div', 'xhero-inner');
  const coverSrc = safeUrl(item.coverUrl);
  if (coverSrc) {
    const cover = el('button', 'xhero-cover');
    cover.type = 'button';
    cover.tabIndex = -1;
    cover.setAttribute('aria-hidden', 'true');
    const accent = cssColor(item.color);
    if (accent) cover.style.setProperty('--accent', accent);
    const img = el('img');
    img.alt = '';
    img.src = coverSrc;
    cover.appendChild(img);
    cover.addEventListener('click', () => hooks.onOpen(item));
    inner.appendChild(cover);
  }
  const text = el('div', 'xhero-text');
  const kicker = el('div', 'xhero-kicker');
  kicker.appendChild(el('span', 'xhero-rank', `#${rank} trending`));
  if (item.type && item.type !== 'Unknown') kicker.appendChild(el('span', 'xchip', item.type));
  if (item.status) kicker.appendChild(el('span', 'xchip', statusLabel(item.status)));
  text.append(kicker, el('h2', 'xhero-title', item.title));
  const facts = el('div', 'xhero-facts');
  if (item.averageScore != null) {
    const score = el('span', 'xscore');
    score.append(el('span', 'xstar', '★'), document.createTextNode(` ${(item.averageScore / 10).toFixed(1)}`));
    facts.appendChild(score);
  }
  if (item.chapters != null) facts.appendChild(el('span', 'xmeta-text', `${item.chapters} chapters`));
  for (const g of (item.genres || []).slice(0, 3)) facts.appendChild(el('span', 'xchip', g));
  text.appendChild(facts);
  if (item.description) text.appendChild(el('p', 'xhero-desc', item.description));
  const actions = el('div', 'xhero-actions');
  const details = button('xbtn xbtn-primary xbtn-lg', 'Details', () => hooks.onOpen(item), `Details: ${item.title}`);
  const save = button('xbtn xbtn-lg xhero-save', '', () => hooks.onList(item, stateOf(item) === 'saved' ? null : 'saved'));
  actions.append(details, save);
  text.appendChild(actions);
  inner.appendChild(text);
  slide.append(bgLayer(item), el('div', 'xhero-shade'), inner);
  slide.xItem = item;
  return slide;
}

// Spotlight carousel of the top trending series. Returns { root, sync, stop }.
export function buildHero(items, hooks) {
  const picks = items.slice(0, MAX_SLIDES);
  if (!picks.length) return null;
  const root = el('section', 'xhero');
  root.setAttribute('aria-roledescription', 'carousel');
  root.setAttribute('aria-label', 'Spotlight');
  const slides = picks.map((item, i) => buildSlide(item, i + 1, hooks));
  const dots = el('div', 'xhero-dots');
  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  let index = 0;
  let timer = null;
  let paused = false;

  function show(next) {
    index = (next + slides.length) % slides.length;
    slides.forEach((slide, i) => {
      const active = i === index;
      slide.classList.toggle('is-active', active);
      slide.inert = !active;
      slide.setAttribute('aria-hidden', String(!active));
    });
    Array.from(dots.children).forEach((dot, i) => dot.setAttribute('aria-current', String(i === index)));
  }

  function stop() { clearInterval(timer); timer = null; }
  function start() {
    stop();
    if (reduce.matches || slides.length < 2) return;
    timer = setInterval(() => { if (!paused) show(index + 1); }, ROTATE_MS);
  }

  picks.forEach((item, i) => dots.appendChild(button('xhero-dot', '', () => { show(i); start(); }, `Show ${item.title}`)));
  root.append(...slides);
  if (slides.length > 1) root.appendChild(dots);
  const hold = value => () => { paused = value; };
  root.addEventListener('mouseenter', hold(true));
  root.addEventListener('mouseleave', hold(false));
  root.addEventListener('focusin', hold(true));
  root.addEventListener('focusout', hold(false));
  reduce.addEventListener?.('change', start);

  function sync() {
    for (const slide of slides) {
      const saved = stateOf(slide.xItem) === 'saved';
      const save = slide.querySelector('.xhero-save');
      save.textContent = saved ? '♥ Saved' : '♡ Save';
      save.setAttribute('aria-pressed', String(saved));
      save.setAttribute('aria-label', `${saved ? 'Remove from saved' : 'Save'}: ${slide.xItem.title}`);
    }
  }

  show(0);
  sync();
  start();
  return { root, sync, stop };
}
