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

    // Called from C# before navigation
    window.scrollRestoration = {
        // The box that actually scrolls the page, for anything else that needs to move or measure
        // it. Exposed rather than duplicated: the window is NOT the scroller here, and code that
        // assumes it is fails silently - window.scrollBy simply moves nothing.
        container: getScrollContainer,

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
