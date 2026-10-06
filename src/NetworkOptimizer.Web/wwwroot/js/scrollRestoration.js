// Scroll Restoration for Blazor Server
// Mobile uses .main-content as scroll container, desktop uses .page-content

(function() {
    const scrollPositions = new Map();
    let isPopState = false;

    // Detect back/forward navigation
    window.addEventListener('popstate', function() {
        isPopState = true;
    });

    function getScrollContainer() {
        if (window.innerWidth <= 1024) return document.querySelector('.main-content');
        return document.querySelector('.page-content');
    }

    // Reload carry-over. The browser's own restore only covers the window, and the window is not
    // the scroller here, so a reload (or an OS-discarded tab coming back) would land at the top.
    const RELOAD_KEY = 'no.scrollOnReload';
    const RESTORE_WINDOW_MS = 8000;

    function currentUrl() {
        return location.pathname + location.search;
    }

    function saveForReload() {
        const container = getScrollContainer();
        if (!container) return;
        try {
            sessionStorage.setItem(RELOAD_KEY, JSON.stringify({ url: currentUrl(), top: container.scrollTop }));
        } catch { /* storage unavailable: reload lands at the top, as before */ }
    }

    function clearForReload() {
        try { sessionStorage.removeItem(RELOAD_KEY); } catch { }
    }

    // Saved on hide, because a mobile OS can discard a backgrounded tab and reload it with no
    // chance to run anything first. Cleared on return: a live page has nothing to carry over.
    document.addEventListener('visibilitychange', function() {
        if (document.visibilityState === 'hidden') saveForReload();
        else clearForReload();
    });

    function restoreAfterReload() {
        let saved = null;
        try {
            saved = JSON.parse(sessionStorage.getItem(RELOAD_KEY) || 'null');
            sessionStorage.removeItem(RELOAD_KEY);
        } catch { return; }
        if (!saved || saved.url !== currentUrl() || !(saved.top > 0)) return;

        // Keep re-applying: data loads in after first paint, and the interactive render replaces
        // the prerendered scroller, which resets it to 0. Any touch, wheel or key hands it back.
        const target = saved.top;
        const started = Date.now();
        const events = ['touchstart', 'wheel', 'pointerdown', 'keydown'];
        let timer = null;
        function stop() {
            clearInterval(timer);
            events.forEach(function(e) { window.removeEventListener(e, stop, true); });
        }
        events.forEach(function(e) { window.addEventListener(e, stop, { capture: true, passive: true }); });
        timer = setInterval(function() {
            const container = getScrollContainer();
            const done = Date.now() - started >= RESTORE_WINDOW_MS;
            if (container) {
                const max = container.scrollHeight - container.clientHeight;
                // Short of the target, wait for content; at the deadline take the closest point.
                if ((max >= target || done) && Math.abs(container.scrollTop - Math.min(target, max)) > 2) {
                    container.scrollTop = Math.min(target, max);
                }
            }
            if (done) stop();
        }, 100);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', restoreAfterReload);
    else restoreAfterReload();

    // Called from C# before navigation
    window.scrollRestoration = {
        // The box that actually scrolls the page, for anything else that needs to move or measure
        // it. Exposed rather than duplicated: the window is NOT the scroller here, and code that
        // assumes it is fails silently - window.scrollBy simply moves nothing.
        container: getScrollContainer,

        // Reload the page and put the scroller back where it was. For recovery reloads (a dead
        // circuit), where the user did not ask to leave their place.
        reload: function() {
            saveForReload();
            location.reload();
        },

        savePosition: function(path) {
            const container = getScrollContainer();
            if (container) {
                scrollPositions.set(path, container.scrollTop);
            }
        },

        // Called from C# after navigation
        restoreOrScrollToTop: function(path) {
            var container = getScrollContainer();
            if (!container) return;
            var hasFragment = !!window.location.hash;

            if (isPopState) {
                var saved = scrollPositions.get(path);
                container.scrollTop = saved !== undefined ? saved : 0;
                isPopState = false;
                return;
            }

            if (hasFragment) {
                // Fragment navigation: hide nav bar, no scroll padding, then scroll to element
                if (window.__setScrollState) window.__setScrollState(true);
                var el = document.getElementById(window.location.hash.substring(1));
                if (el) {
                    requestAnimationFrame(function() {
                        el.scrollIntoView({ behavior: 'instant', block: 'start' });
                    });
                }
            } else {
                // Page navigation: show nav bar, scroll to top
                if (window.__setScrollState) window.__setScrollState(false);
                container.scrollTop = 0;
            }
        }
    };
})();
