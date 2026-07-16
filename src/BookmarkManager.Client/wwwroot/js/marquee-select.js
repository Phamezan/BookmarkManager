// Drag-rectangle (marquee) multi-select for the bookmark card grid (.bookmark-list).
// Note (virtualize v1): only currently rendered/virtualized cards are hit-tested —
// cards scrolled outside the Virtualize overscan window are not included.
//
// Shift+drag (or Ctrl/Meta+drag) starts a marquee even when the gesture begins on top of
// a card, not just empty background — lets the user rubber-band-select without hunting for
// a gap between cards. Without a modifier held, drags starting on a card are left alone so
// the card's own click/native-drag behavior still works. Native HTML5 drag (cards have
// draggable="true") is suppressed for the duration of a shift/ctrl-drag so the browser
// doesn't fight the marquee for the same pointer gesture.
window.BookmarkMarqueeSelect = {
    attach: function (containerEl, dotNetRef) {
        if (!containerEl) {
            return { disconnect: function () { } };
        }

        var HARD_IGNORE_SELECTOR = 'a, button, input, textarea, .bm-checkbox, .mud-button-root';
        var THRESHOLD_PX = 5;

        var pointerId = null;
        var startClientX = 0;
        var startClientY = 0;
        var active = false;
        var additive = false;
        var suppressCardDrag = false;
        var modifierHeldAtStart = false;
        var onCardAtStart = false;
        var rectEl = null;

        function onPointerDown(e) {
            if (e.button !== 0) return;
            if (e.target.closest(HARD_IGNORE_SELECTOR)) return;

            var modifierHeld = e.shiftKey || e.ctrlKey || e.metaKey;
            var onCard = !!e.target.closest('.bookmark-card');
            if (!modifierHeld && onCard) return;

            pointerId = e.pointerId;
            startClientX = e.clientX;
            startClientY = e.clientY;
            active = false;
            additive = e.ctrlKey || e.metaKey;
            modifierHeldAtStart = modifierHeld;
            onCardAtStart = onCard;
            suppressCardDrag = e.shiftKey && onCard;
            if (suppressCardDrag) {
                containerEl.addEventListener('dragstart', onDragStartPrevent, true);
            }

            containerEl.addEventListener('pointermove', onPointerMove);
            containerEl.addEventListener('pointerup', onPointerUp);
            containerEl.addEventListener('pointercancel', onPointerCancel);
        }

        function onDragStartPrevent(e) {
            if (suppressCardDrag) {
                e.preventDefault();
                e.stopPropagation();
            }
        }

        function activate() {
            active = true;
            containerEl.style.userSelect = 'none';
            rectEl = document.createElement('div');
            rectEl.className = 'bookmark-marquee-rect';
            containerEl.appendChild(rectEl);
            try {
                if (pointerId !== null && containerEl.setPointerCapture) {
                    containerEl.setPointerCapture(pointerId);
                }
            } catch (err) {
                // Pointer may already be released; ignore.
            }
        }

        function updateRect(e) {
            var containerRect = containerEl.getBoundingClientRect();
            var x1 = startClientX;
            var y1 = startClientY;
            var x2 = e.clientX;
            var y2 = e.clientY;

            var left = Math.min(x1, x2) - containerRect.left + containerEl.scrollLeft;
            var top = Math.min(y1, y2) - containerRect.top + containerEl.scrollTop;
            var width = Math.abs(x2 - x1);
            var height = Math.abs(y2 - y1);

            rectEl.style.left = left + 'px';
            rectEl.style.top = top + 'px';
            rectEl.style.width = width + 'px';
            rectEl.style.height = height + 'px';

            var marqueeClientRect = {
                left: Math.min(x1, x2),
                top: Math.min(y1, y2),
                right: Math.max(x1, x2),
                bottom: Math.max(y1, y2)
            };

            var cards = containerEl.querySelectorAll('.bookmark-card');
            for (var i = 0; i < cards.length; i++) {
                var card = cards[i];
                var cardRect = card.getBoundingClientRect();
                var intersects = cardRect.left < marqueeClientRect.right &&
                    cardRect.right > marqueeClientRect.left &&
                    cardRect.top < marqueeClientRect.bottom &&
                    cardRect.bottom > marqueeClientRect.top;
                card.classList.toggle('is-marquee-hit', intersects);
            }
        }

        function collectHitIds() {
            var ids = [];
            var hitCards = containerEl.querySelectorAll('.bookmark-card.is-marquee-hit');
            for (var i = 0; i < hitCards.length; i++) {
                var id = hitCards[i].id || '';
                var prefix = 'bookmark-card-';
                if (id.indexOf(prefix) === 0) {
                    ids.push(id.substring(prefix.length));
                }
            }
            return ids;
        }

        function cleanupVisuals() {
            if (rectEl && rectEl.parentNode) {
                rectEl.parentNode.removeChild(rectEl);
            }
            rectEl = null;
            containerEl.style.userSelect = '';
            var hitCards = containerEl.querySelectorAll('.bookmark-card.is-marquee-hit');
            for (var i = 0; i < hitCards.length; i++) {
                hitCards[i].classList.remove('is-marquee-hit');
            }
        }

        function detachMoveListeners() {
            containerEl.removeEventListener('pointermove', onPointerMove);
            containerEl.removeEventListener('pointerup', onPointerUp);
            containerEl.removeEventListener('pointercancel', onPointerCancel);
            containerEl.removeEventListener('dragstart', onDragStartPrevent, true);
            suppressCardDrag = false;
            try {
                if (pointerId !== null && containerEl.releasePointerCapture) {
                    containerEl.releasePointerCapture(pointerId);
                }
            } catch (err) {
                // Pointer capture may already be released; ignore.
            }
            pointerId = null;
        }

        function onPointerMove(e) {
            if (!active) {
                var dx = e.clientX - startClientX;
                var dy = e.clientY - startClientY;
                if (Math.abs(dx) < THRESHOLD_PX && Math.abs(dy) < THRESHOLD_PX) {
                    return;
                }
                activate();
            }
            updateRect(e);
        }

        function finish(wasCancelled) {
            var wasActive = active;
            var wasPlainBackgroundClick = !wasActive && !wasCancelled && !modifierHeldAtStart && !onCardAtStart;
            var hitIds = wasActive && !wasCancelled ? collectHitIds() : [];
            cleanupVisuals();
            detachMoveListeners();
            active = false;

            if (wasActive && !wasCancelled) {
                dotNetRef.invokeMethodAsync('OnMarqueeSelectCompleted', hitIds, additive);
            } else if (wasPlainBackgroundClick) {
                // Unmodified click on empty grid background (never became a drag) —
                // cancels/clears any existing multi-selection.
                dotNetRef.invokeMethodAsync('OnMarqueeBackgroundClick');
            }
        }

        function onPointerUp() {
            finish(false);
        }

        function onPointerCancel() {
            finish(true);
        }

        containerEl.addEventListener('pointerdown', onPointerDown);

        return {
            disconnect: function () {
                containerEl.removeEventListener('pointerdown', onPointerDown);
                detachMoveListeners();
                cleanupVisuals();
            }
        };
    }
};
