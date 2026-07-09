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

// Hoist popover to body on hover to bypass transform boundary containing blocks
window.initCalendarPopups = function (containerSelector) {
    const container = document.querySelector(containerSelector);
    if (!container || container.dataset.popupsInit) return;
    container.dataset.popupsInit = 'true';

    let activeTip = null;
    let originalParent = null;

    container.addEventListener('mouseover', (e) => {
        const eventEl = e.target.closest('.acal-month-event');
        if (!eventEl) return;

        const tipEl = eventEl.querySelector('.rich-tip');
        if (!tipEl) return;

        // If another tip is currently active, restore it immediately
        if (activeTip && activeTip !== tipEl) {
            activeTip.classList.remove('is-visible');
            originalParent.appendChild(activeTip);
            activeTip.style.display = '';
        }

        activeTip = tipEl;
        originalParent = eventEl;

        // Position fixed relative to viewport
        tipEl.style.position = 'fixed';
        tipEl.style.display = 'flex';
        tipEl.style.opacity = '0';
        document.body.appendChild(tipEl);

        const eventRect = eventEl.getBoundingClientRect();
        const tipWidth = tipEl.offsetWidth || 440; // Measured dynamic width
        const tipHeight = tipEl.offsetHeight || 270; // Measured height

        // Center horizontally above the hovered item
        let left = eventRect.left + eventRect.width / 2 - tipWidth / 2;
        let top = eventRect.top - tipHeight - 8;

        // Collision check: Left/Right boundaries
        const margin = 16;
        if (left < margin) {
            left = margin;
        } else if (left + tipWidth > window.innerWidth - margin) {
            left = window.innerWidth - tipWidth - margin;
        }

        // Collision check: Top boundary (flip downward if it would go off-screen)
        if (top < margin) {
            top = eventRect.bottom + 8;
        }

        // Set live pixel coordinates
        tipEl.style.left = left + 'px';
        tipEl.style.top = top + 'px';

        // Force browser reflow to apply transitions
        tipEl.getBoundingClientRect();
        tipEl.classList.add('is-visible');
        tipEl.style.opacity = ''; // Let CSS transition handle opacity
    }, true);

    container.addEventListener('mouseout', (e) => {
        const eventEl = e.target.closest('.acal-month-event');
        if (!eventEl || !activeTip) return;

        const related = e.relatedTarget;
        if (related && eventEl.contains(related)) return;

        const tipToRestore = activeTip;
        const parentToRestore = originalParent;

        tipToRestore.classList.remove('is-visible');

        // Restore back to original cell DOM parent after fade transition completes
        setTimeout(() => {
            if (tipToRestore.parentNode === document.body && !tipToRestore.classList.contains('is-visible')) {
                parentToRestore.appendChild(tipToRestore);
                tipToRestore.style.display = '';
            }
        }, 150);

        activeTip = null;
        originalParent = null;
    }, true);
};


