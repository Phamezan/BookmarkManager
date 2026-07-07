// 3D "roundtable" active-link indicator for the top nav (primary-nav).
// Outer .nav-active-indicator handles position/width via a bouncy transition;
// inner .nav-active-indicator-disc gets a one-shot Y-axis spin class so the
// rotation never fights the translateX/width transition on the outer node.
window.repositionNavIndicator = function() {
    const nav = document.querySelector('.primary-nav');
    const indicator = document.querySelector('.nav-active-indicator');
    const disc = indicator ? indicator.querySelector('.nav-active-indicator-disc') : null;
    if (!nav || !indicator) return;

    nav.querySelectorAll('.mud-nav-link.nav-link-root-active').forEach(l => l.classList.remove('nav-link-root-active'));

    let activeLink = nav.querySelector('.mud-nav-link.active');
    if (!activeLink) {
        // Bookmarks.razor serves both "/bookmarks" and "/" (the landing route),
        // but Blazor's NavLink only matches "/bookmarks" — so on a fresh load at
        // "/" no link ever gets ".active" and the indicator/highlight never
        // appears at all. Fall back to the Bookmarks link for that one case.
        if (location.pathname === '/' || location.pathname === '') {
            activeLink = nav.querySelector('.mud-nav-link[href="bookmarks"]');
            if (activeLink) activeLink.classList.add('nav-link-root-active');
        }
    }
    if (!activeLink) {
        indicator.classList.remove('is-ready');
        return;
    }

    const navRect = nav.getBoundingClientRect();
    const linkRect = activeLink.getBoundingClientRect();
    // Absolutely positioned children offset from the containing block's padding
    // edge, not its border edge, but getBoundingClientRect() measures the border
    // edge — subtract the nav's own border width or the indicator drifts right/down
    // by exactly that amount relative to the link it's supposed to sit under.
    const left = linkRect.left - navRect.left - nav.clientLeft + nav.scrollLeft;
    const wasReady = indicator.classList.contains('is-ready');

    indicator.style.transform = `translateX(${left}px)`;
    indicator.style.width = `${linkRect.width}px`;
    indicator.classList.add('is-ready');

    // Only spin + spark on an actual route change, not first paint.
    if (wasReady && disc) {
        disc.classList.remove('is-spinning');
        // Force reflow so re-adding the class restarts the animation.
        void disc.offsetWidth;
        disc.classList.add('is-spinning');
        spawnNavParticles(indicator, linkRect.width);
        if (document.documentElement.getAttribute('data-theme') === 'grand-line') {
            spawnGearSmoke(indicator, linkRect.width);
        }
    }
};

function spawnNavParticles(indicator, width) {
    const count = 8;
    const centerX = width / 2;
    for (let i = 0; i < count; i++) {
        const particle = document.createElement('span');
        particle.className = 'nav-particle';
        const angle = (Math.PI * 2 * i) / count;
        const distance = 16 + Math.random() * 10;
        particle.style.left = `${centerX}px`;
        particle.style.setProperty('--nav-particle-x', `${Math.cos(angle) * distance}px`);
        particle.style.setProperty('--nav-particle-y', `${Math.sin(angle) * distance}px`);
        indicator.appendChild(particle);
        particle.addEventListener('animationend', () => particle.remove());
        // Safety net in case animationend doesn't fire (e.g. tab backgrounded).
        setTimeout(() => particle.remove(), 900);
    }
}

function spawnGearSmoke(indicator, width) {
    const count = 6;
    const centerX = width / 2;
    for (let i = 0; i < count; i++) {
        const puff = document.createElement('span');
        puff.className = 'nav-smoke-puff';
        
        // Random offsets around the indicator pill
        const offsetX = (Math.random() - 0.5) * width;
        const offsetY = (Math.random() - 0.5) * 10;
        
        puff.style.left = `${centerX + offsetX}px`;
        puff.style.top = `${20 + offsetY}px`;
        
        const floatX = (Math.random() - 0.5) * 40;
        const floatY = -30 - Math.random() * 30;
        const scale = 0.5 + Math.random() * 0.9;
        
        puff.style.setProperty('--smoke-x', `${floatX}px`);
        puff.style.setProperty('--smoke-y', `${floatY}px`);
        puff.style.setProperty('--smoke-scale', scale);
        
        indicator.appendChild(puff);
        puff.addEventListener('animationend', () => puff.remove());
        setTimeout(() => puff.remove(), 900);
    }
}

window.initNavIndicator = function() {
    if (window.navIndicatorInitialized) {
        window.repositionNavIndicator();
        return;
    }
    window.navIndicatorInitialized = true;

    window.repositionNavIndicator();
    window.addEventListener('resize', () => window.repositionNavIndicator());

    // Blazor's NavLink toggles its own ".active" class in response to the same
    // LocationChanged event we listen to from .NET, and subscriber order isn't
    // guaranteed — reading DOM state from the .NET-side handler can race ahead
    // of NavLink's own update, leaving the indicator one click behind until a
    // second click catches it up. Watching the class attribute directly instead
    // makes the reposition fire exactly when the active link actually changes.
    const nav = document.querySelector('.primary-nav');
    if (nav) {
        const observer = new MutationObserver(() => window.repositionNavIndicator());
        nav.querySelectorAll('.mud-nav-link').forEach(link => {
            observer.observe(link, { attributes: true, attributeFilter: ['class'] });
        });
    }
};
