// Library Browse: light ScrollTrigger scrub + infinite-scroll sentinel.
// Optimize: no scrollTop restore, no ScrollTrigger.refresh on soft path, scrub:true (no catch-up lag),
// bind new cards on idle scroll only, edge-triggered load-more.
(function () {
    'use strict';

    var scrubByKey = Object.create(null);
    var infiniteObserver = null;
    var infiniteDotNetRef = null;
    var infiniteSentinel = null;
    var infiniteEnabled = false;
    var loadInFlight = false;
    var fillTimer = null;
    var scrollBindRaf = null;
    var scrollBound = false;
    var reducedMotion = false;

    try {
        reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch (_) { /* ignore */ }

    function getAppScroller() {
        return document.querySelector('.app-content') || document.documentElement;
    }

    function scrubDisabled() {
        return reducedMotion
            || document.documentElement.classList.contains('bm-display-repair')
            || !window.gsap
            || !window.ScrollTrigger;
    }

    function killOne(key) {
        var entry = scrubByKey[key];
        if (!entry) return;
        try { if (entry.trigger) entry.trigger.kill(); } catch (_) { /* disposed */ }
        try { if (entry.tween) entry.tween.kill(); } catch (_) { /* disposed */ }
        delete scrubByKey[key];
    }

    function killScrub() {
        Object.keys(scrubByKey).forEach(killOne);
    }

    function bindCardScrub(card, scroller) {
        var key = card.getAttribute('data-lib-key');
        if (!key || scrubByKey[key]) return;

        var scrollerBottom = window.innerHeight;
        try {
            if (scroller && scroller.getBoundingClientRect) {
                scrollerBottom = scroller.getBoundingClientRect().bottom;
            }
        } catch (_) { /* ignore */ }

        var rect = card.getBoundingClientRect();
        // Already visible — mark seen, no tween (avoids fighting paint mid-scroll).
        if (rect.top < scrollerBottom * 0.95) {
            scrubByKey[key] = { trigger: null, tween: null };
            return;
        }

        // Direct scrub (true) = playhead tracks scroll 1:1 — no laggy catch-up feel.
        gsap.set(card, { y: 20 });
        var tween = gsap.to(card, {
            y: 0,
            ease: 'none',
            scrollTrigger: {
                trigger: card,
                scroller: scroller,
                start: 'top 95%',
                end: 'top 70%',
                scrub: true
            }
        });
        scrubByKey[key] = { trigger: tween.scrollTrigger || null, tween: tween };
    }

    function refreshLibraryScrub(browseSelector) {
        var root = document.querySelector(browseSelector || '.lib-browse');
        if (!root || scrubDisabled()) {
            if (scrubDisabled()) killScrub();
            return;
        }

        gsap.registerPlugin(ScrollTrigger);

        var scroller = getAppScroller();
        var cards = root.querySelectorAll('.lib-card');
        var live = Object.create(null);

        for (var i = 0; i < cards.length; i++) {
            var card = cards[i];
            var key = card.getAttribute('data-lib-key');
            if (!key) continue;
            live[key] = true;
            bindCardScrub(card, scroller);
        }

        Object.keys(scrubByKey).forEach(function (key) {
            if (!live[key]) killOne(key);
        });
    }

    function resetLibraryScrub(browseSelector) {
        killScrub();
        var root = document.querySelector(browseSelector || '.lib-browse');
        if (root && window.gsap) {
            gsap.set(root.querySelectorAll('.lib-card'), { clearProps: 'transform' });
        }
        refreshLibraryScrub(browseSelector);
    }

    function onScrollerScroll() {
        if (scrollBindRaf) return;
        scrollBindRaf = requestAnimationFrame(function () {
            scrollBindRaf = null;
            refreshLibraryScrub('.lib-browse');
        });
    }

    function ensureScrollBind() {
        if (scrollBound) return;
        var scroller = getAppScroller();
        if (!scroller || scroller === document.documentElement) {
            window.addEventListener('scroll', onScrollerScroll, { passive: true });
        } else {
            scroller.addEventListener('scroll', onScrollerScroll, { passive: true });
        }
        scrollBound = true;
    }

    function disconnectInfinite() {
        if (fillTimer) {
            clearTimeout(fillTimer);
            fillTimer = null;
        }
        if (infiniteObserver) {
            try { infiniteObserver.disconnect(); } catch (_) { /* ignore */ }
            infiniteObserver = null;
        }
        infiniteDotNetRef = null;
        infiniteSentinel = null;
        infiniteEnabled = false;
        loadInFlight = false;
    }

    function requestLoadMore() {
        if (!infiniteEnabled || !infiniteDotNetRef || loadInFlight) return;
        loadInFlight = true;
        infiniteEnabled = false;
        try {
            var p = infiniteDotNetRef.invokeMethodAsync('OnBrowseNearEnd');
            if (p && typeof p.then === 'function') {
                p.then(function () { loadInFlight = false; }, function () { loadInFlight = false; });
            } else {
                loadInFlight = false;
            }
        } catch (_) {
            loadInFlight = false;
        }
    }

    function sentinelStillInRange() {
        if (!infiniteSentinel) return false;
        var scroller = getAppScroller();
        var bottom = window.innerHeight;
        try {
            if (scroller && scroller.getBoundingClientRect) {
                bottom = scroller.getBoundingClientRect().bottom;
            }
        } catch (_) { /* ignore */ }
        return infiniteSentinel.getBoundingClientRect().top <= bottom + 80;
    }

    function scheduleFillIfNeeded() {
        if (fillTimer) clearTimeout(fillTimer);
        // Longer settle so we don't stack fetches while Virtualize is still laying out.
        fillTimer = setTimeout(function () {
            fillTimer = null;
            if (!infiniteEnabled || loadInFlight) return;
            if (sentinelStillInRange()) requestLoadMore();
        }, 500);
    }

    function attachLibraryInfiniteScroll(sentinelEl, dotNetRef) {
        disconnectInfinite();
        if (!sentinelEl || !dotNetRef) return;

        infiniteDotNetRef = dotNetRef;
        infiniteSentinel = sentinelEl;
        infiniteEnabled = true;
        loadInFlight = false;
        ensureScrollBind();

        var scroller = getAppScroller();
        var root = scroller === document.documentElement ? null : scroller;
        var wasVisible = false;

        infiniteObserver = new IntersectionObserver(function (entries) {
            for (var i = 0; i < entries.length; i++) {
                var visible = entries[i].isIntersecting;
                if (visible && !wasVisible) requestLoadMore();
                wasVisible = visible;
                break;
            }
        }, { root: root, rootMargin: '80px 0px', threshold: 0 });

        infiniteObserver.observe(sentinelEl);
    }

    function setLibraryInfiniteScrollEnabled(enabled) {
        infiniteEnabled = !!enabled;
        if (!enabled) return;
        loadInFlight = false;
        scheduleFillIfNeeded();
    }

    function disposeLibraryBrowse() {
        killScrub();
        disconnectInfinite();
        if (scrollBound) {
            var scroller = getAppScroller();
            if (scroller && scroller !== document.documentElement) {
                scroller.removeEventListener('scroll', onScrollerScroll);
            } else {
                window.removeEventListener('scroll', onScrollerScroll);
            }
            scrollBound = false;
        }
        if (scrollBindRaf) {
            cancelAnimationFrame(scrollBindRaf);
            scrollBindRaf = null;
        }
    }

    // Keeps the AI assistant transcript pinned to the newest message after a send/reply.
    function libraryChatScrollToBottom(logEl) {
        if (!logEl) {
            return;
        }
        logEl.scrollTop = logEl.scrollHeight;
    }

    window.refreshLibraryScrub = refreshLibraryScrub;
    window.resetLibraryScrub = resetLibraryScrub;
    window.attachLibraryInfiniteScroll = attachLibraryInfiniteScroll;
    window.setLibraryInfiniteScrollEnabled = setLibraryInfiniteScrollEnabled;
    window.disposeLibraryBrowse = disposeLibraryBrowse;
    window.libraryChatScrollToBottom = libraryChatScrollToBottom;
})();
