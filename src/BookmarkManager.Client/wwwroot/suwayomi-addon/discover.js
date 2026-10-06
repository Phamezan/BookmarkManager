const API = '/bm-api/discover';
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

const content = document.getElementById('content');
const statusEl = document.getElementById('status');
const weekLabelEl = document.getElementById('week-label');
const weekRangeEl = document.getElementById('week-range');
const olderBtn = document.getElementById('older');
const newerBtn = document.getElementById('newer');
const hideInput = document.getElementById('hide-library');
const chips = Array.from(document.querySelectorAll('.chip'));

const state = { week: 0, type: 'all', hide: false };
let pollTimer = null;
let hasOlder = false;

function init() {
  const params = new URLSearchParams(location.search);
  const week = parseInt(params.get('week') || '0', 10);
  state.week = Number.isFinite(week) && week > 0 ? week : 0;

  const type = (params.get('type') || 'all').toLowerCase();
  state.type = ['all', 'manhwa', 'manhua', 'manga'].includes(type) ? type : 'all';
  state.hide = params.get('hide') === '1';

  hideInput.checked = state.hide;
  for (const chip of chips) {
    chip.setAttribute('aria-checked', String(chip.dataset.type === state.type));
  }

  bindEvents();
  updatePager(false);
  bootstrap();
}

function bindEvents() {
  for (const chip of chips) {
    chip.addEventListener('click', () => {
      if (state.type === chip.dataset.type) return;
      state.type = chip.dataset.type;
      for (const c of chips) c.setAttribute('aria-checked', String(c === chip));
      applyState();
    });
  }

  hideInput.addEventListener('change', () => {
    state.hide = hideInput.checked;
    applyState();
  });

  olderBtn.addEventListener('click', () => { if (hasOlder) changeWeek(state.week + 1); });
  newerBtn.addEventListener('click', () => { if (state.week > 0) changeWeek(state.week - 1); });

  document.addEventListener('keydown', (event) => {
    if (event.target instanceof HTMLInputElement) return;
    if (event.key === '[') { if (hasOlder) changeWeek(state.week + 1); }
    else if (event.key === ']') { if (state.week > 0) changeWeek(state.week - 1); }
  });
}

function changeWeek(week) {
  state.week = Math.max(0, week);
  applyState();
}

function applyState() {
  const params = new URLSearchParams();
  if (state.week > 0) params.set('week', String(state.week));
  if (state.type !== 'all') params.set('type', state.type);
  if (state.hide) params.set('hide', '1');
  const query = params.toString();
  history.replaceState(null, '', query ? `?${query}` : location.pathname);
  load();
}

async function bootstrap() {
  renderSkeleton();
  try {
    const status = await fetchJson(`${API}/status`);
    updateStatusLine(status);
    if (!status.lastRunAt) {
      renderBuilding();
      startPolling();
      return;
    }
    await load();
  } catch {
    renderError();
  }
}

function startPolling() {
  if (pollTimer) return;
  pollTimer = setInterval(async () => {
    try {
      const status = await fetchJson(`${API}/status`);
      updateStatusLine(status);
      if (status.lastRunAt) {
        clearInterval(pollTimer);
        pollTimer = null;
        await load();
      }
    } catch {
      /* keep polling */
    }
  }, 15000);
}

async function load() {
  renderSkeleton();
  try {
    const feed = await fetchJson(`${API}?week=${state.week}&type=${state.type}&hideLibrary=${state.hide}`);
    renderFeed(feed);
    updateStatusLine({ lastRunAt: feed.lastRunAt, running: false });
  } catch {
    renderError();
  }
}

