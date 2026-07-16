// Bookmark card entrance stagger + cursor-tracked hover bloom (GSAP).
// BookmarkList uses Blazor Virtualize, which renders MULTIPLE `.bookmark-grid`
// row containers. Always query under `.bookmark-list` so every visible card
// gets entrance + bloom — querySelector('.bookmark-grid') alone only hit the
// first row and left the rest without colorful hover bloom.
window.animateBookmarkGrid = function (gridSelector) {
    if (document.documentElement.classList.contains('bm-display-repair')) return;
    const root = document.querySelector('.bookmark-list') || document.querySelector(gridSelector || '.bookmark-grid');
    if (!root) return;

    const cards = Array.from(root.querySelectorAll('.bookmark-card'));
    if (!cards.length) return;

    if (!window.gsap) {
        cards.forEach(c => c.classList.add('bookmark-card--fallback-in'));
        return;
    }

    const signature = cards.map(c => c.dataset.dragId || c.id || '').join('|');
    if (root.dataset.lastAnimatedSignature === signature) return;
    root.dataset.lastAnimatedSignature = signature;

    // Clear any stuck opacity from a prior interrupted tween (folder navigate
    // mid-animation used to leave cards invisible until the next interaction).
    gsap.killTweensOf(cards);
    gsap.set(cards, { clearProps: 'opacity,transform' });

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

// Delegate one mousemove listener on the list container so every virtualized
// row picks up the cursor bloom, not just the first `.bookmark-grid`.
window.initBookmarkGridBloom = function (gridSelector) {
    const root = document.querySelector('.bookmark-list') || document.querySelector(gridSelector || '.bookmark-grid');
    if (!root || root.dataset.bloomDelegated) return;
    root.dataset.bloomDelegated = 'true';

    let raf = null;
    let pendingEvent = null;
    root.addEventListener('mousemove', (e) => {
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
