import { el, button, getJson, isAbort, errorMessage, icon, stateBox } from './explore-util.js?v=3';
import { buildCard, buildGrid, buildSkeletons } from './explore-cards.js?v=3';

// Paged card grid with skeletons, error banner + retry, and IntersectionObserver infinite scroll.
// fetchPage(page, signal) -> { items, hasNextPage, total? }.
export function createGridLoader({ container, banner, hooks, emptyText, onPage }) {
  let generation = 0;
  let controller = null;
  let grid = null;
  let sentinel = null;
  let observer = null;
  let nextPage = 1;
  let hasNext = false;
  let loading = false;
  let fetchPage = null;
  let target = container;
  let empty = emptyText;
  let emptyIcon = 'search';

  function setBanner(error) {
    banner.replaceChildren();
    if (!error) return;
    const box = el('div', 'xerror');
    box.append(icon('alert'), el('span', null, errorMessage(error)), button('xbtn', 'Retry', () => { setBanner(null); loadMore(); }));
    banner.appendChild(box);
  }

  function stop() {
    generation++;
    controller?.abort();
    observer?.disconnect();
    loading = false;
    setBanner(null);
  }

  function reset(fetcher, options = {}) {
    stop();
    target = options.target ?? container;
    empty = options.empty ?? emptyText;
    emptyIcon = options.emptyIcon ?? 'search';
    fetchPage = fetcher;
    nextPage = 1;
    hasNext = true;
    grid = buildGrid();
    grid.setAttribute('aria-busy', 'true');
    sentinel = el('div', 'xsentinel');
    grid.appendChild(buildSkeletons());
    target.replaceChildren(grid, sentinel);
    observer = new IntersectionObserver(entries => {
      if (entries.some(e => e.isIntersecting)) loadMore();
    }, { rootMargin: '600px' });
    observer.observe(sentinel);
    loadMore(); // the first page never waits for the sentinel (skeletons can push it off-screen)
  }

  async function loadMore() {
    if (loading || !hasNext || !fetchPage) return;
    loading = true;
    const mine = generation;
    const page = nextPage;
    controller = new AbortController();
    try {
      const result = await fetchPage(page, controller.signal);
      if (mine !== generation) return;
      grid.querySelectorAll('.xskeleton').forEach(n => n.remove());
      grid.removeAttribute('aria-busy');
      for (const item of result.items || []) grid.appendChild(buildCard(item, hooks));
      hasNext = Boolean(result.hasNextPage);
      nextPage = page + 1;
      onPage?.(result, page);
      if (page === 1 && !grid.childElementCount) {
        target.replaceChildren(stateBox(emptyIcon, empty));
        return;
      }
      // A short, filtered page can leave the sentinel visible without re-triggering the observer.
      loading = false;
      if (hasNext) requestAnimationFrame(() => { if (mine === generation && sentinel.getBoundingClientRect().top < innerHeight + 600) loadMore(); });
    } catch (error) {
      if (isAbort(error) || mine !== generation) return;
      grid.querySelectorAll('.xskeleton').forEach(n => n.remove());
      setBanner(error);
    } finally {
      if (mine === generation) loading = false;
    }
  }

  const listFetcher = (path, params) => (page, signal) => {
    const p = new URLSearchParams(params);
    p.set('page', String(page));
    return getJson(`${path}?${p}`, signal);
  };

  return { reset, stop, listFetcher, loadMore };
}