async function fetchJson(url) {
  const response = await fetch(url, { cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

function updateStatusLine(status) {
  if (!status) { statusEl.textContent = ''; return; }
  if (status.running) { statusEl.textContent = 'Updating…'; return; }
  if (!status.lastRunAt) { statusEl.textContent = ''; return; }
  const diff = (Date.now() - new Date(status.lastRunAt).getTime()) / 60000;
  if (diff < 1) statusEl.textContent = 'Updated just now';
  else if (diff < 60) statusEl.textContent = `Updated ${Math.floor(diff)} min ago`;
  else statusEl.textContent = `Updated ${Math.floor(diff / 60)} h ago`;
}

function renderFeed(feed) {
  hasOlder = Boolean(feed.hasOlder);
  updatePager(hasOlder);
  weekLabelEl.textContent = weekLabel(state.week);
  weekRangeEl.textContent = formatRange(feed.weekStart, feed.weekEnd);

  if (!feed.items || feed.items.length === 0) {
    renderEmpty();
    return;
  }

  const grid = document.createElement('div');
  grid.className = 'grid';
  for (const item of feed.items) grid.appendChild(buildCard(item));
  content.replaceChildren(grid);
}

function updatePager(enabled) {
  olderBtn.disabled = !enabled;
  newerBtn.disabled = state.week === 0;
}

function buildCard(item) {
  const card = document.createElement('article');
  card.className = 'card';

  const cover = document.createElement('a');
  cover.className = 'cover-wrap';
  cover.href = `/manga/${item.seriesMangaId}`;

  const img = document.createElement('img');
  img.loading = 'lazy';
  img.alt = `${item.title} cover`;
  img.src = `/api/v1/manga/${item.coverMangaId}/thumbnail`;
  cover.appendChild(img);

  if (item.type && item.type !== 'Unknown') {
    const typeBadge = document.createElement('span');
    typeBadge.className = 'type-badge';
    typeBadge.textContent = item.type.toUpperCase();
    cover.appendChild(typeBadge);
  }

  if (item.progress) {
    const badge = document.createElement('span');
    badge.className = 'progress-badge';
    if (item.progress.read >= item.progress.latest) badge.classList.add('caught-up');
    badge.textContent = `${item.progress.read}/${item.progress.latest}`;
    badge.title = `Read ${item.progress.read} of ${item.progress.latest}`;
    cover.appendChild(badge);
  }

  card.appendChild(cover);

  const title = document.createElement('a');
  title.className = 'card-title';
  title.href = `/manga/${item.seriesMangaId}`;
  title.title = item.title;
  title.textContent = item.title;
  card.appendChild(title);

  const sources = document.createElement('div');
  sources.className = 'card-sources';
  sources.textContent = (item.sources || []).join(' · ');
  card.appendChild(sources);

  const list = document.createElement('ul');
  list.className = 'chapter-list';
  for (const chapter of item.chapters || []) {
    list.appendChild(buildChapterRow(chapter));
  }
  card.appendChild(list);

  return card;
}

function buildChapterRow(chapter) {
  const li = document.createElement('li');

  const link = document.createElement('a');
  link.className = 'chapter-link';
  link.href = `/manga/${chapter.mangaId}/chapter/${chapter.sourceOrder}`;
  link.textContent = `Ch ${formatNumber(chapter.number)}`;
  li.appendChild(link);

  if (chapter.isNew) {
    const pill = document.createElement('span');
    pill.className = 'new-pill';
    pill.textContent = 'New';
    li.appendChild(pill);
  } else {
    const date = document.createElement('span');
    date.className = 'chapter-date';
    date.textContent = formatDate(chapter.uploadedAt);
    li.appendChild(date);
  }

  return li;
}

function renderSkeleton() {
  const grid = document.createElement('div');
  grid.className = 'grid';
  for (let i = 0; i < 12; i++) {
    const card = document.createElement('article');
    card.className = 'card skeleton';
    const cover = document.createElement('div');
    cover.className = 'sk-cover';
    const bar1 = document.createElement('div');
    bar1.className = 'sk-bar';
    const bar2 = document.createElement('div');
    bar2.className = 'sk-bar short';
    card.append(cover, bar1, bar2);
    grid.appendChild(card);
  }
  content.replaceChildren(grid);
}

function renderEmpty() {
  const div = document.createElement('div');
  div.className = 'state';
  div.textContent = state.type === 'all'
    ? 'No Action series updated in this period.'
    : `No ${capitalize(state.type)} updated in this period.`;
  content.replaceChildren(div);
}

function renderError() {
  const div = document.createElement('div');
  div.className = 'state';
  const message = document.createElement('div');
  message.textContent = "Couldn't load Discover. Check that BookmarkManager is running.";
  const retry = document.createElement('button');
  retry.type = 'button';
  retry.textContent = 'Retry';
  retry.addEventListener('click', () => bootstrap());
  div.append(message, retry);
  content.replaceChildren(div);
}

function renderBuilding() {
  const div = document.createElement('div');
  div.className = 'state';
  div.textContent = 'Building your Discover feed — this takes a few minutes the first time.';
  content.replaceChildren(div);
}

function weekLabel(week) {
  if (week === 0) return 'This week';
  if (week === 1) return 'Last week';
  return `${week} weeks ago`;
}

function formatRange(startIso, endIso) {
  const start = new Date(startIso);
  const end = new Date(endIso);
  return `${start.getUTCDate()} ${MONTHS[start.getUTCMonth()]} – ${end.getUTCDate()} ${MONTHS[end.getUTCMonth()]}`;
}

function formatDate(iso) {
  const date = new Date(iso);
  const base = `${date.getUTCDate()} ${MONTHS[date.getUTCMonth()]}`;
  return date.getUTCFullYear() === new Date().getUTCFullYear()
    ? base
    : `${base} ${date.getUTCFullYear()}`;
}

function formatNumber(value) {
  return (Math.round(value * 100) / 100).toString();
}

function capitalize(value) {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

init();
