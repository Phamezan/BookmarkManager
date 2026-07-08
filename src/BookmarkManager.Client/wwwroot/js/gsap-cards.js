// Bookmark card entrance stagger + cursor-tracked hover bloom (GSAP).
// Cards are `display: contents` wrapped (see .bookmark-grid), and Blazor
// re-renders the grid on every filter/search/sort change — so we key each
// pass by a signature of the visible card ids and skip re-running the
// entrance timeline when nothing actually changed under the hood.
window.animateBookmarkGrid = function (gridSelector) {
    const grid = document.querySelector(gridSelector || '.bookmark-grid');
    if (!grid) return;
    const cards = Array.from(grid.querySelectorAll('.bookmark-card'));
    if (!cards.length) return;

    if (!window.gsap) {
        cards.forEach(c => c.classList.add('bookmark-card--fallback-in'));
        return;
    }

    const signature = cards.map(c => c.dataset.dragId || '').join('|');
    if (grid.dataset.lastAnimatedSignature === signature) return;
    grid.dataset.lastAnimatedSignature = signature;

    gsap.killTweensOf(cards);
    gsap.fromTo(cards,
        { opacity: 0, y: 10, scale: 0.98 },
        {
            opacity: 1,
            y: 0,
            scale: 1,
            duration: 0.32,
            ease: 'back.out(1.2)',
            stagger: { each: 0.03, from: 'start' },
            overwrite: 'auto'
        }
    );
};

// Delegate one mousemove listener at the grid level instead of one per card
// — cheaper for large grids, and new cards picked up automatically since we
// resolve the target from event.target.closest() on every move.
window.initBookmarkGridBloom = function (gridSelector) {
    const grid = document.querySelector(gridSelector || '.bookmark-grid');
    if (!grid || grid.dataset.bloomDelegated) return;
    grid.dataset.bloomDelegated = 'true';

    let raf = null;
    let pendingEvent = null;
    grid.addEventListener('mousemove', (e) => {
        pendingEvent = e;
        if (raf) return;
        raf = requestAnimationFrame(() => {
            const evt = pendingEvent;
            raf = null;
            const card = evt.target.closest('.bookmark-card');
            if (!card) return;
            const rect = card.getBoundingClientRect();
            card.style.setProperty('--mx', ((evt.clientX - rect.left) / rect.width) * 100 + '%');
            card.style.setProperty('--my', ((evt.clientY - rect.top) / rect.height) * 100 + '%');
        });
    });
};
