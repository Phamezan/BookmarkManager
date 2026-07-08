// Recommendation row swipe-dismiss (drag + inertia) and "feature" absorb
// motion. GSAP's Draggable ignores clickable descendants (buttons, links)
// by default, so the row-level drag handle doesn't fight the existing
// Visit/Feature/Archive/Delete icon buttons.
const DISMISS_THRESHOLD_RATIO = 0.3;

// Wires drag-to-dismiss on every `.rec-more-row` under the given container.
// Past the threshold the row continues along the drag vector with inertia,
// fading and rotating out; on release it resolves with the drag direction
// so the caller can sequence the real (C#-side) removal after the
// animation finishes. Below threshold it springs back and resolves null.
window.initRecommendationSwipe = function (containerSelector, dotNetRef) {
    const container = document.querySelector(containerSelector);
    if (!container || !window.gsap || !window.Draggable) return;
    if (container.dataset.swipeBound) return;
    container.dataset.swipeBound = 'true';

    if (window.InertiaPlugin) gsap.registerPlugin(Draggable, InertiaPlugin);
    else gsap.registerPlugin(Draggable);

    container.querySelectorAll('.rec-more-row').forEach(bindRow);

    // Rows are re-rendered by Blazor's diffing on every list change; rebind
    // whenever the DOM underneath actually changes (dedup via a bound flag
    // per row so this is cheap even if called on every mutation).
    const observer = new MutationObserver(() => {
        container.querySelectorAll('.rec-more-row').forEach(bindRow);
    });
    observer.observe(container, { childList: true, subtree: true });

    function bindRow(row) {
        if (row.dataset.swipeBound) return;
        row.dataset.swipeBound = 'true';

        Draggable.create(row, {
            type: 'x',
            inertia: !!window.InertiaPlugin,
            onDragEnd: function () {
                const width = row.offsetWidth;
                const traveled = Math.abs(this.x);
                if (traveled < width * DISMISS_THRESHOLD_RATIO) {
                    gsap.to(row, { x: 0, rotation: 0, duration: 0.32, ease: 'back.out(1.6)', overwrite: 'auto' });
                    return;
                }

                const direction = this.x > 0 ? 1 : -1;
                const id = row.dataset.dismissId;
                gsap.to(row, {
                    x: direction * (window.innerWidth * 0.6),
                    rotation: direction * 8,
                    opacity: 0,
                    duration: 0.38,
                    ease: 'power1.in',
                    overwrite: 'auto',
                    onComplete: () => {
                        row.style.pointerEvents = 'none';
                        if (id && dotNetRef) dotNetRef.invokeMethodAsync('OnRowSwipeDismissed', id);
                    }
                });
            }
        });
    }
};

// Called by C# after a dismiss/skip is confirmed via a button (not drag) —
// same visual treatment, resolves once the animation completes so the
// caller can await it before removing the row from Blazor state (avoids
// Blazor yanking the element mid-flight).
window.dismissRecommendationRow = function (rowEl, direction) {
    return new Promise((resolve) => {
        if (!rowEl || !window.gsap) { resolve(); return; }
        const dir = direction === 'left' ? -1 : 1;
        gsap.to(rowEl, {
            x: dir * (window.innerWidth * 0.6),
            rotation: dir * 8,
            opacity: 0,
            duration: 0.38,
            ease: 'power1.in',
            overwrite: 'auto',
            onComplete: resolve
        });
    });
};

// "Feature this pick" — the row shrinks and glides toward the spotlight
// card's position, communicating "this moved up there" before Blazor swaps
// the spotlight. Resolves once the animation completes.
window.absorbRecommendationRow = function (id, targetSelector) {
    return new Promise((resolve) => {
        const rowEl = document.querySelector(`.rec-more-row[data-dismiss-id="${id}"]`);
        const target = document.querySelector(targetSelector);
        if (!rowEl || !target || !window.gsap) { resolve(); return; }

        const from = rowEl.getBoundingClientRect();
        const to = target.getBoundingClientRect();
        const dx = (to.left + to.width / 2) - (from.left + from.width / 2);
        const dy = (to.top + to.height / 2) - (from.top + from.height / 2);

        gsap.to(rowEl, {
            x: dx,
            y: dy,
            scale: 0.4,
            opacity: 0,
            duration: 0.38,
            ease: 'power2.in',
            overwrite: 'auto',
            onComplete: resolve
        });
    });
};
