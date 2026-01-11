// Scroll Restoration for Blazor Server
// Uses .page-content as the scroll container (not window)

(function() {
    const SCROLL_CONTAINER_SELECTOR = '.page-content';
    const scrollPositions = new Map();
    let isPopState = false;

    // Detect back/forward navigation
    window.addEventListener('popstate', function() {
        isPopState = true;
    });

    function getScrollContainer() {
        return document.querySelector(SCROLL_CONTAINER_SELECTOR);
    }

    // Called from C# before navigation
    window.scrollRestoration = {
        savePosition: function(path) {
            const container = getScrollContainer();
            if (container) {
                scrollPositions.set(path, container.scrollTop);
                console.log('[scroll] Saved:', path, '=', container.scrollTop);
            }
        },

        // Called from C# after navigation
        restoreOrScrollToTop: function(path) {
            const container = getScrollContainer();
            if (!container) return;

            if (isPopState) {
                // Back/forward: restore saved position
                const saved = scrollPositions.get(path);
                console.log('[scroll] Restoring (popstate):', path, '=', saved);
                container.scrollTop = saved !== undefined ? saved : 0;
                isPopState = false;
            } else if (!window.location.hash) {
                // Forward navigation: scroll to top
                console.log('[scroll] Scrolling to top');
                container.scrollTop = 0;
            }
        },

        // For debugging
        getPositions: function() {
            const obj = {};
            scrollPositions.forEach((v, k) => obj[k] = v);
            return obj;
        }
    };
})();
