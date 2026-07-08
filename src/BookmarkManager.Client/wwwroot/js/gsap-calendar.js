// Anime calendar column/card stagger-load (GSAP). All three views (Month/
// Week/Day) share one entry point — pass the container, the selector for
// the items to stagger, and a signature string. The 1s countdown ticker in
// each view's code-behind calls StateHasChanged constantly, which re-runs
// OnAfterRenderAsync every second — the signature check is what stops the
// stagger from replaying on every tick instead of only on a real dataset/
// view change.
window.animateCalendarStagger = function (containerSelector, itemSelector, signature) {
    const container = document.querySelector(containerSelector);
    if (!container) return;

    if (container.dataset.lastCalSignature === signature) return;
    container.dataset.lastCalSignature = signature;

    const items = Array.from(container.querySelectorAll(itemSelector));
    if (!items.length) return;

    if (!window.gsap) {
        items.forEach(el => { el.style.opacity = 1; el.style.transform = 'none'; });
        return;
    }

    gsap.killTweensOf(items);
    gsap.fromTo(items,
        { opacity: 0, y: 12 },
        {
            opacity: 1,
            y: 0,
            duration: 0.28,
            ease: 'power2.out',
            stagger: { each: 0.04, from: 'start' },
            overwrite: 'auto'
        }
    );
};

// Cursor-tracked 3D tilt for populated month cells / episode cards — same
// rAF-throttled delegation pattern as gsap-cards.js.
window.initCalendarTilt = function (containerSelector, itemSelector) {
    const container = document.querySelector(containerSelector);
    if (!container || container.dataset.tiltDelegated) return;
    container.dataset.tiltDelegated = 'true';

    let raf = null;
    let pendingEvent = null;

    container.addEventListener('mousemove', (e) => {
        pendingEvent = e;
        if (raf) return;
        raf = requestAnimationFrame(() => {
            const evt = pendingEvent;
            raf = null;
            const item = evt.target.closest(itemSelector);
            if (!item || !window.gsap) return;
            const rect = item.getBoundingClientRect();
            const px = (evt.clientX - rect.left) / rect.width - 0.5;
            const py = (evt.clientY - rect.top) / rect.height - 0.5;
            gsap.to(item, {
                rotateX: py * -6,
                rotateY: px * 6,
                scale: 1.02,
                duration: 0.2,
                ease: 'power2.out',
                overwrite: 'auto',
                transformPerspective: 800
            });
        });
    });

    container.addEventListener('mouseleave', (e) => {
        const item = e.target.closest && e.target.closest(itemSelector);
        if (!item || !window.gsap) return;
        gsap.to(item, { rotateX: 0, rotateY: 0, scale: 1, duration: 0.28, ease: 'back.out(1.4)', overwrite: 'auto' });
    }, true);
};
